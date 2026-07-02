using System;

namespace soundphysicsadapted
{
    /// <summary>
    /// Manages OpenAL EFX reverb effects for SPR-style multi-slot reverb.
    ///
    /// Creates 4 auxiliary effect slots with different decay times (SPR-matched):
    /// - Slot 0: Short decay (0.15s) - bright early reflections, reflGain=2.5
    /// - Slot 1: Medium-short (0.55s) - low reflections (0.2), density=0
    /// - Slot 2: Medium-long (1.68s) - no reflections, density=0.1, longer delays
    /// - Slot 3: Long decay (4.14s) - no reflections, density=0.5, dark HF (0.89)
    ///
    /// Each sound source connects to all 4 slots with different send gains
    /// based on the calculated reflection delays.
    /// </summary>
    public static class ReverbEffects
    {
        // Auxiliary effect slots (where reverb effects live)
        private static int _auxSlot0;
        private static int _auxSlot1;
        private static int _auxSlot2;
        private static int _auxSlot3;

        // Reverb effect objects
        private static int _reverb0;
        private static int _reverb1;
        private static int _reverb2;
        private static int _reverb3;

        // Send filters (for per-source gain/cutoff control)
        private static int _sendFilter0;
        private static int _sendFilter1;
        private static int _sendFilter2;
        private static int _sendFilter3;

        private static bool _initialized = false;
        private static int _maxAuxSends = 0;

        /// <summary>
        /// Whether the reverb system is initialized.
        /// </summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// Maximum auxiliary sends supported by the device.
        /// </summary>
        public static int MaxAuxSends => _maxAuxSends;

        /// <summary>
        /// Initialize the reverb effect system.
        /// Call once during mod startup after OpenAL context is ready.
        /// </summary>
        public static bool Initialize()
        {
            if (_initialized) return true;

            try
            {
                // Check EFX extension
                if (!EfxHelper.IsEfxSupported())
                {
                    SoundPhysicsAdaptedModSystem.Log("EFX not supported - reverb disabled");
                    return false;
                }

                // Get max auxiliary sends
                _maxAuxSends = EfxHelper.GetMaxAuxiliarySends();
                SoundPhysicsAdaptedModSystem.Log($"Max auxiliary sends: {_maxAuxSends}");

                if (_maxAuxSends < 1)
                {
                    SoundPhysicsAdaptedModSystem.Log("No auxiliary sends available - reverb disabled");
                    return false;
                }

                // Create auxiliary effect slots (only as many as the device supports)
                _auxSlot0 = CreateAuxSlot();
                if (_maxAuxSends >= 2) _auxSlot1 = CreateAuxSlot();
                if (_maxAuxSends >= 3) _auxSlot2 = CreateAuxSlot();
                if (_maxAuxSends >= 4) _auxSlot3 = CreateAuxSlot();

                // Verify at least one slot was created
                int slotsCreated = (_auxSlot0 > 0 ? 1 : 0) + (_auxSlot1 > 0 ? 1 : 0) +
                                   (_auxSlot2 > 0 ? 1 : 0) + (_auxSlot3 > 0 ? 1 : 0);

                SoundPhysicsAdaptedModSystem.Log($"Aux slots created: {slotsCreated} (IDs: {_auxSlot0}, {_auxSlot1}, {_auxSlot2}, {_auxSlot3})");

                if (slotsCreated == 0)
                {
                    SoundPhysicsAdaptedModSystem.Log("REVERB INIT FAILED: No aux slots could be created!");
                    return false;
                }

                // Create reverb effects and attach to slots.
                // With only 2 sends: use reverb 0 (short/bright) + reverb 2 (medium-long/diffuse)
                // to get the best perceptual spread of early reflections + tail.
                if (_maxAuxSends >= 4)
                {
                    // Full 4-slot mode (Windows / high-end OpenAL)
                    _reverb0 = CreateReverbSlot0(); // Short, bright early reflections
                    _reverb1 = CreateReverbSlot1(); // Medium-short
                    _reverb2 = CreateReverbSlot2(); // Medium-long, diffuse
                    _reverb3 = CreateReverbSlot3(); // Long tail, dark
                    AttachEffectToSlot(_auxSlot0, _reverb0);
                    AttachEffectToSlot(_auxSlot1, _reverb1);
                    AttachEffectToSlot(_auxSlot2, _reverb2);
                    AttachEffectToSlot(_auxSlot3, _reverb3);
                }
                else if (_maxAuxSends >= 2)
                {
                    // 2-slot mode (Linux OpenAL Soft default)
                    // Send 0 = short/bright (slot 0 preset), Send 1 = medium-long/diffuse (slot 2 preset)
                    _reverb0 = CreateReverbSlot0();
                    _reverb2 = CreateReverbSlot2();
                    AttachEffectToSlot(_auxSlot0, _reverb0);
                    AttachEffectToSlot(_auxSlot1, _reverb2);
                    SoundPhysicsAdaptedModSystem.Log("Reverb: 2-send mode (short + medium-long)");
                }
                else
                {
                    // 1-slot mode (minimal)
                    _reverb0 = CreateReverbSlot0();
                    AttachEffectToSlot(_auxSlot0, _reverb0);
                    SoundPhysicsAdaptedModSystem.Log("Reverb: 1-send mode (short only)");
                }

                // Create send filters (only for sends we actually use)
                _sendFilter0 = EfxHelper.CreateLowpassFilter();
                if (_maxAuxSends >= 2) _sendFilter1 = EfxHelper.CreateLowpassFilter();
                if (_maxAuxSends >= 3) _sendFilter2 = EfxHelper.CreateLowpassFilter();
                if (_maxAuxSends >= 4) _sendFilter3 = EfxHelper.CreateLowpassFilter();

                int effectsCreated = (_reverb0 > 0 ? 1 : 0) + (_reverb1 > 0 ? 1 : 0) +
                                     (_reverb2 > 0 ? 1 : 0) + (_reverb3 > 0 ? 1 : 0);

                _initialized = true;
                SoundPhysicsAdaptedModSystem.Log($"Reverb system READY: {_maxAuxSends} sends, {effectsCreated} effects");
                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"REVERB INIT EXCEPTION: {ex.Message}");
                SoundPhysicsAdaptedModSystem.Log($"Stack: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Apply reverb to a sound source with calculated send gains.
        /// SPR-style: Use send filters with AL_LOWPASS_GAIN to control reverb level.
        /// </summary>
        /// <param name="sourceId">OpenAL source ID</param>
        /// <param name="result">Calculated reverb parameters</param>
        /// <param name="isSourceUnderwater">Whether the sound source position is in liquid</param>
        public static void ApplyToSource(int sourceId, ReverbResult result, bool isSourceUnderwater = false)
        {
            if (!_initialized || sourceId <= 0) return;

            var config = SoundPhysicsAdaptedModSystem.Config;
            float masterGain = config?.ReverbGain ?? 1.0f;

            // Apply submersion reverb reduction when player OR sound source is submerged
            bool playerSubmerged = SoundPhysicsAdaptedModSystem.IsPlayerSubmerged;
            if (playerSubmerged || isSourceUnderwater)
            {
                float submersionMult = SoundPhysicsAdaptedModSystem.GetSubmersionReverbMultiplier();
                // If only source is underwater (not player), use water defaults
                if (!playerSubmerged && isSourceUnderwater)
                    submersionMult = config?.UnderwaterReverbMultiplier ?? 0.3f;
                masterGain *= submersionMult;

                if (config?.DebugReverb == true)
                {
                    string reason = playerSubmerged && isSourceUnderwater ? "BOTH" :
                                    playerSubmerged ? "PLAYER" : "SOURCE";
                    SoundPhysicsAdaptedModSystem.ReverbDebugLog($"SUBMERSION ({reason}): reverb multiplier={submersionMult:F2}");
                }
            }

            try
            {
                if (_maxAuxSends >= 4)
                {
                    // Full 4-send mode: each slot gets its own send
                    EfxHelper.SetFilterGains(_sendFilter0, result.SendGain0 * masterGain, result.SendCutoff0);
                    EfxHelper.ConnectSourceToAuxSlot(sourceId, _auxSlot0, 0, _sendFilter0);

                    EfxHelper.SetFilterGains(_sendFilter1, result.SendGain1 * masterGain, result.SendCutoff1);
                    EfxHelper.ConnectSourceToAuxSlot(sourceId, _auxSlot1, 1, _sendFilter1);

                    EfxHelper.SetFilterGains(_sendFilter2, result.SendGain2 * masterGain, result.SendCutoff2);
                    EfxHelper.ConnectSourceToAuxSlot(sourceId, _auxSlot2, 2, _sendFilter2);

                    EfxHelper.SetFilterGains(_sendFilter3, result.SendGain3 * masterGain, result.SendCutoff3);
                    EfxHelper.ConnectSourceToAuxSlot(sourceId, _auxSlot3, 3, _sendFilter3);
                }
                else if (_maxAuxSends >= 2)
                {
                    // 2-send mode: send 0 = short (gain0+gain1 combined), send 1 = long (gain2+gain3 combined)
                    float combinedGainShort = Math.Max(result.SendGain0, result.SendGain1);
                    float combinedCutoffShort = Math.Min(result.SendCutoff0, result.SendCutoff1);
                    EfxHelper.SetFilterGains(_sendFilter0, combinedGainShort * masterGain, combinedCutoffShort);
                    EfxHelper.ConnectSourceToAuxSlot(sourceId, _auxSlot0, 0, _sendFilter0);

                    float combinedGainLong = Math.Max(result.SendGain2, result.SendGain3);
                    float combinedCutoffLong = Math.Min(result.SendCutoff2, result.SendCutoff3);
                    EfxHelper.SetFilterGains(_sendFilter1, combinedGainLong * masterGain, combinedCutoffLong);
                    EfxHelper.ConnectSourceToAuxSlot(sourceId, _auxSlot1, 1, _sendFilter1);
                }
                else
                {
                    // 1-send mode: just short reverb
                    EfxHelper.SetFilterGains(_sendFilter0, result.SendGain0 * masterGain, result.SendCutoff0);
                    EfxHelper.ConnectSourceToAuxSlot(sourceId, _auxSlot0, 0, _sendFilter0);
                }

                if (config?.DebugReverb == true)
                {
                    SoundPhysicsAdaptedModSystem.ReverbDebugLog(
                        $"REVERB APPLIED src={sourceId}: g0={result.SendGain0 * masterGain:F3} g1={result.SendGain1 * masterGain:F3} " +
                        $"g2={result.SendGain2 * masterGain:F3} g3={result.SendGain3 * masterGain:F3} (master={masterGain:F2})");
                }
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"Failed to apply reverb to source {sourceId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the auxiliary effect slot IDs for external mod use (API).
        /// Returns array of 4 slot IDs (0 = slot not created).
        /// </summary>
        public static int[] GetAuxSlotIds()
        {
            return new[] { _auxSlot0, _auxSlot1, _auxSlot2, _auxSlot3 };
        }

        /// <summary>
        /// Dispose all reverb resources.
        /// </summary>
        public static void Dispose()
        {
            if (!_initialized) return;

            try
            {
                // Delete filters
                if (_sendFilter0 > 0) EfxHelper.DeleteFilter(_sendFilter0);
                if (_sendFilter1 > 0) EfxHelper.DeleteFilter(_sendFilter1);
                if (_sendFilter2 > 0) EfxHelper.DeleteFilter(_sendFilter2);
                if (_sendFilter3 > 0) EfxHelper.DeleteFilter(_sendFilter3);

                // Delete effects
                if (_reverb0 > 0) EfxHelper.DeleteEffect(_reverb0);
                if (_reverb1 > 0) EfxHelper.DeleteEffect(_reverb1);
                if (_reverb2 > 0) EfxHelper.DeleteEffect(_reverb2);
                if (_reverb3 > 0) EfxHelper.DeleteEffect(_reverb3);

                // Delete aux slots
                if (_auxSlot0 > 0) EfxHelper.DeleteAuxSlot(_auxSlot0);
                if (_auxSlot1 > 0) EfxHelper.DeleteAuxSlot(_auxSlot1);
                if (_auxSlot2 > 0) EfxHelper.DeleteAuxSlot(_auxSlot2);
                if (_auxSlot3 > 0) EfxHelper.DeleteAuxSlot(_auxSlot3);

                _initialized = false;
                SoundPhysicsAdaptedModSystem.Log("Reverb effects disposed");
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"Error disposing reverb effects: {ex.Message}");
            }
        }

        #region Private Helpers

        private static int CreateAuxSlot()
        {
            int slot = EfxHelper.CreateAuxiliaryEffectSlot();
            if (slot > 0)
            {
                // Enable automatic send adjustments
                EfxHelper.SetAuxSlotAutoSend(slot, true);

                // CRITICAL: Set slot gain to 1.0 (full volume)
                // If this defaults to 0, no reverb will be heard!
                EfxHelper.SetAuxSlotGain(slot, 1.0f);
            }
            return slot;
        }

        /// <summary>
        /// Slot 0: Short decay, bright early reflections.
        /// SPR: decayTime=0.15, density=0, gain=0.2*0.7*0.85=0.119, reflectionsGain=2.5
        /// </summary>
        private static int CreateReverbSlot0()
        {
            int effect = EfxHelper.CreateReverbEffect();
            if (effect > 0)
            {
                SoundPhysicsAdaptedModSystem.Log($"[ReverbEffects] Configuring slot 0 (short/bright): effect={effect}");
                EfxHelper.SetReverbDecayTime(effect, 0.15f);
                EfxHelper.SetReverbDecayHFRatio(effect, 0.6f);
                EfxHelper.SetReverbDensity(effect, 0.0f);
                EfxHelper.SetReverbDiffusion(effect, 1.0f);
                EfxHelper.SetReverbGain(effect, 0.12f);
                EfxHelper.SetReverbGainHF(effect, 0.89f);
                EfxHelper.SetReverbReflectionsGain(effect, 0.8f);
                EfxHelper.SetReverbReflectionsDelay(effect, 0.005f);
                EfxHelper.SetReverbLateReverbGain(effect, 1.26f);
                EfxHelper.SetReverbLateReverbDelay(effect, 0.011f);
                EfxHelper.SetReverbAirAbsorptionGainHF(effect, 0.994f);
                EfxHelper.SetReverbRoomRolloffFactor(effect, 0.16f);
            }
            return effect;
        }

        /// <summary>
        /// Slot 1: Medium-short decay.
        /// SPR: decayTime=0.55, density=0, gain=0.3*0.7*0.85=0.178, reflectionsGain=0.2
        /// </summary>
        private static int CreateReverbSlot1()
        {
            int effect = EfxHelper.CreateReverbEffect();
            if (effect > 0)
            {
                SoundPhysicsAdaptedModSystem.Log($"[ReverbEffects] Configuring slot 1 (medium-short): effect={effect}");
                EfxHelper.SetReverbDecayTime(effect, 0.55f);
                EfxHelper.SetReverbDecayHFRatio(effect, 0.7f);
                EfxHelper.SetReverbDensity(effect, 0.0f);
                EfxHelper.SetReverbDiffusion(effect, 1.0f);
                EfxHelper.SetReverbGain(effect, 0.18f);
                EfxHelper.SetReverbGainHF(effect, 0.99f);
                EfxHelper.SetReverbReflectionsGain(effect, 0.2f);
                EfxHelper.SetReverbReflectionsDelay(effect, 0.015f);
                EfxHelper.SetReverbLateReverbGain(effect, 1.26f);
                EfxHelper.SetReverbLateReverbDelay(effect, 0.011f);
                EfxHelper.SetReverbAirAbsorptionGainHF(effect, 0.994f);
                EfxHelper.SetReverbRoomRolloffFactor(effect, 0.15f);
            }
            return effect;
        }

        /// <summary>
        /// Slot 2: Medium-long decay, increasing density for diffuse tail.
        /// SPR: decayTime=1.68, density=0.1, gain=0.5*0.7*0.85=0.297, reflectionsGain=0
        /// </summary>
        private static int CreateReverbSlot2()
        {
            int effect = EfxHelper.CreateReverbEffect();
            if (effect > 0)
            {
                SoundPhysicsAdaptedModSystem.Log($"[ReverbEffects] Configuring slot 2 (medium-long): effect={effect}");
                EfxHelper.SetReverbDecayTime(effect, 1.68f);
                EfxHelper.SetReverbDecayHFRatio(effect, 0.7f);
                EfxHelper.SetReverbDensity(effect, 0.1f);
                EfxHelper.SetReverbDiffusion(effect, 1.0f);
                EfxHelper.SetReverbGain(effect, 0.30f);
                EfxHelper.SetReverbGainHF(effect, 0.99f);
                EfxHelper.SetReverbReflectionsGain(effect, 0.0f);
                EfxHelper.SetReverbReflectionsDelay(effect, 0.021f);
                EfxHelper.SetReverbLateReverbGain(effect, 1.26f);
                EfxHelper.SetReverbLateReverbDelay(effect, 0.021f);
                EfxHelper.SetReverbAirAbsorptionGainHF(effect, 0.994f);
                EfxHelper.SetReverbRoomRolloffFactor(effect, 0.13f);
            }
            return effect;
        }

        /// <summary>
        /// Slot 3: Long decay tail, high density for smooth diffusion, darker HF.
        /// SPR: decayTime=4.142, density=0.5, gain=0.4*0.7*0.85=0.238, gainHF=0.89
        /// </summary>
        private static int CreateReverbSlot3()
        {
            int effect = EfxHelper.CreateReverbEffect();
            if (effect > 0)
            {
                SoundPhysicsAdaptedModSystem.Log($"[ReverbEffects] Configuring slot 3 (long/dark): effect={effect}");
                EfxHelper.SetReverbDecayTime(effect, 4.142f);
                EfxHelper.SetReverbDecayHFRatio(effect, 0.7f);
                EfxHelper.SetReverbDensity(effect, 0.5f);
                EfxHelper.SetReverbDiffusion(effect, 1.0f);
                EfxHelper.SetReverbGain(effect, 0.24f);
                EfxHelper.SetReverbGainHF(effect, 0.89f);
                EfxHelper.SetReverbReflectionsGain(effect, 0.0f);
                EfxHelper.SetReverbReflectionsDelay(effect, 0.025f);
                EfxHelper.SetReverbLateReverbGain(effect, 1.26f);
                EfxHelper.SetReverbLateReverbDelay(effect, 0.021f);
                EfxHelper.SetReverbAirAbsorptionGainHF(effect, 0.994f);
                EfxHelper.SetReverbRoomRolloffFactor(effect, 0.11f);
            }
            return effect;
        }

        private static void AttachEffectToSlot(int slot, int effect)
        {
            if (slot > 0 && effect > 0)
            {
                EfxHelper.AttachEffectToAuxSlot(slot, effect);
            }
        }

        #endregion
    }

    /// <summary>
    /// Result of reverb calculation - send gains for each reverb slot.
    /// </summary>
    public struct ReverbResult
    {
        public float SendGain0;     // Short reverb gain
        public float SendGain1;     // Medium-short gain
        public float SendGain2;     // Medium-long gain
        public float SendGain3;     // Long reverb gain

        public float SendCutoff0;   // HF cutoff for slot 0
        public float SendCutoff1;
        public float SendCutoff2;
        public float SendCutoff3;

        public ReverbResult(float g0, float g1, float g2, float g3,
                           float c0 = 1f, float c1 = 1f, float c2 = 1f, float c3 = 1f)
        {
            SendGain0 = g0;
            SendGain1 = g1;
            SendGain2 = g2;
            SendGain3 = g3;
            SendCutoff0 = c0;
            SendCutoff1 = c1;
            SendCutoff2 = c2;
            SendCutoff3 = c3;
        }

        /// <summary>
        /// Default result with no reverb.
        /// </summary>
        public static ReverbResult None => new ReverbResult(0, 0, 0, 0);
    }
}
