using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted.Patches
{
    /// <summary>
    /// Harmony patches to suppress vanilla VS weather sounds (6 global loops).
    /// Our WeatherAudioManager plays replacement sounds with EFX lowpass filtering.
    ///
    /// CRITICAL: Only targets WeatherSimulationSound's 6 global loops.
    /// Block-level ambient sounds (glass rainwindow.ogg) are NOT affected.
    /// Those are managed by SystemPlayerSounds/AmbientSound, not this class.
    ///
    /// Approach: Postfix on updateSounds — VS calculates all its values normally,
    /// we just zero the volume on its ILoadedSound references. Our replacement
    /// handler reads VS's computed state (intensity, leafy/leafless, wind speed)
    /// via reflection and roomVolumePitchLoss via its public static field.
    /// </summary>
    public static class WeatherSoundPatches
    {
        private static bool _patchApplied = false;
        private static ICoreClientAPI _api;

        // Cached reflection for reading VS weather state
        private static Type _weatherSimSoundType;
        // Rain sounds are ILoadedSound[] arrays in VS (not single ILoadedSound)
        private static FieldInfo _rainSoundsLeafyField;
        private static FieldInfo _rainSoundsLeaflessField;
        private static FieldInfo _windSoundLeafyField;
        private static FieldInfo _windSoundLeaflessField;
        private static FieldInfo _lowTrembleSoundField;
        private static FieldInfo _hailSoundField;

        // Fields for reading weather intensity values from WeatherSimulationSound
        // VS stores per-channel volumes, not raw intensities
        private static FieldInfo _curRainVolumeLeafyField;
        private static FieldInfo _curRainVolumeLeaflessField;
        private static FieldInfo _curWindVolumeLeafyField;
        private static FieldInfo _curWindVolumeLeaflessField;
        private static FieldInfo _curHailVolumeField;
        // For accessing weatherSys.BlendedWeatherData.curWindSpeed
        private static FieldInfo _weatherSysField;
        // Direct leaviness value (0-1) — lives on WeatherSimulationSound
        private static FieldInfo _nearbyLeavinessField;

        // The WeatherSimulationSound instance (captured from postfix)
        private static object _weatherSimInstance;

        // Latched leafy state — holds last reliable reading when volumes are too low
        private static bool _latchedLeafy = false;
        private static bool _hasLatchedLeafy = false;
        // Latched leaviness float (0-1) for crossfade blending
        private static float _latchedLeaviness = 0f;

        /// <summary>Whether VS weather sounds are currently being suppressed.</summary>
        public static bool IsActive => _patchApplied && SoundPhysicsAdaptedModSystem.Config?.EnableWeatherEnhancement == true;

        // Phase 5C: Thunder handler reference for routing lightning events
        private static ThunderAudioHandler _thunderHandler;
        private static WeatherAudioManager _weatherManager;

        // Cached reflection for WeatherSimulationLightning
        private static Type _weatherSimLightningType;
        private static FieldInfo _lightningWeatherSysField;
        private static FieldInfo _lightningCapiField;
        private static FieldInfo _nearLightningCoolDownField;
        private static bool _lightningPatchApplied = false;

        /// <summary>
        /// Set the thunder handler reference so patches can route events to it.
        /// Called from WeatherAudioManager after initialization.
        /// </summary>
        public static void SetThunderHandler(ThunderAudioHandler handler, WeatherAudioManager manager)
        {
            _thunderHandler = handler;
            _weatherManager = manager;
        }

        /// <summary>
        /// Apply manual Harmony patches. Follows ReverbPatch pattern.
        /// </summary>
        public static void ApplyPatches(Harmony harmony, ICoreClientAPI api)
        {
            _api = api;

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableWeatherEnhancement)
            {
                api.Logger.Debug("[SoundPhysicsAdapted] Weather patches NOT applied (config disabled)");
                return;
            }

            try
            {
                // Find WeatherSimulationSound in VintagestoryLib via reflection
                _weatherSimSoundType = FindType("Vintagestory.GameContent.WeatherSimulationSound");

                if (_weatherSimSoundType == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] WeatherSimulationSound type not found — weather enhancement disabled");
                    return;
                }

                api.Logger.Debug($"[SoundPhysicsAdapted] Found WeatherSimulationSound: {_weatherSimSoundType.FullName}");

                // Cache sound field references (private ILoadedSound fields)
                CacheSoundFields(api);

                // Cache weather intensity fields
                CacheWeatherStateFields(api);

                // Find updateSounds method
                MethodInfo updateSoundsMethod = _weatherSimSoundType.GetMethod("updateSounds",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                if (updateSoundsMethod == null)
                {
                    // Try alternative names
                    updateSoundsMethod = _weatherSimSoundType.GetMethod("UpdateSounds",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                }

                if (updateSoundsMethod == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] updateSounds method not found on WeatherSimulationSound");
                    // List available methods for debugging
                    foreach (var m in _weatherSimSoundType.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.DeclaringType == _weatherSimSoundType)
                            api.Logger.Debug($"  Method: {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))})");
                    }
                    return;
                }

                // Apply prefix + postfix — prefix zeroes volumes BEFORE VS processes them,
                // postfix zeroes AFTER. Double suppression eliminates any audio-thread race
                // window where VS's computed volumes could be heard.
                MethodInfo prefixMethod = typeof(WeatherSoundPatches).GetMethod(nameof(UpdateSoundsPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfixMethod = typeof(WeatherSoundPatches).GetMethod(nameof(UpdateSoundsPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(updateSoundsMethod,
                    prefix: new HarmonyMethod(prefixMethod),
                    postfix: new HarmonyMethod(postfixMethod));

                _patchApplied = true;
                api.Logger.Notification("[SoundPhysicsAdapted] Weather sound suppression patches APPLIED");

                // Phase 5C: Lightning/thunder interception patches
                ApplyLightningPatches(harmony, api);
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to apply weather patches: {ex.Message}");
                api.Logger.Error($"[SoundPhysicsAdapted] Stack: {ex.StackTrace}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        // PHASE 5C: Lightning/Thunder Interception
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Apply Harmony patches for lightning/thunder interception.
        /// - Prefix on WeatherSimulationLightning.ClientTick: suppress + replace ambient thunder
        /// - Postfix on LightningFlash.ClientInit: capture bolt position for Layer 2
        /// </summary>
        private static void ApplyLightningPatches(Harmony harmony, ICoreClientAPI api)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableThunderPositioning)
            {
                api.Logger.Debug("[SoundPhysicsAdapted] Thunder patches NOT applied (config disabled)");
                return;
            }

            try
            {
                // Find WeatherSimulationLightning
                _weatherSimLightningType = FindType("Vintagestory.GameContent.WeatherSimulationLightning");
                if (_weatherSimLightningType == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] WeatherSimulationLightning type not found — thunder positioning disabled");
                    return;
                }

                // Cache fields for reading lightning state
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                _lightningWeatherSysField = _weatherSimLightningType.GetField("weatherSysc", flags)
                    ?? _weatherSimLightningType.GetField("weatherSys", flags);
                _lightningCapiField = _weatherSimLightningType.GetField("capi", flags);
                _nearLightningCoolDownField = _weatherSimLightningType.GetField("nearLightningCoolDown", flags);

                api.Logger.Debug($"[SoundPhysicsAdapted] Lightning fields: weatherSys={_lightningWeatherSysField?.Name ?? "NULL"} " +
                    $"capi={_lightningCapiField?.Name ?? "NULL"} cooldown={_nearLightningCoolDownField?.Name ?? "NULL"}");

                // Patch 1: Prefix on ClientTick to replace ambient thunder
                MethodInfo clientTickMethod = _weatherSimLightningType.GetMethod("ClientTick",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                if (clientTickMethod != null)
                {
                    MethodInfo prefixMethod = typeof(WeatherSoundPatches).GetMethod(nameof(ClientTickPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic);

                    harmony.Patch(clientTickMethod, prefix: new HarmonyMethod(prefixMethod));
                    api.Logger.Debug("[SoundPhysicsAdapted] Patched WeatherSimulationLightning.ClientTick (prefix)");
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] ClientTick method not found on WeatherSimulationLightning");
                }

                // Patch 2: Transpiler + Postfix on LightningFlash.ClientInit
                // Transpiler NOPs PlaySoundAt calls (suppresses VS bolt sounds)
                // Postfix reads bolt position and plays our custom Layer 1 + Layer 2
                Type lightningFlashType = FindType("Vintagestory.GameContent.LightningFlash");
                if (lightningFlashType != null)
                {
                    MethodInfo clientInitMethod = lightningFlashType.GetMethod("ClientInit",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                    if (clientInitMethod != null)
                    {
                        // Transpiler: suppress VS's 3x PlaySoundAt in ClientInit
                        MethodInfo transpilerMethod = typeof(WeatherSoundPatches).GetMethod(nameof(BoltSoundTranspiler),
                            BindingFlags.Static | BindingFlags.NonPublic);

                        MethodInfo postfixMethod = typeof(WeatherSoundPatches).GetMethod(nameof(BoltClientInitPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic);

                        harmony.Patch(clientInitMethod,
                            transpiler: new HarmonyMethod(transpilerMethod),
                            postfix: new HarmonyMethod(postfixMethod));
                        api.Logger.Debug("[SoundPhysicsAdapted] Patched LightningFlash.ClientInit (transpiler + postfix)");
                    }
                    else
                    {
                        api.Logger.Warning("[SoundPhysicsAdapted] ClientInit not found on LightningFlash");
                    }

                    // Patch 3: Transpiler on LightningFlash.Render to suppress nodistance.ogg
                    MethodInfo renderMethod = lightningFlashType.GetMethod("Render",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                    if (renderMethod != null)
                    {
                        MethodInfo renderTranspilerMethod = typeof(WeatherSoundPatches).GetMethod(nameof(BoltSoundTranspiler),
                            BindingFlags.Static | BindingFlags.NonPublic);

                        harmony.Patch(renderMethod,
                            transpiler: new HarmonyMethod(renderTranspilerMethod));
                        api.Logger.Debug("[SoundPhysicsAdapted] Patched LightningFlash.Render (transpiler)");
                    }
                    else
                    {
                        api.Logger.Warning("[SoundPhysicsAdapted] Render method not found on LightningFlash");
                    }
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] LightningFlash type not found");
                }

                _lightningPatchApplied = true;
                api.Logger.Notification("[SoundPhysicsAdapted] Lightning/thunder patches APPLIED (Phase 5C)");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to apply lightning patches: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on WeatherSimulationLightning.ClientTick — replaces ambient thunder.
        /// Re-implements VS's random roll logic but routes sounds through our ThunderAudioHandler.
        /// Returns false to skip the original method when our system is active.
        /// </summary>
        private static bool ClientTickPrefix(object __instance, float dt)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableThunderPositioning || _thunderHandler == null || _weatherManager == null)
                return true; // Let VS handle thunder normally

            if (_lightningCapiField == null || _lightningWeatherSysField == null)
                return true;

            try
            {
                var capi = _lightningCapiField.GetValue(__instance) as ICoreClientAPI;
                if (capi == null) return true;

                var weatherSys = _lightningWeatherSysField.GetValue(__instance);
                if (weatherSys == null) return true;

                // Read BlendedWeatherData
                var blendedProp = weatherSys.GetType().GetProperty("BlendedWeatherData",
                    BindingFlags.Public | BindingFlags.Instance);
                if (blendedProp == null) return true;

                var weatherData = blendedProp.GetValue(weatherSys);
                if (weatherData == null) return true;

                // Read clientClimateCond for temperature check
                var climateField = weatherSys.GetType().GetField("clientClimateCond",
                    BindingFlags.Public | BindingFlags.Instance);
                if (climateField == null) return true;

                var climateCond = climateField.GetValue(weatherSys);
                if (climateCond == null) return true;

                var tempField = climateCond.GetType().GetField("Temperature",
                    BindingFlags.Public | BindingFlags.Instance);
                if (tempField == null) return true;
                float temperature = Convert.ToSingle(tempField.GetValue(climateCond));

                // Read lightningMinTemp from weather data
                var minTempField = weatherData.GetType().GetField("lightningMinTemp",
                    BindingFlags.Public | BindingFlags.Instance);
                if (minTempField == null) return true;
                float lightningMinTemp = Convert.ToSingle(minTempField.GetValue(weatherData));

                // Too cold for lightning — skip both us and VS
                if (temperature < lightningMinTemp) return false;

                // Read lightning rates
                var distRateField = weatherData.GetType().GetField("distantLightningRate",
                    BindingFlags.Public | BindingFlags.Instance);
                var nearRateField = weatherData.GetType().GetField("nearLightningRate",
                    BindingFlags.Public | BindingFlags.Instance);
                if (distRateField == null || nearRateField == null) return true;

                float distantRate = Convert.ToSingle(distRateField.GetValue(weatherData));
                float nearRate = Convert.ToSingle(nearRateField.GetValue(weatherData));

                // Read RainCloudOverlay
                var rainOverlayField = climateCond.GetType().GetField("RainCloudOverlay",
                    BindingFlags.Public | BindingFlags.Instance);
                float rainOverlay = rainOverlayField != null ? Convert.ToSingle(rainOverlayField.GetValue(climateCond)) : 1f;

                // Get player ear position + enclosure metrics
                var player = capi.World.Player?.Entity;
                if (player == null) return true;
                Vec3d earPos = player.Pos.XYZ.Add(player.LocalEyePos);
                float skyCoverage = _weatherManager.SkyCoverage;
                float occlusionFactor = _weatherManager.OcclusionFactor;

                // Get tracked openings from OpeningTracker (via manager reflection or public property)
                var openings = GetTrackedOpenings();

                var worldRand = capi.World.Rand;

                // ── Roll for distant lightning ──
                double rndval = worldRand.NextDouble();
                rndval -= distantRate * rainOverlay;
                if (rndval <= 0)
                {
                    // VS calculates: lightningTime, lightningIntensity, pitch, volume
                    float lightningTime = 0.07f + (float)worldRand.NextDouble() * 0.17f;
                    float lightningIntensity = 0.25f + (float)worldRand.NextDouble();

                    // Set lightningTime and lightningIntensity on the instance for visual effects
                    SetLightningVisuals(__instance, lightningTime, lightningIntensity);

                    // Raw intensity encodes lightning type — our handler computes final volume
                    float rawIntensity = 0.3f;
                    float basePitch = 0.85f + (float)worldRand.NextDouble() * 0.3f;

                    var asset = new AssetLocation("sounds/weather/lightning-distant.ogg");
                    _thunderHandler.PlayAmbientThunder(asset, rawIntensity, basePitch, openings, earPos, skyCoverage, occlusionFactor);
                    return false; // Skip original — we handled this roll
                }

                // ── Roll for near lightning ──
                // Read and manage cooldown
                float cooldown = 0f;
                if (_nearLightningCoolDownField != null)
                {
                    cooldown = Convert.ToSingle(_nearLightningCoolDownField.GetValue(__instance));
                }

                if (cooldown <= 0)
                {
                    rndval -= nearRate * rainOverlay;
                    if (rndval <= 0)
                    {
                        float lightningTime = 0.07f + (float)worldRand.NextDouble() * 0.17f;
                        float lightningIntensity = 1f + (float)worldRand.NextDouble() * 0.9f;

                        SetLightningVisuals(__instance, lightningTime, lightningIntensity);

                        // Raw intensity encodes lightning type — our handler computes final volume
                        float basePitch = 0.85f + (float)worldRand.NextDouble() * 0.3f;

                        AssetLocation asset;
                        float rawIntensity;
                        if (worldRand.NextDouble() > 0.25)
                        {
                            asset = new AssetLocation("sounds/weather/lightning-near.ogg");
                            rawIntensity = 0.7f;
                            if (_nearLightningCoolDownField != null)
                                _nearLightningCoolDownField.SetValue(__instance, 5f);
                        }
                        else
                        {
                            asset = new AssetLocation("sounds/weather/lightning-verynear.ogg");
                            rawIntensity = 1.0f;
                            if (_nearLightningCoolDownField != null)
                                _nearLightningCoolDownField.SetValue(__instance, 10f);
                        }

                        _thunderHandler.PlayAmbientThunder(asset, rawIntensity, basePitch, openings, earPos, skyCoverage, occlusionFactor);
                    }
                }

                // We ran the rolls — skip VS's original ClientTick entirely
                return false;
            }
            catch (Exception ex)
            {
                WeatherAudioManager.WeatherDebugLog($"ClientTickPrefix EXCEPTION: {ex.Message}");
                return true; // On error, fall back to VS thunder
            }
        }

        // ════════════════════════════════════════════════════════════════
        // TRANSPILER: Suppress VS bolt PlaySoundAt calls
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Transpiler shared by ClientInit and Render — replaces all PlaySoundAt callvirt
        /// instructions with calls to our no-op static method, preserving stack layout.
        /// VS visuals (mesh, lights, particles) remain completely untouched.
        /// </summary>
        private static IEnumerable<CodeInstruction> BoltSoundTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);

            // Find the specific PlaySoundAt overload:
            // void PlaySoundAt(AssetLocation, double, double, double, IPlayer, EnumSoundType, float, float, float)
            var targetMethod = typeof(IWorldAccessor).GetMethod("PlaySoundAt", new Type[]
            {
                typeof(AssetLocation), typeof(double), typeof(double), typeof(double),
                typeof(IPlayer), typeof(EnumSoundType), typeof(float), typeof(float), typeof(float)
            });

            var suppressMethod = typeof(WeatherSoundPatches).GetMethod(nameof(SuppressedPlaySoundAt),
                BindingFlags.Static | BindingFlags.NonPublic);

            if (targetMethod == null || suppressMethod == null)
            {
                _api?.Logger.Warning("[SoundPhysicsAdapted] BoltSoundTranspiler: Could not resolve PlaySoundAt or suppress method");
                return codes;
            }

            int replaced = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if ((codes[i].opcode == OpCodes.Callvirt || codes[i].opcode == OpCodes.Call)
                    && codes[i].operand is MethodInfo method
                    && method.Name == "PlaySoundAt"
                    && method.GetParameters().Length == 9)
                {
                    // Replace callvirt with call to our static no-op
                    // Static method takes IWorldAccessor as first param (consumes 'this' from stack)
                    codes[i] = new CodeInstruction(OpCodes.Call, suppressMethod);
                    replaced++;
                }
            }

            _api?.Logger.Debug($"[SoundPhysicsAdapted] BoltSoundTranspiler: replaced {replaced} PlaySoundAt calls");
            return codes;
        }

        /// <summary>
        /// No-op replacement for VS's PlaySoundAt calls in LightningFlash.
        /// Same signature (with IWorldAccessor as first param for stack compatibility).
        /// Our ThunderAudioHandler plays custom positioned/filtered replacements.
        /// </summary>
        private static void SuppressedPlaySoundAt(
            IWorldAccessor world, AssetLocation location, double x, double y, double z,
            IPlayer player, EnumSoundType soundType, float pitch, float range, float volume)
        {
            // Intentionally empty — bolt sounds suppressed, our handler replaces them
        }

        // ════════════════════════════════════════════════════════════════
        // POSTFIX: Capture bolt position and play custom thunder
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Postfix on LightningFlash.ClientInit — captures bolt position and plays
        /// our custom Layer 1 (LPF-filtered) + Layer 2 (positional at opening).
        /// VS's PlaySoundAt calls are already suppressed by the transpiler above.
        /// </summary>
        private static void BoltClientInitPostfix(object __instance)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableThunderPositioning || _thunderHandler == null || _weatherManager == null)
                return;

            try
            {
                // Read bolt position: origin + points[last]
                var originField = __instance.GetType().GetField("origin",
                    BindingFlags.Public | BindingFlags.Instance);
                var pointsField = __instance.GetType().GetField("points",
                    BindingFlags.Public | BindingFlags.Instance);

                if (originField == null || pointsField == null) return;

                var origin = originField.GetValue(__instance) as Vec3d;
                var points = pointsField.GetValue(__instance) as System.Collections.Generic.List<Vec3d>;

                if (origin == null || points == null || points.Count == 0) return;

                // Bolt impact position = origin + last point
                var lastPoint = points[points.Count - 1];
                Vec3d boltPos = new Vec3d(
                    origin.X + lastPoint.X,
                    origin.Y + lastPoint.Y,
                    origin.Z + lastPoint.Z);

                // Get player info
                if (_api == null) return;
                var player = _api.World.Player?.Entity;
                if (player == null) return;

                Vec3d earPos = player.Pos.XYZ.Add(player.LocalEyePos);
                float distance = (float)earPos.DistanceTo(boltPos);

                if (distance > 500) return; // Must cover visual range — transpiler suppresses VS sounds at ALL distances

                float skyCoverage = _weatherManager.SkyCoverage;
                float occlusionFactor = _weatherManager.OcclusionFactor;
                var openings = GetTrackedOpenings();

                _thunderHandler.PlayBoltThunder(boltPos, distance, openings, earPos, skyCoverage, occlusionFactor);
            }
            catch (Exception ex)
            {
                WeatherAudioManager.WeatherDebugLog($"BoltClientInitPostfix EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>
        /// Get tracked openings from the WeatherAudioManager's opening tracker.
        /// </summary>
        private static System.Collections.Generic.IReadOnlyList<TrackedOpening> GetTrackedOpenings()
        {
            if (_weatherManager == null) return null;

            try
            {
                // Access openingTracker field via reflection
                var trackerField = typeof(WeatherAudioManager).GetField("openingTracker",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (trackerField == null) return null;

                var tracker = trackerField.GetValue(_weatherManager) as OpeningTracker;
                return tracker?.TrackedOpenings;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Set lightningTime and lightningIntensity on the WeatherSimulationLightning instance.
        /// Required for the visual flash effect to work even though we skip the sound.
        /// </summary>
        private static void SetLightningVisuals(object instance, float time, float intensity)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.Instance;
                var timeField = instance.GetType().GetField("lightningTime", flags);
                var intensityField = instance.GetType().GetField("lightningIntensity", flags);

                timeField?.SetValue(instance, time);
                intensityField?.SetValue(instance, intensity);
            }
            catch { }
        }

        /// <summary>
        /// Prefix on WeatherSimulationSound.updateSounds().
        /// Zeroes all sound volumes BEFORE VS processes them, eliminating the
        /// audio-thread race window where VS's computed volumes could leak through.
        /// Also captures the instance early so our weather tick can read intensities sooner.
        /// </summary>
        private static void UpdateSoundsPrefix(object __instance)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableWeatherEnhancement)
                return;

            // Capture instance ASAP — allows our weather tick to read intensities
            // even before the postfix fires
            _weatherSimInstance = __instance;

            try
            {
                // CRITICAL: Zero ALL vanilla sound volumes UNCONDITIONALLY — even non-playing.
                // VS loads weather sounds with Volume=1. When updateSounds() calls Start(),
                // the OpenAL source begins playing at that loaded volume. By zeroing the
                // volume BEFORE Start() is called, the source starts at gain=0 instead of 1.0.
                // This eliminates the single-frame full-volume spike on world join.
                // Uses *Always variants that skip the IsPlaying check.
                SuppressSoundArrayAlways(_rainSoundsLeafyField, __instance);
                SuppressSoundArrayAlways(_rainSoundsLeaflessField, __instance);
                SuppressSoundAlways(_windSoundLeafyField, __instance);
                SuppressSoundAlways(_windSoundLeaflessField, __instance);
                SuppressSoundAlways(_lowTrembleSoundField, __instance);
                SuppressSoundAlways(_hailSoundField, __instance);
            }
            catch
            {
                // Best-effort — postfix will catch anything we miss
            }
        }

        /// <summary>
        /// Postfix on WeatherSimulationSound.updateSounds().
        /// Zeroes volume on all 6 global weather loops so our handler can replace them.
        /// Uses unconditional suppression (*Always variants) — same as prefix.
        /// Double-suppression (prefix + postfix) ensures VS's computed volumes
        /// never leak to the OpenAL audio thread.
        /// </summary>
        private static void UpdateSoundsPostfix(object __instance)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableWeatherEnhancement)
                return; // Let vanilla play normally

            try
            {
                SuppressSoundArrayAlways(_rainSoundsLeafyField, __instance);
                SuppressSoundArrayAlways(_rainSoundsLeaflessField, __instance);
                SuppressSoundAlways(_windSoundLeafyField, __instance);
                SuppressSoundAlways(_windSoundLeaflessField, __instance);
                SuppressSoundAlways(_lowTrembleSoundField, __instance);
                SuppressSoundAlways(_hailSoundField, __instance);
            }
            catch (Exception ex)
            {
                WeatherAudioManager.WeatherDebugLog($"UpdateSoundsPostfix EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>
        /// Unconditionally zero volume on a single ILoadedSound field.
        /// Does NOT check IsPlaying — zeros loaded-but-not-started sounds too.
        /// This prevents Start() from inheriting Volume=1 from LoadSound params.
        /// </summary>
        private static void SuppressSoundAlways(FieldInfo field, object instance)
        {
            if (field == null) return;
            try
            {
                var sound = field.GetValue(instance) as ILoadedSound;
                sound?.SetVolume(0f);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Unconditionally zero volume on an ILoadedSound[] array field.
        /// Skips IsPlaying check — works on loaded-but-not-started sounds too.
        /// </summary>
        private static void SuppressSoundArrayAlways(FieldInfo field, object instance)
        {
            if (field == null) return;
            try
            {
                var sounds = field.GetValue(instance) as ILoadedSound[];
                if (sounds != null)
                {
                    for (int i = 0; i < sounds.Length; i++)
                    {
                        sounds[i]?.SetVolume(0f);
                    }
                }
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Read current rain intensity from VS climate data.
        /// Uses raw Rainfall from clientClimateCond (0-1), independent of enclosure.
        /// Previous approach of reconstructing from curRainVolume + loss was broken:
        /// loss jumps instantly but volume smooths slowly → overshoot → rain gets louder indoors.
        /// </summary>
        public static float ReadRainIntensity()
        {
            // Read raw Rainfall from weatherSys.clientClimateCond
            float rainfall = ReadClimateRainfall();
            if (rainfall > 0f) return rainfall;

            // Fallback: use VS's smoothed volumes (less accurate)
            float leafy = ReadFloatField(_curRainVolumeLeafyField, "curRainVolumeLeafy");
            float leafless = ReadFloatField(_curRainVolumeLeaflessField, "curRainVolumeLeafless");
            return Math.Max(leafy, leafless);
        }

        /// <summary>
        /// Read current wind speed from VS via WeatherDataSnapshot.
        /// Falls back to combined wind volume if BlendedWeatherData not accessible.
        /// </summary>
        public static float ReadWindSpeed()
        {
            // Try to get raw wind speed from BlendedWeatherData
            if (_weatherSysField != null && _weatherSimInstance != null)
            {
                try
                {
                    var weatherSys = _weatherSysField.GetValue(_weatherSimInstance);
                    if (weatherSys != null)
                    {
                        var blendedProp = weatherSys.GetType().GetProperty("BlendedWeatherData",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (blendedProp != null)
                        {
                            var weatherData = blendedProp.GetValue(weatherSys);
                            if (weatherData != null)
                            {
                                var windField = weatherData.GetType().GetField("curWindSpeed",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (windField != null)
                                {
                                    var windVec = windField.GetValue(weatherData);
                                    if (windVec != null)
                                    {
                                        // Vec3d — get X component
                                        var xProp = windVec.GetType().GetField("X",
                                            BindingFlags.Public | BindingFlags.Instance);
                                        if (xProp != null)
                                        {
                                            return Convert.ToSingle(xProp.GetValue(windVec));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // Fallback: use combined wind volume (less accurate but usable)
            float leafy = ReadFloatField(_curWindVolumeLeafyField, "curWindVolumeLeafy");
            float leafless = ReadFloatField(_curWindVolumeLeaflessField, "curWindVolumeLeafless");
            return Math.Max(leafy, leafless);
        }

        /// <summary>
        /// Read current hail intensity from VS climate data.
        /// VS uses conds.Rainfall for hail when precType==Hail.
        /// Falls back to curHailVolume if climate data unavailable.
        /// </summary>
        public static float ReadHailIntensity()
        {
            // Hail uses same Rainfall value when precipitation type is hail
            // Check if hail volume is active (indicates hail precip type)
            float hailVol = ReadFloatField(_curHailVolumeField, "curHailVolume");
            if (hailVol > 0.01f)
            {
                // Hail is active — read raw rainfall as hail intensity
                float rainfall = ReadClimateRainfall();
                if (rainfall > 0f) return GameMath.Clamp(rainfall * 2f, 0f, 1f);
            }
            return hailVol; // Fallback or no hail
        }

        /// <summary>
        /// Read roomVolumePitchLoss — public static float on WeatherSimulationSound.
        /// </summary>
        public static float ReadRoomVolumePitchLoss()
        {
            if (_weatherSimSoundType == null) return 0f;

            try
            {
                var field = _weatherSimSoundType.GetField("roomVolumePitchLoss",
                    BindingFlags.Public | BindingFlags.Static);
                if (field != null)
                    return (float)field.GetValue(null);
            }
            catch { }

            return 0f;
        }

        /// <summary>
        /// Determine if current biome uses leafy sounds.
        /// 
        /// Strategy: Read VS's per-channel volumes and compare leafy vs leafless.
        /// BUT: indoors, VS suppresses both channels toward 0 via roomVolumePitchLoss.
        /// When both are near-zero, any comparison is noise. So we LATCH the last
        /// reliable outdoor reading and hold it while indoors.
        /// 
        /// Also tries nearbyLeaviness field directly if available (VS version dependent).
        /// </summary>
        public static bool IsLeafy()
        {
            if (_weatherSimInstance == null) return _latchedLeafy;

            try
            {
                // Attempt 1: Direct nearbyLeaviness field (if it exists on this VS version)
                if (_nearbyLeavinessField != null)
                {
                    float leaviness = ReadFloatField(_nearbyLeavinessField, "nearbyLeaviness");
                    _latchedLeafy = leaviness >= 0.5f;
                    _hasLatchedLeafy = true;
                    return _latchedLeafy;
                }

                // Attempt 2: Try reading nearbyLeaviness from weatherSys instance
                // (it may live on WeatherSystemClient rather than WeatherSimulationSound)
                if (_weatherSysField != null)
                {
                    var weatherSys = _weatherSysField.GetValue(_weatherSimInstance);
                    if (weatherSys != null)
                    {
                        var leavinessField = weatherSys.GetType().GetField("nearbyLeaviness",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (leavinessField != null)
                        {
                            float leaviness = Convert.ToSingle(leavinessField.GetValue(weatherSys));
                            _latchedLeafy = leaviness >= 0.5f;
                            _hasLatchedLeafy = true;
                            return _latchedLeafy;
                        }
                    }
                }

                // Attempt 3: Compare per-channel volumes with latch
                float leafyVol = ReadFloatField(_curRainVolumeLeafyField, "curRainVolumeLeafy");
                float leaflessVol = ReadFloatField(_curRainVolumeLeaflessField, "curRainVolumeLeafless");

                // Only update latch when we have a clear signal
                // (at least one channel significantly above zero)
                const float RELIABLE_THRESHOLD = 0.02f;
                float maxRain = Math.Max(leafyVol, leaflessVol);

                if (maxRain >= RELIABLE_THRESHOLD)
                {
                    // Clear rain signal — reliable comparison
                    _latchedLeafy = leafyVol >= leaflessVol;
                    _hasLatchedLeafy = true;
                    return _latchedLeafy;
                }

                // Rain too quiet — try wind
                float windLeafy = ReadFloatField(_curWindVolumeLeafyField, "curWindVolumeLeafy");
                float windLeafless = ReadFloatField(_curWindVolumeLeaflessField, "curWindVolumeLeafless");
                float maxWind = Math.Max(windLeafy, windLeafless);

                if (maxWind >= RELIABLE_THRESHOLD)
                {
                    _latchedLeafy = windLeafy >= windLeafless;
                    _hasLatchedLeafy = true;
                    return _latchedLeafy;
                }

                // Both too quiet (deep indoors) — return latched value
                return _hasLatchedLeafy ? _latchedLeafy : false;
            }
            catch
            {
                return _hasLatchedLeafy ? _latchedLeafy : false;
            }
        }

        /// <summary>
        /// Get raw leaviness as a float (0 = fully leafless, 1 = fully leafy).
        /// Used for crossfade blending between leafy and leafless wind/rain sounds.
        /// Vanilla VS plays BOTH variants simultaneously, splitting volume by this value.
        /// Falls back to latched value when indoors (same strategy as IsLeafy).
        /// </summary>
        public static float GetLeaviness()
        {
            if (_weatherSimInstance == null) return _latchedLeaviness;

            try
            {
                // Attempt 1: Direct nearbyLeaviness field
                if (_nearbyLeavinessField != null)
                {
                    float leaviness = ReadFloatField(_nearbyLeavinessField, "nearbyLeaviness");
                    _latchedLeaviness = GameMath.Clamp(leaviness * 60f, 0f, 1f); // VS uses: Clamp(value * 60, 0, 1)
                    return _latchedLeaviness;
                }

                // Attempt 2: Try reading from weatherSys instance
                if (_weatherSysField != null)
                {
                    var weatherSys = _weatherSysField.GetValue(_weatherSimInstance);
                    if (weatherSys != null)
                    {
                        var leavinessField = weatherSys.GetType().GetField("nearbyLeaviness",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (leavinessField != null)
                        {
                            float leaviness = Convert.ToSingle(leavinessField.GetValue(weatherSys));
                            _latchedLeaviness = GameMath.Clamp(leaviness * 60f, 0f, 1f);
                            return _latchedLeaviness;
                        }
                    }
                }

                // Attempt 3: Derive from per-channel volumes when direct field unavailable
                float leafyVol = ReadFloatField(_curRainVolumeLeafyField, "curRainVolumeLeafy");
                float leaflessVol = ReadFloatField(_curRainVolumeLeaflessField, "curRainVolumeLeafless");
                float totalRain = leafyVol + leaflessVol;

                if (totalRain >= 0.02f)
                {
                    _latchedLeaviness = leafyVol / totalRain;
                    return _latchedLeaviness;
                }

                // Try wind
                float windLeafy = ReadFloatField(_curWindVolumeLeafyField, "curWindVolumeLeafy");
                float windLeafless = ReadFloatField(_curWindVolumeLeaflessField, "curWindVolumeLeafless");
                float totalWind = windLeafy + windLeafless;

                if (totalWind >= 0.02f)
                {
                    _latchedLeaviness = windLeafy / totalWind;
                    return _latchedLeaviness;
                }

                return _latchedLeaviness; // Latched from last reliable reading
            }
            catch
            {
                return _latchedLeaviness;
            }
        }

        // ── Climate data access (raw values, independent of enclosure) ──

        /// <summary>
        /// Read raw Rainfall from weatherSys.clientClimateCond.
        /// This is the actual weather intensity, unaffected by roomVolumePitchLoss.
        /// </summary>
        private static float ReadClimateRainfall()
        {
            if (_weatherSysField == null || _weatherSimInstance == null) return 0f;

            try
            {
                var weatherSys = _weatherSysField.GetValue(_weatherSimInstance);
                if (weatherSys == null) return 0f;

                // Access clientClimateCond (public field on WeatherSystemClient)
                var climateField = weatherSys.GetType().GetField("clientClimateCond",
                    BindingFlags.Public | BindingFlags.Instance);
                if (climateField == null) return 0f;

                var climateCond = climateField.GetValue(weatherSys);
                if (climateCond == null) return 0f;

                // Read Rainfall field (public float, 0-1)
                var rainfallField = climateCond.GetType().GetField("Rainfall",
                    BindingFlags.Public | BindingFlags.Instance);
                if (rainfallField != null)
                {
                    return Convert.ToSingle(rainfallField.GetValue(climateCond));
                }

                // Try as property fallback
                var rainfallProp = climateCond.GetType().GetProperty("Rainfall",
                    BindingFlags.Public | BindingFlags.Instance);
                if (rainfallProp != null)
                {
                    return Convert.ToSingle(rainfallProp.GetValue(climateCond));
                }
            }
            catch (Exception ex)
            {
                WeatherAudioManager.WeatherDebugLog($"ReadClimateRainfall error: {ex.Message}");
            }

            return 0f;
        }

        // ── Private helpers ──

        private static float ReadFloatField(FieldInfo field, string name)
        {
            if (field == null || _weatherSimInstance == null) return 0f;

            try
            {
                object val = field.GetValue(_weatherSimInstance);
                if (val is float f) return f;
                if (val is double d) return (float)d;
                return Convert.ToSingle(val);
            }
            catch
            {
                return 0f;
            }
        }

        private static void CacheSoundFields(ICoreClientAPI api)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

            // Rain sounds are ILoadedSound[] arrays, not single ILoadedSound
            _rainSoundsLeafyField = FindField(_weatherSimSoundType, "rainSoundsLeafy", flags, api);
            _rainSoundsLeaflessField = FindField(_weatherSimSoundType, "rainSoundsLeafless", flags, api);
            _windSoundLeafyField = FindField(_weatherSimSoundType, "windSoundLeafy", flags, api);
            _windSoundLeaflessField = FindField(_weatherSimSoundType, "windSoundLeafless", flags, api);
            _lowTrembleSoundField = FindField(_weatherSimSoundType, "lowTrembleSound", flags, api);
            _hailSoundField = FindField(_weatherSimSoundType, "hailSound", flags, api);

            int found = 0;
            if (_rainSoundsLeafyField != null) found++;
            if (_rainSoundsLeaflessField != null) found++;
            if (_windSoundLeafyField != null) found++;
            if (_windSoundLeaflessField != null) found++;
            if (_lowTrembleSoundField != null) found++;
            if (_hailSoundField != null) found++;

            api.Logger.Debug($"[SoundPhysicsAdapted] Weather sound fields resolved: {found}/6");

            if (found < 6)
            {
                api.Logger.Debug("[SoundPhysicsAdapted] Available fields on WeatherSimulationSound:");
                foreach (var f in _weatherSimSoundType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                {
                    api.Logger.Debug($"  {f.FieldType.Name} {f.Name}");
                }
            }
        }

        private static void CacheWeatherStateFields(ICoreClientAPI api)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

            // VS stores per-channel volumes that already include rainfall, enclosure, leafiness
            _curRainVolumeLeafyField = FindField(_weatherSimSoundType, "curRainVolumeLeafy", flags, api);
            _curRainVolumeLeaflessField = FindField(_weatherSimSoundType, "curRainVolumeLeafless", flags, api);

            // Wind volume per channel
            _curWindVolumeLeafyField = FindField(_weatherSimSoundType, "curWindVolumeLeafy", flags, api);
            _curWindVolumeLeaflessField = FindField(_weatherSimSoundType, "curWindVolumeLeafless", flags, api);

            // Hail volume
            _curHailVolumeField = FindField(_weatherSimSoundType, "curHailVolume", flags, api);

            // Reference to WeatherSystemClient for accessing raw Rainfall, WindSpeed
            _weatherSysField = FindField(_weatherSimSoundType, "weatherSys", flags, api);

            // nearbyLeaviness — direct biome leaviness value, independent of volume
            _nearbyLeavinessField = FindField(_weatherSimSoundType, "nearbyLeaviness", flags, api);

            int found = 0;
            if (_curRainVolumeLeafyField != null) found++;
            if (_curRainVolumeLeaflessField != null) found++;
            if (_curWindVolumeLeafyField != null) found++;
            if (_curWindVolumeLeaflessField != null) found++;
            if (_curHailVolumeField != null) found++;
            if (_weatherSysField != null) found++;
            if (_nearbyLeavinessField != null) found++;

            api.Logger.Debug($"[SoundPhysicsAdapted] Weather state fields: rainLeafy={_curRainVolumeLeafyField?.Name ?? "NULL"}, " +
                $"rainLeafless={_curRainVolumeLeaflessField?.Name ?? "NULL"}, " +
                $"windLeafy={_curWindVolumeLeafyField?.Name ?? "NULL"}, " +
                $"windLeafless={_curWindVolumeLeaflessField?.Name ?? "NULL"}, " +
                $"hail={_curHailVolumeField?.Name ?? "NULL"}, " +
                $"weatherSys={_weatherSysField?.Name ?? "NULL"}, " +
                $"nearbyLeaviness={_nearbyLeavinessField?.Name ?? "NULL"} ({found}/7)");
        }

        private static FieldInfo FindField(Type type, string name, BindingFlags flags, ICoreClientAPI api)
        {
            var field = type.GetField(name, flags);
            if (field != null)
            {
                api.Logger.Debug($"[SoundPhysicsAdapted] Found field: {name} ({field.FieldType.Name})");
            }
            return field;
        }

        private static Type FindType(string fullName)
        {
            // First pass: check known VS assemblies (fast path)
            string[] knownAssemblies = { "VintagestoryLib", "Vintagestory", "VSSurvivalMod", "VSEssentials", "game", "survival" };
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name;
                foreach (var known in knownAssemblies)
                {
                    if (asmName == known || asmName.Contains(known))
                    {
                        var type = asm.GetType(fullName);
                        if (type != null)
                        {
                            _api?.Logger.Debug($"[SoundPhysicsAdapted] Found {fullName} in assembly: {asmName}");
                            return type;
                        }
                        break;
                    }
                }
            }

            // Second pass: search ALL assemblies as fallback
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(fullName);
                    if (type != null)
                    {
                        _api?.Logger.Debug($"[SoundPhysicsAdapted] Found {fullName} in assembly (fallback): {asm.GetName().Name}");
                        return type;
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
