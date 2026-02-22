using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Public API for other mods to query SoundPhysicsAdapted calculations.
    ///
    /// Designed for voice chat mods (RPVoiceChat) and any mod managing its own
    /// OpenAL sources that wants occlusion, reverb, and sound repositioning
    /// without conflicting with our internal filter management.
    ///
    /// Usage pattern (from another mod):
    ///   1. Check IsAvailable at startup
    ///   2. Each tick, call QueryAcoustics() with your source + listener positions
    ///   3. Apply the returned values to your own OpenAL sources/filters
    ///
    /// This API returns VALUES only — it never touches your OpenAL sources.
    /// You keep full control of your filters, aux sends, and source state.
    ///
    /// Example (RPVoiceChat integration, ~10 lines):
    /// <code>
    /// // In your muffling update tick (200ms):
    /// var mod = capi.ModLoader.GetModSystem("soundphysicsadapted.SoundPhysicsAdaptedModSystem");
    /// if (mod != null)
    /// {
    ///     var result = SoundPhysicsAPI.QueryAcoustics(speakerWorldPos, listenerWorldPos);
    ///     if (result.IsValid)
    ///     {
    ///         lowpassFilter.SetHFGain(result.OcclusionGainHF);
    ///         // Apply reverb send gains to your aux slots...
    ///         // Reposition source toward result.ApparentPosition...
    ///     }
    /// }
    /// </code>
    /// </summary>
    public static class SoundPhysicsAPI
    {
        // ════════════════════════════════════════════════════════════════
        // Availability Check
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Whether SoundPhysicsAdapted is loaded, initialized, and ready to serve queries.
        /// Check this once at startup or before first use.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                var config = SoundPhysicsAdaptedModSystem.Config;
                return config != null && config.Enabled && EfxHelper.IsAvailable;
            }
        }

        /// <summary>
        /// Whether the reverb system is initialized and can provide reverb data.
        /// Reverb requires EFX auxiliary effect slots — some sound cards may not support them.
        /// </summary>
        public static bool IsReverbAvailable => ReverbEffects.IsInitialized;

        /// <summary>
        /// Number of reverb auxiliary send slots available (0-4).
        /// RPVoiceChat or similar mods need this to know how many aux sends to connect.
        /// </summary>
        public static int ReverbSlotCount => ReverbEffects.MaxAuxSends;

        // ════════════════════════════════════════════════════════════════
        // Simple Queries (One Value)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get occlusion lowpass filter value for a sound path.
        /// Uses our multi-ray material-aware raycaster.
        ///
        /// Returns a gainHF value ready to apply to an OpenAL lowpass filter:
        ///   1.0 = no occlusion (clear line of sight)
        ///   0.0 = fully occluded (never actually returned — clamped to MinLowPassFilter)
        ///
        /// Typical use: lowpassFilter.SetHFGain(GetOcclusionGainHF(speakerPos, listenerPos))
        /// </summary>
        /// <param name="sourcePos">World position of the sound source (e.g., player speaking)</param>
        /// <param name="listenerPos">World position of the listener</param>
        /// <returns>GainHF value (0.001 - 1.0), or 1.0 if unavailable</returns>
        public static float GetOcclusionGainHF(Vec3d sourcePos, Vec3d listenerPos)
        {
            if (!IsAvailable) return 1f;

            try
            {
                var blockAccessor = SoundPhysicsAdaptedModSystem.ClientApi?.World?.BlockAccessor;
                if (blockAccessor == null) return 1f;

                float occlusion = OcclusionCalculator.Calculate(sourcePos, listenerPos, blockAccessor);
                return OcclusionCalculator.OcclusionToFilter(occlusion);
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[API] GetOcclusionGainHF error: {ex.Message}");
                return 1f;
            }
        }

        /// <summary>
        /// Get raw occlusion accumulation value (before conversion to gainHF).
        /// Useful if caller wants to apply their own attenuation curve.
        ///
        /// Returns: 0m = no occlusion, higher = more material in path.
        /// Convert to gainHF yourself with: exp(-occlusion * absorptionCoeff)
        /// </summary>
        public static float GetRawOcclusion(Vec3d sourcePos, Vec3d listenerPos)
        {
            if (!IsAvailable) return 0f;

            try
            {
                var blockAccessor = SoundPhysicsAdaptedModSystem.ClientApi?.World?.BlockAccessor;
                if (blockAccessor == null) return 0f;

                return OcclusionCalculator.Calculate(sourcePos, listenerPos, blockAccessor);
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[API] GetRawOcclusion error: {ex.Message}");
                return 0f;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Full Acoustic Query (Occlusion + Reverb + Repositioning)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Full acoustic query — returns occlusion, reverb send gains, AND sound
        /// repositioning in a single call. This is the recommended entry point
        /// for voice chat mods.
        ///
        /// Runs our full AcousticRaytracer (fibonacci-sphere rays with bouncing),
        /// which simultaneously computes all three systems in one pass.
        ///
        /// Performance note: This fires config.ReverbRayCount rays (default 32)
        /// with bouncing. At 200ms tick interval, this is negligible per player.
        /// Scale interval by player count if needed (e.g., 200ms * nearbyPlayers).
        /// </summary>
        /// <param name="sourcePos">World position of the sound source</param>
        /// <param name="listenerPos">World position of the listener</param>
        /// <returns>Complete acoustic result. Check result.IsValid before use.</returns>
        public static AcousticResult QueryAcoustics(Vec3d sourcePos, Vec3d listenerPos)
        {
            if (!IsAvailable)
                return AcousticResult.Unavailable;

            try
            {
                var blockAccessor = SoundPhysicsAdaptedModSystem.ClientApi?.World?.BlockAccessor;
                if (blockAccessor == null)
                    return AcousticResult.Unavailable;

                // Get multi-ray occlusion (our superior raycaster)
                float occlusion = OcclusionCalculator.Calculate(sourcePos, listenerPos, blockAccessor);
                float gainHF = OcclusionCalculator.OcclusionToFilter(occlusion);

                // Get reverb + path resolution in single raycast pass
                var (reverb, pathResult) = AcousticRaytracer.CalculateWithPaths(
                    sourcePos, listenerPos, blockAccessor, occlusion);

                // Build result
                var result = new AcousticResult
                {
                    IsValid = true,
                    OcclusionGainHF = gainHF,
                    RawOcclusion = occlusion,

                    // Reverb send gains (4-slot SPR-style)
                    ReverbSendGain0 = reverb.SendGain0,
                    ReverbSendGain1 = reverb.SendGain1,
                    ReverbSendGain2 = reverb.SendGain2,
                    ReverbSendGain3 = reverb.SendGain3,
                    ReverbSendCutoff0 = reverb.SendCutoff0,
                    ReverbSendCutoff1 = reverb.SendCutoff1,
                    ReverbSendCutoff2 = reverb.SendCutoff2,
                    ReverbSendCutoff3 = reverb.SendCutoff3,
                };

                // Sound repositioning (Phase 4B)
                if (pathResult.HasValue)
                {
                    var path = pathResult.Value;
                    result.HasRepositioning = true;
                    result.ApparentPosition = path.ApparentPosition;
                    result.RepositionOffset = (float)path.RepositionOffset;
                    result.BlendedOcclusion = (float)path.BlendedOcclusion;
                    result.SharedAirspaceRatio = path.SharedAirspaceRatio;
                    result.OpenPathCount = path.PathCount;
                }

                return result;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[API] QueryAcoustics error: {ex.Message}");
                return AcousticResult.Unavailable;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Reverb Slot Access (For Direct OpenAL Aux Send Connection)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get our reverb auxiliary effect slot IDs so callers can connect
        /// their OpenAL sources directly to our reverb processing.
        ///
        /// Returns the 4 aux slot IDs (0 = not available for that slot).
        /// Caller uses these with:
        ///   AL.Source(sourceId, EFXSourceInteger3.AuxiliarySendFilter, slotId, sendIndex, filterId);
        ///
        /// This is OPTIONAL — callers can also just use the send gain values
        /// from QueryAcoustics() with their own reverb effects.
        /// But connecting to OUR slots means all sounds share the same room reverb,
        /// which sounds more consistent.
        /// </summary>
        /// <returns>Array of 4 aux slot IDs, or null if reverb unavailable</returns>
        public static int[] GetReverbAuxSlotIds()
        {
            if (!IsReverbAvailable) return null;

            return ReverbEffects.GetAuxSlotIds();
        }

        // ════════════════════════════════════════════════════════════════
        // Material Override API (For Content Mods)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get the MaterialSoundConfig instance for direct access.
        /// Prefer the typed methods below for common operations.
        /// Returns null if mod not yet initialized.
        /// </summary>
        public static MaterialSoundConfig GetMaterialConfig()
        {
            return SoundPhysicsAdaptedModSystem.MaterialConfig;
        }

        /// <summary>
        /// Register an occlusion override for a block pattern.
        /// Call during AssetsFinalize or later — config must be loaded.
        ///
        /// Example: SetOcclusionOverride("mymod:glasswall-*", 0.8f)
        /// </summary>
        /// <param name="blockPattern">Block code pattern with * wildcards (e.g. "mymod:myblock-*")</param>
        /// <param name="occlusionValue">Occlusion multiplier (0=transparent, 1=full block)</param>
        /// <returns>True if applied, false if config not ready</returns>
        public static bool SetOcclusionOverride(string blockPattern, float occlusionValue)
        {
            var config = SoundPhysicsAdaptedModSystem.MaterialConfig;
            if (config == null) return false;
            config.SetOcclusionOverride(blockPattern, occlusionValue);
            return true;
        }

        /// <summary>
        /// Register a material-level occlusion value.
        /// Affects all blocks of that material unless they have a block override.
        ///
        /// Example: SetMaterialOcclusion("myite", 0.7f)
        /// </summary>
        /// <param name="materialName">Material name (lowercase, e.g. "stone", "wood")</param>
        /// <param name="occlusionValue">Occlusion multiplier (0=transparent, 1=full)</param>
        /// <returns>True if applied, false if config not ready</returns>
        public static bool SetMaterialOcclusion(string materialName, float occlusionValue)
        {
            var config = SoundPhysicsAdaptedModSystem.MaterialConfig;
            if (config == null) return false;
            config.SetMaterialOcclusion(materialName, occlusionValue);
            return true;
        }

        /// <summary>
        /// Register a material-level reflectivity value for reverb.
        /// Higher = more reflective surface = more reverb energy returned.
        ///
        /// Example: SetMaterialReflectivity("myite", 1.2f)
        /// </summary>
        /// <param name="materialName">Material name (lowercase)</param>
        /// <param name="reflectivityValue">Reflectivity multiplier (e.g. stone=1.5, cloth=0.1)</param>
        /// <returns>True if applied, false if config not ready</returns>
        public static bool SetMaterialReflectivity(string materialName, float reflectivityValue)
        {
            var config = SoundPhysicsAdaptedModSystem.MaterialConfig;
            if (config == null) return false;
            config.SetMaterialReflectivity(materialName, reflectivityValue);
            return true;
        }

        /// <summary>
        /// Register a block pattern to be treated as a full cube for occlusion.
        /// Skips expensive AABB collision testing — treat as solid.
        ///
        /// Example: AddTreatAsFullCube("mymod:thickglass-*")
        /// </summary>
        /// <param name="blockPattern">Block code pattern with * wildcards</param>
        /// <returns>True if applied, false if config not ready</returns>
        public static bool AddTreatAsFullCube(string blockPattern)
        {
            var config = SoundPhysicsAdaptedModSystem.MaterialConfig;
            if (config == null) return false;
            config.AddTreatAsFullCube(blockPattern);
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Result Struct
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Complete acoustic calculation result for a sound source→listener path.
    /// Contains everything needed to apply realistic sound physics to an OpenAL source.
    /// </summary>
    public struct AcousticResult
    {
        /// <summary>Whether this result contains valid data. Always check this first.</summary>
        public bool IsValid;

        // ── Occlusion ──

        /// <summary>
        /// Lowpass filter gainHF value (0.001 - 1.0).
        /// Apply directly to OpenAL EFX lowpass: EFX.Filter(filterId, FilterFloat.LowpassGainHF, this value)
        /// 1.0 = clear path, no filtering. Lower = more muffled.
        /// </summary>
        public float OcclusionGainHF;

        /// <summary>
        /// Raw accumulated occlusion (material thickness sum). For custom curves.
        /// Higher = more material between source and listener.
        /// </summary>
        public float RawOcclusion;

        // ── Reverb (4-slot SPR-style) ──

        /// <summary>Reverb send gain for slot 0 (short, 0.15s decay). Range 0-1.</summary>
        public float ReverbSendGain0;
        /// <summary>Reverb send gain for slot 1 (medium-short, 0.55s decay). Range 0-1.</summary>
        public float ReverbSendGain1;
        /// <summary>Reverb send gain for slot 2 (medium-long, 1.68s decay). Range 0-1.</summary>
        public float ReverbSendGain2;
        /// <summary>Reverb send gain for slot 3 (long, 4.14s decay). Range 0-1.</summary>
        public float ReverbSendGain3;

        /// <summary>HF cutoff gain for reverb slot 0 send filter. Range 0-1.</summary>
        public float ReverbSendCutoff0;
        /// <summary>HF cutoff gain for reverb slot 1 send filter. Range 0-1.</summary>
        public float ReverbSendCutoff1;
        /// <summary>HF cutoff gain for reverb slot 2 send filter. Range 0-1.</summary>
        public float ReverbSendCutoff2;
        /// <summary>HF cutoff gain for reverb slot 3 send filter. Range 0-1.</summary>
        public float ReverbSendCutoff3;

        // ── Sound Repositioning (Phase 4B) ──

        /// <summary>Whether repositioning data is available.</summary>
        public bool HasRepositioning;

        /// <summary>
        /// Repositioned apparent sound position (world coordinates).
        /// When a sound is behind a wall but near an opening, this position shifts
        /// toward the opening — simulating sound traveling around corners.
        ///
        /// For voice: set your OpenAL source position to this instead of the speaker's
        /// actual position. The voice will appear to come from the doorway/window.
        ///
        /// null if repositioning unavailable or direct LOS is clear.
        /// When direct LOS is clear, ApparentPosition ≈ sourcePos (negligible offset).
        /// </summary>
        public Vec3d ApparentPosition;

        /// <summary>
        /// Distance between actual source position and apparent position (meters).
        /// Small (< 1m) = sound barely moved (direct path dominates).
        /// Large (> 3m) = sound shifted significantly toward opening.
        /// Useful for deciding whether to apply repositioning (threshold at ~0.5m).
        /// </summary>
        public float RepositionOffset;

        /// <summary>
        /// Blended occlusion across all paths (direct + indirect).
        /// This is the path-resolved version of RawOcclusion — accounts for
        /// sound traveling through openings. Lower than RawOcclusion when
        /// open paths exist. Good for a more nuanced LPF value.
        /// Convert to gainHF: exp(-blendedOcclusion * absorptionCoeff)
        /// </summary>
        public float BlendedOcclusion;

        /// <summary>
        /// Ratio of rays that found shared airspace (0-1).
        /// High = many clear paths (open area). Low = enclosed/occluded.
        /// </summary>
        public float SharedAirspaceRatio;

        /// <summary>
        /// Number of open (low-occlusion) paths found by the raytracer.
        /// 0 = fully blocked. Higher = more openings around the source.
        /// </summary>
        public int OpenPathCount;

        /// <summary>Default result when API is unavailable (passthrough values).</summary>
        public static AcousticResult Unavailable => new AcousticResult
        {
            IsValid = false,
            OcclusionGainHF = 1f,
            RawOcclusion = 0f,
            ReverbSendGain0 = 0f,
            ReverbSendGain1 = 0f,
            ReverbSendGain2 = 0f,
            ReverbSendGain3 = 0f,
            ReverbSendCutoff0 = 1f,
            ReverbSendCutoff1 = 1f,
            ReverbSendCutoff2 = 1f,
            ReverbSendCutoff3 = 1f,
            HasRepositioning = false,
            ApparentPosition = null,
            RepositionOffset = 0f,
            BlendedOcclusion = 0f,
            SharedAirspaceRatio = 0f,
            OpenPathCount = 0,
        };
    }
}
