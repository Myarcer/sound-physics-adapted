using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace soundphysicsadapted.Patches
{
    /// <summary>
    /// Stores frozen rotation state when resonator is paused.
    /// </summary>
    public class FrozenRotation
    {
        public float RotationY;
        public long FrozenAtMs;
        public long PausedAtElapsedMs;  // The InWorldEllapsedMilliseconds when we paused
        public long OriginalUpdatedTotalMs;  // The updatedTotalMs value when we paused
        
        public FrozenRotation(float rotY, long frozenAtMs, long pausedAtElapsedMs = 0, long originalUpdatedMs = 0)
        {
            RotationY = rotY;
            FrozenAtMs = frozenAtMs;
            PausedAtElapsedMs = pausedAtElapsedMs;
            OriginalUpdatedTotalMs = originalUpdatedMs;
        }
    }

    /// <summary>
    /// Patches ResonatorRenderer to freeze the spinning disc when music is paused.
    /// Without this, the disc continues spinning based on elapsed time regardless of playback state.
    /// </summary>
    [HarmonyPatch]
    public static class ResonatorRendererPatch
    {
        // Track frozen rotation per renderer instance (WeakTable = auto-cleanup when renderer disposed)
        private static ConditionalWeakTable<ResonatorRenderer, FrozenRotation> frozenRotations = 
            new ConditionalWeakTable<ResonatorRenderer, FrozenRotation>();
        
        // Persist frozen rotation by BlockPos for chunk reload survival
        private static System.Collections.Generic.Dictionary<BlockPos, float> savedRotationsByPos = 
            new System.Collections.Generic.Dictionary<BlockPos, float>();
        
        // Track pause timing for proper resume
        private static System.Collections.Generic.Dictionary<BlockPos, (long pausedAtMs, long originalUpdatedMs)> pauseTimingByPos = 
            new System.Collections.Generic.Dictionary<BlockPos, (long, long)>();

        /// <summary>
        /// Get saved rotation for a position (for tree attribute saving).
        /// </summary>
        public static float? GetSavedRotation(BlockPos pos)
        {
            if (pos == null) return null;
            foreach (var kvp in savedRotationsByPos)
            {
                if (kvp.Key.Equals(pos)) return kvp.Value;
            }
            return null;
        }

        /// <summary>
        /// Set saved rotation for a position (for tree attribute loading).
        /// </summary>
        public static void SetSavedRotation(BlockPos pos, float rotation)
        {
            if (pos == null) return;
            // Remove old entry if exists
            BlockPos toRemove = null;
            foreach (var key in savedRotationsByPos.Keys)
            {
                if (key.Equals(pos)) { toRemove = key; break; }
            }
            if (toRemove != null) savedRotationsByPos.Remove(toRemove);
            savedRotationsByPos[pos.Copy()] = rotation;
        }

        /// <summary>
        /// Clear saved rotation for a position (on disc eject).
        /// </summary>
        public static void ClearSavedRotation(BlockPos pos)
        {
            if (pos == null) return;
            BlockPos toRemove = null;
            foreach (var key in savedRotationsByPos.Keys)
            {
                if (key.Equals(pos)) { toRemove = key; break; }
            }
            if (toRemove != null) savedRotationsByPos.Remove(toRemove);
        }

        // Cached field accessors
        private static AccessTools.FieldRef<ResonatorRenderer, BlockPos> posField;
        private static AccessTools.FieldRef<ResonatorRenderer, ICoreClientAPI> apiField;
        private static AccessTools.FieldRef<ResonatorRenderer, Vec3f> discRotRadField;
        private static AccessTools.FieldRef<ResonatorRenderer, long> updatedTotalMsField;
        private static bool fieldsInitialized = false;

        /// <summary>
        /// Apply the patch manually since ResonatorRenderer is from VSSurvivalMod.
        /// </summary>
        public static void ApplyPatches(Harmony harmony, ICoreClientAPI api)
        {
            try
            {
                var rendererType = typeof(ResonatorRenderer);
                var onRenderFrame = AccessTools.Method(rendererType, "OnRenderFrame");
                
                if (onRenderFrame == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] ResonatorRenderer.OnRenderFrame not found. Disc freeze disabled.");
                    return;
                }

                // Initialize field accessors
                posField = AccessTools.FieldRefAccess<ResonatorRenderer, BlockPos>("pos");
                apiField = AccessTools.FieldRefAccess<ResonatorRenderer, ICoreClientAPI>("api");
                discRotRadField = AccessTools.FieldRefAccess<ResonatorRenderer, Vec3f>("discRotRad");
                updatedTotalMsField = AccessTools.FieldRefAccess<ResonatorRenderer, long>("updatedTotalMs");
                
                if (posField == null || apiField == null || discRotRadField == null || updatedTotalMsField == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not access ResonatorRenderer fields. Disc freeze disabled.");
                    return;
                }
                fieldsInitialized = true;

                var prefix = typeof(ResonatorRendererPatch).GetMethod("OnRenderFramePrefix");
                harmony.Patch(onRenderFrame, prefix: new HarmonyMethod(prefix));
                
                api.Logger.Notification("[SoundPhysicsAdapted] ResonatorRenderer disc freeze patch applied.");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to patch ResonatorRenderer: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix for OnRenderFrame - checks IsPlaying and freezes rotation when paused.
        /// On resume, we need to adjust updatedTotalMs to compensate for the pause duration
        /// so the rotation continues from where it left off without a jump.
        /// </summary>
        public static void OnRenderFramePrefix(ResonatorRenderer __instance)
        {
            if (!fieldsInitialized) return;

            try
            {
                var api = apiField(__instance);
                var pos = posField(__instance);
                
                if (api?.World?.BlockAccessor == null || pos == null) return;

                // Get the BlockEntity to check IsPlaying state
                var resonator = api.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityResonator;
                if (resonator == null) return;

                var discRotRad = discRotRadField(__instance);
                if (discRotRad == null) return;

                long ellapsedMs = api.InWorldEllapsedMilliseconds;
                long updatedTotalMs = updatedTotalMsField(__instance);

                if (!resonator.IsPlaying)
                {
                    // Paused - freeze the disc at its current (or last known) rotation
                    if (!frozenRotations.TryGetValue(__instance, out var frozen))
                    {
                        // Check if we have a saved rotation from chunk reload
                        float? savedRot = GetSavedRotation(pos);
                        float currentRot;
                        if (savedRot.HasValue)
                        {
                            currentRot = savedRot.Value;
                            api.Logger.Debug($"[SoundPhysicsAdapted] RendererPrefix: Using savedRot={currentRot:F3} for {pos}");
                        }
                        else
                        {
                            // First frame paused: calculate current rotation and freeze it
                            currentRot = (ellapsedMs - updatedTotalMs) / 500f * GameMath.PI;
                            api.Logger.Debug($"[SoundPhysicsAdapted] RendererPrefix: No savedRot, calculated currentRot={currentRot:F3} (elapsed={ellapsedMs}, updated={updatedTotalMs})");
                        }
                        // Store pause timing for resume compensation
                        frozen = new FrozenRotation(currentRot, ellapsedMs, ellapsedMs, updatedTotalMs);
                        frozenRotations.Add(__instance, frozen);
                        
                        // Also save to pos-based dictionary for cross-instance resume
                        BlockPos posKey = null;
                        foreach (var k in pauseTimingByPos.Keys) { if (k.Equals(pos)) { posKey = k; break; } }
                        if (posKey != null) pauseTimingByPos.Remove(posKey);
                        pauseTimingByPos[pos.Copy()] = (ellapsedMs, updatedTotalMs);
                        
                        // Save rotation for persistence
                        SetSavedRotation(pos, currentRot);
                        api.Logger.Debug($"[SoundPhysicsAdapted] RendererPrefix: Created freeze at rotation {frozen.RotationY:F3}, pausedAt={ellapsedMs}");
                    }
                    
                    // Override the rotation with frozen value
                    // Formula: discRotRad.Y = (ellapsedMs - updatedTotalMs) / 500f * PI
                    // We want: frozenRot = (ellapsedMs - X) / 500f * PI
                    // Solve for X: X = ellapsedMs - (frozenRot * 500f / PI)
                    long adjustedUpdatedMs = ellapsedMs - (long)(frozen.RotationY * 500f / GameMath.PI);
                    updatedTotalMsField(__instance) = adjustedUpdatedMs;
                }
                else
                {
                    // Playing - check if we're resuming from pause
                    if (frozenRotations.TryGetValue(__instance, out var frozen))
                    {
                        // RESUME: Calculate how long we were paused and adjust updatedTotalMs
                        long pauseDuration = ellapsedMs - frozen.PausedAtElapsedMs;
                        
                        // The new updatedTotalMs should be the original + pause duration
                        // This makes the elapsed-updated delta the same as it was when we paused
                        long newUpdatedTotalMs = frozen.OriginalUpdatedTotalMs + pauseDuration;
                        updatedTotalMsField(__instance) = newUpdatedTotalMs;
                        
                        api.Logger.Debug($"[SoundPhysicsAdapted] RendererPrefix: RESUMING - pauseDuration={pauseDuration}ms, newUpdated={newUpdatedTotalMs}");
                        
                        frozenRotations.Remove(__instance);
                        
                        // Clean up pos-based timing
                        BlockPos posKey = null;
                        foreach (var k in pauseTimingByPos.Keys) { if (k.Equals(pos)) { posKey = k; break; } }
                        if (posKey != null) pauseTimingByPos.Remove(posKey);
                    }
                }
            }
            catch
            {
                // Silently ignore - don't spam logs for render issues
            }
        }
    }
}
