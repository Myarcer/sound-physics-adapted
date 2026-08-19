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
            public float CurrentValue;  // Smoothed filter GAINHF, before the throttle envelope
            public float AppliedValue;  // What OpenAL holds now (CurrentValue x envelope)
            public float TargetValue;   // Target filter value (what we're smoothing toward)

            // Throttle envelope. Created on first use — most sounds never throttle.
            public ThrottleFadeState Fade;

            // Reverb sends. The physics tick writes TargetReverb, SmoothAll converges
            // CurrentReverb toward it and writes it to OpenAL only while it moves.
            public ReverbResult TargetReverb;
            public ReverbResult CurrentReverb;
            public bool HasReverbTarget;
            public bool ReverbConverged;
            public WeakReference<ILoadedSound> SoundRef;  // Weak ref to detect disposal
            public Vec3d LastPosition;  // Last known sound position for recalculation
            public string SoundName;    // For debug logging

            // PHASE 4B: Repositioned position smoothing
            // Target is set by ApplySoundPath(); SmoothAll() lerps Current toward it.
            // SetPosition postfixes re-apply CurrentRepositionedPos to prevent VS overwrite.
            public Vec3d TargetRepositionedPos;   // null = no active repositioning target
            public Vec3d CurrentRepositionedPos;   // null = not yet smoothing; the interpolated value
            public Vec3d OriginalSoundPos;         // actual source pos, for reset lerp
            public Vec3d SmoothedTargetPos;         // EMA-smoothed target to damp oscillation

            // Resonator fix: MusicGlitchunaffected sounds that are actually positional world sounds
            // When true, RecalculateAllUnderwater treats as non-music (applies underwater filter)
            public bool TreatAsPositional;          // false by default
        }

        // === The one temporal stage ===
        // Every constant lives in SmoothingCurves. The physics tick writes raw targets;
        // this 25ms tick is the only place an audible value moves toward one (audit A4).
        private const float SMOOTH_TICK_MS = SmoothingCurves.TickMs;

        // Filter convergence threshold, in linear gain. Still used to decide whether a
        // value is worth writing to OpenAL — that question is about the write, not about
        // the transition, so linear is right for it.
        private const float CONVERGE_EPSILON = 0.002f;

        // Convergence threshold for the transition itself, in natural-log gain units.
        // 0.02 is about 0.17 dB and means the same thing at every level.
        private const float LOG_CONVERGE_EPSILON = 0.02f;

        // Below this gain the filter is at its floor — clamp before the log conversion.
        private const float MIN_LOG_GAIN = 1e-5f;

        private static float POS_MAX_SPEED_PER_TICK => SmoothingCurves.PositionMaxSpeedPerTick;
        private static float POS_SMOOTH_FACTOR => SmoothingCurves.PositionFactor;
        private static float POS_SNAP_THRESHOLD => SmoothingCurves.PositionSnapThreshold;
        private static float POS_CONVERGE_EPSILON => SmoothingCurves.PositionConvergeEpsilon;
        private static float TARGET_EMA_FACTOR => SmoothingCurves.PositionTargetStabilizer;

        // Reverb depends on submersion state, which ApplyToSource reads for itself. When
        // it changes, converged entries must write their sends once more.
        private static float lastSubmersionReverbMult = float.NaN;
        private static bool forceReverbApply = false;

        // Game time of the previous smoothing tick, for the throttle ramp.
        private static long lastSmoothTickMs = 0;

        // Track filters by sound instance
        private static ConcurrentDictionary<ILoadedSound, FilterEntry> activeFilters
            = new ConcurrentDictionary<ILoadedSound, FilterEntry>();

        // OPTIMIZATION: Reverse lookup from sourceId -> sound for O(1) lookups in HandleSourcePlay
        // Without this, IsSourceTracked/GetFilterForSource iterate the entire activeFilters dict (O(n))
        // which causes O(n²) during world join when hundreds of sounds start simultaneously
        private static ConcurrentDictionary<int, ILoadedSound> sourceIdToSound
            = new ConcurrentDictionary<int, ILoadedSound>();

        // Reflection for getting sourceId from LoadedSoundNative
        private static FieldInfo sourceIdField;
        private static Type loadedSoundNativeType;

        // Reflection for AL.Source to attach filter
        private static MethodInfo alSourceMethod;
        private static object efxDirectFilterValue;

        // ==== Compiled hot-path delegates (fallback: reflection Invoke) ====
        // AttachFilter runs per sound per 25ms SmoothAll tick; SetALSourcePosition runs
        // per repositioned sound per tick AND per VS SetPosition call (every frame for
        // moving sounds). Compiled via EfxHelper's expression helpers — zero allocation.
        private static Action<int, int, int> dAlSourceInt;              // AL.Source(src, ALSourcei, int)
        private static Action<int, int, float> dAlSourceFloat;          // AL.Source(src, ALSourcef, float)
        private static Action<int, int, float, float, float> dAlSource3f; // AL.Source(src, ALSource3f, f, f, f)
        private static int efxDirectFilterInt;
        private static int alSourcefPitchInt;
        private static int alSource3fPositionInt;

        // OPTIMIZATION: Compiled IsDisposed getter (no reflection Invoke + bool boxing
        // per sound per 25ms SmoothAll tick). Falls back to PropertyInfo, then weak ref.
        private static System.Func<ILoadedSound, bool> isDisposedGetter;
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
        /// Lightweight check: is the sound still alive (not disposed, weak ref valid)?
        /// Uses cached isDisposedProperty reflection — no per-call GetProperty overhead.
        /// Returns false if the sound is disposed or unreachable.
        /// </summary>
        private static bool IsSoundAlive(ILoadedSound sound, FilterEntry entry)
        {
            try
            {
                if (!isDisposedPropertyChecked)
                {
                    isDisposedPropertyChecked = true;
                    isDisposedProperty = sound.GetType().GetProperty("IsDisposed");
                    if (isDisposedProperty != null)
                    {
                        try
                        {
                            // Compile: s => ((ConcreteType)s).IsDisposed
                            // One-time cost; per-call is then a direct call, no boxing.
                            var p = System.Linq.Expressions.Expression.Parameter(typeof(ILoadedSound), "s");
                            var body = System.Linq.Expressions.Expression.Property(
                                System.Linq.Expressions.Expression.Convert(p, sound.GetType()),
                                isDisposedProperty);
                            isDisposedGetter = System.Linq.Expressions.Expression
                                .Lambda<System.Func<ILoadedSound, bool>>(body, p).Compile();
                        }
                        catch { /* keep PropertyInfo fallback */ }
                    }
                }

                if (isDisposedGetter != null)
                {
                    return !isDisposedGetter(sound);
                }
                if (isDisposedProperty != null)
                {
                    return !(bool)isDisposedProperty.GetValue(sound);
                }

                // Fallback: weak reference check
                return entry.SoundRef.TryGetTarget(out _);
            }
            catch
            {
                return false;
            }
        }

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

                // Compile zero-alloc delegate for the per-tick filter attach path
                efxDirectFilterInt = Convert.ToInt32(efxDirectFilterValue);
                dAlSourceInt = EfxHelper.CompileIntEnumInt(alSourceMethod, alSourceiType);

                api.Logger.Debug($"[SoundFilterManager] Found AL.Source: {alSourceMethod} (compiled={dAlSourceInt != null})");
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
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] Cannot get sourceId for sound");
                    return false;
                }

                // Create a new OpenAL filter for this sound
                int filterId = EfxHelper.GenFilter();
                if (filterId == 0)
                {
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                // Use reverse lookup for O(1) instead of iterating all activeFilters
                if (sourceIdToSound.TryGetValue(sourceId, out var oldSound) && oldSound != sound)
                {
                    if (activeFilters.TryGetValue(oldSound, out var oldEntry))
                    {
                        oldEntry.CurrentRepositionedPos = null;
                        oldEntry.TargetRepositionedPos = null;
                        oldEntry.OriginalSoundPos = null;
                        oldEntry.SmoothedTargetPos = null;
                    }
                }

                // Track it
                var entry = new FilterEntry
                {
                    FilterId = filterId,
                    SourceId = sourceId,
                    CurrentValue = 1.0f,
                    AppliedValue = 1.0f,
                    TargetValue = 1.0f,
                    SoundRef = new WeakReference<ILoadedSound>(sound)
                };

                if (!activeFilters.TryAdd(sound, entry))
                {
                    // Lost a race with another thread registering the same sound
                    // (Start() can fire on the music thread). Theirs won — delete
                    // our just-created filter so it doesn't leak.
                    EfxHelper.DeleteFilter(filterId);
                    return true;
                }

                // New sound<->sourceId pairing: VS just ran createSoundSource() with fresh
                // vanilla distance params, so the distance-model dedupe for this id must reset.
                // Must run AFTER the TryAdd win — a losing thread invalidating here would let
                // the winner's already-applied multipliers get applied a second time (stacking).
                LoadSoundPatch.InvalidateDistanceModel(sourceId);
                sourceIdToSound[sourceId] = sound;
                totalFiltersCreated++;

                if (!loggedOnce)
                {
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog(
                            $"[SoundFilterManager] Registered sound: sourceId={sourceId}, filterId={filterId}");
                    loggedOnce = true;
                }

                return true;
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] AttachFilter FAILED: alSourceMethod={alSourceMethod != null}, efxDirectFilterValue={efxDirectFilterValue != null}");
                return false;
            }

            if (sourceId <= 0 || filterId <= 0)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] AttachFilter INVALID IDs: source={sourceId}, filter={filterId}");
                return false;
            }

            try
            {
                // Clear any pending errors before our call
                EfxHelper.GetALError();

                if (dAlSourceInt != null)
                    dAlSourceInt(sourceId, efxDirectFilterInt, filterId);
                else
                    alSourceMethod.Invoke(null, new object[] { sourceId, efxDirectFilterValue, filterId });

                // Check if OpenAL reported an error
                int error = EfxHelper.GetALError();
                if (error != 0)
                {
                    // Common errors: 0xA001 = AL_INVALID_NAME, 0xA002 = AL_INVALID_ENUM, 0xA003 = AL_INVALID_VALUE
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] ATTACH ERROR: OpenAL error 0x{error:X} attaching filter={filterId} to source={sourceId}");
                    return false;
                }

                // Success - no log (fires multiple times per sound per tick)
                return true;
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] AttachFilter EXCEPTION: {ex.Message}");
                return false;
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
                        alSourcefPitchInt = Convert.ToInt32(alSourcefPitchValue);
                        dAlSourceFloat = EfxHelper.CompileIntEnumFloat(alSourceFloatMethod, alSourcefType);
                        if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                            SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] Initialized AL.Source float method (compiled={dAlSourceFloat != null})");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                if (dAlSourceFloat != null)
                    dAlSourceFloat(sourceId, alSourcefPitchInt, finalPitch);
                else
                    alSourceFloatMethod.Invoke(null, new object[] { sourceId, alSourcefPitchValue, finalPitch });

                return true;
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] ApplyPitchOffset error: {ex.Message}");
                return false;
            }
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
                        if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                        alSource3fPositionInt = Convert.ToInt32(alSource3fPositionValue);
                        dAlSource3f = EfxHelper.CompileIntEnum3Float(alSource3fMethod, alSource3fType);
                        if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                            SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] AL.Source3f init OK via {asm.GetName().Name} (compiled={dAlSource3f != null})");
                        return true;
                    }
                    else
                    {
                        // Log available signatures for debugging
                        var sigs = alType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .Where(m => m.Name == "Source")
                            .Select(m => string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name)));
                        if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                            SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] AL.Source3f NOT FOUND. Available: {string.Join(" | ", sigs)}");
                    }
                }

                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] FAILED: scanned {opentkCount} OpenTK assemblies, AL.Source3f not found");
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                if (dAlSource3f != null)
                {
                    dAlSource3f(sourceId, alSource3fPositionInt, (float)pos.X, (float)pos.Y, (float)pos.Z);
                    return;
                }
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
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] ApplySoundPath SKIP: init={IsInitialized} sound={sound != null}");
                return false;
            }

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableSoundRepositioning)
            {
                if (repositionFailLogCount++ < 5)
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] ApplySoundPath SKIP: config={config != null} reposEnabled={config?.EnableSoundRepositioning}");
                return false;
            }

            // Initialize AL.Source3f if needed
            if (!InitializeALSource3f())
            {
                if (repositionFailLogCount++ < 10)
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog("[PHASE4B] ApplySoundPath FAILED: AL.Source3f not available (see init logs above)");
                return false;
            }

            if (!activeFilters.TryGetValue(sound, out var entry))
            {
                if (repositionFailLogCount++ < 5)
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog("[PHASE4B] ApplySoundPath SKIP: sound not in activeFilters");
                return false;
            }
            if (entry.SourceId <= 0)
            {
                if (repositionFailLogCount++ < 5)
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog($"[PHASE4B] ApplySoundPath STALE: stored={entry.SourceId} current={currentSourceId}");
                return false;
            }

            try
            {
                // SPR-STYLE SMOOTHING: Set the TARGET position, SmoothAll() will interpolate.
                Vec3d newPos = pathResult.ApparentPosition;
                entry.OriginalSoundPos = originalPos;

                // TARGET EMA: Smooth the target itself to damp dual-path oscillation.
                // When two diffraction paths alternate dominance (e.g. around opposite
                // sides of a wall), the raw target teleports ~7m each tick. EMA prevents
                // this from reaching SmoothAll(), keeping the audible position stable.
                if (entry.SmoothedTargetPos != null)
                {
                    entry.SmoothedTargetPos = new Vec3d(
                        entry.SmoothedTargetPos.X + (newPos.X - entry.SmoothedTargetPos.X) * TARGET_EMA_FACTOR,
                        entry.SmoothedTargetPos.Y + (newPos.Y - entry.SmoothedTargetPos.Y) * TARGET_EMA_FACTOR,
                        entry.SmoothedTargetPos.Z + (newPos.Z - entry.SmoothedTargetPos.Z) * TARGET_EMA_FACTOR
                    );
                }
                else
                {
                    entry.SmoothedTargetPos = newPos.Clone();
                }
                entry.TargetRepositionedPos = entry.SmoothedTargetPos;

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
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                entry.SmoothedTargetPos = null; // Clear EMA so next reposition starts fresh
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

            // Skip if sound was disposed — prevents OpenAL InvalidName on stale sourceId
            if (!IsSoundAlive(sound, entry)) return;

            // Runs on every VS SetPosition call (per frame for moving sounds) —
            // SetALSourcePosition uses the compiled zero-alloc delegate.
            SetALSourcePosition(entry.SourceId, entry.CurrentRepositionedPos);
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
                if (dAlSourceInt != null)
                    dAlSourceInt(sourceId, efxDirectFilterInt, 0);
                else
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
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] Failed to register sound: {soundName ?? "unknown"}");
                    return false;
                }

                if (!activeFilters.TryGetValue(sound, out entry))
                {
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                entry.AppliedValue = finalValue;
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
        /// Sets the reverb a sound must reach. The physics tick calls this with the raw
        /// raytrace result; <see cref="SmoothAll"/> converges the sends and writes them to
        /// OpenAL only while they move.
        ///
        /// The first target of a sound is applied at once — a one-shot (a hit, a splash)
        /// ends before any ramp could finish, and it must not start dry.
        /// </summary>
        public static void SetReverbTarget(ILoadedSound sound, ReverbResult target)
        {
            if (!IsInitialized || sound == null) return;
            if (!activeFilters.TryGetValue(sound, out var entry)) return;
            if (entry.SourceId <= 0 || !ReverbEffects.IsInitialized) return;

            entry.TargetReverb = target;

            if (!entry.HasReverbTarget)
            {
                entry.HasReverbTarget = true;
                entry.CurrentReverb = target;
                entry.ReverbConverged = true;
                ReverbEffects.ApplyToSource(entry.SourceId, target);
                return;
            }

            entry.ReverbConverged = false;
        }

        /// <summary>
        /// The filter gain of this sound as the geometry produced it: what OpenAL holds
        /// now, with the submersion multiplier taken back out. A caller asking how well a
        /// sound is heard wants the acoustic value, not "the player has their head under
        /// water". Returns -1 when the sound is not tracked.
        /// </summary>
        public static float GetCurrentFilterGain(ILoadedSound sound)
        {
            if (!IsInitialized || sound == null) return -1f;
            if (!activeFilters.TryGetValue(sound, out var entry)) return -1f;

            float underwaterMult = SoundPhysicsAdaptedModSystem.GetUnderwaterMultiplier();
            if (underwaterMult <= 0.001f) return entry.CurrentValue;
            return Math.Min(1f, entry.CurrentValue / underwaterMult);
        }

        /// <summary>
        /// Moves the four reverb sends toward their target and writes them only while they
        /// move. A converged sound costs nothing until its room changes.
        /// </summary>
        private static void SmoothReverb(FilterEntry entry)
        {
            if (entry.ReverbConverged && !forceReverbApply) return;

            var cur = entry.CurrentReverb;
            var tgt = entry.TargetReverb;

            float maxDelta = Math.Abs(tgt.SendGain0 - cur.SendGain0);
            maxDelta = Math.Max(maxDelta, Math.Abs(tgt.SendGain1 - cur.SendGain1));
            maxDelta = Math.Max(maxDelta, Math.Abs(tgt.SendGain2 - cur.SendGain2));
            maxDelta = Math.Max(maxDelta, Math.Abs(tgt.SendGain3 - cur.SendGain3));

            if (maxDelta < SmoothingCurves.ReverbConvergeEpsilon)
            {
                entry.CurrentReverb = tgt;
                entry.ReverbConverged = true;
                if (!forceReverbApply) return;
            }
            else
            {
                float alpha = SmoothingCurves.ReverbAlpha(maxDelta);
                // Cutoffs follow the target directly — they shape the send, they are not
                // a level, so a ramp adds nothing audible.
                entry.CurrentReverb = new ReverbResult(
                    cur.SendGain0 + (tgt.SendGain0 - cur.SendGain0) * alpha,
                    cur.SendGain1 + (tgt.SendGain1 - cur.SendGain1) * alpha,
                    cur.SendGain2 + (tgt.SendGain2 - cur.SendGain2) * alpha,
                    cur.SendGain3 + (tgt.SendGain3 - cur.SendGain3) * alpha,
                    tgt.SendCutoff0, tgt.SendCutoff1, tgt.SendCutoff2, tgt.SendCutoff3);
            }

            ReverbEffects.ApplyToSource(entry.SourceId, entry.CurrentReverb);
        }

        /// <summary>
        /// Runs on the fixed 25 ms tick and is the ONLY place an audible value moves
        /// toward its target: filter gain, reverb sends, source position, and the throttle
        /// envelope. Everything upstream writes raw targets (audit item A4).
        ///
        /// Because the rate is fixed, convergence takes the same wall-clock time whether a
        /// sound is raycast every 50 ms at 5 m or every 500 ms at 50 m.
        ///
        /// Rates and their reasons: <see cref="SmoothingCurves"/>.
        /// </summary>
        /// <param name="nowMs">Game time, for the throttle envelope.</param>
        /// <summary>
        /// Distance from the listener to the sound, in blocks, or 0 when either position
        /// is unknown. Only the slew ceiling reads it, and its unknown case is the
        /// reference distance, so 0 is a safe answer.
        /// </summary>
        private static float DistanceToListener(FilterEntry entry, Vec3d listenerPos)
        {
            if (listenerPos == null || entry.LastPosition == null) return 0f;
            return (float)entry.LastPosition.DistanceTo(listenerPos);
        }

        // Listener speed in blocks per second, for the slew ceiling. Measured here rather
        // than read off the entity so it needs no plumbing and follows whatever moved the
        // ear, including a boat or a mount.
        private static Vec3d lastListenerPos;
        private static float listenerSpeed;

        /// <summary>Speed above which a jump counts as a teleport and is not a speed.</summary>
        private const float MAX_LISTENER_SPEED = 40f;

        /// <summary>
        /// Updates <see cref="listenerSpeed"/> from the distance the ear moved since the
        /// last tick. Smoothed upward slowly and downward slowly enough that one stuttered
        /// frame cannot spike the ceiling, but fast enough to catch the start of a fall.
        /// </summary>
        private static void UpdateListenerSpeed(Vec3d listenerPos, float elapsedMs)
        {
            if (listenerPos == null || elapsedMs <= 0f)
            {
                lastListenerPos = listenerPos?.Clone();
                return;
            }

            if (lastListenerPos == null)
            {
                lastListenerPos = listenerPos.Clone();
                listenerSpeed = 0f;
                return;
            }

            float raw = (float)lastListenerPos.DistanceTo(listenerPos) / (elapsedMs / 1000f);
            lastListenerPos.Set(listenerPos);

            // A teleport or a world change is not motion through geometry.
            if (raw > MAX_LISTENER_SPEED) raw = 0f;

            listenerSpeed += (raw - listenerSpeed) * 0.4f;
        }

        public static void SmoothAll(long nowMs, Vec3d listenerPos = null)
        {
            if (!IsInitialized) return;

            var config = SoundPhysicsAdaptedModSystem.Config;
            float underwaterMult = SoundPhysicsAdaptedModSystem.GetUnderwaterMultiplier();
            float minFilter = config?.MinLowPassFilter ?? 0.001f;
            // filter = exp(-occlusion * BlockAbsorption * 2) — the divisor that turns a
            // log-gain difference back into occlusion units.
            float absorptionScale = Math.Max(1e-4f, (config?.BlockAbsorption ?? 0.5f) * 2f);
            float fadeDurationMs = (config?.ThrottleFadeSeconds ?? 5.0f) * 1000f;
            var throttle = SoundPhysicsAdaptedModSystem.Throttle;

            // Real elapsed time for the throttle ramp, so a frame hitch does not stretch a
            // 5 s fade. Clamped: the first tick and a long freeze must not jump the ramp.
            float fadeElapsedMs = SMOOTH_TICK_MS;
            if (lastSmoothTickMs > 0 && nowMs > lastSmoothTickMs)
                fadeElapsedMs = Math.Min(250f, nowMs - lastSmoothTickMs);
            lastSmoothTickMs = nowMs;

            // Feeds the slew ceiling: the shadow boundary a sound goes quiet across is a
            // distance, so how fast it is crossed decides how fast the sound may change.
            UpdateListenerSpeed(listenerPos, fadeElapsedMs);

            // Diving or surfacing changes the reverb send gains inside ApplyToSource.
            // Converged entries must write their sends once more when that happens.
            float submersionMult = SoundPhysicsAdaptedModSystem.GetSubmersionReverbMultiplier();
            if (float.IsNaN(lastSubmersionReverbMult) || Math.Abs(submersionMult - lastSubmersionReverbMult) > 0.001f)
            {
                lastSubmersionReverbMult = submersionMult;
                forceReverbApply = true;
            }

            int smoothed = 0;
            int posSmoothed = 0;

            foreach (var kvp in activeFilters)
            {
                var sound = kvp.Key;
                var entry = kvp.Value;

                // Skip disposed/dead sounds — prevents OpenAL InvalidName errors
                // when DisposeOnFinish sounds are recycled between ticks
                if (!IsSoundAlive(sound, entry))
                    continue;

                // === FILTER: the one temporal stage ===
                // The target is raw — the physics tick never smooths it. Convergence runs
                // in the log domain, so equal steps are equal steps in dB and in occlusion
                // units. A linear-gain EMA would race through the loud part of a transition
                // and crawl through the quiet part.
                float effectiveTarget = entry.TargetValue * underwaterMult;
                if (effectiveTarget < MIN_LOG_GAIN) effectiveTarget = MIN_LOG_GAIN;
                if (effectiveTarget > 1f) effectiveTarget = 1f;

                float current = entry.CurrentValue < MIN_LOG_GAIN ? MIN_LOG_GAIN : entry.CurrentValue;

                float logCurrent = MathF.Log(current);
                float logDiff = MathF.Log(effectiveTarget) - logCurrent;

                // Converged test in the LOG domain, because the step below is a log step.
                // A linear threshold means a different thing at every level: 0.002 of gain
                // is 0.35 dB at -26 dB but 6 dB at -54 dB, so a transition deep in the
                // muffled range used to fall under it and snap instead of converging. The
                // muffled range is where this mod spends its time.
                if (MathF.Abs(logDiff) >= LOG_CONVERGE_EPSILON)
                {
                    bool muffling = logDiff < 0f;

                    // Occlusion units, so the bands in SmoothingCurves mean what they say:
                    // filter = exp(-occlusion * BlockAbsorption * 2).
                    float occDelta = Math.Abs(logDiff) / absorptionScale;
                    float alpha = SmoothingCurves.GainAlpha(occDelta, muffling);
                    float step = logDiff * alpha;

                    // Slew ceiling (audit A14). The table sets the shape of the
                    // transition, this bounds its speed in dB per second. A large step
                    // rides the ceiling at a constant dB rate and drops back onto the
                    // table for the last part, so it still eases out. The ceiling falls
                    // with the square root of the distance, because the shadow boundary
                    // a sound goes quiet across widens the same way.
                    float maxStep = SmoothingCurves.MaxLogStepPerTick(
                        DistanceToListener(entry, listenerPos), listenerSpeed, muffling);
                    if (step > maxStep) step = maxStep;
                    else if (step < -maxStep) step = -maxStep;

                    current = MathF.Exp(logCurrent + step);
                    entry.CurrentValue = current;
                    smoothed++;
                }
                else
                {
                    entry.CurrentValue = effectiveTarget;
                    current = effectiveTarget;
                }

                // === THROTTLE ENVELOPE ===
                // Multiplies the smoothed filter; it never feeds back into it. A throttled
                // sound is not raycast at all, so this ramp is what keeps it moving.
                float envelope = 1f;
                bool throttled = throttle != null && throttle.IsThrottled(sound);
                if (entry.Fade != null || throttled)
                {
                    entry.Fade ??= new ThrottleFadeState();
                    envelope = entry.Fade.Step(throttled, nowMs, fadeElapsedMs, fadeDurationMs,
                        entry.SoundName ?? "?");

                    // Back at full level and no longer throttled — drop the state object.
                    if (entry.Fade.IsIdle) entry.Fade = null;
                }

                float applied = envelope >= 1f ? current : minFilter + (current - minFilter) * envelope;

                if (Math.Abs(applied - entry.AppliedValue) >= CONVERGE_EPSILON * 0.5f)
                {
                    if (EfxHelper.SetLowpassGainHF(entry.FilterId, applied))
                    {
                        entry.AppliedValue = applied;
                        // Reattach — VS may have overwritten our filter
                        AttachFilter(entry.SourceId, entry.FilterId);
                    }
                }

                // === REVERB: same rule, one stage ===
                if (entry.HasReverbTarget && ReverbEffects.IsInitialized && entry.SourceId > 0)
                {
                    SmoothReverb(entry);
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
                        entry.SmoothedTargetPos = null;
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
                                // Smoothly returned to original position — clear state.
                                // Capture the position BEFORE nulling the fields, otherwise
                                // the final apply below is a null no-op.
                                Vec3d finalPos = entry.OriginalSoundPos;
                                entry.TargetRepositionedPos = null;
                                entry.CurrentRepositionedPos = null;
                                entry.OriginalSoundPos = null;
                                entry.SmoothedTargetPos = null;
                                // Apply original pos one last time
                                SetALSourcePosition(entry.SourceId, finalPos);
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

            forceReverbApply = false;

            // Only log smooth stats every ~5s (200 ticks at 25ms) to avoid per-tick noise
            smoothLogAccumulator++;
            if (smoothLogAccumulator >= 200 && (smoothed > 0 || posSmoothed > 0))
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"SMOOTH: {smoothed}/{activeFilters.Count} filters, {posSmoothed} positions (5s sample)");
                smoothLogAccumulator = 0;
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
                        entry.AppliedValue = newValue;
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

            if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                // Remove from reverse lookup map
                if (entry.SourceId > 0)
                {
                    sourceIdToSound.TryRemove(entry.SourceId, out _);
                }

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
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
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
                if (!IsSoundAlive(kvp.Key, kvp.Value))
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var sound in toRemove)
            {
                if (activeFilters.TryRemove(sound, out var entry))
                {
                    CleanupEntry(entry);
                }
            }
        }

        /// <summary>
        /// Hand every live source back to vanilla: no occlusion filter, no reverb send,
        /// no pitch offset, original position. Called by the master toggle.
        ///
        /// The registry itself is KEPT. The sounds are still playing and VS still owns
        /// them, so a re-enable must find them again. Only the OpenAL state we wrote is
        /// undone, and each entry is reset to "no muffle" so re-enable starts from dry.
        ///
        /// Unlike CleanupEntry, detaching the filter here IS correct: we verify the
        /// sound is alive and that its sourceId still matches, so the source cannot
        /// have been recycled to a different sound.
        /// </summary>
        public static int RestoreAllToVanilla()
        {
            if (!IsInitialized) return 0;

            int restored = 0;
            bool canSetPosition = InitializeALSource3f();

            foreach (var kvp in activeFilters)
            {
                var sound = kvp.Key;
                var entry = kvp.Value;

                try
                {
                    if (entry.SourceId <= 0) continue;
                    if (!IsSoundAlive(sound, entry)) continue;
                    if (GetSourceId(sound) != entry.SourceId) continue;

                    DetachFilter(entry.SourceId);
                    ReverbEffects.DetachFromSource(entry.SourceId);
                    ApplyPitchOffset(sound, 0f);

                    // Put a repositioned sound back where VS thinks it is. VS only rewrites
                    // the OpenAL position when it calls SetPosition, which a static looping
                    // sound never does — so an unwritten source would stay repositioned.
                    if (canSetPosition && entry.CurrentRepositionedPos != null && entry.OriginalSoundPos != null)
                    {
                        SetALSourcePosition(entry.SourceId, entry.OriginalSoundPos);
                    }

                    entry.TargetRepositionedPos = null;
                    entry.CurrentRepositionedPos = null;
                    entry.SmoothedTargetPos = null;
                    entry.CurrentValue = 1.0f;
                    entry.AppliedValue = 1.0f;
                    entry.TargetValue = 1.0f;
                    entry.HasReverbTarget = false;
                    entry.ReverbConverged = false;
                    entry.Fade = null;

                    restored++;
                }
                catch (Exception ex)
                {
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog($"[SoundFilterManager] RestoreAllToVanilla failed on {entry.SoundName}: {ex.Message}");
                }
            }

            return restored;
        }

        /// <summary>
        /// Re-attach our filter to every live source after the master toggle turns back on.
        /// The occlusion tick sets real values within one raycast interval; this only
        /// puts our filter object back in the send chain so nothing plays unfiltered
        /// through a stale vanilla filter in the meantime.
        /// </summary>
        public static int ReattachAllFilters()
        {
            if (!IsInitialized) return 0;

            int reattached = 0;
            foreach (var kvp in activeFilters)
            {
                try
                {
                    if (!IsSoundAlive(kvp.Key, kvp.Value)) continue;
                    if (ReattachFilter(kvp.Key)) reattached++;
                }
                catch
                {
                    // Sound died mid-iteration — CleanupDisposed will collect it.
                }
            }

            return reattached;
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
            sourceIdToSound.Clear();

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

            return sourceIdToSound.ContainsKey(sourceId);
        }

        /// <summary>
        /// Get the filter ID for a given sourceId.
        /// Used by AL.SourcePlay hook to attach filter right before play.
        /// </summary>
        public static int GetFilterForSource(int sourceId)
        {
            if (!IsInitialized || sourceId <= 0)
                return 0;

            if (sourceIdToSound.TryGetValue(sourceId, out var sound))
            {
                if (activeFilters.TryGetValue(sound, out var entry))
                    return entry.FilterId;
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
