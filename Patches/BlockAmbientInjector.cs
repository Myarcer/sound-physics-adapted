using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted.Patches
{
    /// <summary>
    /// Injects ambient sound properties onto blocks at startup and patches
    /// GetAmbientSoundStrength to control when they play.
    ///
    /// Uses VS's built-in ambient sound system — the same mechanism as leaded
    /// glass panes (BlockRainAmbient). VS automatically:
    ///   - Scans blocks in a radius around the player
    ///   - Clusters adjacent blocks with the same ambient sound asset
    ///   - Merges nearby bounding boxes (MaxDistanceMerge)
    ///   - Manages one sound source per cluster, positioned at nearest bbox surface
    ///   - Handles lifecycle (start/stop/fade)
    ///
    /// Rain Surface Impacts:
    ///   Metal blocks (anvils, metalblocks, etc.) play a rain impact sound when
    ///   exposed to rain. Volume scales with rainfall intensity. Clustered: a row
    ///   of metalblocks becomes one louder sound source, not N individual ones.
    ///
    /// Torch Ambient:
    ///   Lit torches emit a quiet fire crackling loop. Uses the same fireplace.ogg
    ///   that firepits use, at reduced volume. Extinct torches are excluded.
    /// </summary>
    internal static class BlockAmbientInjector
    {
        // Block IDs we've injected ambient sounds onto (for the Harmony postfix)
        private static readonly HashSet<int> rainSurfaceBlockIds = new();
        private static readonly HashSet<int> torchBlockIds = new();

        private static ICoreClientAPI capi;
        private static bool patchApplied = false;

        // Stats for logging
        private static int rainSurfaceCount = 0;
        private static int torchCount = 0;

        /// <summary>
        /// Apply the Harmony postfix on Block.GetAmbientSoundStrength.
        /// Must be called during StartClientSide, before block injection.
        /// </summary>
        public static void ApplyPatches(Harmony harmony, ICoreClientAPI api)
        {
            try
            {
                var method = AccessTools.Method(typeof(Block), nameof(Block.GetAmbientSoundStrength));
                if (method == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] BlockAmbientInjector: Could not find GetAmbientSoundStrength method");
                    return;
                }

                var postfix = new HarmonyMethod(typeof(BlockAmbientInjector), nameof(GetAmbientSoundStrengthPostfix));
                harmony.Patch(method, postfix: postfix);
                patchApplied = true;

                api.Logger.Notification("[SoundPhysicsAdapted] BlockAmbientInjector: Harmony patch applied");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] BlockAmbientInjector: Patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Scan all registered blocks and inject Sounds.Ambient where configured.
        /// Call after ApplyPatches, during StartClientSide.
        /// </summary>
        public static void InjectAmbientSounds(ICoreClientAPI api)
        {
            capi = api;
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null) return;

            rainSurfaceBlockIds.Clear();
            torchBlockIds.Clear();
            rainSurfaceCount = 0;
            torchCount = 0;

            foreach (var block in api.World.Blocks)
            {
                if (block?.Code == null) continue;
                string path = block.Code.Path;

                // Rain surface injection
                if (config.EnableRainSurfaceImpacts && config.RainSurfaceBlockPatterns != null)
                {
                    if (MatchesAnyPattern(path, config.RainSurfaceBlockPatterns))
                    {
                        TryInjectRainSurface(block);
                    }
                }

                // Torch ambient injection (exclude extinct variants)
                if (config.EnableTorchAmbient && config.TorchBlockPatterns != null)
                {
                    if (MatchesAnyPattern(path, config.TorchBlockPatterns) && !path.Contains("extinct"))
                    {
                        TryInjectTorchAmbient(block, config);
                    }
                }
            }

            if (rainSurfaceCount > 0)
                api.Logger.Notification($"[SoundPhysicsAdapted] Rain surface impacts: injected ambient on {rainSurfaceCount} block variants");
            if (torchCount > 0)
                api.Logger.Notification($"[SoundPhysicsAdapted] Torch ambient: injected ambient on {torchCount} block variants");
        }

        private static bool MatchesAnyPattern(string blockPath, string[] patterns)
        {
            for (int i = 0; i < patterns.Length; i++)
            {
                if (blockPath.StartsWith(patterns[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ════════════════════════════════════════════════════════════════
        // Block injection
        // ════════════════════════════════════════════════════════════════

        private static void TryInjectRainSurface(Block block)
        {
            if (block.Sounds == null)
                block.Sounds = new BlockSounds();

            // Don't overwrite blocks that already have an ambient sound
            if (block.Sounds.Ambient != null) return;

            // Sound asset: mod-provided rain-on-metal loop
            // User places .ogg at: resources/assets/soundphysicsadapted/sounds/weather/rain-on-metal.ogg
            block.Sounds.Ambient = new AssetLocation("soundphysicsadapted:sounds/weather/rain-on-metal");

            // AmbientBlockCount = ratio for volume curve: sqrt(N) / ratio
            // 6 → single block=0.17, 4 blocks=0.33, 9=0.50, 36=1.0
            // Metal surfaces should scale noticeably with area
            block.Sounds.AmbientBlockCount = 6f;

            // Merge distance: metal blocks placed together cluster within 4 blocks
            block.Sounds.AmbientMaxDistanceMerge = 4f;

            rainSurfaceBlockIds.Add(block.Id);
            rainSurfaceCount++;
        }

        private static void TryInjectTorchAmbient(Block block, SoundPhysicsConfig config)
        {
            if (block.Sounds == null)
                block.Sounds = new BlockSounds();

            // Don't overwrite blocks that already have an ambient sound
            if (block.Sounds.Ambient != null) return;

            // Sound asset: vanilla fireplace crackling (same as firepits)
            block.Sounds.Ambient = new AssetLocation(config.TorchAmbientSoundPath);

            // AmbientBlockCount = 1: each torch (or small cluster) is equally loud.
            // Volume is controlled by GetAmbientSoundStrength returning TorchAmbientVolume.
            block.Sounds.AmbientBlockCount = 1f;

            // Merge distance: torches are typically spaced out, but adjacent ones should cluster
            block.Sounds.AmbientMaxDistanceMerge = 2f;

            torchBlockIds.Add(block.Id);
            torchCount++;
        }

        // ════════════════════════════════════════════════════════════════
        // Harmony postfix — controls WHEN and HOW LOUD each injected
        // ambient sound plays. Fires for all calls to the BASE
        // Block.GetAmbientSoundStrength (not overrides like BlockRainAmbient).
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Postfix on Block.GetAmbientSoundStrength.
        /// For our injected blocks, override the default return (1f) with
        /// weather-dependent or constant values.
        /// </summary>
        public static void GetAmbientSoundStrengthPostfix(
            Block __instance, IWorldAccessor world, BlockPos pos, ref float __result)
        {
            if (!patchApplied) return;

            int blockId = __instance.Id;

            // Rain surface blocks: return 0 when not raining, rainfall-scaled when exposed
            if (rainSurfaceBlockIds.Contains(blockId))
            {
                __result = CalculateRainSurfaceStrength(world, pos);
                return;
            }

            // Torch blocks: constant volume (always audible when lit)
            if (torchBlockIds.Contains(blockId))
            {
                __result = SoundPhysicsAdaptedModSystem.Config?.TorchAmbientVolume ?? 0.12f;
                return;
            }
        }

        /// <summary>
        /// Calculate rain surface ambient strength. Mirrors BlockRainAmbient logic:
        /// - Rainfall > 0.1 and temperature > 3°C (no rain pinging on frozen metal)
        /// - Block exposed to rain (rain height map check + nearby rainfall check)
        /// Returns rainfall * config volume multiplier.
        /// </summary>
        private static float CalculateRainSurfaceStrength(IWorldAccessor world, BlockPos pos)
        {
            if (capi == null) return 0f;

            var conds = capi.World.Player?.Entity?.selfClimateCond;
            if (conds == null) return 0f;
            if (conds.Rainfall <= 0.1f) return 0f;
            if (conds.Temperature <= 3f) return 0f;

            // Sky exposure: direct rain OR rain falling within 3 blocks horizontally, 1 above
            // Same check as vanilla BlockRainAmbient — handles slight overhangs
            bool exposed = world.BlockAccessor.GetRainMapHeightAt(pos) <= pos.Y
                        || world.BlockAccessor.GetDistanceToRainFall(pos, 3, 1) <= 2;

            if (!exposed) return 0f;

            float volumeMultiplier = SoundPhysicsAdaptedModSystem.Config?.RainSurfaceVolume ?? 0.5f;
            return conds.Rainfall * volumeMultiplier;
        }

        // ════════════════════════════════════════════════════════════════
        // Cleanup
        // ════════════════════════════════════════════════════════════════

        public static void Clear()
        {
            rainSurfaceBlockIds.Clear();
            torchBlockIds.Clear();
            capi = null;
            patchApplied = false;
            rainSurfaceCount = 0;
            torchCount = 0;
        }
    }
}
