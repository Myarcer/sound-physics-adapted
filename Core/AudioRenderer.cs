using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Manages unique OpenAL filters for each sound source.
    /// Solves the "global filter thrashing" problem where VS uses one filter for all sounds.
    /// </summary>
    public static class AudioRenderer
    {
        private class FilterEntry
        {
            public int FilterId;        // Our custom OpenAL filter
            public int SourceId;        // OpenAL source ID from LoadedSoundNative
            public float CurrentValue;  // Current filter GAINHF value (what's applied now)
            public float TargetValue;   // Target filter value (what we're smoothing toward)
            public bool IsAttached;     // Have we attached filter to source?
            public WeakReference<ILoadedSound> SoundRef;  // Weak ref to detect disposal
            public Vec3d LastPosition;  // Last known sound position for recalculation
            public string SoundName;    // For debug logging

            // PHASE 4B: Repositioned position smoothing
            // Target is set by ApplySoundPath(); SmoothAll() lerps Current toward it.
            // SetPosition postfixes re-apply CurrentRepositionedPos to prevent VS overwrite.
            public Vec3d TargetRepositionedPos;   // null = no active repositioning target
            public Vec3d CurrentRepositionedPos;   // null = not yet smoothing; the interpolated value
            public Vec3d OriginalSoundPos;         // actual source pos, for reset lerp

            // Resonator fix: MusicGlitchunaffected sounds that are actually positional world sounds
            // When true, RecalculateAllUnderwater treats as non-music (applies underwater filter)
            public bool TreatAsPositional;          // false by default
        }

        // === Smoothing runs on its own 25ms tick, decoupled from raycast intervals ===
        // Convergence = time to reach 95% of target
        // Math: factor = 1 - 0.05^(1 / (convergenceTime / tickInterval))
        private const float SMOOTH_TICK_MS = 25f;

        // Muffling (sound disappearing behind wall): moderately fast
        // Not instant - diffraction causes gradual fade in reality
        // 0.25s convergence at 25ms tick = 10 ticks: factor = 1 - 0.05^(1/10) = 0.259
        private const float SMOOTH_FACTOR_DOWN = 0.259f;

        // Un-muffling (coming around corner): slower to avoid jarring pop-in
        // Humans notice sound appearing more than disappearing
        // 0.4s convergence at 25ms tick = 16 ticks: factor = 1 - 0.05^(1/16) = 0.172
        private const float SMOOTH_FACTOR_UP = 0.172f;

        // Snap threshold: if difference > this, apply instantly (no smoothing)
        // Handles cases like teleporting or large geometry changes
        private const float SNAP_THRESHOLD = 0.6f;

        // Convergence threshold: stop smoothing when close enough
        private const float CONVERGE_EPSILON = 0.002f;

        // PHASE 4B: Position smoothing constants
        // Speed of sound cap: 343 m/s; at 25ms tick = ~8.6m per tick max
        // SPP uses 17.15 m/tick at 50ms MC tick (same effective speed)
        private const float POS_MAX_SPEED_PER_TICK = 8.6f;
        // Position convergence: exponential factor per 25ms tick
        // 0.3 gives ~0.25s convergence (fast but smooth, matches occlusion feel)
        private const float POS_SMOOTH_FACTOR = 0.3f;
        // Position snap: if target jumps more than this, snap instantly (teleport/new sound)
        private const float POS_SNAP_THRESHOLD = 15.0f;
        // Position converge epsilon: stop updating when within this distance (meters)
        private const float POS_CONVERGE_EPSILON = 0.02f;

        // Track filters by sound instance
        private static ConcurrentDictionary<ILoadedSound, FilterEntry> activeFilters
            = new ConcurrentDictionary<ILoadedSound, FilterEntry>();

        // Reflection for getting sourceId from LoadedSoundNative
        private static FieldInfo sourceIdField;
        private static Type loadedSoundNativeType;

        // Reflection for AL.Source to attach filter
        private static MethodInfo alSourceMethod;
        private static object efxDirectFilterValue;

        // OPTIMIZATION: Cached PropertyInfo for IsDisposed check (avoids GetProperty per sound)
        private static PropertyInfo isDisposedProperty;
        private static bool isDisposedPropertyChecked = false;

        // Stats
        private static int totalFiltersCreated = 0;
        private static int totalFiltersDeleted = 0;
        private static bool loggedOnce = false;
        private static int smoothLogAccumulator = 0;

        public static bool IsInitialized { get; private set; } = false;
        public static int ActiveFilterCount => activeFilters.Count;

        /// <summary>
        /// Check if a sound is registered in the active filters pipeline.
        /// </summary>
        public static bool IsRegistered(ILoadedSound sound)
        {
            return sound != null && activeFilters.ContainsKey(sound);
        }

        /// <summary>
        /// Mark a sound as positional even if its SoundType is Music/MusicGlitchunaffected.
        /// This ensures RecalculateAllUnderwater applies the non-music underwater multiplier.
        /// Used for Resonator sounds which are MusicGlitchunaffected but play from a block position.
        /// </summary>
        public static void MarkAsPositional(ILoadedSound sound)
        {
            if (sound != null && activeFilters.TryGetValue(sound, out var entry))
            {
                entry.TreatAsPositional = true;
            }
        }

        /// <summary>
        /// Initialize the manager. Must be called after EfxHelper.Initialize().
        /// </summary>
        public static bool Initialize(Type loadedSoundType, ICoreClientAPI api)
        {
            if (!EfxHelper.IsAvailable)
            {
                api.Logger.Warning("[AudioRenderer] EfxHelper not available");
                return false;
            }

            try
            {
                loadedSoundNativeType = loadedSoundType;

                // Get sourceId field from LoadedSoundNative
                sourceIdField = loadedSoundNativeType.GetField("sourceId",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (sourceIdField == null)
                {
                    // Try other possible field names
                    sourceIdField = loadedSoundNativeType.GetField("SourceId",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                }

                if (sourceIdField == null)
                {
                    // List all fields for debugging
                    api.Logger.Debug("[AudioRenderer] sourceId field not found. Available fields:");
                    foreach (var field in loadedSoundNativeType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                    {
                        api.Logger.Debug($"  - {field.Name} ({field.FieldType.Name})");
                    }
                    api.Logger.Warning("[AudioRenderer] Cannot find sourceId field in LoadedSoundNative");
                    return false;
                }

                api.Logger.Debug($"[AudioRenderer] Found sourceId field: {sourceIdField.Name}");

                // Get AL.Source method for attaching filters
                if (!SetupAlSource(api))
                {
                    api.Logger.Warning("[AudioRenderer] Could not setup AL.Source - filters won't attach");
                    return false;
                }

                IsInitialized = true;
                api.Logger.Notification($"[AudioRenderer] Initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[AudioRenderer] Initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Setup reflection for AL.Source(int source, ALSourcei param, int value)
        /// </summary>
        private static bool SetupAlSource(ICoreClientAPI api)
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
                            api.Logger.Debug($"[SoundFilterManager] Found AL type: {type.FullName}");
                        }
                        else if (type.Name == "ALSourcei" && type.IsEnum)
                        {
                            alSourceiType = type;
                            api.Logger.Debug($"[SoundFilterManager] Found ALSourcei: {type.FullName}");
                        }
                    }

                    if (alType != null && alSourceiType != null) break;
                }

                if (alType == null || alSourceiType == null)
                {
                    api.Logger.Debug("[SoundFilterManager] AL or ALSourcei type not found");
                    return false;
                }

                // Get EfxDirectFilter enum value
                try
                {
                    efxDirectFilterValue = Enum.Parse(alSourceiType, "EfxDirectFilter");
                }
                catch
                {
                    // Try numeric value (EfxDirectFilter = 0x20005 in OpenAL)
                    efxDirectFilterValue = Enum.ToObject(alSourceiType, 0x20005);
                }
                // Log both name and numeric value for debugging
                int numericValue = Convert.ToInt32(efxDirectFilterValue);
                api.Logger.Debug($"[SoundFilterManager] EfxDirectFilter: {efxDirectFilterValue} (numeric=0x{numericValue:X} = {numericValue}, expected=0x20005 = 131077)");

                // Get AL.Source(int, ALSourcei, int) method
                alSourceMethod = alType.GetMethod("Source",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int), alSourceiType, typeof(int) },
                    null);

                if (alSourceMethod == null)
                {
                    api.Logger.Debug("[SoundFilterManager] AL.Source method not found");
                    return false;
                }

                api.Logger.Debug($"[SoundFilterManager] Found AL.Source: {alSourceMethod}");
                return true;
            }
            catch (Exception ex)
            {
                api.Logger.Debug($"[SoundFilterManager] SetupAlSource failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get OpenAL source ID from a LoadedSoundNative instance.
        /// </summary>
        public static int GetSourceId(ILoadedSound sound)
        {
            if (sourceIdField == null || sound == null)
                return 0;

            try
            {
                return (int)sourceIdField.GetValue(sound);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Get validated OpenAL source ID, checking for VS sourceId recycling.
        /// Returns null if the sourceId has been recycled to a different sound.
        /// Use this before applying effects to prevent cross-contamination.
        /// </summary>
        public static int? GetValidatedSourceId(ILoadedSound sound)
        {
            if (sound == null) return null;

            int currentSourceId = GetSourceId(sound);
            if (currentSourceId <= 0) return null;

            if (!activeFilters.TryGetValue(sound, out var entry))
                return null;

            // Check if the sourceId in our records matches what the sound currently has.
            // If mismatched, VS recycled this sourceId to a different sound.
            if (currentSourceId != entry.SourceId)
            {
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[STALE] GetValidatedSourceId: {entry.SoundName} stored={entry.SourceId} current={currentSourceId}");
                return null;
            }

            return currentSourceId;
        }

        /// <summary>
        /// Register a sound and create its filter.
        /// Call this when a new sound is loaded.
        /// </summary>
        public static bool RegisterSound(ILoadedSound sound)
        {
            if (!IsInitialized || sound == null)
                return false;

            // Already registered?
            if (activeFilters.ContainsKey(sound))
                return true;

            try
            {
                int sourceId = GetSourceId(sound);
                if (sourceId == 0)
                {
                    SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] Cannot get sourceId for sound");
                    return false;
                }

                // Create a new OpenAL filter for this sound
                int filterId = EfxHelper.GenFilter();
                if (filterId == 0)
                {
                    SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] Failed to create filter");
                    return false;
                }

                // NOTE: Do NOT configure or attach filter here!
                // Filter will be configured with correct value AND attached in SetOcclusion
                // This prevents the race where sound plays with gainHF=1.0 before SetOcclusion runs

                // CRITICAL: Before registering, invalidate position state on ANY old entries
                // sharing this sourceId. When VS recycles a sourceId for a new sound, stale
                // FilterEntries from finished sounds (not yet GC'd) still have
                // CurrentRepositionedPos set. SmoothAll() then overwrites the new sound's
                // correct AL position with the old entry's stale repositioned position.
                // This caused intermittent panning bugs (thud sounds playing from
                // grasshopper's repositioned position because they shared a sourceId).
                foreach (var kvp in activeFilters)
                {
                    if (kvp.Value.SourceId == sourceId && kvp.Key != sound)
                    {
                        kvp.Value.CurrentRepositionedPos = null;
                        kvp.Value.TargetRepositionedPos = null;
                        kvp.Value.OriginalSoundPos = null;
                    }
                }

                // Track it
                var entry = new FilterEntry
                {
                    FilterId = filterId,
                    SourceId = sourceId,
                    CurrentValue = 1.0f,
                    TargetValue = 1.0f,
                    IsAttached = true,
                    SoundRef = new WeakReference<ILoadedSound>(sound)
                };

                activeFilters[sound] = entry;
                totalFiltersCreated++;



                if (!loggedOnce)
                {
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[SoundFilterManager] Registered sound: sourceId={sourceId}, filterId={filterId}");
                    loggedOnce = true;
                }

                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] RegisterSound failed: {ex.Message}");
                return false;
            }
        }

        // Cached method for AL.Source(int, ALSourcef, float) - for gain/pitch manipulation
        private static MethodInfo alSourceFloatMethod;
        private static object alSourcefGainValue;
        private static object alSourcefPitchValue;
        private static bool alSourceFloatInitialized = false;

        // PHASE 4B: Cached method for AL.Source(int, ALSource3f, float, float, float) - for position
        private static MethodInfo alSource3fMethod;
        private static object alSource3fPositionValue;
        private static bool alSource3fInitialized = false;

        /// <summary>
        /// Attach filter to source using AL.Source.
        /// Public for use by weather audio system (Phase 5A) which manages its own filters.
        /// </summary>
        public static bool AttachFilter(int sourceId, int filterId)
        {
            if (alSourceMethod == null || efxDirectFilterValue == null)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] AttachFilter FAILED: alSourceMethod={alSourceMethod != null}, efxDirectFilterValue={efxDirectFilterValue != null}");
                return false;
            }

            if (sourceId <= 0 || filterId <= 0)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] AttachFilter INVALID IDs: source={sourceId}, filter={filterId}");
                return false;
            }

            try
            {
                // Clear any pending errors before our call
                EfxHelper.GetALError();

                alSourceMethod.Invoke(null, new object[] { sourceId, efxDirectFilterValue, filterId });

                // Check if OpenAL reported an error
                int error = EfxHelper.GetALError();
                if (error != 0)
                {
                    // Common errors: 0xA001 = AL_INVALID_NAME, 0xA002 = AL_INVALID_ENUM, 0xA003 = AL_INVALID_VALUE
                    SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] ATTACH ERROR: OpenAL error 0x{error:X} attaching filter={filterId} to source={sourceId}");
                    return false;
                }

                // Success - no log (fires multiple times per sound per tick)
                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] AttachFilter EXCEPTION: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Experimental: Set source gain directly to test if we can affect the source at all
        /// </summary>
        public static void TestSetSourceGain(int sourceId, float gain)
        {
            if (alSourceFloatMethod == null)
            {
                // Try to get AL.Source(int, ALSourcef, float) method
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!asm.GetName().Name.Contains("OpenTK")) continue;
                        foreach (var type in asm.GetTypes())
                        {
                            if (type.Name == "AL" && type.Namespace?.Contains("OpenAL") == true)
                            {
                                // Find ALSourcef enum
                                Type alSourcefType = null;
                                foreach (var t in asm.GetTypes())
                                {
                                    if (t.Name == "ALSourcef" && t.IsEnum)
                                    {
                                        alSourcefType = t;
                                        break;
                                    }
                                }
                                if (alSourcefType == null) continue;

                                alSourcefGainValue = Enum.Parse(alSourcefType, "Gain");

                                alSourceFloatMethod = type.GetMethod("Source",
                                    BindingFlags.Public | BindingFlags.Static,
                                    null,
                                    new Type[] { typeof(int), alSourcefType, typeof(float) },
                                    null);
                                break;
                            }
                        }
                        if (alSourceFloatMethod != null) break;
                    }
                }
                catch { }
            }

            if (alSourceFloatMethod != null && alSourcefGainValue != null)
            {
                try
                {
                    // Set the gain
                    alSourceFloatMethod.Invoke(null, new object[] { sourceId, alSourcefGainValue, gain });

                    // Try to read it back to verify
                    try
                    {
                        // Find AL.GetSource(int, ALSourcef, out float)
                        var alType = alSourceFloatMethod.DeclaringType;
                        var getSourceMethod = alType.GetMethod("GetSource",
                            BindingFlags.Public | BindingFlags.Static,
                            null,
                            new Type[] { typeof(int), alSourcefGainValue.GetType(), typeof(float).MakeByRefType() },
                            null);

                        if (getSourceMethod != null)
                        {
                            object[] args = new object[] { sourceId, alSourcefGainValue, 0f };
                            getSourceMethod.Invoke(null, args);
                            float readBack = (float)args[2];
                        }
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    SoundPhysicsAdaptedModSystem.DebugLog($"[TEST] Failed to set gain: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Initialize AL.Source float method for pitch/gain manipulation.
        /// Called lazily on first use.
        /// </summary>
        private static bool InitializeALSourceFloat()
        {
            if (alSourceFloatInitialized) return alSourceFloatMethod != null;
            alSourceFloatInitialized = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("OpenTK")) continue;

                    Type alType = null;
                    Type alSourcefType = null;

                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "AL" && type.Namespace?.Contains("OpenAL") == true)
                            alType = type;
                        else if (type.Name == "ALSourcef" && type.IsEnum)
                            alSourcefType = type;
                    }

                    if (alType == null || alSourcefType == null) continue;

                    // Get enum values
                    alSourcefGainValue = Enum.Parse(alSourcefType, "Gain");
                    alSourcefPitchValue = Enum.Parse(alSourcefType, "Pitch");

                    // Get AL.Source(int, ALSourcef, float) method
                    alSourceFloatMethod = alType.GetMethod("Source",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new Type[] { typeof(int), alSourcefType, typeof(float) },
                        null);

                    if (alSourceFloatMethod != null)
                    {
                        SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] Initialized AL.Source float method");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] Failed to init AL.Source float: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Apply pitch offset to a sound via OpenAL.
        /// This bypasses VS's SetPitchOffset method (which we block) and sets pitch directly.
        /// Pitch value should be in range 0.1 to 3.0, with 1.0 being normal pitch.
        /// VS stores pitch as basePitch + offset, so we need to add our offset to the sound's base pitch.
        /// </summary>
        public static bool ApplyPitchOffset(ILoadedSound sound, float pitchOffset)
        {
            if (!IsInitialized || sound == null) return false;
            if (!InitializeALSourceFloat()) return false;

            try
            {
                int sourceId = GetSourceId(sound);
                if (sourceId <= 0) return false;

                // Get the sound's base pitch from params
                float basePitch = sound.Params?.Pitch ?? 1.0f;

                // Calculate final pitch (clamped to valid range)
                float finalPitch = Math.Max(0.1f, Math.Min(3.0f, basePitch + pitchOffset));

                // Apply via OpenAL
                alSourceFloatMethod.Invoke(null, new object[] { sourceId, alSourcefPitchValue, finalPitch });

                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] ApplyPitchOffset error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reset pitch offset to 0 (restore base pitch) for a sound.
        /// </summary>
        public static bool ResetPitchOffset(ILoadedSound sound)
        {
            return ApplyPitchOffset(sound, 0f);
        }

        /// <summary>
        /// Initialize AL.Source3f method for position manipulation (Phase 4B).
        /// Called lazily on first use.
        /// </summary>
        private static bool InitializeALSource3f()
        {
            if (alSource3fInitialized) return alSource3fMethod != null;
            alSource3fInitialized = true;

            try
            {
                int opentkCount = 0;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("OpenTK")) continue;
                    opentkCount++;

                    Type alType = null;
                    Type alSource3fType = null;

                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "AL" && type.Namespace?.Contains("OpenAL") == true)
                            alType = type;
                        else if (type.Name == "ALSource3f" && type.IsEnum)
                            alSource3fType = type;
                    }

                    if (alType == null || alSource3fType == null)
                        continue;

                    // Get Position enum value
                    try
                    {
                        alSource3fPositionValue = Enum.Parse(alSource3fType, "Position");
                    }
                    catch (Exception ex)
                    {
                        SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] Failed to parse Position enum: {ex.Message} (available: {string.Join(",", Enum.GetNames(alSource3fType))})");
                        continue;
                    }

                    // Get AL.Source(int, ALSource3f, float, float, float) method
                    alSource3fMethod = alType.GetMethod("Source",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new Type[] { typeof(int), alSource3fType, typeof(float), typeof(float), typeof(float) },
                        null);

                    if (alSource3fMethod != null)
                    {
                        SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] AL.Source3f init OK via {asm.GetName().Name}");
                        return true;
                    }
                    else
                    {
                        // Log available signatures for debugging
                        var sigs = alType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .Where(m => m.Name == "Source")
                            .Select(m => string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name)));
                        SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] AL.Source3f NOT FOUND. Available: {string.Join(" | ", sigs)}");
                    }
                }

                SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] FAILED: scanned {opentkCount} OpenTK assemblies, AL.Source3f not found");
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] InitializeALSource3f EXCEPTION: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Set OpenAL source position via reflected AL.Source3f call.
        /// Used by both ApplySoundPath (first frame) and SmoothAll (interpolation).
        /// </summary>
        private static void SetALSourcePosition(int sourceId, Vec3d pos)
        {
            if (alSource3fMethod == null || alSource3fPositionValue == null || pos == null || sourceId <= 0)
                return;
            try
            {
                alSource3fMethod.Invoke(null, new object[]
                {
                    sourceId,
                    alSource3fPositionValue,
                    (float)pos.X,
                    (float)pos.Y,
                    (float)pos.Z
                });
            }
            catch { }
        }

        /// <summary>
        /// Apply sound path resolution result - reposition sound toward opening.
        /// SPP-style single blended source: position moved to opening, filter set by blended occlusion.
        /// </summary>
        /// <param name="sound">The sound to reposition</param>
        /// <param name="pathResult">Sound path calculation result</param>
        /// <param name="originalPos">Original (actual) sound position</param>
        /// <returns>True if repositioning was applied</returns>
        private static int repositionLogCount = 0;
        private static int repositionFailLogCount = 0;
        public static bool ApplySoundPath(ILoadedSound sound, SoundPathResult pathResult, Vec3d originalPos)
        {
            if (!IsInitialized || sound == null)
            {
                if (repositionFailLogCount++ < 5)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] ApplySoundPath SKIP: init={IsInitialized} sound={sound != null}");
                return false;
            }

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableSoundRepositioning)
            {
                if (repositionFailLogCount++ < 5)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] ApplySoundPath SKIP: config={config != null} reposEnabled={config?.EnableSoundRepositioning}");
                return false;
            }

            // Initialize AL.Source3f if needed
            if (!InitializeALSource3f())
            {
                if (repositionFailLogCount++ < 10)
                    SoundPhysicsAdaptedModSystem.DebugLog("[PHASE4B] ApplySoundPath FAILED: AL.Source3f not available (see init logs above)");
                return false;
            }

            if (!activeFilters.TryGetValue(sound, out var entry))
            {
                if (repositionFailLogCount++ < 5)
                    SoundPhysicsAdaptedModSystem.DebugLog("[PHASE4B] ApplySoundPath SKIP: sound not in activeFilters");
                return false;
            }
            if (entry.SourceId <= 0)
            {
                if (repositionFailLogCount++ < 5)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] ApplySoundPath SKIP: sourceId={entry.SourceId}");
                return false;
            }

            // CRITICAL: Validate sourceId hasn't been recycled by VS.
            // When sound A finishes and VS recycles its sourceId to sound B,
            // applying position to entry.SourceId would affect the wrong sound.
            int currentSourceId = GetSourceId(sound);
            if (currentSourceId != entry.SourceId)
            {
                if (repositionFailLogCount++ < 10)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] ApplySoundPath STALE: stored={entry.SourceId} current={currentSourceId}");
                return false;
            }

            try
            {
                // SPR-STYLE SMOOTHING: Set the TARGET position, SmoothAll() will interpolate.
                Vec3d newPos = pathResult.ApparentPosition;
                entry.TargetRepositionedPos = newPos;
                entry.OriginalSoundPos = originalPos;

                // CRITICAL: On first repositioning, seed current at ORIGINAL position (not target).
                // SmoothAll() then lerps from original → target over ~250ms.
                // Old bug: seeding at target caused instant position jump on state entry.
                // With SPR-style, the target is already near original when direct path dominates,
                // so the first reposition is naturally a small offset. But seeding at original
                // guarantees smooth entry even if the first result has a large offset.
                if (entry.CurrentRepositionedPos == null)
                {
                    entry.CurrentRepositionedPos = originalPos.Clone();
                    SetALSourcePosition(entry.SourceId, originalPos);
                }

                // SPP-style single blended source: no separate permeated source needed.
                // LPF is driven by blended occlusion (open + permeated paths) in AudioPhysicsSystem.

                // Always log first 20 repositions, then only when DebugSoundPaths is on
                if (repositionLogCount < 20 || (config.DebugMode && config.DebugSoundPaths))
                {
                    repositionLogCount++;
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[PHASE4B] REPOSITION #{repositionLogCount}: {entry.SoundName ?? "?"} " +
                        $"src={entry.SourceId} offset={pathResult.RepositionOffset:F1}m " +
                        $"target=({(float)newPos.X:F1},{(float)newPos.Y:F1},{(float)newPos.Z:F1}) " +
                        $"orig=({(float)originalPos.X:F1},{(float)originalPos.Y:F1},{(float)originalPos.Z:F1}) " +
                        $"blendedOcc={pathResult.BlendedOcclusion:F2} open={pathResult.PathCount} perm={pathResult.PermeatedPathCount}");
                }

                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] ApplySoundPath EXCEPTION: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reset sound position to original location.
        /// Called when sound no longer needs repositioning (e.g., direct line of sight restored).
        /// </summary>
        public static bool ResetSoundPosition(ILoadedSound sound, Vec3d originalPos)
        {
            if (!IsInitialized || sound == null || originalPos == null) return false;
            if (!InitializeALSource3f()) return false;

            if (!activeFilters.TryGetValue(sound, out var entry)) return false;
            if (entry.SourceId <= 0) return false;

            // PHASE 4B SMOOTHING: Set target to original pos, let SmoothAll() lerp back.
            // Once converged, SmoothAll() clears the smoothing state.
            if (entry.CurrentRepositionedPos != null)
            {
                entry.TargetRepositionedPos = originalPos.Clone();
                entry.OriginalSoundPos = originalPos;

                return true;
            }

            // No active repositioning — nothing to reset
            return true;
        }

        /// <summary>
        /// Re-apply repositioned position after VS overwrites it via SetPosition.
        /// Called from SetPosition postfixes to prevent VS from resetting our override.
        /// Phase 4B: Without this, VS calls alSource3f(Position, original) every frame,
        /// overwriting our repositioned position before the audio system can use it.
        /// </summary>
        public static void ReapplyRepositionedPosition(ILoadedSound sound)
        {
            if (!IsInitialized || sound == null) return;
            if (!InitializeALSource3f()) return;

            if (!activeFilters.TryGetValue(sound, out var entry)) return;
            if (entry.SourceId <= 0 || entry.CurrentRepositionedPos == null) return;

            try
            {
                Vec3d pos = entry.CurrentRepositionedPos;
                alSource3fMethod.Invoke(null, new object[]
                {
                    entry.SourceId,
                    alSource3fPositionValue,
                    (float)pos.X,
                    (float)pos.Y,
                    (float)pos.Z
                });
            }
            catch { }
        }

        /// <summary>
        /// Detach filter from source (set filter to 0)
        /// </summary>
        private static bool DetachFilter(int sourceId)
        {
            if (alSourceMethod == null || efxDirectFilterValue == null)
                return false;

            try
            {
                alSourceMethod.Invoke(null, new object[] { sourceId, efxDirectFilterValue, 0 });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Detach any filter from an OpenAL source (set filter to 0 = no filter).
        /// Used to remove VS's global filter after createSoundSource.
        /// </summary>
        public static void DetachGlobalFilter(int sourceId)
        {
            if (!IsInitialized || sourceId == 0)
                return;

            DetachFilter(sourceId);
        }

        /// <summary>
        /// Update the stored position for a sound without recalculating anything.
        /// Called from SetPosition patches - AcousticsManager handles recalculation.
        /// </summary>
        public static void UpdateStoredPosition(ILoadedSound sound, Vec3d newPosition)
        {
            if (!IsInitialized || sound == null) return;

            if (activeFilters.TryGetValue(sound, out var entry))
            {
                entry.LastPosition = newPosition;
            }
        }

        /// <summary>
        /// Get the last known stored position for a sound.
        /// Returns null if sound is not registered or has no position.
        /// Used by AcousticsManager to read positions updated via SetPosition patches.
        /// </summary>
        public static Vec3d GetStoredPosition(ILoadedSound sound)
        {
            if (!IsInitialized || sound == null) return null;

            if (activeFilters.TryGetValue(sound, out var entry))
            {
                return entry.LastPosition;
            }
            return null;
        }

        /// <summary>
        /// Get the filter ID associated with a sound, or 0 if not registered.
        /// </summary>
        public static int GetFilterId(ILoadedSound sound)
        {
            if (!IsInitialized || sound == null)
                return 0;

            if (activeFilters.TryGetValue(sound, out var entry))
                return entry.FilterId;

            return 0;
        }

        /// <summary>
        /// Re-attach the filter for a sound that was already registered.
        /// Used after VS applies its own effects which may overwrite our filter.
        /// </summary>
        public static bool ReattachFilter(ILoadedSound sound)
        {
            if (!IsInitialized || sound == null)
                return false;

            if (!activeFilters.TryGetValue(sound, out var entry))
                return false;

            // CRITICAL: Verify sourceId hasn't changed (VS recycles source IDs)
            int currentSourceId = GetSourceId(sound);
            if (currentSourceId != entry.SourceId)
            {
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"REATTACH SKIP: {entry.SoundName} sourceId changed {entry.SourceId}->{currentSourceId}");
                return false;
            }

            // Re-attach our filter to the source
            return AttachFilter(entry.SourceId, entry.FilterId);
        }

        /// <summary>
        /// Set occlusion filter value for a sound.
        /// </summary>
        public static bool SetOcclusion(ILoadedSound sound, float filterValue, Vec3d soundPos = null, string soundName = null)
        {
            if (!IsInitialized || sound == null)
                return false;

            // Try to get existing entry
            bool isNewRegistration = false;
            if (!activeFilters.TryGetValue(sound, out var entry))
            {
                // Not registered yet - try to register
                if (!RegisterSound(sound))
                {
                    SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] Failed to register sound: {soundName ?? "unknown"}");
                    return false;
                }

                if (!activeFilters.TryGetValue(sound, out entry))
                {
                    SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] BUG: RegisterSound succeeded but entry not in dict!");
                    return false;
                }

                isNewRegistration = true;
            }
            else
            {
                // CRITICAL: Verify sourceId hasn't changed (VS recycles source IDs)
                // If the sourceId changed, this entry is stale - the sound was recycled
                int currentSourceId = GetSourceId(sound);
                if (currentSourceId != entry.SourceId)
                {
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[SoundFilterManager] STALE ENTRY: {entry.SoundName} sourceId changed {entry.SourceId}->{currentSourceId}, removing");

                    // Remove stale entry and re-register
                    if (activeFilters.TryRemove(sound, out var staleEntry))
                    {
                        CleanupEntry(staleEntry);
                    }

                    // Re-register with new sourceId
                    if (!RegisterSound(sound))
                        return false;

                    if (!activeFilters.TryGetValue(sound, out entry))
                        return false;

                    isNewRegistration = true;
                }
            }

            // Store position for later recalculation
            if (soundPos != null)
            {
                entry.LastPosition = soundPos.Clone();
            }
            if (soundName != null)
            {
                entry.SoundName = soundName;
            }

            // Set the TARGET - SmoothAll() on the 25ms tick will interpolate toward this
            entry.TargetValue = filterValue;

            // For NEW sounds: apply filter INSTANTLY (no smoothing)
            // One-shot sounds (splashes, hits) finish before smoothing could converge
            if (isNewRegistration)
            {
                float underwaterMult = SoundPhysicsAdaptedModSystem.GetUnderwaterMultiplier();
                float finalValue = filterValue * underwaterMult;

                // Configure filter type AND value BEFORE attaching
                // Prevents the race where sound plays with gainHF=1.0
                EfxHelper.ConfigureLowpass(entry.FilterId, finalValue);
                entry.CurrentValue = finalValue;
                AttachFilter(entry.SourceId, entry.FilterId);

                return true;
            }

            // Existing sounds: target is set, SmoothAll() handles the rest
            // Still reattach filter in case VS overwrote it
            AttachFilter(entry.SourceId, entry.FilterId);

            return true;
        }

        /// <summary>
        /// Smoothing tick interval in milliseconds. Register this as a game tick.
        /// </summary>
        public static float SmoothTickIntervalMs => SMOOTH_TICK_MS;

        /// <summary>
        /// Run on a fast 25ms tick. Smooths ALL sound filters toward their targets.
        /// Decoupled from raycast intervals so convergence time is consistent
        /// regardless of whether a sound is 5m or 50m away.
        ///
        /// Separate rates for muffling vs un-muffling:
        /// - Muffling (going behind wall): fast (~0.15s) - snappy response
        /// - Un-muffling (coming around corner): slower (~0.4s) - avoids jarring pop-in
        /// </summary>
        public static void SmoothAll()
        {
            if (!IsInitialized) return;

            float underwaterMult = SoundPhysicsAdaptedModSystem.GetUnderwaterMultiplier();
            int smoothed = 0;
            int posSmoothed = 0;

            foreach (var kvp in activeFilters)
            {
                var entry = kvp.Value;

                // === FILTER SMOOTHING (existing) ===
                // Calculate target with underwater multiplier
                float effectiveTarget = entry.TargetValue * underwaterMult;

                // Already converged?
                float diff = effectiveTarget - entry.CurrentValue;
                if (Math.Abs(diff) >= CONVERGE_EPSILON)
                {
                    // Always interpolate — no snap threshold.
                    // The old snap (>0.6 = instant) fired on every wall-edge transition
                    // because filter oscillated between 0.169 and 1.000 (diff=0.831).
                    // Smooth interpolation is always correct; teleport-like jumps
                    // converge in ~10 ticks (0.25s) which is fast enough.
                    float factor = diff < 0 ? SMOOTH_FACTOR_DOWN : SMOOTH_FACTOR_UP;
                    float newValue = entry.CurrentValue + diff * factor;

                    // Apply to OpenAL filter
                    if (EfxHelper.SetLowpassGainHF(entry.FilterId, newValue))
                    {
                        entry.CurrentValue = newValue;
                        // Reattach - VS may have overwritten our filter
                        AttachFilter(entry.SourceId, entry.FilterId);
                        smoothed++;
                    }
                }

                // === PHASE 4B: POSITION SMOOTHING ===
                // Safety: if the sound reference is dead or sourceId was recycled,
                // clear stale position state to prevent overwriting new sounds
                if (entry.CurrentRepositionedPos != null)
                {
                    if (!entry.SoundRef.TryGetTarget(out var posSound) ||
                        GetSourceId(posSound) != entry.SourceId)
                    {
                        entry.CurrentRepositionedPos = null;
                        entry.TargetRepositionedPos = null;
                        entry.OriginalSoundPos = null;
                    }
                }

                if (entry.TargetRepositionedPos != null && entry.CurrentRepositionedPos != null)
                {
                    double dx = entry.TargetRepositionedPos.X - entry.CurrentRepositionedPos.X;
                    double dy = entry.TargetRepositionedPos.Y - entry.CurrentRepositionedPos.Y;
                    double dz = entry.TargetRepositionedPos.Z - entry.CurrentRepositionedPos.Z;
                    double posDist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                    if (posDist < POS_CONVERGE_EPSILON)
                    {
                        // Converged. If target == original sound pos, clear repositioning entirely.
                        if (entry.OriginalSoundPos != null)
                        {
                            double toOrig = entry.TargetRepositionedPos.DistanceTo(entry.OriginalSoundPos);
                            if (toOrig < 0.1)
                            {
                                // Smoothly returned to original position — clear state
                                entry.TargetRepositionedPos = null;
                                entry.CurrentRepositionedPos = null;
                                entry.OriginalSoundPos = null;
                                // Apply original pos one last time
                                SetALSourcePosition(entry.SourceId, entry.TargetRepositionedPos ?? entry.OriginalSoundPos);
                                continue;
                            }
                        }
                        // Converged at repositioned target — just re-apply (VS overwrites)
                        SetALSourcePosition(entry.SourceId, entry.CurrentRepositionedPos);
                    }
                    else if (posDist > POS_SNAP_THRESHOLD)
                    {
                        // Teleport / huge jump — snap immediately
                        entry.CurrentRepositionedPos = entry.TargetRepositionedPos.Clone();
                        SetALSourcePosition(entry.SourceId, entry.CurrentRepositionedPos);
                        posSmoothed++;
                    }
                    else
                    {
                        // Exponential lerp toward target, capped by speed of sound
                        double moveAmount = posDist * POS_SMOOTH_FACTOR;
                        double maxMove = POS_MAX_SPEED_PER_TICK;
                        if (moveAmount > maxMove) moveAmount = maxMove;

                        double t = moveAmount / posDist;
                        entry.CurrentRepositionedPos = new Vec3d(
                            entry.CurrentRepositionedPos.X + dx * t,
                            entry.CurrentRepositionedPos.Y + dy * t,
                            entry.CurrentRepositionedPos.Z + dz * t
                        );
                        SetALSourcePosition(entry.SourceId, entry.CurrentRepositionedPos);
                        posSmoothed++;
                    }
                }

            }

            // Only log smooth stats every ~5s (200 ticks at 25ms) to avoid per-tick noise
            smoothLogAccumulator++;
            if (smoothLogAccumulator >= 200 && (smoothed > 0 || posSmoothed > 0))
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"SMOOTH: {smoothed}/{activeFilters.Count} filters, {posSmoothed} positions (5s sample)");
                smoothLogAccumulator = 0;
            }
        }

        /// <summary>
        /// Recalculate occlusion for all active sounds based on current player position.
        /// Call this when player moves to update stationary sound occlusion.
        /// </summary>
        public static void RecalculateAll(Vec3d playerPos, IBlockAccessor blockAccessor)
        {
            if (!IsInitialized || blockAccessor == null || playerPos == null)
                return;

            int updated = 0;
            var toRemove = new List<ILoadedSound>();

            foreach (var kvp in activeFilters)
            {
                var sound = kvp.Key;
                var entry = kvp.Value;

                // Skip if no position stored
                if (entry.LastPosition == null)
                    continue;

                // Check if sound is disposed
                if (!entry.SoundRef.TryGetTarget(out _))
                {
                    toRemove.Add(sound);
                    continue;
                }

                // CRITICAL: Verify sourceId hasn't changed (VS recycles source IDs)
                int currentSourceId = GetSourceId(sound);
                if (currentSourceId != entry.SourceId)
                {
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"RECALC SKIP: {entry.SoundName} sourceId changed {entry.SourceId}->{currentSourceId}");
                    toRemove.Add(sound);
                    continue;
                }

                // Recalculate occlusion - set as new TARGET
                // SmoothAll() on the 25ms tick handles interpolation
                // For repositioned sounds: AudioPhysicsSystem sets the target from path data.
                // We skip recalculation here because path-based LPF is set in the main tick.
                // Only recalculate for NON-repositioned sounds using OcclusionCalculator.
                if (entry.CurrentRepositionedPos != null)
                {
                    // Repositioned sound — LPF is managed by AudioPhysicsSystem path data.
                    // Don't override with OcclusionCalculator (which would raycast from wrong pos).
                    continue;
                }
                float occlusion = OcclusionCalculator.Calculate(entry.LastPosition, playerPos, blockAccessor);
                float targetFilter = occlusion <= 0 ? 1.0f : OcclusionCalculator.OcclusionToFilter(occlusion);

                entry.TargetValue = targetFilter;
                updated++;
            }

            // Clean up stale entries found during recalculation
            foreach (var sound in toRemove)
            {
                if (activeFilters.TryRemove(sound, out var staleEntry))
                {
                    CleanupEntry(staleEntry);
                }
            }

            if (updated > 0 || toRemove.Count > 0)
            {
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[SoundFilterManager] Recalculated {updated} sounds, removed {toRemove.Count} stale");
            }
        }

        /// <summary>
        /// Recalculate underwater filter and pitch for all active sounds.
        /// Called when player enters/exits water to update already-playing sounds.
        /// For sounds with positions: recalculates occlusion + underwater
        /// For sounds without positions (music): applies only underwater filter
        /// Also applies pitch offset to non-music sounds.
        /// </summary>
        public static void RecalculateAllUnderwater()
        {
            if (!IsInitialized) return;

            float underwaterMult = SoundPhysicsAdaptedModSystem.GetUnderwaterMultiplier();
            float pitchOffset = SoundPhysicsAdaptedModSystem.GetUnderwaterPitchOffset();
            int updated = 0;
            int pitchUpdated = 0;
            var toRemove = new List<ILoadedSound>();

            foreach (var kvp in activeFilters)
            {
                var sound = kvp.Key;
                var entry = kvp.Value;

                // Check if sound is disposed
                if (!entry.SoundRef.TryGetTarget(out _))
                {
                    toRemove.Add(sound);
                    continue;
                }

                // Verify sourceId hasn't changed
                int currentSourceId = GetSourceId(sound);
                if (currentSourceId != entry.SourceId)
                {
                    toRemove.Add(sound);
                    continue;
                }

                // Check if this is a music sound (needs special handling)
                bool isMusic = false;
                try
                {
                    var soundType = sound.Params?.SoundType;
                    isMusic = soundType == EnumSoundType.Music ||
                              soundType == EnumSoundType.MusicGlitchunaffected;
                }
                catch { }

                // Override: positional music (e.g. Resonator) should use non-music underwater filter
                if (isMusic && entry.TreatAsPositional)
                    isMusic = false;

                // Get appropriate underwater multiplier
                float thisSoundUnderwaterMult = SoundPhysicsAdaptedModSystem.GetUnderwaterMultiplier(isMusic);

                // Calculate new filter value
                float newValue;
                if (entry.LastPosition == null)
                {
                    // No position = music/ambient - just use underwater multiplier
                    newValue = thisSoundUnderwaterMult;
                }
                else
                {
                    // Has position - multiply current target with underwater
                    newValue = entry.TargetValue * thisSoundUnderwaterMult;
                }

                // Apply lowpass filter if different
                if (Math.Abs(entry.CurrentValue - newValue) > 0.001f)
                {
                    if (EfxHelper.SetLowpassGainHF(entry.FilterId, newValue))
                    {
                        entry.CurrentValue = newValue;
                        AttachFilter(entry.SourceId, entry.FilterId);
                        updated++;
                    }
                }

                // Apply pitch offset (respects UnderwaterPitchAffectsMusic config)
                var pitchConfig = SoundPhysicsAdaptedModSystem.Config;
                if (!isMusic || (pitchConfig != null && pitchConfig.UnderwaterPitchAffectsMusic))
                {
                    if (ApplyPitchOffset(sound, pitchOffset))
                    {
                        pitchUpdated++;
                    }
                }
            }

            // Cleanup stale entries
            foreach (var sound in toRemove)
            {
                if (activeFilters.TryRemove(sound, out var staleEntry))
                {
                    CleanupEntry(staleEntry);
                }
            }

            SoundPhysicsAdaptedModSystem.DebugLog(
                $"[SoundFilterManager] Underwater recalc: filter={updated}, pitch={pitchUpdated}, removed={toRemove.Count}");
        }

        /// <summary>
        /// Unregister a sound and delete its filter.
        /// Call this when a sound is disposed.
        /// </summary>
        public static void UnregisterSound(ILoadedSound sound)
        {
            if (sound == null) return;

            if (activeFilters.TryRemove(sound, out var entry))
            {
                CleanupEntry(entry);
            }
        }

        /// <summary>
        /// Clean up a filter entry.
        /// CRITICAL: Do NOT detach filter from source here!
        /// The sourceId may have been recycled for a new sound, and detaching
        /// would remove the filter from that new sound, causing it to play unmuffled.
        /// We only delete the filter object itself - if it's still attached to a
        /// recycled source, OpenAL will handle it when that source is reconfigured.
        /// </summary>
        private static void CleanupEntry(FilterEntry entry)
        {
            try
            {
                // DO NOT detach filter from source!
                // The sourceId may have been recycled for a new sound.
                // Detaching here would remove the filter from that new sound.
                //
                // OLD CODE (caused unmuffling bug):
                // if (entry.IsAttached && entry.SourceId != 0)
                // {
                //     DetachFilter(entry.SourceId);
                // }

                // Only delete the OpenAL filter object
                if (entry.FilterId != 0)
                {
                    EfxHelper.DeleteFilter(entry.FilterId);
                    totalFiltersDeleted++;
                }
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] CleanupEntry failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clean up filters for disposed sounds.
        /// Call this periodically (e.g., once per second).
        /// </summary>
        public static void CleanupDisposed()
        {
            if (!IsInitialized) return;

            var toRemove = new List<ILoadedSound>();

            foreach (var kvp in activeFilters)
            {
                var sound = kvp.Key;
                var entry = kvp.Value;

                // Check if sound is disposed
                bool isDisposed = false;
                try
                {
                    // OPTIMIZATION: Cache the PropertyInfo lookup (one-time cost)
                    if (!isDisposedPropertyChecked)
                    {
                        isDisposedProperty = sound.GetType().GetProperty("IsDisposed");
                        isDisposedPropertyChecked = true;
                    }

                    if (isDisposedProperty != null)
                    {
                        isDisposed = (bool)isDisposedProperty.GetValue(sound);
                    }
                    else
                    {
                        // Try to check via weak reference
                        if (!entry.SoundRef.TryGetTarget(out _))
                        {
                            isDisposed = true;
                        }
                    }
                }
                catch
                {
                    // If we can't access the sound, assume it's disposed
                    isDisposed = true;
                }

                if (isDisposed)
                {
                    toRemove.Add(sound);
                }
            }

            // Remove disposed sounds
            foreach (var sound in toRemove)
            {
                if (activeFilters.TryRemove(sound, out var entry))
                {
                    CleanupEntry(entry);
                }
            }
        }

        /// <summary>
        /// Dispose all filters. Call on mod unload.
        /// </summary>
        public static void Dispose()
        {
            foreach (var kvp in activeFilters)
            {
                CleanupEntry(kvp.Value);
            }
            activeFilters.Clear();

            SoundPhysicsAdaptedModSystem.Log(
                $"[SoundFilterManager] Disposed. Created={totalFiltersCreated}, Deleted={totalFiltersDeleted}");

            IsInitialized = false;
            totalFiltersCreated = 0;
            totalFiltersDeleted = 0;
            loggedOnce = false;
        }

        /// <summary>
        /// Get debug stats
        /// </summary>
        public static string GetStats()
        {
            return $"Active={activeFilters.Count}, Created={totalFiltersCreated}, Deleted={totalFiltersDeleted}";
        }

        /// <summary>
        /// Check if a sourceId is tracked by our system.
        /// Used by AL.SourcePlay hook to detect untracked sources.
        /// </summary>
        public static bool IsSourceTracked(int sourceId)
        {
            if (!IsInitialized || sourceId <= 0)
                return false;

            foreach (var kvp in activeFilters)
            {
                if (kvp.Value.SourceId == sourceId)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get the filter ID for a given sourceId.
        /// Used by AL.SourcePlay hook to attach filter right before play.
        /// </summary>
        public static int GetFilterForSource(int sourceId)
        {
            if (!IsInitialized || sourceId <= 0)
                return 0;

            foreach (var kvp in activeFilters)
            {
                if (kvp.Value.SourceId == sourceId)
                    return kvp.Value.FilterId;
            }
            return 0;
        }

        /// <summary>
        /// Get all active sounds for occlusion recalculation.
        /// Returns sounds that are still valid (not disposed).
        /// </summary>
        public static IEnumerable<ILoadedSound> GetActiveSounds()
        {
            foreach (var kvp in activeFilters)
            {
                var sound = kvp.Key;
                // Check if sound is still valid
                if (kvp.Value.SoundRef.TryGetTarget(out _))
                {
                    yield return sound;
                }
            }
        }
    }
}
