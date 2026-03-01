using ConfigLib;
using System;
using System.Linq;
using Vintagestory.API.Common;

namespace soundphysicsadapted
{
    /// <summary>
    /// Optional ConfigLib integration bridge.
    /// ALL ConfigLib type references are isolated in this class.
    /// Only ever instantiated when ConfigLib is confirmed present via IsModEnabled("configlib").
    /// This prevents TypeLoadException when ConfigLib is absent.
    /// </summary>
    internal class ConfigLibBridge
    {
        internal ConfigLibBridge(ICoreAPI api, SoundPhysicsConfig cfg)
        {
            // IModLoader.GetModSystem<T> has a where T : ModSystem constraint, so we can't
            // pass IConfigProvider (an interface) directly at compile time.
            // Resolve ConfigLib's concrete type at runtime and invoke via reflection.
            IConfigProvider provider = GetConfigProvider(api);
            if (provider == null)
            {
                api.Logger.Warning("[SoundPhysicsAdapted] ConfigLib detected but IConfigProvider not found. Settings GUI unavailable.");
                return;
            }

            api.Logger.Notification("[SoundPhysicsAdapted] ConfigLib integration active.");

            // Push all settings from ConfigLib's YAML into our config object when loaded
            provider.ConfigsLoaded += () =>
            {
                var config = provider.GetConfig(SoundPhysicsAdaptedModSystem.MOD_ID);
                if (config != null)
                {
                    config.AssignSettingsValues(cfg);
                    api.Logger.Notification("[SoundPhysicsAdapted] ConfigLib settings loaded into config.");
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] ConfigLib: no config found for domain 'soundphysicsadapted'.");
                }
            };

            // Sync individual setting changes in real-time as user adjusts in GUI
            provider.SettingChanged += (domain, config, setting) =>
            {
                if (domain != SoundPhysicsAdaptedModSystem.MOD_ID) return;
                setting.AssignSettingValue(cfg);
            };
        }

        private static IConfigProvider GetConfigProvider(ICoreAPI api)
        {
            // Find ConfigLib's concrete mod system type across all loaded assemblies
            Type configLibType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { configLibType = asm.GetType("ConfigLib.ConfigLibModSystem"); } catch { }
                if (configLibType != null) break;
            }
            if (configLibType == null) return null;

            // Invoke GetModSystem<ConfigLibModSystem>() via reflection to bypass the
            // where T : ModSystem compile-time constraint
            var method = typeof(IModLoader)
                .GetMethods()
                .FirstOrDefault(m => m.Name == "GetModSystem" && m.IsGenericMethod)
                ?.MakeGenericMethod(configLibType);
            if (method == null) return null;

            var args = method.GetParameters().Length == 1 ? new object[] { false } : Array.Empty<object>();
            return method.Invoke(api.ModLoader, args) as IConfigProvider;
        }
    }
}
