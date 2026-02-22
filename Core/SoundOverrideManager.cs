using Vintagestory.API.Common;
using Vintagestory.API.Client;
using System.Collections.Generic;

namespace soundphysicsadapted.Core
{
    /// <summary>
    /// Manages optional sound file overrides.
    /// When enabled via config, registers replacement .ogg files to supersede vanilla sounds.
    /// 
    /// VS automatically loads mod assets that match vanilla domain paths.
    /// We place sounds in resources/assets/survival/sounds/ to override survival domain.
    /// Config controls whether this feature is active (logged at startup).
    /// </summary>
    public static class SoundOverrideManager
    {
        private static bool initialized = false;
        private static List<string> activeOverrides = new List<string>();

        /// <summary>
        /// Check sound overrides based on config and log status.
        /// Called during mod Start().
        /// 
        /// Note: VS loads assets at mod init time. The config toggle here
        /// is informational - to truly disable, remove the sound files or
        /// use a Harmony patch on audio loading (future enhancement).
        /// </summary>
        public static void Initialize(ICoreAPI api, SoundPhysicsConfig config)
        {
            if (initialized) return;

            activeOverrides.Clear();

            if (!config.EnableSoundOverrides)
            {
                api.Logger.Notification("[SoundPhysicsAdapted] Sound overrides: DISABLED (set EnableSoundOverrides=true to enable)");
                initialized = true;
                return;
            }

            // Check individual overrides
            if (config.OverrideBeehiveSound)
            {
                activeOverrides.Add("survival:sounds/creature/beehive-wild");
            }

            if (activeOverrides.Count > 0)
            {
                api.Logger.Notification($"[SoundPhysicsAdapted] Sound overrides: ENABLED ({activeOverrides.Count} sounds)");
                foreach (var path in activeOverrides)
                {
                    api.Logger.Debug($"[SoundPhysicsAdapted]   Override active: {path}");
                }
            }
            else
            {
                api.Logger.Notification("[SoundPhysicsAdapted] Sound overrides: Enabled but no individual overrides active");
            }

            initialized = true;
        }

        /// <summary>
        /// Check if a sound path has an active override.
        /// Can be used by future Harmony patches to conditionally intercept.
        /// </summary>
        public static bool IsOverrideActive(string assetPath)
        {
            return activeOverrides.Contains(assetPath);
        }

        /// <summary>
        /// Get list of active override paths (for debugging).
        /// </summary>
        public static IReadOnlyList<string> GetActiveOverrides() => activeOverrides.AsReadOnly();

        /// <summary>
        /// Reset state (for mod dispose/reload).
        /// </summary>
        public static void Dispose()
        {
            activeOverrides.Clear();
            initialized = false;
        }
    }
}
