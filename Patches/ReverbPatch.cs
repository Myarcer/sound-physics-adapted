using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;

namespace soundphysicsadapted.Patches
{
    /// <summary>
    /// Harmony patches to disable vanilla reverb system.
    ///
    /// VS applies reverb via SetReverb(float) on LoadedSoundNative.
    /// We intercept this with a prefix that returns false to skip the original method.
    ///
    /// This allows our custom SPR-style reverb system to take full control.
    /// </summary>
    public static class ReverbPatch
    {
        private static bool _vanillaReverbDisabled = false;
        private static ICoreClientAPI _api;

        /// <summary>
        /// Whether vanilla reverb is currently disabled.
        /// </summary>
        public static bool VanillaReverbDisabled => _vanillaReverbDisabled;

        /// <summary>
        /// Apply patches to disable vanilla reverb.
        /// </summary>
        public static void ApplyPatches(Harmony harmony, ICoreClientAPI api)
        {
            _api = api;

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableCustomReverb || !config.DisableVanillaReverb)
            {
                api.Logger.Debug("[SoundPhysicsAdapted] Vanilla reverb NOT disabled (config)");
                return;
            }

            try
            {
                // Find VintagestoryLib assembly using reflection
                Type loadedSoundNativeType = null;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = assembly.GetName().Name;
                    if (name == "VintagestoryLib" || name.Contains("VintagestoryLib") || name == "Vintagestory")
                    {
                        loadedSoundNativeType = assembly.GetType("Vintagestory.Client.NoObf.LoadedSoundNative")
                                              ?? assembly.GetType("Vintagestory.Client.LoadedSoundNative");

                        if (loadedSoundNativeType != null)
                        {
                            api.Logger.Debug($"[SoundPhysicsAdapted] Found LoadedSoundNative in: {name}");
                            break;
                        }
                    }
                }

                if (loadedSoundNativeType == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find LoadedSoundNative type for reverb patch");
                    return;
                }

                // Find SetReverb method
                MethodInfo setReverbMethod = loadedSoundNativeType.GetMethod("SetReverb",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(float) },
                    null);

                if (setReverbMethod == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find SetReverb method");
                    return;
                }

                // Apply prefix patch to skip vanilla reverb
                MethodInfo prefixMethod = typeof(ReverbPatch).GetMethod(nameof(SetReverbPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(setReverbMethod, prefix: new HarmonyMethod(prefixMethod));

                _vanillaReverbDisabled = true;
                api.Logger.Notification("[SoundPhysicsAdapted] Vanilla reverb DISABLED - our system takes control");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to patch SetReverb: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix patch for SetReverb - returns false to skip original method entirely.
        /// This disables vanilla reverb so our system can take full control.
        /// </summary>
        /// <param name="reverbDecayTime">Original reverb value (ignored)</param>
        /// <returns>False to skip original method, True to run it</returns>
        private static bool SetReverbPrefix(float reverbDecayTime)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;

            // If our reverb system is disabled, let vanilla work
            if (config == null || !config.EnableCustomReverb || !config.DisableVanillaReverb)
                return true; // Run original

            // Skip vanilla reverb - our system handles it
            if (config.DebugMode && config.DebugReverb)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"VANILLA REVERB BLOCKED: value={reverbDecayTime:F2}");
            }

            return false; // Skip original method
        }

        /// <summary>
        /// Re-enable vanilla reverb (for comparison/debugging).
        /// </summary>
        public static void EnableVanillaReverb()
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config != null)
            {
                config.DisableVanillaReverb = false;
                _api?.Logger.Notification("[SoundPhysicsAdapted] Vanilla reverb RE-ENABLED");
            }
        }

        /// <summary>
        /// Disable vanilla reverb again.
        /// </summary>
        public static void DisableVanillaReverb()
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config != null)
            {
                config.DisableVanillaReverb = true;
                _api?.Logger.Notification("[SoundPhysicsAdapted] Vanilla reverb DISABLED");
            }
        }
    }
}
