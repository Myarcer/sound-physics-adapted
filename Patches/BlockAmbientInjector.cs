using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace soundphysicsadapted.Patches
{
    /// <summary>
    /// Injects ambient sound properties onto blocks at startup and patches
    /// GetAmbientSoundStrength to control when they play.
    ///
    /// Uses VS's built-in ambient sound system — the same mechanism as leaded
    /// glass panes (BlockRainAmbient). VS automatically clusters adjacent blocks,
    /// merges bounding boxes, and manages one sound source per cluster.
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

        // Debug: throttle per-tick logging
        private static long lastDebugLogMs = 0;
        private const long DEBUG_LOG_INTERVAL_MS = 5000;

        // One-shot debug: confirm postfix is being called
        private static bool loggedFirstRainCall = false;
        private static bool loggedFirstTorchCall = false;

        /// <summary>
        /// Apply the Harmony postfix on Block.GetAmbientSoundStrength.
        /// </summary>
        public static void ApplyPatches(Harmony harmony, ICoreClientAPI api)
        {
            try
            {
                api.Logger.Notification("[SoundPhysicsAdapted] BlockAmbientInjector: Attempting to patch GetAmbientSoundStrength...");

                var method = AccessTools.Method(typeof(Block), nameof(Block.GetAmbientSoundStrength));
                if (method == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] BlockAmbientInjector: Could not find GetAmbientSoundStrength method");
                    return;
                }

                api.Logger.Debug($"[SoundPhysicsAdapted] BlockAmbientInjector: Found method {method.DeclaringType.Name}.{method.Name}");

                var postfix = new HarmonyMethod(typeof(BlockAmbientInjector), nameof(GetAmbientSoundStrengthPostfix));
                harmony.Patch(method, postfix: postfix);
                patchApplied = true;

                api.Logger.Notification("[SoundPhysicsAdapted] BlockAmbientInjector: Harmony patch applied OK");

                // Patch SystemClientTickingBlocks ctor to grab instance for weather-change rescan
                RainScanForcer.ApplyPatch(harmony, api);
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] BlockAmbientInjector: Patch FAILED: {ex.Message}");
                api.Logger.Error($"[SoundPhysicsAdapted] BlockAmbientInjector: Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Scan all registered blocks and inject Sounds.Ambient where configured.
        /// </summary>
        public static void InjectAmbientSounds(ICoreClientAPI api)
        {
            capi = api;
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null)
            {
                api.Logger.Warning("[SoundPhysicsAdapted] BlockAmbientInjector: Config is null, skipping injection");
                return;
            }

            rainSurfaceBlockIds.Clear();
            torchBlockIds.Clear();
            rainSurfaceCount = 0;
            torchCount = 0;

            int totalBlocks = 0;
            int nullBlocks = 0;
            int rainSkippedExistingAmbient = 0;
            int torchSkippedExistingAmbient = 0;
            var sampleRainMatches = new List<string>();
            var sampleTorchMatches = new List<string>();

            api.Logger.Notification($"[SoundPhysicsAdapted] BlockAmbientInjector: Starting injection scan. " +
                $"RainEnabled={config.EnableRainSurfaceImpacts} TorchEnabled={config.EnableTorchAmbient}");

            var materialConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;

            if (materialConfig?.RainSurfaceBlockPatterns != null)
                api.Logger.Notification($"[SoundPhysicsAdapted] BlockAmbientInjector: Rain patterns: [{string.Join(", ", materialConfig.RainSurfaceBlockPatterns)}]");
            if (materialConfig?.TorchBlockPatterns != null)
                api.Logger.Notification($"[SoundPhysicsAdapted] BlockAmbientInjector: Torch patterns: [{string.Join(", ", materialConfig.TorchBlockPatterns)}]");

            foreach (var block in api.World.Blocks)
            {
                totalBlocks++;
                if (block?.Code == null) { nullBlocks++; continue; }
                string path = block.Code.Path;

                // Rain surface injection
                if (config.EnableRainSurfaceImpacts && materialConfig?.RainSurfaceBlockPatterns != null)
                {
                    if (MatchesAnyPattern(path, materialConfig.RainSurfaceBlockPatterns))
                    {
                        bool injected = TryInjectRainSurface(block);
                        if (!injected) rainSkippedExistingAmbient++;
                        if (sampleRainMatches.Count < 10)
                            sampleRainMatches.Add($"{block.Code} (id={block.Id}, injected={injected}, existingAmbient={block.Sounds?.Ambient})");
                    }
                }

                // Torch ambient injection (exclude extinct/burnedout/empty variants)
                if (config.EnableTorchAmbient && materialConfig?.TorchBlockPatterns != null)
                {
                    if (MatchesAnyPattern(path, materialConfig.TorchBlockPatterns) && !path.Contains("extinct") && !path.Contains("burnedout") && !path.Contains("-empty"))
                    {
                        bool injected = TryInjectTorchAmbient(block, config);
                        if (!injected) torchSkippedExistingAmbient++;
                        if (sampleTorchMatches.Count < 10)
                            sampleTorchMatches.Add($"{block.Code} (id={block.Id}, injected={injected}, existingAmbient={block.Sounds?.Ambient})");
                    }
                }
            }

            // Always log results regardless of count
            api.Logger.Notification($"[SoundPhysicsAdapted] BlockAmbientInjector: Scanned {totalBlocks} blocks ({nullBlocks} null). " +
                $"Rain: {rainSurfaceCount} injected, {rainSkippedExistingAmbient} skipped (existing ambient). " +
                $"Torch: {torchCount} injected, {torchSkippedExistingAmbient} skipped (existing ambient).");

            if (sampleRainMatches.Count > 0)
                api.Logger.Notification($"[SoundPhysicsAdapted] BlockAmbientInjector: Rain matches (first {sampleRainMatches.Count}): {string.Join(" | ", sampleRainMatches)}");
            else if (config.EnableRainSurfaceImpacts)
                api.Logger.Warning("[SoundPhysicsAdapted] BlockAmbientInjector: NO blocks matched rain surface patterns!");

            if (sampleTorchMatches.Count > 0)
                api.Logger.Notification($"[SoundPhysicsAdapted] BlockAmbientInjector: Torch matches (first {sampleTorchMatches.Count}): {string.Join(" | ", sampleTorchMatches)}");
            else if (config.EnableTorchAmbient)
                api.Logger.Warning("[SoundPhysicsAdapted] BlockAmbientInjector: NO blocks matched torch patterns!");
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

        /// <returns>true if ambient was injected, false if skipped</returns>
        private static bool TryInjectRainSurface(Block block)
        {
            if (block.Sounds == null)
                block.Sounds = new BlockSounds();

            // Don't overwrite blocks that already have an ambient sound
            if (block.Sounds.Ambient != null) return false;

            block.Sounds.Ambient = new AssetLocation("soundphysicsadapted:sounds/weather/rain-on-metal");
            block.Sounds.AmbientBlockCount = 6f;
            block.Sounds.AmbientMaxDistanceMerge = 4f;

            rainSurfaceBlockIds.Add(block.Id);
            rainSurfaceCount++;
            return true;
        }

        /// <returns>true if ambient was injected, false if skipped</returns>
        private static bool TryInjectTorchAmbient(Block block, SoundPhysicsConfig config)
        {
            if (block.Sounds == null)
                block.Sounds = new BlockSounds();

            if (block.Sounds.Ambient != null) return false;

            block.Sounds.Ambient = new AssetLocation(config.TorchAmbientSoundPath);
            block.Sounds.AmbientBlockCount = 1f;
            block.Sounds.AmbientMaxDistanceMerge = 2f;

            torchBlockIds.Add(block.Id);
            torchCount++;
            return true;
        }

        // ════════════════════════════════════════════════════════════════
        // Harmony postfix
        // ════════════════════════════════════════════════════════════════

        public static void GetAmbientSoundStrengthPostfix(
            Block __instance, IWorldAccessor world, BlockPos pos, ref float __result)
        {
            if (!patchApplied) return;

            // Multi-block placeholders (e.g. upper half of 2-block-tall doors) carry their own
            // block ID that is NOT in our injected HashSets, but VS's ambient scan calls
            // GetSounds() on them which forwards to the controller block (returning the door's
            // injected Ambient). Without routing the strength call to the controller block too,
            // the base Block.GetAmbientSoundStrength returns 1f and the sound plays at full
            // volume regardless of rain. Resolve the controller block here.
            Block effectiveBlock = __instance;
            BlockPos effectivePos = pos;
            if (__instance is BlockMultiblock mb && world?.BlockAccessor != null)
            {
                effectivePos = pos.AddCopy(mb.OffsetInv);
                var controller = world.BlockAccessor.GetBlock(effectivePos);
                if (controller == null || controller is BlockMultiblock)
                {
                    return; // bail; leave VS default
                }
                effectiveBlock = controller;
            }

            int blockId = effectiveBlock.Id;

            // Rain surface blocks
            if (rainSurfaceBlockIds.Contains(blockId))
            {
                if (!loggedFirstRainCall && capi != null)
                {
                    loggedFirstRainCall = true;
                    capi.Logger.Notification($"[SoundPhysicsAdapted] BlockAmbientInjector: POSTFIX CALLED for rain block {effectiveBlock.Code} at {effectivePos} (instance={__instance.GetType().Name})");
                }
                __result = CalculateRainSurfaceStrength(world, effectivePos, effectiveBlock);
                return;
            }

            // Torch blocks
            if (torchBlockIds.Contains(blockId))
            {
                if (!loggedFirstTorchCall && capi != null)
                {
                    loggedFirstTorchCall = true;
                    capi.Logger.Notification($"[SoundPhysicsAdapted] BlockAmbientInjector: POSTFIX CALLED for torch block {effectiveBlock.Code} at {effectivePos} (instance={__instance.GetType().Name})");
                }
                __result = SoundPhysicsAdaptedModSystem.Config?.TorchAmbientVolume ?? 0.35f;
                return;
            }

            // If the controller block has our injected ambient but isn't in either HashSet
            // (shouldn't happen, but defensive): silence it instead of leaving 1f from base.
            if (effectiveBlock != __instance && effectiveBlock.Sounds?.Ambient != null)
            {
                var path = effectiveBlock.Sounds.Ambient.Path;
                if (path != null && (path.Contains("rain-on-metal") || path.Contains("torch")))
                {
                    __result = 0f;
                }
            }
        }

        private static float CalculateRainSurfaceStrength(IWorldAccessor world, BlockPos pos, Block block)
        {
            if (capi == null) return 0f;

            var conds = capi.World.Player?.Entity?.selfClimateCond;
            if (conds == null)
            {
                DebugLogThrottled("RainSurface: selfClimateCond is null");
                return 0f;
            }

            if (conds.Rainfall <= 0.1f)
            {
                DebugLogThrottled($"RainSurface: Rainfall too low ({conds.Rainfall:F2})");
                return 0f;
            }

            if (conds.Temperature <= 3f)
            {
                DebugLogThrottled($"RainSurface: Temperature too low ({conds.Temperature:F1}C, need >3C)");
                return 0f;
            }

            // Sky exposure check
            int rainMapH = world.BlockAccessor.GetRainMapHeightAt(pos);
            int distToRain = world.BlockAccessor.GetDistanceToRainFall(pos, 3, 1);
            bool exposed = rainMapH <= pos.Y || distToRain <= 2;

            if (!exposed)
            {
                DebugLogThrottled($"RainSurface: Block {block.Code} at {pos} NOT exposed (rainMapH={rainMapH}, blockY={pos.Y}, distToRain={distToRain})");
                return 0f;
            }

            float volumeMultiplier = SoundPhysicsAdaptedModSystem.Config?.RainSurfaceVolume ?? 0.5f;
            float result = conds.Rainfall * volumeMultiplier;

            DebugLogThrottled($"RainSurface: Block {block.Code} at {pos} PLAYING (rainfall={conds.Rainfall:F2}, vol={result:F3}, rainMapH={rainMapH}, distToRain={distToRain})");
            return result;
        }

        /// <summary>Throttled debug log — max once per DEBUG_LOG_INTERVAL_MS.</summary>
        private static void DebugLogThrottled(string message)
        {
            if (capi == null) return;
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.DebugMode || !config.DebugWeather) return;

            long now = capi.ElapsedMilliseconds;
            if (now - lastDebugLogMs < DEBUG_LOG_INTERVAL_MS) return;
            lastDebugLogMs = now;

            WeatherAudioManager.WeatherDebugLog($"[BlockAmbientInjector] {message}");
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
            RainScanForcer.Clear();
        }
    }

    /// <summary>
    /// Forces VS's ambient block scan to restart when rain state changes.
    ///
    /// VS's SystemClientTickingBlocks only rescans every 20 seconds or when the
    /// player moves 8 blocks. Weather changes don't trigger a rescan, so rain-on-metal
    /// sounds would persist for up to 20 seconds after rain stops.
    ///
    /// Fix: grab the SystemClientTickingBlocks instance via constructor postfix,
    /// then watch rainfall every 2 seconds and force shouldStartScanning = true
    /// when the rain state changes. This matches how glass panes behave but with
    /// prompt stop/start instead of up to 20s delay.
    /// </summary>
    internal static class RainScanForcer
    {
        private static object ctbInstance;
        private static FieldInfo shouldStartScanningField;
        private static FieldInfo shouldStartScanningLockField;
        private static ICoreClientAPI capi;
        private static float lastRainfall = -1f;

        public static void ApplyPatch(Harmony harmony, ICoreClientAPI api)
        {
            capi = api;
            try
            {
                var ctbType = AccessTools.TypeByName("Vintagestory.Client.NoObf.SystemClientTickingBlocks");
                if (ctbType == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] RainScanForcer: Could not find SystemClientTickingBlocks type");
                    return;
                }

                shouldStartScanningField = AccessTools.Field(ctbType, "shouldStartScanning");
                shouldStartScanningLockField = AccessTools.Field(ctbType, "shouldStartScanningLock");

                if (shouldStartScanningField == null || shouldStartScanningLockField == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] RainScanForcer: Could not find shouldStartScanning fields");
                    return;
                }

                var clientMainType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain");
                if (clientMainType == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] RainScanForcer: Could not find ClientMain type");
                    return;
                }

                var ctor = AccessTools.Constructor(ctbType, new Type[] { clientMainType });
                if (ctor == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] RainScanForcer: Could not find SystemClientTickingBlocks constructor");
                    return;
                }

                var ctorPostfix = new HarmonyMethod(typeof(RainScanForcer), nameof(CtorPostfix));
                harmony.Patch(ctor, postfix: ctorPostfix);

                // Poll rainfall every 2 seconds; force rescan on state change
                api.Event.RegisterGameTickListener(OnTick, 2000, 500);

                api.Logger.Notification("[SoundPhysicsAdapted] RainScanForcer: Patch applied — weather-change rescan active");
            }
            catch (Exception ex)
            {
                api.Logger.Warning($"[SoundPhysicsAdapted] RainScanForcer: Patch failed (non-critical): {ex.Message}");
            }
        }

        public static void CtorPostfix(object __instance)
        {
            ctbInstance = __instance;
        }

        private static void OnTick(float dt)
        {
            if (ctbInstance == null || capi == null) return;

            float rainfall = capi.World.Player?.Entity?.selfClimateCond?.Rainfall ?? 0f;
            bool wasRaining = lastRainfall > 0.1f;
            bool isRaining = rainfall > 0.1f;

            if (lastRainfall >= 0f && wasRaining != isRaining)
            {
                // Rain state toggled — force ambient scan to restart immediately
                try
                {
                    var lockObj = shouldStartScanningLockField.GetValue(ctbInstance);
                    lock (lockObj)
                    {
                        shouldStartScanningField.SetValue(ctbInstance, true);
                    }

                    if (SoundPhysicsAdaptedModSystem.Config?.DebugMode == true &&
                        SoundPhysicsAdaptedModSystem.Config?.DebugWeather == true)
                        capi.Logger.Notification($"[SoundPhysicsAdapted] RainScanForcer: rain state changed ({lastRainfall:F2}→{rainfall:F2}), forcing ambient rescan");
                }
                catch (Exception ex)
                {
                    if (SoundPhysicsAdaptedModSystem.Config?.DebugMode == true)
                        capi.Logger.Warning($"[SoundPhysicsAdapted] RainScanForcer: failed to force rescan: {ex.Message}");
                }
            }

            lastRainfall = rainfall;
        }

        public static void Clear()
        {
            ctbInstance = null;
            capi = null;
            lastRainfall = -1f;
        }
    }
}
