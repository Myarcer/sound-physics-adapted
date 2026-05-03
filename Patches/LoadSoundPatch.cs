using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace soundphysicsadapted
{
    /// <summary>
    /// Harmony patches for sound loading, position updates, and universal mono downmix.
    /// Uses per-sound filters via AudioRenderer to avoid global filter thrashing.
    /// 
    /// Key patches:
    /// - StartPlayingAudioMonoPrefix: Universal stereo→mono for ALL positional sounds
    ///   (catches both PlaySoundAt and LoadSound paths at their convergence point)
    /// - LoadSoundMonoPrefix: Legacy explicit mono requests (weather pools, resonator)
    /// - SoundStartPrefix/Postfix: Occlusion, reverb, filter management
    /// - SetPosition: Position tracking for acoustics updates
    /// </summary>
    public static class LoadSoundPatch
    {
        private static Type clientMainType;
        private static Type loadedSoundNativeType;
        private static MethodInfo loadSoundMethod;
        private static PropertyInfo soundParamsProperty;

        // Cached for SetPosition patch
        private static ICoreClientAPI cachedApi;
        private static IBlockAccessor cachedBlockAccessor;

        // Mono cache swap for explicit LoadSound requests (weather pools, resonator)
        // When ForceMonoNextLoad is set, we temporarily replace the cached AudioMetaData
        // in ScreenManager.soundAudioData with a mono clone before LoadSound runs.
        private static Dictionary<AssetLocation, AudioData> soundAudioDataDict;
        private static AssetLocation monoSwapKey;       // The key we swapped (for restore)
        private static AudioData monoSwapOriginal;      // The original stereo data (for restore)

        // === PRE-WARMUP SOUND QUEUE ===
        // Sounds that call Start() between LevelFinalize and warmup completion are queued here.
        // Block entities (querns, forges, beehives) initialized during chunk loading start their
        // ambient sounds before IsWorldReady, causing them to be permanently invisible to the
        // occlusion tick system. After warmup completes, ProcessPreWarmupQueue() retroactively
        // registers and applies initial occlusion to these sounds.
        private static List<WeakReference<ILoadedSound>> preWarmupSoundQueue = new List<WeakReference<ILoadedSound>>();
        private static bool preWarmupQueueProcessed = false;

        /// <summary>
        /// Manually apply patches using reflection
        /// Called from ModSystem.StartClientSide
        /// </summary>
        public static void ApplyPatches(Harmony harmony, ICoreClientAPI api)
        {
            cachedApi = api;
            
            try
            {
                Assembly vsLib = null;

                // Find VintagestoryLib assembly - try multiple possible names
                api.Logger.Debug("[SoundPhysicsAdapted] Searching for VintagestoryLib among loaded assemblies...");

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = assembly.GetName().Name;

                    // Try multiple possible assembly names (VS changed structure over versions)
                    if (name == "VintagestoryLib" || name.Contains("VintagestoryLib") || name == "Vintagestory")
                    {
                        api.Logger.Debug($"[SoundPhysicsAdapted] Checking assembly: {name}");

                        // Try to find ClientMain type - check both NoObf and direct Client namespaces
                        var testType = assembly.GetType("Vintagestory.Client.NoObf.ClientMain")
                                    ?? assembly.GetType("Vintagestory.Client.ClientMain");
                        if (testType != null)
                        {
                            vsLib = assembly;
                            clientMainType = testType;
                            loadedSoundNativeType = assembly.GetType("Vintagestory.Client.NoObf.LoadedSoundNative")
                                                 ?? assembly.GetType("Vintagestory.Client.LoadedSoundNative");

                            api.Logger.Notification($"[SoundPhysicsAdapted] Found ClientMain in: {name}");
                            api.Logger.Notification($"[SoundPhysicsAdapted] LoadedSoundNative found: {loadedSoundNativeType != null}");

                            // If LoadedSoundNative not found, search for similar types
                            if (loadedSoundNativeType == null)
                            {
                                api.Logger.Debug("[SoundPhysicsAdapted] Searching for types containing 'LoadedSound'...");
                                foreach (var type in assembly.GetTypes())
                                {
                                    if (type.Name.Contains("LoadedSound"))
                                    {
                                        api.Logger.Debug($"[SoundPhysicsAdapted]   Found type: {type.FullName}");
                                        if (type.Name == "LoadedSoundNative")
                                        {
                                            loadedSoundNativeType = type;
                                        }
                                    }
                                }
                            }
                            break;
                        }
                    }
                }

                // Fallback: try Type.GetType with assembly-qualified name
                if (clientMainType == null)
                {
                    api.Logger.Debug("[SoundPhysicsAdapted] Direct search failed, trying Type.GetType...");
                    clientMainType = Type.GetType("Vintagestory.Client.NoObf.ClientMain, VintagestoryLib")
                                  ?? Type.GetType("Vintagestory.Client.ClientMain, VintagestoryLib");
                    if (clientMainType != null)
                    {
                        vsLib = clientMainType.Assembly;
                        loadedSoundNativeType = vsLib.GetType("Vintagestory.Client.NoObf.LoadedSoundNative")
                                             ?? vsLib.GetType("Vintagestory.Client.LoadedSoundNative");
                        api.Logger.Notification("[SoundPhysicsAdapted] Found types via Type.GetType");
                    }
                }

                if (clientMainType == null || loadedSoundNativeType == null)
                {
                    // Log all Vintage-related assemblies for debugging
                    api.Logger.Error("[SoundPhysicsAdapted] Could not find required types. Loaded assemblies:");
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name.Contains("Vintage") || asm.GetName().Name.Contains("vintage"))
                        {
                            api.Logger.Error($"[SoundPhysicsAdapted]   - {asm.GetName().Name}");
                        }
                    }
                    return;
                }

                // Get Params property from LoadedSoundNative
                soundParamsProperty = loadedSoundNativeType.GetProperty("Params");

                // Phase 5B: Resolve ScreenManager.soundAudioData for mono cache swap
                try
                {
                    var screenManagerType = clientMainType.Assembly.GetType("Vintagestory.Client.NoObf.ScreenManager")
                                         ?? clientMainType.Assembly.GetType("Vintagestory.Client.ScreenManager");
                    if (screenManagerType != null)
                    {
                        var field = screenManagerType.GetField("soundAudioData",
                            BindingFlags.Public | BindingFlags.Static);
                        if (field != null)
                        {
                            soundAudioDataDict = field.GetValue(null) as Dictionary<AssetLocation, AudioData>;
                            if (soundAudioDataDict != null)
                            {
                                api.Logger.Debug($"[SoundPhysicsAdapted] Resolved ScreenManager.soundAudioData ({soundAudioDataDict.Count} entries)");
                            }
                        }
                    }
                    if (soundAudioDataDict == null)
                    {
                        api.Logger.Warning("[SoundPhysicsAdapted] Could not resolve ScreenManager.soundAudioData — positional weather mono downmix unavailable");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning($"[SoundPhysicsAdapted] Failed to resolve soundAudioData: {ex.Message}");
                }

                // Patch 0: Universal mono downmix — StartPlaying(AudioData, SoundParams, AssetLocation)
                // This is the convergence point for BOTH PlaySoundAtInternal and LoadSound paths.
                // If a positional sound has stereo AudioData, we swap it to mono before CreateAudio.
                // This fixes explosions, block sounds, and ALL other stereo sounds played via PlaySoundAt.
                try
                {
                    var audioDataType = clientMainType.Assembly.GetType("AudioData")
                                    ?? clientMainType.Assembly.GetType("Vintagestory.Client.NoObf.AudioData");
                    
                    if (audioDataType == null)
                    {
                        // Search all types for AudioData
                        foreach (var t in clientMainType.Assembly.GetTypes())
                        {
                            if (t.Name == "AudioData" && t.IsAbstract)
                            {
                                audioDataType = t;
                                break;
                            }
                        }
                    }

                    if (audioDataType != null)
                    {
                        var startPlayingAudioMethod = clientMainType.GetMethod("StartPlaying",
                            BindingFlags.NonPublic | BindingFlags.Instance,
                            null,
                            new Type[] { audioDataType, typeof(SoundParams), typeof(AssetLocation) },
                            null);

                        if (startPlayingAudioMethod != null)
                        {
                            var monoPrefix = typeof(LoadSoundPatch).GetMethod("StartPlayingAudioMonoPrefix",
                                BindingFlags.Static | BindingFlags.Public);
                            harmony.Patch(startPlayingAudioMethod, prefix: new HarmonyMethod(monoPrefix));
                            api.Logger.Notification("[SoundPhysicsAdapted] Patched ClientMain.StartPlaying(AudioData) [UNIVERSAL MONO DOWNMIX]");
                        }
                        else
                        {
                            api.Logger.Warning("[SoundPhysicsAdapted] Could not find StartPlaying(AudioData, SoundParams, AssetLocation)");
                        }
                    }
                    else
                    {
                        api.Logger.Warning("[SoundPhysicsAdapted] Could not find AudioData type for universal mono downmix");
                    }
                }
                catch (Exception monoEx)
                {
                    api.Logger.Warning($"[SoundPhysicsAdapted] Universal mono downmix patch failed: {monoEx.Message}");
                }

                // Patch 0b: PlaySoundAt(Entity) overloads — tag local player entity sounds
                // These prefixes detect when the sound source entity is the local player
                // and record the exact position, so StartPlayingAudioMonoPrefix can skip
                // mono downmix even if the player moves before async audio loads.
                PatchPlaySoundAtEntityOverloads(harmony, api);

                // Patch 1: ClientMain.LoadSound - for initial sound loading
                loadSoundMethod = clientMainType.GetMethod("LoadSound", 
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(SoundParams) },
                    null);

                if (loadSoundMethod != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("LoadSoundMonoPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    var postfix = typeof(LoadSoundPatch).GetMethod("LoadSoundPostfix", 
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(loadSoundMethod,
                        prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                        postfix: new HarmonyMethod(postfix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched ClientMain.LoadSound() [prefix={prefix != null}]");
                }

                // Patch 2: LoadedSoundNative.SetPosition(Vec3f) - for continuous position updates
                var setPositionVec3 = loadedSoundNativeType.GetMethod("SetPosition",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(Vec3f) },
                    null);

                if (setPositionVec3 != null)
                {
                    var postfix = typeof(LoadSoundPatch).GetMethod("SetPositionPostfix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(setPositionVec3, postfix: new HarmonyMethod(postfix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched LoadedSoundNative.SetPosition(Vec3f)");
                }

                // Patch 3: LoadedSoundNative.SetPosition(float, float, float) - alternate signature
                var setPositionXYZ = loadedSoundNativeType.GetMethod("SetPosition",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(float), typeof(float), typeof(float) },
                    null);

                if (setPositionXYZ != null)
                {
                    var postfix = typeof(LoadSoundPatch).GetMethod("SetPositionXYZPostfix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(setPositionXYZ, postfix: new HarmonyMethod(postfix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched LoadedSoundNative.SetPosition(x,y,z)");
                }

                // Patch 4: LoadedSoundNative.Start() - PREFIX and POSTFIX
                // PREFIX: Register sound and configure filter (but don't attach yet)
                // POSTFIX: Attach filter AFTER AL.SourcePlay() - filter only works on playing sources!
                var startMethod = loadedSoundNativeType.GetMethod("Start",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,  // No parameters
                    null);

                if (startMethod != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("SoundStartPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    var postfix = typeof(LoadSoundPatch).GetMethod("SoundStartPostfix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(startMethod, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched LoadedSoundNative.Start() [PREFIX+POSTFIX]");
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find LoadedSoundNative.Start method");
                }

                // Patch 5: ClientMain.StartPlaying - RE-APPLY filter after VS finishes setup
                // VS applies underwater effects and reverb AFTER Start() which can overwrite our filter
                // This postfix runs AFTER all VS setup is complete, ensuring our filter is final
                var startPlayingMethod = clientMainType.GetMethod("StartPlaying",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(ILoadedSound), typeof(AssetLocation) },
                    null);

                if (startPlayingMethod != null)
                {
                    var postfix = typeof(LoadSoundPatch).GetMethod("StartPlayingFinalPostfix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(startPlayingMethod, postfix: new HarmonyMethod(postfix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched ClientMain.StartPlaying() [FINAL POSTFIX]");
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find ClientMain.StartPlaying method");
                }

                // Patch 6: Block vanilla SetLowPassfiltering when DisableVanillaFilters is enabled
                // This prevents VS's underwater/reverb effects from interfering with our occlusion
                var setLowPassMethod = loadedSoundNativeType.GetMethod("SetLowPassfiltering",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(float) },
                    null);

                if (setLowPassMethod != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("SetLowPassFilteringPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(setLowPassMethod, prefix: new HarmonyMethod(prefix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched LoadedSoundNative.SetLowPassfiltering() [BLOCK VANILLA]");
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find SetLowPassfiltering method");
                }

                // Patch 6b: Block vanilla SetPitchOffset and apply our own
                // Vanilla applies -0.15 to -0.2 pitch offset underwater - we want our configurable value instead
                var setPitchOffsetMethod = loadedSoundNativeType.GetMethod("SetPitchOffset",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(float) },
                    null);

                if (setPitchOffsetMethod != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("SetPitchOffsetPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(setPitchOffsetMethod, prefix: new HarmonyMethod(prefix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched LoadedSoundNative.SetPitchOffset() [BLOCK VANILLA]");
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find SetPitchOffset method");
                }

                // Patch 7: LoadedSoundNative.createSoundSource - BLOCK global filter attachment
                // VS uses a GLOBAL filter shared by all sounds. createSoundSource() attaches this global filter.
                // We need to prevent this so our per-sound filters work properly.
                var createSoundSourceMethod = loadedSoundNativeType.GetMethod("createSoundSource",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

                if (createSoundSourceMethod != null)
                {
                    var postfix = typeof(LoadSoundPatch).GetMethod("CreateSoundSourcePostfix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(createSoundSourceMethod, postfix: new HarmonyMethod(postfix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched LoadedSoundNative.createSoundSource() [POSTFIX - BLOCK GLOBAL FILTER]");
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find createSoundSource method - global filter may interfere!");
                }

                // Initialize per-sound filter system
                // This creates unique filters per sound to avoid global filter thrashing
                if (EfxHelper.Initialize(api))
                {
                    if (AudioRenderer.Initialize(loadedSoundNativeType, api))
                    {
                        api.Logger.Notification("[SoundPhysicsAdapted] Per-sound filter system enabled");
                    }
                }

                // Patch 8: AL.SourcePlay - DIAGNOSTIC HOOK
                // This hooks the lowest level OpenAL play call to see exactly which sourceIds
                // VS is actually playing, and attach filter at the last possible moment
                if (!PatchALSourcePlay(harmony, api))
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not patch AL.SourcePlay - diagnostic hook unavailable");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to apply patches: {ex.Message}");
                api.Logger.Error($"[SoundPhysicsAdapted] Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Apply lowpass filter using per-sound filter system.
        /// Each sound gets its own OpenAL filter to avoid global thrashing.
        /// </summary>
        private static void ApplyLowPassFilter(ILoadedSound sound, float filterValue, Vec3d soundPos = null, string soundName = null)
        {
            // Use per-sound filter if available
            if (AudioRenderer.IsInitialized)
            {
                AudioRenderer.SetOcclusion(sound, filterValue, soundPos, soundName);
            }
            else
            {
                // Fallback to VS's implementation (global filter, suboptimal)
                sound.SetLowPassfiltering(filterValue);
            }
        }

        /// <summary>
        /// Apply reverb to a sound using SPR-style calculation.
        /// Phase 3: Multi-slot EAX reverb based on ray bouncing.
        /// </summary>
        private static void ApplyReverb(ILoadedSound sound, Vec3d soundPos, Vec3d playerPos, IBlockAccessor blockAccessor)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableCustomReverb) return;
            if (!ReverbEffects.IsInitialized) return;

            try
            {
                // Get OpenAL source ID
                int sourceId = AudioRenderer.GetSourceId(sound);
                if (sourceId <= 0) return;

                // Check if sound source is underwater (fluid layer handles waterlogged blocks)
                BlockPos soundBlockPos = new BlockPos((int)soundPos.X, (int)soundPos.Y, (int)soundPos.Z);
                Block soundBlock = blockAccessor.GetBlock(soundBlockPos, BlockLayersAccess.Fluid);
                bool isSourceUnderwater = soundBlock != null && soundBlock.IsLiquid();

                // Calculate reverb parameters
                var reverbResult = AcousticRaytracer.Calculate(soundPos, playerPos, blockAccessor);

                // Apply to source (with underwater state for both player and source)
                ReverbEffects.ApplyToSource(sourceId, reverbResult, isSourceUnderwater);
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ApplyReverb error: {ex.Message}");
            }
        }

        // Track which sourceIds we've already applied the distance model to,
        // so re-attaches (e.g., after underwater state change) don't double-multiply.
        // OpenAL recycles source IDs, so the entry is overwritten naturally on reuse.
        private static readonly Dictionary<int, int> _distanceModelApplied = new Dictionary<int, int>();
        private static int _distanceModelGen = 0;

        /// <summary>
        /// Apply per-source distance attenuation overrides:
        ///  - SoundRangeMultiplier → AL_MAX_DISTANCE multiplier
        ///  - DistanceRolloffFactor → AL_ROLLOFF_FACTOR multiplier
        ///  - AirAbsorptionFactor → AL_AIR_ABSORPTION_FACTOR (EFX, 0 = vanilla)
        /// Skips music sounds when DistanceModelExcludeMusic is true.
        /// Idempotent per-source: only applies once per Start() to avoid stacking.
        /// </summary>
        private static void ApplyDistanceModel(ILoadedSound sound, int sourceId, string soundName)
        {
            try
            {
                if (sourceId <= 0) return;

                var config = SoundPhysicsAdaptedModSystem.Config;
                if (config == null || !config.Enabled) return;
                if (!config.EnableDistanceModelOverrides) return;

                // Filter by sound type — music tracks are typically non-positional
                // or pre-tuned, leave them alone if user prefers.
                if (config.DistanceModelExcludeMusic)
                {
                    var stype = sound?.Params?.SoundType ?? EnumSoundType.Sound;
                    if (stype == EnumSoundType.Music || stype == EnumSoundType.MusicGlitchunaffected)
                        return;
                }

                // Weather sounds (thunder, rain) use rolloff=0 and manage their own distance
                // model entirely. Air absorption must NOT be applied — thunder sources sit
                // 300-1000 blocks away in world space, so factor=1.0 would kill all HF content,
                // leaving pure bass at full volume (the "bass-boosted" distortion).
                {
                    var stype = sound?.Params?.SoundType ?? EnumSoundType.Sound;
                    if (stype == EnumSoundType.Weather)
                        return;
                }

                // De-dup against re-attachments / our own SoundStartPostfix double-fire.
                // Generation counter rolls every ~1B starts to avoid pathological stale state.
                int gen = _distanceModelGen;
                if (_distanceModelApplied.TryGetValue(sourceId, out int prevGen) && prevGen == gen)
                    return;
                _distanceModelApplied[sourceId] = gen;

                if (!EfxHelper.IsAvailable) return;

                // Range multiplier — affects AL_MAX_DISTANCE.
                // VS sets MaxDistance = SoundParams.Range when starting the source.
                if (config.SoundRangeMultiplier > 0f &&
                    Math.Abs(config.SoundRangeMultiplier - 1.0f) > 0.001f)
                {
                    float curMax = EfxHelper.ALGetSourceMaxDistance(sourceId);
                    if (curMax > 0f && !float.IsNaN(curMax) && !float.IsInfinity(curMax))
                    {
                        float newMax = curMax * config.SoundRangeMultiplier;
                        // Clamp to a sane upper bound — OpenAL accepts huge values but
                        // they cause voice contention and stress the mixer.
                        if (newMax > 4096f) newMax = 4096f;
                        EfxHelper.ALSetSourceMaxDistance(sourceId, newMax);
                    }
                }

                // Rolloff multiplier — affects AL_ROLLOFF_FACTOR (curve steepness).
                if (config.DistanceRolloffFactor > 0f &&
                    Math.Abs(config.DistanceRolloffFactor - 1.0f) > 0.001f)
                {
                    float curRoll = EfxHelper.ALGetSourceRolloff(sourceId);
                    if (curRoll > 0f && !float.IsNaN(curRoll) && !float.IsInfinity(curRoll))
                    {
                        float newRoll = curRoll * config.DistanceRolloffFactor;
                        if (newRoll < 0f) newRoll = 0f;
                        if (newRoll > 10f) newRoll = 10f;
                        EfxHelper.ALSetSourceRolloff(sourceId, newRoll);
                    }
                }

                // Air absorption (EFX). 0 = vanilla. SPR uses ~1.0.
                // Silently no-ops if the OpenAL binding lacks the enum value.
                if (config.AirAbsorptionFactor > 0.001f && EfxHelper.IsAirAbsorptionSupported)
                {
                    EfxHelper.ALSetSourceAirAbsorption(sourceId, config.AirAbsorptionFactor);
                }

                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                {
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[DistModel] {soundName} src={sourceId} rangeMul={config.SoundRangeMultiplier:F2} " +
                        $"rolloffMul={config.DistanceRolloffFactor:F2} airAbs={config.AirAbsorptionFactor:F2}");
                }
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ApplyDistanceModel error: {ex.Message}");
            }
        }

        /// <summary>
        /// UNIVERSAL PREFIX for ClientMain.StartPlaying(AudioData, SoundParams, AssetLocation).
        /// This is the convergence point where BOTH PlaySoundAtInternal and LoadSound paths land.
        /// Swaps stereo AudioData to mono for positional sounds before CreateAudio runs.
        /// 
        /// Sounds spawned via PlaySoundAt(Entity == local player) are tagged at call time
        /// via fingerprint queue, so they stay stereo (no 3D panning, no L/R offset).
        /// Reverb still works on stereo sources via EFX aux sends.
        /// 
        /// Trader/NPC sounds at different entity positions are NOT tagged and
        /// will still be mono-downmixed for proper 3D positional audio.
        /// 
        /// Harmony maps __0 = audiodata (first param). Using ref to allow swapping.
        /// </summary>
        public static void StartPlayingAudioMonoPrefix(ref AudioData __0, SoundParams __1, AssetLocation __2)
        {
            try
            {
                // STARTUP GATE: Skip mono downmix during loading — no audible difference
                if (!SoundPhysicsAdaptedModSystem.IsWorldReady) return;

                var config = SoundPhysicsAdaptedModSystem.Config;
                if (config == null || !config.Enabled) return;

                // Check if this sound was tagged at PlaySoundAt(Entity) call time
                // as originating from the local player entity. Uses exact float
                // position match — immune to player movement during async load.
                if (ConsumeLocalPlayerSoundPosition(__1?.Position))
                {
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[MonoDownmix] SKIP local-player sound: {__2?.ToShortString() ?? "unknown"} — keeping stereo (no 3D panning)");
                    return;
                }

                __0 = MonoDownmixManager.EnsureMono(__0, __1);
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[MonoDownmix] StartPlayingAudio prefix error: {ex.Message}");
            }
        }

        #region Local Player Sound Fingerprinting

        /// <summary>
        /// Position fingerprints of recently-spawned local player entity sounds.
        /// Populated by PlaySoundAt(Entity) prefixes when entity == local player.
        /// Consumed by StartPlayingAudioMonoPrefix via exact float match.
        /// 
        /// Why not distance-based? Between PlaySoundAt() and async StartPlaying(),
        /// the player entity moves. The stored SoundParams.Position is from call time,
        /// but entity.Pos is current — so distance checks fail for fast-moving players.
        /// 
        /// This queue stores the exact (float)position that VS bakes into SoundParams,
        /// computed from the same entity.Pos doubles at the same instant.
        /// Float comparison is exact because both sides do the same double→float cast.
        /// </summary>
        private static readonly List<(float x, float y, float z, long tickMs)> _localPlayerSoundPositions
            = new List<(float, float, float, long)>();
        private const long POSITION_EXPIRY_MS = 2000;
        private const int MAX_POSITION_QUEUE = 32;

        // ---- Local-player occlusion-skip set ----
        // Separate from the consume-on-match fingerprint queue above. This is a
        // non-consuming TTL set keyed by integer block coordinates. Sounds whose
        // world position floors to a recently-tagged block coord are treated as
        // "local player originated" and skip occlusion entirely (only reverb is
        // applied) by AudioPhysicsSystem.ProcessSoundRaycast.
        //
        // Why block-int keys? Sound positions get jittered by VS (block-center,
        // entity offset, sample randomization). Per-block matching is robust
        // against these small offsets and avoids false-negatives for break/place
        // sounds spawned at slightly different exact coords than the player click.
        private static readonly Dictionary<long, long> _localPlayerOcclusionSkip
            = new Dictionary<long, long>();
        private const long OCCLUSION_SKIP_TTL_MS = 3000;
        private const int OCCLUSION_SKIP_MAX = 256;

        private static long PackBlockKey(int x, int y, int z)
        {
            // 21 bits per axis (-1M..+1M block range, plenty for VS world).
            return ((long)(x & 0x1FFFFF)) | (((long)(y & 0x1FFFFF)) << 21) | (((long)(z & 0x1FFFFF)) << 42);
        }

        /// <summary>
        /// Mark a world position (in world coords) as "local-player originated".
        /// AudioPhysicsSystem will skip occlusion calculations for sounds whose
        /// floor() position matches any tagged block within the TTL window.
        /// Reverb is still applied normally.
        /// </summary>
        public static void MarkLocalPlayerSoundPosition(double x, double y, double z)
        {
            int bx = (int)Math.Floor(x);
            int by = (int)Math.Floor(y);
            int bz = (int)Math.Floor(z);
            long now = Environment.TickCount64;

            // Tag a 3x3x3 block neighborhood — the VS sound system can spawn
            // break/place sounds at the block center while the player click
            // hits an adjacent face; this avoids edge misses without going wider.
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                _localPlayerOcclusionSkip[PackBlockKey(bx + dx, by + dy, bz + dz)] = now;
            }

            // Opportunistic prune.
            if (_localPlayerOcclusionSkip.Count > OCCLUSION_SKIP_MAX)
                PruneOcclusionSkip(now);
        }

        private static void PruneOcclusionSkip(long now)
        {
            List<long> stale = null;
            foreach (var kv in _localPlayerOcclusionSkip)
            {
                if (now - kv.Value > OCCLUSION_SKIP_TTL_MS)
                {
                    if (stale == null) stale = new List<long>();
                    stale.Add(kv.Key);
                }
            }
            if (stale != null) foreach (var k in stale) _localPlayerOcclusionSkip.Remove(k);
        }

        /// <summary>
        /// Returns true if this world position falls inside a recently tagged
        /// local-player sound block. Used by AudioPhysicsSystem to skip occlusion.
        /// </summary>
        public static bool IsLocalPlayerSoundPosition(Vec3d pos)
        {
            if (pos == null || _localPlayerOcclusionSkip.Count == 0) return false;
            int bx = (int)Math.Floor(pos.X);
            int by = (int)Math.Floor(pos.Y);
            int bz = (int)Math.Floor(pos.Z);
            long now = Environment.TickCount64;
            long key = PackBlockKey(bx, by, bz);
            if (_localPlayerOcclusionSkip.TryGetValue(key, out long stamp))
            {
                if (now - stamp <= OCCLUSION_SKIP_TTL_MS) return true;
                _localPlayerOcclusionSkip.Remove(key);
            }
            return false;
        }

        /// <summary>
        /// Record that a sound was spawned at the local player entity position.
        /// Called from PlaySoundAt(Entity) / PlaySoundAt(IPlayer) prefix patches.
        /// </summary>
        private static void EnqueueLocalPlayerSoundPosition(float x, float y, float z)
        {
            long now = Environment.TickCount64;

            // Expire old entries
            _localPlayerSoundPositions.RemoveAll(e => now - e.tickMs > POSITION_EXPIRY_MS);
            while (_localPlayerSoundPositions.Count >= MAX_POSITION_QUEUE)
                _localPlayerSoundPositions.RemoveAt(0);

            _localPlayerSoundPositions.Add((x, y, z, now));

            // Also tag for occlusion-skip — covers entity-position sounds the
            // local player emits (footsteps, swing, voice) at any distance from
            // the listener (e.g. third-person eye offset edge cases).
            MarkLocalPlayerSoundPosition(x, y, z);
        }

        /// <summary>
        /// Check if a SoundParams position matches a recently-enqueued local player sound.
        /// Uses exact float equality — safe because both sides compute from the same
        /// entity.Pos doubles with the same double→float cast, on the same thread,
        /// at the same point in time (Harmony prefix on PlaySoundAt).
        /// Consumes (removes) the matched entry.
        /// </summary>
        private static bool ConsumeLocalPlayerSoundPosition(Vec3f position)
        {
            if (position == null || _localPlayerSoundPositions.Count == 0) return false;

            long now = Environment.TickCount64;

            for (int i = _localPlayerSoundPositions.Count - 1; i >= 0; i--)
            {
                var e = _localPlayerSoundPositions[i];

                // Expire stale
                if (now - e.tickMs > POSITION_EXPIRY_MS)
                {
                    _localPlayerSoundPositions.RemoveAt(i);
                    continue;
                }

                // Exact float match — both computed from same entity.Pos doubles
                if (position.X == e.x && position.Y == e.y && position.Z == e.z)
                {
                    _localPlayerSoundPositions.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Patch PlaySoundAt overloads that take Entity or IPlayer parameters.
        /// When the entity/player is the local player, we record the exact position
        /// that VS will bake into SoundParams, so StartPlayingAudioMonoPrefix can
        /// detect and skip mono downmix even after async audio loading.
        /// </summary>
        private static void PatchPlaySoundAtEntityOverloads(Harmony harmony, ICoreClientAPI api)
        {
            int patched = 0;

            try
            {
                // Entity overload 1: (AssetLocation, Entity, IPlayer, float pitch, float range, float volume)
                var method1 = clientMainType.GetMethod("PlaySoundAt",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(AssetLocation), typeof(Entity), typeof(IPlayer), typeof(float), typeof(float), typeof(float) },
                    null);
                if (method1 != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("PlaySoundAtEntityPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(method1, prefix: new HarmonyMethod(prefix));
                    patched++;
                }

                // Entity overload 2: (AssetLocation, Entity, IPlayer, bool randomizePitch, float range, float volume)
                var method2 = clientMainType.GetMethod("PlaySoundAt",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(AssetLocation), typeof(Entity), typeof(IPlayer), typeof(bool), typeof(float), typeof(float) },
                    null);
                if (method2 != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("PlaySoundAtEntityPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(method2, prefix: new HarmonyMethod(prefix));
                    patched++;
                }

                // IPlayer overload: (AssetLocation, IPlayer, IPlayer, bool, float, float)
                var method3 = clientMainType.GetMethod("PlaySoundAt",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(AssetLocation), typeof(IPlayer), typeof(IPlayer), typeof(bool), typeof(float), typeof(float) },
                    null);
                if (method3 != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("PlaySoundAtPlayerPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(method3, prefix: new HarmonyMethod(prefix));
                    patched++;
                }

                // PlaySoundFor: (AssetLocation, IPlayer, float pitch, float range, float volume)
                var method4 = clientMainType.GetMethod("PlaySoundFor",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(AssetLocation), typeof(IPlayer), typeof(float), typeof(float), typeof(float) },
                    null);
                if (method4 != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("PlaySoundForPlayerPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(method4, prefix: new HarmonyMethod(prefix));
                    patched++;
                }

                // World-pos overload: (AssetLocation, double x, double y, double z, IPlayer dualCallByPlayer, bool, float, float)
                // Block break/place sounds use this with dualCallByPlayer = local player.
                // Tag the world position for occlusion-skip so player-originated block
                // sounds are never muffled (only reverb applies).
                var method5 = clientMainType.GetMethod("PlaySoundAt",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(AssetLocation), typeof(double), typeof(double), typeof(double), typeof(IPlayer), typeof(bool), typeof(float), typeof(float) },
                    null);
                if (method5 != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("PlaySoundAtWorldPosDualPrefix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(method5, prefix: new HarmonyMethod(prefix));
                    patched++;
                }

                api.Logger.Notification($"[SoundPhysicsAdapted] Patched {patched} PlaySoundAt/For overloads [LOCAL PLAYER DETECT]");
            }
            catch (Exception ex)
            {
                api.Logger.Warning($"[SoundPhysicsAdapted] PlaySoundAt entity patches failed ({patched} ok): {ex.Message}");
            }
        }

        /// <summary>
        /// PREFIX for PlaySoundAt(AssetLocation, Entity, IPlayer, ...).
        /// Covers both the (Entity, IPlayer, float pitch) and (Entity, IPlayer, bool randomize) overloads.
        /// When entity == local player, enqueues the exact position VS will bake into SoundParams.
        /// 
        /// Harmony: __0 = AssetLocation, __1 = Entity (atEntity).
        /// </summary>
        public static void PlaySoundAtEntityPrefix(AssetLocation __0, Entity __1)
        {
            try
            {
                if (__1 == null || cachedApi == null) return;
                var localEntity = cachedApi.World?.Player?.Entity;
                if (localEntity == null || __1 != localEntity) return;

                // Compute exact same position VS will pass to PlaySoundAtInternal:
                //   x = entity.Pos.X, y = entity.Pos.InternalY + SelectionBox.Y2/2, z = entity.Pos.Z
                float bodyOffset = 0f;
                if (__1.SelectionBox != null)
                    bodyOffset = __1.SelectionBox.Y2 / 2f;
                else if (__1.Properties?.CollisionBoxSize != null)
                    bodyOffset = __1.Properties.CollisionBoxSize.Y / 2f;

                float x = (float)__1.Pos.X;
                float y = (float)(__1.Pos.InternalY + (double)bodyOffset);
                float z = (float)__1.Pos.Z;

                EnqueueLocalPlayerSoundPosition(x, y, z);
            }
            catch { }
        }

        /// <summary>
        /// PREFIX for PlaySoundAt(AssetLocation, IPlayer atPlayer, IPlayer ignore, bool, float, float).
        /// IPlayer overload uses entity position WITHOUT body offset.
        /// When atPlayer == local player (or null, which VS defaults to local player), enqueues position.
        /// 
        /// Harmony: __0 = AssetLocation, __1 = IPlayer (atPlayer).
        /// </summary>
        public static void PlaySoundAtPlayerPrefix(AssetLocation __0, IPlayer __1)
        {
            try
            {
                if (cachedApi == null) return;
                var localPlayer = cachedApi.World?.Player;
                if (localPlayer == null) return;

                // null defaults to local player in VS code
                IPlayer player = __1 ?? localPlayer;
                if (player != localPlayer) return;

                var entity = player.Entity;
                if (entity?.Pos == null) return;

                // IPlayer overload: NO body offset (raw entity.Pos.InternalY)
                float x = (float)entity.Pos.X;
                float y = (float)entity.Pos.InternalY;
                float z = (float)entity.Pos.Z;

                EnqueueLocalPlayerSoundPosition(x, y, z);
            }
            catch { }
        }

        /// <summary>
        /// PREFIX for PlaySoundFor(AssetLocation, IPlayer, float pitch, float range, float volume).
        /// This overload calls PlaySoundAtInternal directly (bypasses PlaySoundAt).
        /// Uses entity position WITHOUT body offset, same as PlaySoundAt(IPlayer).
        /// 
        /// Harmony: __0 = AssetLocation, __1 = IPlayer (forPlayer).
        /// </summary>
        public static void PlaySoundForPlayerPrefix(AssetLocation __0, IPlayer __1)
        {
            try
            {
                if (cachedApi == null) return;
                var localPlayer = cachedApi.World?.Player;
                if (localPlayer == null) return;

                IPlayer player = __1 ?? localPlayer;
                if (player != localPlayer) return;

                var entity = player.Entity;
                if (entity?.Pos == null) return;

                float x = (float)entity.Pos.X;
                float y = (float)entity.Pos.InternalY;
                float z = (float)entity.Pos.Z;

                EnqueueLocalPlayerSoundPosition(x, y, z);
            }
            catch { }
        }

        /// <summary>
        /// PREFIX for PlaySoundAt(AssetLocation, double x, double y, double z, IPlayer dualCallByPlayer, bool, float, float).
        /// This is the world-position overload used by block-break, block-place,
        /// and other player-triggered world sounds. When dualCallByPlayer ==
        /// local player, the local client is the originator (server skips it),
        /// so we tag the world position for occlusion-skip.
        ///
        /// We do NOT enqueue the float fingerprint here because this overload
        /// uses arbitrary world coordinates, not entity positions \u2014 mono downmix
        /// (stereo panning) is still desirable for distant block sounds.
        ///
        /// Harmony: __0 = AssetLocation, __1..__3 = doubles, __4 = IPlayer dualCallByPlayer.
        /// </summary>
        public static void PlaySoundAtWorldPosDualPrefix(
            AssetLocation __0, double __1, double __2, double __3, IPlayer __4)
        {
            try
            {
                if (__4 == null || cachedApi == null) return;
                var localPlayer = cachedApi.World?.Player;
                if (localPlayer == null || __4 != localPlayer) return;

                MarkLocalPlayerSoundPosition(__1, __2, __3);
            }
            catch { }
        }

        #endregion

        /// <summary>
        /// PREFIX for ClientMain.LoadSound() — Legacy mono downmix for explicit requests.
        /// Now simplified: delegates to MonoDownmixManager for explicit ForceMonoNextLoad
        /// and per-asset requests. Auto-detection for positional sounds is handled by
        /// StartPlayingAudioMonoPrefix instead (covers both LoadSound and PlaySoundAt paths).
        /// 
        /// Still needed for the cache-swap mechanism used by weather positional pools
        /// and resonator patches that need mono BEFORE LoadSound creates the buffer.
        /// </summary>
        public static void LoadSoundMonoPrefix(SoundParams sound)
        {
            // Reset any previous swap state
            monoSwapKey = null;
            monoSwapOriginal = null;

            // STARTUP GATE: Skip mono swap during loading
            if (!SoundPhysicsAdaptedModSystem.IsWorldReady) return;

            if (soundAudioDataDict == null) return;

            // Check both mechanisms: thread-local flag (weather) and per-asset set (resonator)
            bool wantMono = false;
            if (MonoDownmixManager.ForceMonoNextLoad)
            {
                MonoDownmixManager.ForceMonoNextLoad = false; // Consume the flag
                wantMono = true;
            }

            // Check per-asset mono request (resonator tracks)
            if (!wantMono && sound?.Location != null)
            {
                wantMono = MonoDownmixManager.CheckAndConsumeMonoRequest(sound.Location);
            }

            if (!wantMono) return;

            try
            {
                // Resolve the asset location the same way LoadSound does
                var location = sound.Location?.Clone();
                if (location == null) return;

                location.WithPathAppendixOnce(".ogg");

                AudioData stereoData = null;

                // Try exact match first
                if (soundAudioDataDict.TryGetValue(location, out stereoData))
                {
                    // found
                }
                else
                {
                    // Try with "game:" domain prefix (VS resolves domains internally)
                    foreach (var kvp in soundAudioDataDict)
                    {
                        if (kvp.Key.Path == location.Path ||
                            kvp.Key.ToString() == location.ToString())
                        {
                            location = kvp.Key;
                            stereoData = kvp.Value;
                            break;
                        }
                    }
                }

                if (stereoData == null)
                {
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog($"MonoPrefix: could not find cached data for '{location}'");
                    return;
                }

                var stereoMeta = stereoData as AudioMetaData;
                if (stereoMeta == null || stereoMeta.Channels != 2)
                {
                    return;
                }

                // Create/get the mono version (now via centralized manager)
                var monoMeta = MonoDownmixManager.GetOrCreateMonoVersion(stereoMeta);
                if (monoMeta == null || monoMeta == stereoMeta) return;

                // Temporarily replace the cache entry with the mono version
                monoSwapKey = location;
                monoSwapOriginal = stereoData;
                soundAudioDataDict[location] = monoMeta;

                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"MonoPrefix: swapped '{location}' to mono ({monoMeta.Channels}ch, Loaded={monoMeta.Loaded})");
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"MonoPrefix error: {ex.Message}");
                monoSwapKey = null;
                monoSwapOriginal = null;
            }
        }

        /// <summary>
        /// Postfix for ClientMain.LoadSound()
        /// </summary>
        public static void LoadSoundPostfix(ILoadedSound __result, SoundParams sound, object __instance)
        {
            // Phase 5B: Restore stereo AudioMetaData in cache after mono swap
            if (monoSwapKey != null && monoSwapOriginal != null && soundAudioDataDict != null)
            {
                try
                {
                    soundAudioDataDict[monoSwapKey] = monoSwapOriginal;
                }
                catch { }
                monoSwapKey = null;
                monoSwapOriginal = null;
            }

            // STARTUP GATE: Skip reflection + occlusion during loading
            if (!SoundPhysicsAdaptedModSystem.IsWorldReady) return;

            try
            {
                if (__result == null) return;

                var config = SoundPhysicsAdaptedModSystem.Config;
                if (config == null || !config.Enabled) return;

                // Check if sound has a position (either from params or stored by ResonatorPatch)
                Vec3d soundPos = null;

                // First try to get stored position (e.g., from ResonatorPatch)
                soundPos = AudioRenderer.GetStoredPosition(__result);

                // Fall back to sound.Position if no stored position
                if (soundPos == null && sound.Position != null)
                {
                    soundPos = new Vec3d(sound.Position.X, sound.Position.Y, sound.Position.Z);
                }

                // Skip if truly non-positional
                if (soundPos == null)
                    return;

                if (soundPos.X == 0 && soundPos.Y == 0 && soundPos.Z == 0)
                    return;

                string soundName = sound.Location?.ToShortString() ?? "unknown";

                // PHASE 5C: Skip thunder/lightning sounds - they have custom audio handling
                // ThunderAudioHandler manages LPF directly, don't let our system double-process
                if (soundName.Contains("lightning"))
                {
                    // Thunder handled by ThunderAudioHandler - no log needed
                    return;
                }

                // Get World via reflection from ClientMain
                var worldProp = clientMainType.GetProperty("World");
                if (worldProp == null) return;
                
                var world = worldProp.GetValue(__instance) as IClientWorldAccessor;
                if (world == null) return;

                // Cache block accessor for SetPosition patches
                cachedBlockAccessor = world.BlockAccessor;

                // Convert Vec3d to Vec3f for ApplyOcclusion
                var soundPosF = new Vec3f((float)soundPos.X, (float)soundPos.Y, (float)soundPos.Z);
                ApplyOcclusion(__result, world, soundPosF, soundName);
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ERROR in LoadSound patch: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix for LoadedSoundNative.SetPosition(Vec3f)
        /// Only stores the updated position - AudioPhysicsSystem handles recalculation.
        /// Phase 4B: Also re-applies repositioned position to prevent VS from overwriting it.
        /// </summary>
        public static void SetPositionPostfix(object __instance, Vec3f position)
        {
            try
            {
                // STARTUP GATE: Skip position tracking during loading
                if (!SoundPhysicsAdaptedModSystem.IsWorldReady) return;

                if (position.X == 0 && position.Y == 0 && position.Z == 0) return;

                var loadedSound = __instance as ILoadedSound;
                if (loadedSound == null) return;

                // Only store position - AcousticsManager will recalculate on its schedule
                AudioRenderer.UpdateStoredPosition(loadedSound, new Vec3d(position.X, position.Y, position.Z));

                // PHASE 4B: Re-apply repositioned position if active
                // VS just called alSource3f(Position, original) which overwrites our override.
                // We must immediately re-set it to the repositioned position.
                AudioRenderer.ReapplyRepositionedPosition(loadedSound);
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ERROR in SetPosition patch: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix for LoadedSoundNative.SetPosition(float x, float y, float z)
        /// Only stores the updated position - AudioPhysicsSystem handles recalculation.
        /// Phase 4B: Also re-applies repositioned position to prevent VS from overwriting it.
        /// </summary>
        public static void SetPositionXYZPostfix(object __instance, float x, float y, float z)
        {
            try
            {
                // STARTUP GATE: Skip position tracking during loading
                if (!SoundPhysicsAdaptedModSystem.IsWorldReady) return;

                if (x == 0 && y == 0 && z == 0) return;

                var loadedSound = __instance as ILoadedSound;
                if (loadedSound == null) return;

                // Only store position - AcousticsManager will recalculate on its schedule
                AudioRenderer.UpdateStoredPosition(loadedSound, new Vec3d(x, y, z));

                // PHASE 4B: Re-apply repositioned position if active
                AudioRenderer.ReapplyRepositionedPosition(loadedSound);
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ERROR in SetPositionXYZ patch: {ex.Message}");
            }
        }

        /// <summary>
        /// PREFIX for LoadedSoundNative.Start()
        /// This is THE critical patch - ALL sounds call Start() which triggers AL.SourcePlay().
        /// MUST be a PREFIX to apply filter BEFORE AL.SourcePlay() executes.
        /// A postfix would allow initial sound samples to play unfiltered (audible "pop").
        /// __instance is the LoadedSoundNative object.
        /// </summary>
        public static void SoundStartPrefix(object __instance)
        {
            try
            {
                // STARTUP GATE: Skip processing until warmup completes.
                // During chunk loading, hundreds of sounds fire Start() simultaneously.
                // Without this gate, reflection calls + DDA raycasts + OpenAL ops
                // block the main thread for 86+ seconds (zero FPS freeze).
                //
                // PRE-WARMUP QUEUE: Block entities (querns, forges, beehives) start their
                // ambient sounds during chunk loading, before IsWorldReady. Queue positional
                // sounds so they can be retroactively registered after warmup completes.
                // Queuing is cheap (just a WeakReference); the expensive DDA runs later.
                if (!SoundPhysicsAdaptedModSystem.IsWorldReady)
                {
                    if (!preWarmupQueueProcessed)
                    {
                        var queueSound = __instance as ILoadedSound;
                        if (queueSound?.Params != null)
                        {
                            var pos = queueSound.Params.Position;
                            bool hasPosition = pos != null && (pos.X != 0 || pos.Y != 0 || pos.Z != 0);
                            if (hasPosition)
                            {
                                preWarmupSoundQueue.Add(new WeakReference<ILoadedSound>(queueSound));
                            }
                        }
                    }
                    return;
                }

                // Cast to ILoadedSound interface
                var loadedSound = __instance as ILoadedSound;
                if (loadedSound == null) return;

                var config = SoundPhysicsAdaptedModSystem.Config;
                if (config == null || !config.Enabled) return;
                if (cachedApi == null) return;

                // Get sound params for position and name
                var soundParams = loadedSound.Params;
                if (soundParams == null) return;

                Vec3f position = soundParams.Position;
                var soundType = soundParams.SoundType;
                string soundName = soundParams.Location?.ToShortString() ?? "unknown";

                // PHASE 5C: Skip thunder/lightning sounds - they have custom audio handling
                // ThunderAudioHandler manages LPF directly, don't let our system double-process
                if (soundName.Contains("lightning"))
                {
                    // Thunder handled by ThunderAudioHandler - no log needed
                    return;
                }

                // Check if this is a music or ambient sound (no position expected)
                bool isMusic = soundType == EnumSoundType.Music || soundType == EnumSoundType.MusicGlitchunaffected;
                bool isNonPositional = position == null || (position.X == 0 && position.Y == 0 && position.Z == 0);

                // For positional sounds, we need a valid position for occlusion
                // If it IS music but HAS a position (like our patched Resonator), we DO want to process it!
                if (isNonPositional && !isMusic)
                {
                    // Only apply underwater filter to ambient/UI sounds if enabled
                    // These can't have occlusion calculated (no position)
                    if (!SoundPhysicsAdaptedModSystem.IsPlayerUnderwater) return;
                    
                    // Apply underwater-only filter (no occlusion)
                    ApplyUnderwaterOnlyFilter(loadedSound, soundName, isMusic: false);
                    return;
                }

                // If it is music AND non-positional, treat as background music
                if (isMusic && isNonPositional)
                {
                    if (!config.UnderwaterFilterAffectsMusic) return;

                    ApplyUnderwaterOnlyFilter(loadedSound, soundName, isMusic: true);
                    return;
                }

                // --- Regular positional sound processing below ---
                // (Includes positional Music now!)

                // Get block accessor if not cached
                if (cachedBlockAccessor == null)
                {
                    cachedBlockAccessor = cachedApi.World?.BlockAccessor;
                    if (cachedBlockAccessor == null) return;
                }

                int sourceId = AudioRenderer.GetSourceId(loadedSound);

                // CRITICAL: First detach VS's global filter from this source
                // This must happen HERE (not in createSoundSource) to avoid race conditions
                // with recycled sourceIds that might still be playing other sounds
                AudioRenderer.DetachGlobalFilter(sourceId);

                // World is ready (gated at top of method) — apply immediate occlusion
                ApplyOcclusion(loadedSound, position, soundName);
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ERROR in SoundStartPrefix: {ex.Message}");
            }
        }

        /// <summary>
        /// POSTFIX for LoadedSoundNative.Start() - Attach filter AND reverb AFTER AL.SourcePlay()
        /// OpenAL filters and aux sends only attach successfully to sources in AL_PLAYING state!
        /// </summary>
        public static void SoundStartPostfix(object __instance)
        {
            try
            {
                // STARTUP GATE: Skip until warmup completes — no filter/reverb work during loading
                if (!SoundPhysicsAdaptedModSystem.IsWorldReady) return;

                var loadedSound = __instance as ILoadedSound;
                if (loadedSound == null) return;

                var config = SoundPhysicsAdaptedModSystem.Config;
                if (config == null || !config.Enabled) return;

                int sourceId = AudioRenderer.GetSourceId(loadedSound);
                if (sourceId <= 0) return;

                string soundName = loadedSound.Params?.Location?.ToShortString() ?? "unknown";

                // PHASE 5C: Skip thunder/lightning sounds - they have custom audio handling
                if (soundName.Contains("lightning"))
                {
                    return;
                }

                // Now that AL.SourcePlay() has run, the source is in PLAYING state
                // Attach our filter - it should work now!
                if (AudioRenderer.IsInitialized)
                {
                    AudioRenderer.ReattachFilter(loadedSound);
                }

                // Apply distance model overrides (range mult, rolloff, air absorption).
                // Source must be in valid state — Postfix runs after AL.SourcePlay so it is.
                ApplyDistanceModel(loadedSound, sourceId, soundName);

                // Apply reverb only after world is fully loaded and warmed up.
                // During loading, tick system will handle reverb with budget limits.
                if (SoundPhysicsAdaptedModSystem.IsWorldReady && config.EnableCustomReverb)
                {
                    if (cachedBlockAccessor == null)
                        cachedBlockAccessor = cachedApi?.World?.BlockAccessor;

                    if (cachedBlockAccessor != null && cachedApi?.World?.Player?.Entity != null)
                    {
                        var player = cachedApi.World.Player.Entity;
                        Vec3d playerPos = player.Pos.XYZ.Add(player.LocalEyePos);

                        var soundParams = loadedSound.Params;
                        Vec3f pos = soundParams?.Position;
                        bool hasPosition = pos != null && (pos.X != 0 || pos.Y != 0 || pos.Z != 0);

                        if (hasPosition)
                        {
                            Vec3d soundPosD = new Vec3d(pos.X, pos.Y, pos.Z);
                            ApplyReverb(loadedSound, soundPosD, playerPos, cachedBlockAccessor);
                        }
                        else
                        {
                            ApplyReverb(loadedSound, playerPos, playerPos, cachedBlockAccessor);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ERROR in SoundStartPostfix: {ex.Message}");
            }
        }
        /// <summary>
        /// FINAL Postfix for ClientMain.StartPlaying - runs AFTER VS applies underwater/reverb effects.
        /// This re-applies our filter to ensure it's not overwritten by VS's post-processing.
        /// </summary>
        public static void StartPlayingFinalPostfix(ILoadedSound loadedSound, AssetLocation location)
        {
            try
            {
                // STARTUP GATE: Skip reflection + filter reattachment during loading
                if (!SoundPhysicsAdaptedModSystem.IsWorldReady) return;

                if (loadedSound == null) return;

                var config = SoundPhysicsAdaptedModSystem.Config;
                if (config == null || !config.Enabled) return;

                // Only re-apply if we have this sound registered
                if (!AudioRenderer.IsInitialized) return;

                // Re-attach our filter - VS may have overwritten it with underwater/reverb effects
                int sourceId = AudioRenderer.GetSourceId(loadedSound);
                if (sourceId == 0) return;

                string soundName = location?.ToShortString() ?? "unknown";

                // Force re-attach the filter we already set up in SoundStartPrefix
                if (AudioRenderer.ReattachFilter(loadedSound))
                {
                    // Filter reattached - no log (fires on every sound start)
                }
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ERROR in StartPlayingFinal patch: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix for LoadedSoundNative.createSoundSource - Detach VS's global filter.
        /// VS attaches a shared global filter here. We must detach it so our per-sound
        /// filters work correctly.
        /// </summary>
        public static void CreateSoundSourcePostfix(object __instance)
        {
            try
            {
                // STARTUP GATE: Skip reflection + OpenAL calls during loading
                if (!SoundPhysicsAdaptedModSystem.IsWorldReady) return;

                var config = SoundPhysicsAdaptedModSystem.Config;
                if (config == null || !config.Enabled) return;

                // Get the sourceId that was just created
                var loadedSound = __instance as ILoadedSound;
                if (loadedSound == null) return;

                int sourceId = AudioRenderer.GetSourceId(loadedSound);
                if (sourceId <= 0) return;

                // Detach VS's global filter from this source
                AudioRenderer.DetachGlobalFilter(sourceId);
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ERROR in CreateSoundSourcePostfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix for LoadedSoundNative.SetLowPassfiltering - blocks vanilla underwater lowpass.
        /// Returns false to skip the original method when ReplaceVanillaLowpass is enabled.
        /// NOTE: This only blocks lowpass (underwater), NOT reverb (uses separate EFX system).
        /// </summary>
        public static bool SetLowPassFilteringPrefix(float value)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;

            // If mod disabled or vanilla lowpass allowed, let vanilla handle it
            if (config == null || !config.Enabled || !config.ReplaceVanillaLowpass)
                return true; // Run original method

            // Block vanilla lowpass calls - our mod handles underwater filtering
            // No log - fires constantly when player is underwater

            return false; // Skip original method
        }

        /// <summary>
        /// Prefix for LoadedSoundNative.SetPitchOffset - blocks vanilla underwater pitch.
        /// Vanilla applies -0.15 to -0.2 pitch offset underwater.
        /// We block this and apply our own configurable value via SoundFilterManager.
        /// __instance is the LoadedSoundNative object.
        /// </summary>
        public static bool SetPitchOffsetPrefix(float val, object __instance)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;

            // If mod disabled or vanilla lowpass allowed (pitch goes with it), let vanilla handle it
            if (config == null || !config.Enabled || !config.ReplaceVanillaLowpass)
                return true; // Run original method

            // Block vanilla pitch offset - our mod handles it

            // Apply our own pitch offset instead
            var loadedSound = __instance as ILoadedSound;
            if (loadedSound != null)
            {
                // Check if this is a music sound - music needs special handling
                bool isMusic = false;
                try
                {
                    var soundType = loadedSound.Params?.SoundType;
                    isMusic = soundType == EnumSoundType.Music ||
                              soundType == EnumSoundType.MusicGlitchunaffected;
                }
                catch { }

                float ourPitch;
                if (isMusic && !config.UnderwaterPitchAffectsMusic)
                {
                    // Music with pitch disabled: always apply 0 to preserve base pitch.
                    // Without this check, a race condition occurs on water exit:
                    // VS calls SetPitchOffset(-0.15) on music before our tick updates
                    // isPlayerUnderwater to false — we'd apply -0.15 to music here,
                    // then RecalculateAllUnderwater skips the reset (UnderwaterPitchAffectsMusic=false),
                    // leaving music permanently pitched down.
                    ourPitch = 0f;
                }
                else
                {
                    // Get our configured pitch offset (only non-zero if underwater)
                    ourPitch = SoundPhysicsAdaptedModSystem.GetUnderwaterPitchOffset();
                }

                // Apply directly via OpenAL (bypass the blocked method)
                AudioRenderer.ApplyPitchOffset(loadedSound, ourPitch);
            }

            return false; // Skip original method
        }

        /// <summary>
        /// Apply initial occlusion filter to a sound (on load).
        /// Reverb is NOT applied here - SoundStartPostfix handles it after AL.SourcePlay().
        /// </summary>
        private static void ApplyOcclusion(ILoadedSound sound, IClientWorldAccessor world, Vec3f soundPos, string soundName)
        {
            var player = world.Player?.Entity;
            if (player == null) return;

            Vec3d playerPos = player.Pos.XYZ.Add(player.LocalEyePos);
            Vec3d soundPosD = new Vec3d(soundPos.X, soundPos.Y, soundPos.Z);

            // Adjust sound position for multi-block sources (e.g. doors) before initial occlusion
            soundPosD = SoundSourceAdjuster.Adjust(soundPosD, world.BlockAccessor);

            float occlusion = OcclusionCalculator.Calculate(soundPosD, playerPos, world.BlockAccessor);

            if (occlusion <= 0)
            {
                ApplyLowPassFilter(sound, 1.0f, soundPosD, soundName);
            }
            else
            {
                float filterValue = OcclusionCalculator.OcclusionToFilter(occlusion);
                ApplyLowPassFilter(sound, filterValue, soundPosD, soundName);

                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"PATCHED: {soundName} pos=({soundPos.X:F0},{soundPos.Y:F0},{soundPos.Z:F0}) " +
                    $"occlusion={occlusion:F2} filter={filterValue:F3}"
                );
            }
        }

        /// <summary>
        /// Apply initial occlusion using cached block accessor (for SoundStartPrefix).
        /// Reverb is NOT applied here - SoundStartPostfix handles it after AL.SourcePlay().
        /// AudioPhysicsSystem handles all subsequent recalculations.
        /// </summary>
        private static void ApplyOcclusion(ILoadedSound sound, Vec3f soundPos, string soundName)
        {
            if (cachedApi == null || cachedBlockAccessor == null) return;

            var player = cachedApi.World?.Player?.Entity;
            if (player == null) return;

            Vec3d playerPos = player.Pos.XYZ.Add(player.LocalEyePos);
            Vec3d soundPosD = new Vec3d(soundPos.X, soundPos.Y, soundPos.Z);

            // Adjust sound position for multi-block sources (e.g. doors) before initial occlusion
            soundPosD = SoundSourceAdjuster.Adjust(soundPosD, cachedBlockAccessor);

            float occlusion = OcclusionCalculator.Calculate(soundPosD, playerPos, cachedBlockAccessor);

            if (occlusion <= 0)
            {
                ApplyLowPassFilter(sound, 1.0f, soundPosD, soundName);
            }
            else
            {
                float filterValue = OcclusionCalculator.OcclusionToFilter(occlusion);
                ApplyLowPassFilter(sound, filterValue, soundPosD, soundName);

                if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"INIT: {soundName} occ={occlusion:F2} filt={filterValue:F3}");
            }
        }

        /// <summary>
        /// Apply underwater filter only (no occlusion) for non-positional sounds like music.
        /// Uses the isMusic parameter to check config.UnderwaterFilterAffectsMusic.
        /// ALWAYS registers the sound so it can be updated when player enters/exits water.
        /// </summary>
        private static void ApplyUnderwaterOnlyFilter(ILoadedSound sound, string soundName, bool isMusic)
        {
            if (!AudioRenderer.IsInitialized) return;

            // Get underwater multiplier based on sound type
            float underwaterMult = SoundPhysicsAdaptedModSystem.GetUnderwaterMultiplier(isMusic);

            // ALWAYS register/set the occlusion for this sound
            // If not underwater, filter is 1.0 (no effect) but sound is still tracked
            // This way RecalculateAllUnderwater can update it when player enters water
            AudioRenderer.SetOcclusion(sound, underwaterMult, null, soundName);

            // Apply pitch offset if underwater and allowed for this sound type
            if (SoundPhysicsAdaptedModSystem.IsPlayerUnderwater)
            {
                var config = SoundPhysicsAdaptedModSystem.Config;
                if (!isMusic || (config != null && config.UnderwaterPitchAffectsMusic))
                {
                    float pitchOffset = SoundPhysicsAdaptedModSystem.GetUnderwaterPitchOffset();
                    if (pitchOffset != 0f)
                    {
                        AudioRenderer.ApplyPitchOffset(sound, pitchOffset);
                    }
                }
            }

            // No per-sound log - underwater state changes are logged at transition only
        }

        /// <summary>
        /// Process sounds that were queued during the warmup window.
        /// Called once from the smoothing tick when warmup completes.
        /// Retroactively applies initial occlusion + registers these sounds so the
        /// tick system can maintain them going forward.
        /// </summary>
        public static void ProcessPreWarmupQueue()
        {
            if (preWarmupQueueProcessed) return;
            preWarmupQueueProcessed = true;

            // Ensure block accessor is available for occlusion raycasts
            if (cachedBlockAccessor == null && cachedApi != null)
                cachedBlockAccessor = cachedApi.World?.BlockAccessor;

            int processed = 0;
            int disposed = 0;

            foreach (var weakRef in preWarmupSoundQueue)
            {
                if (!weakRef.TryGetTarget(out var sound)) { disposed++; continue; }

                try
                {
                    var soundParams = sound.Params;
                    if (soundParams == null) { disposed++; continue; }

                    Vec3f position = soundParams.Position;
                    if (position == null || (position.X == 0 && position.Y == 0 && position.Z == 0))
                    { disposed++; continue; }

                    string soundName = soundParams.Location?.ToShortString() ?? "unknown";

                    // Skip lightning (handled by ThunderAudioHandler)
                    if (soundName.Contains("lightning")) continue;

                    // Detach VS's global filter
                    int sourceId = AudioRenderer.GetSourceId(sound);
                    AudioRenderer.DetachGlobalFilter(sourceId);

                    // Apply initial occlusion (registers the sound in activeFilters)
                    ApplyOcclusion(sound, position, soundName);

                    // Reattach our filter (sound is already in PLAYING state)
                    if (AudioRenderer.IsInitialized)
                    {
                        AudioRenderer.ReattachFilter(sound);
                    }

                    // Apply reverb if enabled
                    var config = SoundPhysicsAdaptedModSystem.Config;
                    if (config != null && config.EnableCustomReverb && cachedBlockAccessor != null
                        && cachedApi?.World?.Player?.Entity != null)
                    {
                        var player = cachedApi.World.Player.Entity;
                        Vec3d playerPos = player.Pos.XYZ.Add(player.LocalEyePos);
                        Vec3d soundPosD = new Vec3d(position.X, position.Y, position.Z);
                        ApplyReverb(sound, soundPosD, playerPos, cachedBlockAccessor);
                    }

                    processed++;
                }
                catch (Exception ex)
                {
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog($"[PRE-WARMUP] Error processing queued sound: {ex.Message}");
                }
            }

            if (processed > 0 || disposed > 0)
            {
                cachedApi?.Logger.Notification(
                    $"[SoundPhysicsAdapted] Pre-warmup queue: {processed} sounds registered, {disposed} already disposed");
            }

            preWarmupSoundQueue.Clear();
        }

        #region AL.SourcePlay Diagnostic Hook

        // Track which sourceIds we've seen played vs which we've registered
        private static HashSet<int> playedSourceIds = new HashSet<int>();
        private static HashSet<int> registeredSourceIds = new HashSet<int>();
        private static object sourceTrackLock = new object();

        // FREEZE DIAGNOSTIC: Track HandleSourcePlay call frequency
        private static int _diagHookCallCount = 0;
        private static int _diagHookUntrackedCount = 0;

        // Cached reflection for attaching filter in hook
        private static MethodInfo alSourceMethod_Hook;
        private static object efxDirectFilterValue_Hook;

        /// <summary>
        /// Patch OpenTK's AL.SourcePlay to intercept all sound play calls.
        /// This is the lowest level hook possible before native OpenAL.
        /// </summary>
        private static bool PatchALSourcePlay(Harmony harmony, ICoreClientAPI api)
        {
            try
            {
                // Find OpenTK's AL class
                Type alType = null;
                Type alSourceiType = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("OpenTK")) continue;

                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "AL" && type.Namespace?.Contains("OpenAL") == true)
                        {
                            alType = type;
                        }
                        else if (type.Name == "ALSourcei" && type.IsEnum)
                        {
                            alSourceiType = type;
                        }
                    }

                    if (alType != null) break;
                }

                if (alType == null)
                {
                    api.Logger.Debug("[SoundPhysicsAdapted] AL type not found for SourcePlay hook");
                    return false;
                }

                // Cache AL.Source method for attaching filter in the hook
                if (alSourceiType != null)
                {
                    try
                    {
                        efxDirectFilterValue_Hook = Enum.Parse(alSourceiType, "EfxDirectFilter");
                    }
                    catch
                    {
                        efxDirectFilterValue_Hook = Enum.ToObject(alSourceiType, 0x20005);
                    }

                    alSourceMethod_Hook = alType.GetMethod("Source",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new Type[] { typeof(int), alSourceiType, typeof(int) },
                        null);
                }

                // Log ALL SourcePlay variants available
                api.Logger.Debug("[SoundPhysicsAdapted] Available AL.SourcePlay methods:");
                foreach (var method in alType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == "SourcePlay")
                    {
                        var parms = string.Join(", ", Array.ConvertAll(method.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                        api.Logger.Debug($"  - SourcePlay({parms})");
                    }
                }

                bool anyPatched = false;

                // Try AL.SourcePlay(int)
                var sourcePlayInt = alType.GetMethod("SourcePlay",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int) },
                    null);

                if (sourcePlayInt != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("ALSourcePlayPrefix_Int",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(sourcePlayInt, prefix: new HarmonyMethod(prefix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched AL.SourcePlay(int)");
                    anyPatched = true;
                }

                // Try AL.SourcePlay(uint)
                var sourcePlayUint = alType.GetMethod("SourcePlay",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(uint) },
                    null);

                if (sourcePlayUint != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("ALSourcePlayPrefix_UInt",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(sourcePlayUint, prefix: new HarmonyMethod(prefix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched AL.SourcePlay(uint)");
                    anyPatched = true;
                }

                // Try AL.SourcePlay(int ns, int[] sids) - batch play array
                var sourcePlayBatchArray = alType.GetMethod("SourcePlay",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int), typeof(int[]) },
                    null);

                if (sourcePlayBatchArray != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("ALSourcePlayPrefix_BatchArray",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(sourcePlayBatchArray, prefix: new HarmonyMethod(prefix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched AL.SourcePlay(int ns, int[] sids)");
                    anyPatched = true;
                }

                // Try AL.SourcePlay(int ns, ref int sids) - batch play ref
                var sourcePlayBatchRef = alType.GetMethod("SourcePlay",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int), typeof(int).MakeByRefType() },
                    null);

                if (sourcePlayBatchRef != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("ALSourcePlayPrefix_BatchRef",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(sourcePlayBatchRef, prefix: new HarmonyMethod(prefix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched AL.SourcePlay(int ns, ref int sids)");
                    anyPatched = true;
                }

                // Try ReadOnlySpan variant - need to find the type first
                Type spanType = typeof(ReadOnlySpan<int>);
                var sourcePlaySpan = alType.GetMethod("SourcePlay",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { spanType },
                    null);

                if (sourcePlaySpan != null)
                {
                    var prefix = typeof(LoadSoundPatch).GetMethod("ALSourcePlayPrefix_Span",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(sourcePlaySpan, prefix: new HarmonyMethod(prefix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Patched AL.SourcePlay(ReadOnlySpan<int>)");
                    anyPatched = true;
                }

                if (!anyPatched)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not patch any AL.SourcePlay variant!");
                }

                return anyPatched;
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to patch AL.SourcePlay: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Common logic for all SourcePlay hooks
        /// </summary>
        private static void HandleSourcePlay(int sid, string variant)
        {
            try
            {
                // STARTUP GATE: Skip lock + reflection + tracking during loading.
                // AL.SourcePlay fires on EVERY sound including UI/menu — hundreds during chunk load.
                if (!SoundPhysicsAdaptedModSystem.IsWorldReady) return;

                // FREEZE DIAGNOSTIC: Count every call
                System.Threading.Interlocked.Increment(ref _diagHookCallCount);

                // Track this sourceId as "actually played"
                lock (sourceTrackLock)
                {
                    playedSourceIds.Add(sid);
                }

                // Check if this sourceId is one we've registered a filter for
                bool isTracked = AudioRenderer.IsSourceTracked(sid);

                if (isTracked)
                {
                    // Good - we know about this source, get its filter and reattach right now
                    int filterId = AudioRenderer.GetFilterForSource(sid);
                    if (filterId > 0 && alSourceMethod_Hook != null && efxDirectFilterValue_Hook != null)
                    {
                        // Attach filter RIGHT before play - most reliable timing possible
                        alSourceMethod_Hook.Invoke(null, new object[] { sid, efxDirectFilterValue_Hook, filterId });
                    }
                }
                else
                {
                    // FREEZE DIAGNOSTIC: Track untracked source plays (e.g. altoflute retry loop)
                    System.Threading.Interlocked.Increment(ref _diagHookUntrackedCount);
                }
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"HOOK[{variant}] ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// PREFIX for AL.SourcePlay(int) - intercepts OpenAL play calls.
        /// </summary>
        public static void ALSourcePlayPrefix_Int(int sid)
        {
            HandleSourcePlay(sid, "int");
        }

        /// <summary>
        /// PREFIX for AL.SourcePlay(uint) - intercepts OpenAL play calls.
        /// </summary>
        public static void ALSourcePlayPrefix_UInt(uint sid)
        {
            HandleSourcePlay((int)sid, "uint");
        }

        /// <summary>
        /// PREFIX for AL.SourcePlay(int ns, int[] sids) - batch play array.
        /// Parameter names MUST match OpenTK's names exactly!
        /// </summary>
        public static void ALSourcePlayPrefix_BatchArray(int ns, int[] sids)
        {
            if (sids == null) return;
            for (int i = 0; i < ns && i < sids.Length; i++)
            {
                HandleSourcePlay(sids[i], "batch[]");
            }
        }

        /// <summary>
        /// PREFIX for AL.SourcePlay(int ns, ref int sids) - batch play ref variant.
        /// Parameter names MUST match OpenTK's names exactly!
        /// </summary>
        public static void ALSourcePlayPrefix_BatchRef(int ns, ref int sids)
        {
            // This is a ref to first element, we can only access the first one safely here
            // But ns tells us how many - for now just log the first
            HandleSourcePlay(sids, $"batch&[{ns}]");
        }

        /// <summary>
        /// PREFIX for AL.SourcePlay(ReadOnlySpan) - span variant.
        /// </summary>
        public static void ALSourcePlayPrefix_Span(ReadOnlySpan<int> sources)
        {
            for (int i = 0; i < sources.Length; i++)
            {
                HandleSourcePlay(sources[i], "span");
            }
        }

        // Keep old method name for compatibility
        public static void ALSourcePlayPrefix(int sid)
        {
            HandleSourcePlay(sid, "legacy");
        }

        /// <summary>
        /// Register a sourceId as tracked by our system.
        /// Called from SoundFilterManager when we register a sound.
        /// </summary>
        public static void TrackSourceId(int sourceId)
        {
            lock (sourceTrackLock)
            {
                registeredSourceIds.Add(sourceId);
            }
        }

        /// <summary>
        /// Unregister a sourceId when sound is disposed.
        /// </summary>
        public static void UntrackSourceId(int sourceId)
        {
            lock (sourceTrackLock)
            {
                registeredSourceIds.Remove(sourceId);
                playedSourceIds.Remove(sourceId);
            }
        }

        /// <summary>
        /// Get diagnostic stats about tracked vs played sources.
        /// </summary>
        public static string GetSourceTrackingStats()
        {
            lock (sourceTrackLock)
            {
                int tracked = registeredSourceIds.Count;
                int played = playedSourceIds.Count;
                int untracked = 0;

                foreach (var id in playedSourceIds)
                {
                    if (!registeredSourceIds.Contains(id))
                        untracked++;
                }

                return $"Registered={tracked}, Played={played}, UntrackedPlays={untracked}";
            }
        }

        /// <summary>
        /// FREEZE DIAGNOSTIC: Get and reset hook call count since last query.
        /// </summary>
        public static int DiagGetAndResetHookCount()
        {
            return System.Threading.Interlocked.Exchange(ref _diagHookCallCount, 0);
        }

        /// <summary>
        /// FREEZE DIAGNOSTIC: Get and reset untracked source play count since last query.
        /// High values here indicate VS retry loops (e.g. altoflute.ogg InvalidValue).
        /// </summary>
        public static int DiagGetAndResetUntrackedCount()
        {
            return System.Threading.Interlocked.Exchange(ref _diagHookUntrackedCount, 0);
        }

        #endregion
    }
}
