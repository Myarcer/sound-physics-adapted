using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;

namespace soundphysicsadapted
{
    /// <summary>
    /// Wrapper for OpenAL EFX filter operations via reflection.
    /// Provides GenFilter, Filter (set params), DeleteFilter capabilities.
    /// </summary>
    public static class EfxHelper
    {
        // Cached reflection info
        private static MethodInfo genFilterMethod;
        private static MethodInfo filterFloatMethod;
        private static MethodInfo deleteFilterMethod;
        private static MethodInfo alGetErrorMethod;
        private static Type filterFloatType;
        private static Type filterIntegerType;
        private static object lowpassGainHFValue;
        private static object filterTypeValue;

        // Filter type constant for lowpass = 1
        private const int LOWPASS_FILTER_TYPE = 1;

        public static bool IsAvailable { get; private set; } = false;

        /// <summary>
        /// Initialize EFX reflection. Must be called once at startup.
        /// </summary>
        public static bool Initialize(ICoreClientAPI api)
        {
            try
            {
                // Find OpenTK assembly
                Assembly openTkAsm = null;
                Type efxType = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("OpenTK")) continue;

                    // Try to find EFX type
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "EFX")
                        {
                            efxType = type;
                            openTkAsm = asm;
                            api.Logger.Debug($"[EfxHelper] Found EFX type: {type.FullName}");
                            break;
                        }
                    }
                    if (efxType != null) break;
                }

                if (efxType == null)
                {
                    api.Logger.Warning("[EfxHelper] EFX type not found in any OpenTK assembly");
                    return false;
                }

                // Find FilterFloat and FilterInteger enum types
                foreach (var type in openTkAsm.GetTypes())
                {
                    if (type.Name == "FilterFloat" && type.IsEnum)
                    {
                        filterFloatType = type;
                        api.Logger.Debug($"[EfxHelper] Found FilterFloat: {type.FullName}");
                    }
                    else if (type.Name == "FilterInteger" && type.IsEnum)
                    {
                        filterIntegerType = type;
                        api.Logger.Debug($"[EfxHelper] Found FilterInteger: {type.FullName}");
                    }
                }

                if (filterFloatType == null || filterIntegerType == null)
                {
                    api.Logger.Warning("[EfxHelper] FilterFloat or FilterInteger enum not found");
                    return false;
                }

                // Get LowpassGainHF enum value
                try
                {
                    lowpassGainHFValue = Enum.Parse(filterFloatType, "LowpassGainHF");
                }
                catch
                {
                    // Try numeric value (LowpassGainHF = 2 in OpenAL spec)
                    lowpassGainHFValue = Enum.ToObject(filterFloatType, 2);
                }
                api.Logger.Debug($"[EfxHelper] LowpassGainHF value: {lowpassGainHFValue}");

                // Get FilterType enum value
                try
                {
                    filterTypeValue = Enum.Parse(filterIntegerType, "FilterType");
                }
                catch
                {
                    // Try numeric value (FilterType = 0x8001 in OpenAL spec)
                    filterTypeValue = Enum.ToObject(filterIntegerType, 0x8001);
                }
                api.Logger.Debug($"[EfxHelper] FilterType value: {filterTypeValue}");

                // Get EFX.GenFilter() method - returns int
                genFilterMethod = efxType.GetMethod("GenFilter",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);

                if (genFilterMethod == null)
                {
                    api.Logger.Warning("[EfxHelper] GenFilter method not found");
                    return false;
                }
                api.Logger.Debug($"[EfxHelper] Found GenFilter: {genFilterMethod}");

                // Get EFX.Filter(int, FilterFloat, float) for setting GAINHF
                filterFloatMethod = efxType.GetMethod("Filter",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int), filterFloatType, typeof(float) },
                    null);

                if (filterFloatMethod == null)
                {
                    api.Logger.Warning("[EfxHelper] Filter(int, FilterFloat, float) method not found");
                    return false;
                }
                api.Logger.Debug($"[EfxHelper] Found Filter (float): {filterFloatMethod}");

                // Get EFX.DeleteFilter(int) method
                deleteFilterMethod = efxType.GetMethod("DeleteFilter",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int) },
                    null);

                if (deleteFilterMethod == null)
                {
                    // Try ref parameter version
                    deleteFilterMethod = efxType.GetMethod("DeleteFilter",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new Type[] { typeof(int).MakeByRefType() },
                        null);
                }

                if (deleteFilterMethod == null)
                {
                    api.Logger.Warning("[EfxHelper] DeleteFilter method not found - will leak filters!");
                }
                else
                {
                    api.Logger.Debug($"[EfxHelper] Found DeleteFilter: {deleteFilterMethod}");
                }

                // Also need Filter(int, FilterInteger, int) for setting filter type
                var filterIntMethod = efxType.GetMethod("Filter",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int), filterIntegerType, typeof(int) },
                    null);

                if (filterIntMethod != null)
                {
                    api.Logger.Debug($"[EfxHelper] Found Filter (int): {filterIntMethod}");
                }

                // Also get AL.GetError for diagnostic purposes
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("OpenTK")) continue;
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "AL" && type.Namespace?.Contains("OpenAL") == true)
                        {
                            alGetErrorMethod = type.GetMethod("GetError",
                                BindingFlags.Public | BindingFlags.Static,
                                null, Type.EmptyTypes, null);
                            if (alGetErrorMethod != null)
                            {
                                api.Logger.Debug($"[EfxHelper] Found AL.GetError: {alGetErrorMethod}");
                            }
                            break;
                        }
                    }
                    if (alGetErrorMethod != null) break;
                }

                IsAvailable = true;
                api.Logger.Notification("[EfxHelper] EFX reflection initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[EfxHelper] Initialization failed: {ex.Message}");
                return false;
            }
        }

        private static int totalFiltersGenerated = 0;

        /// <summary>
        /// Generate a new OpenAL filter. Returns filter ID or 0 on failure.
        /// </summary>
        public static int GenFilter()
        {
            if (!IsAvailable || genFilterMethod == null)
                return 0;

            try
            {
                int filterId = (int)genFilterMethod.Invoke(null, null);
                totalFiltersGenerated++;
                if (filterId == 0)
                {
                    SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] GenFilter returned 0! Total generated: {totalFiltersGenerated}");
                }
                return filterId;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] GenFilter EXCEPTION: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Configure filter as lowpass with given GAINHF value.
        /// </summary>
        public static bool ConfigureLowpass(int filterId, float gainHF)
        {
            if (!IsAvailable || filterId == 0)
                return false;

            try
            {
                // Set filter type to lowpass (value = 1)
                // We need to find and call Filter(int, FilterInteger, int)
                var efxType = filterFloatMethod.DeclaringType;
                var filterIntMethod = efxType.GetMethod("Filter",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(int), filterIntegerType, typeof(int) },
                    null);

                if (filterIntMethod != null)
                {
                    filterIntMethod.Invoke(null, new object[] { filterId, filterTypeValue, LOWPASS_FILTER_TYPE });
                }
                else
                {
                    SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] WARNING: filterIntMethod is NULL - cannot set filter type!");
                }

                // Set LowpassGainHF
                filterFloatMethod.Invoke(null, new object[] { filterId, lowpassGainHFValue, gainHF });
                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] ConfigureLowpass failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Update filter's LowpassGainHF value.
        /// </summary>
        public static bool SetLowpassGainHF(int filterId, float gainHF)
        {
            if (!IsAvailable || filterId == 0 || filterFloatMethod == null)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] SetLowpassGainHF SKIPPED: available={IsAvailable}, filterId={filterId}, method={filterFloatMethod != null}");
                return false;
            }

            try
            {
                filterFloatMethod.Invoke(null, new object[] { filterId, lowpassGainHFValue, gainHF });
                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] SetLowpassGainHF EXCEPTION: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check for OpenAL errors. Returns error code (0 = no error).
        /// </summary>
        public static int GetALError()
        {
            if (alGetErrorMethod == null) return 0;
            try
            {
                return (int)alGetErrorMethod.Invoke(null, null);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Check and log any OpenAL errors.
        /// </summary>
        public static void CheckALError(string context)
        {
            int error = GetALError();
            if (error != 0)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] OpenAL ERROR {error} after {context}");
            }
        }

        /// <summary>
        /// Delete an OpenAL filter to free resources.
        /// </summary>
        public static bool DeleteFilter(int filterId)
        {
            if (!IsAvailable || filterId == 0 || deleteFilterMethod == null)
                return false;

            try
            {
                // Check if method takes ref parameter
                var parms = deleteFilterMethod.GetParameters();
                if (parms.Length == 1 && parms[0].ParameterType.IsByRef)
                {
                    object[] args = new object[] { filterId };
                    deleteFilterMethod.Invoke(null, args);
                }
                else
                {
                    deleteFilterMethod.Invoke(null, new object[] { filterId });
                }
                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] DeleteFilter failed: {ex.Message}");
                return false;
            }
        }

        // ============================================================
        // PHASE 3: REVERB EFFECT METHODS
        // Extended EFX support for auxiliary effect slots and reverb
        // ============================================================

        // Cached reflection for reverb methods
        private static Type _efxType;
        private static Type _alcType;
        private static MethodInfo _genAuxSlotMethod;
        private static MethodInfo _deleteAuxSlotMethod;
        private static MethodInfo _auxSlotIntMethod;
        private static MethodInfo _genEffectMethod;
        private static MethodInfo _deleteEffectMethod;
        private static MethodInfo _effectIntMethod;
        private static MethodInfo _effectFloatMethod;
        private static MethodInfo _source3iMethod;
        private static MethodInfo _alcGetIntegerMethod;
        private static Type _effectIntegerType;
        private static Type _effectFloatType;
        private static Type _alSource3iType;  // For aux send connections
        private static Type _effectSlotIntType;  // For aux slot settings
        private static object _auxSendFilterValue;  // ALSource3i.AuxiliarySendFilter enum value
        private static object _effectSlotEffectValue;  // EffectSlotInteger.Effect enum value (0x0001)
        private static bool _reverbInitialized = false;

        // ============================================================
        // PHASE 4B STAGE 2: AL Source Management (for permeated sources)
        // Cached reflection for creating/managing secondary OpenAL sources.
        // ============================================================
        private static bool _alSourceMgmtInitialized = false;
        private static Type _alType;            // OpenTK AL class
        private static Type _alSourceiType;     // ALSourcei enum
        private static Type _alSourcefType;     // ALSourcef enum
        private static Type _alGetSourceiType;  // ALGetSourcei enum (may be same as ALSourcei)

        // Method cache
        private static MethodInfo _genSourcesMethod;      // AL.GenSources(int n, out int sources) or GenSource()
        private static MethodInfo _deleteSourceMethod;     // AL.DeleteSource(int source)
        private static MethodInfo _getSourceiMethod;       // AL.GetSource(int, ALGetSourcei, out int)
        private static MethodInfo _getSourcefMethod;       // AL.GetSource(int, ALSourcef, out float)
        private static MethodInfo _sourcefMethod;          // AL.Source(int, ALSourcef, float)
        private static MethodInfo _sourceiMethod;          // AL.Source(int, ALSourcei, int)
        private static MethodInfo _sourcePlayMethod;       // AL.SourcePlay(int)
        private static MethodInfo _sourceStopMethod;       // AL.SourceStop(int)
        private static MethodInfo _sourcePauseMethod;      // AL.SourcePause(int)

        // Enum value cache
        private static object _alSourceiBuffer;       // ALSourcei.Buffer
        private static object _alSourceiLooping;      // ALSourcei.Looping (or SourceBoolean)
        private static object _alSourcefGain;         // ALSourcef.Gain
        private static object _alSourcefSecOffset;    // ALSourcef.SecOffset
        private static object _alGetSourceiState;     // ALGetSourcei.SourceState
        private static object _alGetSourceiLooping;   // looping getter
        private static object _alGetSourceiBuffer;    // ALGetSourcei.Buffer
        private static object _alSourcefRefDist;      // ALSourcef.ReferenceDistance
        private static object _alSourcefMaxDist;      // ALSourcef.MaxDistance
        private static object _alSourcefRolloff;      // ALSourcef.RolloffFactor

        // OpenAL state constants
        private const int AL_PLAYING = 0x1012;
        private const int AL_STOPPED = 0x1014;
        private const int AL_PAUSED  = 0x1013;
        private const int AL_INITIAL = 0x1011;

        /// <summary>
        /// Initialize AL source management reflection for creating/managing permeated sources.
        /// Lazy init — called on first use.
        /// </summary>
        private static bool InitializeALSourceManagement()
        {
            if (_alSourceMgmtInitialized) return _genSourcesMethod != null;
            _alSourceMgmtInitialized = true;

            try
            {
                // Find the OpenTK assembly and AL types
                Assembly openTkAsm = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("OpenTK")) continue;

                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "AL" && type.Namespace?.Contains("OpenAL") == true)
                            _alType = type;
                        else if (type.Name == "ALSourcei" && type.IsEnum)
                            _alSourceiType = type;
                        else if (type.Name == "ALSourcef" && type.IsEnum)
                            _alSourcefType = type;
                        else if (type.Name == "ALGetSourcei" && type.IsEnum)
                            _alGetSourceiType = type;
                    }

                    if (_alType != null) { openTkAsm = asm; break; }
                }

                if (_alType == null)
                {
                    SoundPhysicsAdaptedModSystem.Log("[EfxHelper] AL source mgmt init FAILED: AL type not found");
                    return false;
                }

                // --- GenSource / GenSources ---
                // Try GenSource() first (returns int), then GenSources(int, out int)
                _genSourcesMethod = _alType.GetMethod("GenSource",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (_genSourcesMethod == null)
                {
                    // Try GenSources(int n, out int sources) - we'll handle the out param in wrapper
                    _genSourcesMethod = _alType.GetMethod("GenSources",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), typeof(int).MakeByRefType() }, null);
                }

                // --- DeleteSource ---
                _deleteSourceMethod = _alType.GetMethod("DeleteSource",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(int) }, null);
                if (_deleteSourceMethod == null)
                {
                    // Try ref variant
                    _deleteSourceMethod = _alType.GetMethod("DeleteSource",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int).MakeByRefType() }, null);
                }

                // --- GetSource (int out) --- for state, looping, buffer
                if (_alGetSourceiType != null)
                {
                    _getSourceiMethod = _alType.GetMethod("GetSource",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), _alGetSourceiType, typeof(int).MakeByRefType() }, null);
                }
                // Fallback: some OpenTK versions use ALSourcei for gets too
                if (_getSourceiMethod == null && _alSourceiType != null)
                {
                    _getSourceiMethod = _alType.GetMethod("GetSource",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), _alSourceiType, typeof(int).MakeByRefType() }, null);
                }

                // --- GetSource (float out) --- for gain, sec offset
                if (_alSourcefType != null)
                {
                    _getSourcefMethod = _alType.GetMethod("GetSource",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), _alSourcefType, typeof(float).MakeByRefType() }, null);
                }

                // --- Source(int, ALSourcef, float) --- set gain, sec offset
                if (_alSourcefType != null)
                {
                    _sourcefMethod = _alType.GetMethod("Source",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), _alSourcefType, typeof(float) }, null);
                }

                // --- Source(int, ALSourcei, int) --- set buffer, looping
                if (_alSourceiType != null)
                {
                    _sourceiMethod = _alType.GetMethod("Source",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), _alSourceiType, typeof(int) }, null);
                }

                // --- SourcePlay, SourceStop, SourcePause ---
                _sourcePlayMethod = _alType.GetMethod("SourcePlay",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(int) }, null);
                _sourceStopMethod = _alType.GetMethod("SourceStop",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(int) }, null);
                _sourcePauseMethod = _alType.GetMethod("SourcePause",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(int) }, null);

                // --- Resolve enum values ---
                if (_alSourceiType != null)
                {
                    try { _alSourceiBuffer = Enum.Parse(_alSourceiType, "Buffer"); } catch { }
                    try { _alSourceiLooping = Enum.Parse(_alSourceiType, "Looping"); } catch { }
                }
                if (_alSourcefType != null)
                {
                    try { _alSourcefGain = Enum.Parse(_alSourcefType, "Gain"); } catch { }
                    try { _alSourcefSecOffset = Enum.Parse(_alSourcefType, "SecOffset"); } catch { }
                    try { _alSourcefRefDist = Enum.Parse(_alSourcefType, "ReferenceDistance"); } catch { }
                    try { _alSourcefMaxDist = Enum.Parse(_alSourcefType, "MaxDistance"); } catch { }
                    try { _alSourcefRolloff = Enum.Parse(_alSourcefType, "RolloffFactor"); } catch { }
                }
                if (_alGetSourceiType != null)
                {
                    try { _alGetSourceiState = Enum.Parse(_alGetSourceiType, "SourceState"); } catch { }
                    try { _alGetSourceiLooping = Enum.Parse(_alGetSourceiType, "Looping"); } catch { }
                    try { _alGetSourceiBuffer = Enum.Parse(_alGetSourceiType, "Buffer"); } catch { }
                }

                // Log results
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] AL source mgmt: " +
                    $"genSrc={_genSourcesMethod != null} delSrc={_deleteSourceMethod != null} " +
                    $"getSrcI={_getSourceiMethod != null} getSrcF={_getSourcefMethod != null} " +
                    $"srcF={_sourcefMethod != null} srcI={_sourceiMethod != null} " +
                    $"play={_sourcePlayMethod != null} stop={_sourceStopMethod != null} " +
                    $"pause={_sourcePauseMethod != null}");

                bool allCritical = _genSourcesMethod != null && _deleteSourceMethod != null &&
                                   _sourcePlayMethod != null && _sourceStopMethod != null;
                if (!allCritical)
                {
                    SoundPhysicsAdaptedModSystem.Log("[EfxHelper] WARNING: Some critical AL source methods not found — permeated sources disabled");
                }

                return _genSourcesMethod != null;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] AL source mgmt init EXCEPTION: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Whether AL source management is available (retained for potential future use).
        /// </summary>
        public static bool IsSourceManagementAvailable
        {
            get { InitializeALSourceManagement(); return _genSourcesMethod != null; }
        }

        // ---- Public wrappers for AL source operations ----

        /// <summary>Generate a new OpenAL source. Returns source ID or 0 on failure.</summary>
        public static int ALGenSource()
        {
            if (!InitializeALSourceManagement() || _genSourcesMethod == null) return 0;
            try
            {
                var parms = _genSourcesMethod.GetParameters();
                if (parms.Length == 0)
                {
                    // GenSource() -> int
                    return (int)_genSourcesMethod.Invoke(null, null);
                }
                else
                {
                    // GenSources(int n, out int source)
                    object[] args = new object[] { 1, 0 };
                    _genSourcesMethod.Invoke(null, args);
                    return (int)args[1];
                }
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] ALGenSource failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Delete an OpenAL source. Stops it first.</summary>
        public static void ALDeleteSource(int source)
        {
            if (source <= 0 || _deleteSourceMethod == null) return;
            try
            {
                ALSourceStop(source); // OpenAL requires stop before delete
                var parms = _deleteSourceMethod.GetParameters();
                if (parms.Length == 1 && parms[0].ParameterType.IsByRef)
                {
                    object[] args = new object[] { source };
                    _deleteSourceMethod.Invoke(null, args);
                }
                else
                {
                    _deleteSourceMethod.Invoke(null, new object[] { source });
                }
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] ALDeleteSource({source}) failed: {ex.Message}");
            }
        }

        /// <summary>Get source state (AL_PLAYING, AL_STOPPED, etc).</summary>
        public static int ALGetSourceState(int source)
        {
            if (source <= 0 || _getSourceiMethod == null || _alGetSourceiState == null) return AL_STOPPED;
            try
            {
                object[] args = new object[] { source, _alGetSourceiState, 0 };
                _getSourceiMethod.Invoke(null, args);
                return (int)args[2];
            }
            catch { return AL_STOPPED; }
        }

        /// <summary>Get source integer property (buffer, looping, etc).</summary>
        public static int ALGetSourcei(int source, object enumValue)
        {
            if (source <= 0 || _getSourceiMethod == null || enumValue == null) return 0;
            try
            {
                object[] args = new object[] { source, enumValue, 0 };
                _getSourceiMethod.Invoke(null, args);
                return (int)args[2];
            }
            catch { return 0; }
        }

        /// <summary>Get source float property (gain, sec offset, etc).</summary>
        public static float ALGetSourcef(int source, object enumValue)
        {
            if (source <= 0 || _getSourcefMethod == null || enumValue == null) return 0f;
            try
            {
                object[] args = new object[] { source, enumValue, 0f };
                _getSourcefMethod.Invoke(null, args);
                return (float)args[2];
            }
            catch { return 0f; }
        }

        /// <summary>Set source float property.</summary>
        public static void ALSourcef(int source, object enumValue, float value)
        {
            if (source <= 0 || _sourcefMethod == null || enumValue == null) return;
            try { _sourcefMethod.Invoke(null, new object[] { source, enumValue, value }); }
            catch { }
        }

        /// <summary>Set source integer property.</summary>
        public static void ALSourcei(int source, object enumValue, int value)
        {
            if (source <= 0 || _sourceiMethod == null || enumValue == null) return;
            try { _sourceiMethod.Invoke(null, new object[] { source, enumValue, value }); }
            catch { }
        }

        /// <summary>Play an OpenAL source.</summary>
        public static void ALSourcePlay(int source)
        {
            if (source <= 0 || _sourcePlayMethod == null) return;
            try { _sourcePlayMethod.Invoke(null, new object[] { source }); }
            catch { }
        }

        /// <summary>Stop an OpenAL source.</summary>
        public static void ALSourceStop(int source)
        {
            if (source <= 0 || _sourceStopMethod == null) return;
            try { _sourceStopMethod.Invoke(null, new object[] { source }); }
            catch { }
        }

        /// <summary>Pause an OpenAL source.</summary>
        public static void ALSourcePause(int source)
        {
            if (source <= 0 || _sourcePauseMethod == null) return;
            try { _sourcePauseMethod.Invoke(null, new object[] { source }); }
            catch { }
        }

        // ---- Convenience wrappers with named parameters ----

        /// <summary>Get the buffer ID attached to a source.</summary>
        public static int ALGetSourceBuffer(int source) => ALGetSourcei(source, _alGetSourceiBuffer ?? _alSourceiBuffer);

        /// <summary>Set buffer on a source.</summary>
        public static void ALSetSourceBuffer(int source, int buffer) => ALSourcei(source, _alSourceiBuffer, buffer);

        /// <summary>Get source gain.</summary>
        public static float ALGetSourceGain(int source) => ALGetSourcef(source, _alSourcefGain);

        /// <summary>Set source gain.</summary>
        public static void ALSetSourceGain(int source, float gain) => ALSourcef(source, _alSourcefGain, gain);

        /// <summary>Get source playback offset in seconds.</summary>
        public static float ALGetSourceSecOffset(int source) => ALGetSourcef(source, _alSourcefSecOffset);

        /// <summary>Set source playback offset in seconds.</summary>
        public static void ALSetSourceSecOffset(int source, float offset) => ALSourcef(source, _alSourcefSecOffset, offset);

        /// <summary>Get source looping state (1=looping, 0=not).</summary>
        public static int ALGetSourceLooping(int source) => ALGetSourcei(source, _alGetSourceiLooping ?? _alSourceiLooping);

        /// <summary>Set source looping state.</summary>
        public static void ALSetSourceLooping(int source, int looping) => ALSourcei(source, _alSourceiLooping, looping);

        /// <summary>Get source reference distance (for distance attenuation).</summary>
        public static float ALGetSourceRefDistance(int source) => ALGetSourcef(source, _alSourcefRefDist);

        /// <summary>Set source reference distance.</summary>
        public static void ALSetSourceRefDistance(int source, float dist) => ALSourcef(source, _alSourcefRefDist, dist);

        /// <summary>Get source max distance.</summary>
        public static float ALGetSourceMaxDistance(int source) => ALGetSourcef(source, _alSourcefMaxDist);

        /// <summary>Set source max distance.</summary>
        public static void ALSetSourceMaxDistance(int source, float dist) => ALSourcef(source, _alSourcefMaxDist, dist);

        /// <summary>Get source rolloff factor.</summary>
        public static float ALGetSourceRolloff(int source) => ALGetSourcef(source, _alSourcefRolloff);

        /// <summary>Set source rolloff factor.</summary>
        public static void ALSetSourceRolloff(int source, float factor) => ALSourcef(source, _alSourcefRolloff, factor);

        /// <summary>Copy distance attenuation model from one source to another.</summary>
        public static void CopyDistanceModel(int fromSource, int toSource)
        {
            if (fromSource <= 0 || toSource <= 0) return;
            ALSetSourceRefDistance(toSource, ALGetSourceRefDistance(fromSource));
            ALSetSourceMaxDistance(toSource, ALGetSourceMaxDistance(fromSource));
            ALSetSourceRolloff(toSource, ALGetSourceRolloff(fromSource));
        }

        /// <summary>
        /// Check if EFX extension is supported.
        /// </summary>
        public static bool IsEfxSupported()
        {
            return IsAvailable;
        }

        /// <summary>
        /// Get the maximum number of auxiliary sends supported.
        /// </summary>
        public static int GetMaxAuxiliarySends()
        {
            if (!InitializeReverbReflection()) return 0;

            try
            {
                // ALC_MAX_AUXILIARY_SENDS = 0x20003
                // Need to get current device first
                // For simplicity, return a reasonable default
                // VS typically supports 4 aux sends
                return 4;
            }
            catch
            {
                return 2; // Safe fallback
            }
        }

        /// <summary>
        /// Create an auxiliary effect slot.
        /// </summary>
        public static int CreateAuxiliaryEffectSlot()
        {
            if (!InitializeReverbReflection())
            {
                SoundPhysicsAdaptedModSystem.Log("[EfxHelper] CreateAuxSlot FAILED: reflection not initialized");
                return 0;
            }

            if (_genAuxSlotMethod == null)
            {
                SoundPhysicsAdaptedModSystem.Log("[EfxHelper] CreateAuxSlot FAILED: GenAuxiliaryEffectSlot method not found");
                return 0;
            }

            try
            {
                int slot = (int)_genAuxSlotMethod.Invoke(null, null);
                if (slot > 0)
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Created aux slot: {slot}");
                }
                else
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] CreateAuxSlot returned 0 - OpenAL error?");
                    CheckALError("CreateAuxiliaryEffectSlot");
                }
                return slot;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] CreateAuxiliaryEffectSlot EXCEPTION: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Delete an auxiliary effect slot.
        /// </summary>
        public static void DeleteAuxSlot(int slot)
        {
            if (!_reverbInitialized || _deleteAuxSlotMethod == null || slot <= 0)
                return;

            try
            {
                var parms = _deleteAuxSlotMethod.GetParameters();
                if (parms.Length == 1 && parms[0].ParameterType.IsByRef)
                {
                    object[] args = new object[] { slot };
                    _deleteAuxSlotMethod.Invoke(null, args);
                }
                else
                {
                    _deleteAuxSlotMethod.Invoke(null, new object[] { slot });
                }
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] DeleteAuxSlot failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Set auxiliary slot to auto-adjust sends.
        /// </summary>
        public static void SetAuxSlotAutoSend(int slot, bool auto)
        {
            if (!_reverbInitialized || _auxSlotIntMethod == null || slot <= 0 || _effectSlotIntType == null)
                return;

            try
            {
                // Get EffectSlotInteger.AuxiliarySendAuto enum value
                object auxSendAutoValue;
                try
                {
                    auxSendAutoValue = Enum.Parse(_effectSlotIntType, "AuxiliarySendAuto");
                }
                catch
                {
                    // Fallback to numeric (AL_EFFECTSLOT_AUXILIARY_SEND_AUTO = 0x0003)
                    auxSendAutoValue = Enum.ToObject(_effectSlotIntType, 0x0003);
                }

                _auxSlotIntMethod.Invoke(null, new object[] { slot, auxSendAutoValue, auto ? 1 : 0 });
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] SetAuxSlotAutoSend failed: {ex.Message}");
            }
        }

        // Cached method for AuxiliaryEffectSlot with float parameter (for gain)
        private static MethodInfo _auxSlotFloatMethod;
        private static Type _effectSlotFloatType;
        private static object _effectSlotGainValue;

        /// <summary>
        /// Set auxiliary slot gain (master volume for reverb from this slot).
        /// CRITICAL: Must be > 0 for reverb to be audible!
        /// </summary>
        public static void SetAuxSlotGain(int slot, float gain)
        {
            if (!_reverbInitialized || slot <= 0)
                return;

            try
            {
                // Try to find AuxiliaryEffectSlot(int, EffectSlotFloat, float) if not cached
                if (_auxSlotFloatMethod == null && _efxType != null)
                {
                    // Find EffectSlotFloat enum type
                    foreach (var type in _efxType.Assembly.GetTypes())
                    {
                        if (type.Name == "EffectSlotFloat" && type.IsEnum)
                        {
                            _effectSlotFloatType = type;
                            break;
                        }
                    }

                    if (_effectSlotFloatType != null)
                    {
                        _auxSlotFloatMethod = _efxType.GetMethod("AuxiliaryEffectSlot",
                            BindingFlags.Public | BindingFlags.Static, null,
                            new Type[] { typeof(int), _effectSlotFloatType, typeof(float) }, null);

                        // Get EffectSlotFloat.Gain enum value (AL_EFFECTSLOT_GAIN = 0x0002)
                        try
                        {
                            _effectSlotGainValue = Enum.Parse(_effectSlotFloatType, "Gain");
                        }
                        catch
                        {
                            _effectSlotGainValue = Enum.ToObject(_effectSlotFloatType, 0x0002);
                        }

                        SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Found AuxSlotFloat method: {_auxSlotFloatMethod != null}, GainEnum: {_effectSlotGainValue}");
                    }
                }

                if (_auxSlotFloatMethod == null || _effectSlotGainValue == null)
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] SetAuxSlotGain FAILED: method={_auxSlotFloatMethod != null}, gainEnum={_effectSlotGainValue != null}");
                    return;
                }

                // Clear any previous error
                GetALError();

                _auxSlotFloatMethod.Invoke(null, new object[] { slot, _effectSlotGainValue, gain });

                int err = GetALError();
                if (err != 0)
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] SetAuxSlotGain slot={slot} gain={gain} OpenAL ERROR {err}");
                }
                else
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] SetAuxSlotGain slot={slot} gain={gain:F2} OK");
                }
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] SetAuxSlotGain EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>
        /// Create an EAX reverb effect.
        /// </summary>
        public static int CreateReverbEffect()
        {
            if (!InitializeReverbReflection())
            {
                SoundPhysicsAdaptedModSystem.Log("[EfxHelper] CreateReverbEffect FAILED: reflection not initialized");
                return 0;
            }

            if (_genEffectMethod == null)
            {
                SoundPhysicsAdaptedModSystem.Log("[EfxHelper] CreateReverbEffect FAILED: GenEffect method not found");
                return 0;
            }

            try
            {
                int effect = (int)_genEffectMethod.Invoke(null, null);
                if (effect <= 0)
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] CreateReverbEffect returned 0 - OpenAL error?");
                    CheckALError("GenEffect");
                    return 0;
                }

                // Set effect type to EAX Reverb (0x8000) or standard reverb (0x0001)
                if (_effectIntMethod != null)
                {
                    var effectTypeEnum = GetEffectIntegerValue("EffectType");
                    bool typeSet = false;

                    // Try EAX reverb first (better quality)
                    try
                    {
                        if (effectTypeEnum != null)
                        {
                            _effectIntMethod.Invoke(null, new object[] { effect, effectTypeEnum, 0x8000 });
                            int err = GetALError();
                            if (err == 0)
                            {
                                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Created EAX reverb effect: {effect}");
                                typeSet = true;
                            }
                        }
                    }
                    catch { /* EAX not supported, try standard */ }

                    // Fallback to standard reverb
                    if (!typeSet)
                    {
                        try
                        {
                            if (effectTypeEnum != null)
                            {
                                _effectIntMethod.Invoke(null, new object[] { effect, effectTypeEnum, 0x0001 });
                                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Created standard reverb effect: {effect}");
                                typeSet = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Failed to set reverb type: {ex.Message}");
                        }
                    }

                    if (!typeSet)
                    {
                        SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] WARNING: Could not set effect type for effect {effect}");
                    }
                }

                return effect;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] CreateReverbEffect EXCEPTION: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Delete an effect.
        /// </summary>
        public static void DeleteEffect(int effect)
        {
            if (!_reverbInitialized || _deleteEffectMethod == null || effect <= 0)
                return;

            try
            {
                var parms = _deleteEffectMethod.GetParameters();
                if (parms.Length == 1 && parms[0].ParameterType.IsByRef)
                {
                    object[] args = new object[] { effect };
                    _deleteEffectMethod.Invoke(null, args);
                }
                else
                {
                    _deleteEffectMethod.Invoke(null, new object[] { effect });
                }
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] DeleteEffect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Attach an effect to an auxiliary slot.
        /// </summary>
        public static void AttachEffectToAuxSlot(int slot, int effect)
        {
            if (!_reverbInitialized || _auxSlotIntMethod == null || slot <= 0)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] AttachEffectToAuxSlot SKIPPED: init={_reverbInitialized}, method={_auxSlotIntMethod != null}, slot={slot}");
                return;
            }

            if (_effectSlotEffectValue == null)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] AttachEffectToAuxSlot FAILED: EffectSlotInteger.Effect enum not found");
                return;
            }

            try
            {
                // Clear any previous error
                GetALError();

                // Use the proper enum value for EffectSlotInteger.Effect
                _auxSlotIntMethod.Invoke(null, new object[] { slot, _effectSlotEffectValue, effect });

                // Check for OpenAL errors
                int err = GetALError();
                if (err != 0)
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] AttachEffectToAuxSlot OpenAL ERROR {err}: slot={slot}, effect={effect}");
                }
                else
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Attached effect {effect} to slot {slot} OK");
                }
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] AttachEffectToAuxSlot EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>
        /// Connect a source to an auxiliary effect slot.
        /// </summary>
        public static void ConnectSourceToAuxSlot(int source, int slot, int sendIndex, int filter)
        {
            if (!_reverbInitialized || _source3iMethod == null || source <= 0)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] ConnectSourceToAuxSlot SKIPPED: " +
                    $"init={_reverbInitialized}, method={_source3iMethod != null}, src={source}");
                return;
            }

            try
            {
                // Use enum value if we found it, otherwise use raw constant
                object paramValue = _auxSendFilterValue ?? 0x20006;

                // Clear any previous error
                GetALError();

                // OpenTK signature: AL.Source(int sid, ALSource3i param, int val1, int val2, int val3)
                // val1 = effect slot, val2 = send index, val3 = filter
                _source3iMethod.Invoke(null, new object[] { source, paramValue, slot, sendIndex, filter });

                // Check for OpenAL errors
                int err = GetALError();
                if (err != 0)
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] ConnectSourceToAuxSlot OpenAL ERROR {err}: src={source}, slot={slot}, send={sendIndex}, filter={filter}");
                }
                // Success - no log needed (fires every reverb connection)
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] ConnectSourceToAuxSlot EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>
        /// Disconnect a source from an auxiliary slot.
        /// </summary>
        public static void DisconnectSourceFromAuxSlot(int source, int sendIndex)
        {
            ConnectSourceToAuxSlot(source, 0, sendIndex, 0);
        }

        /// <summary>
        /// Create a lowpass filter for send filtering.
        /// </summary>
        public static int CreateLowpassFilter()
        {
            int filter = GenFilter();
            if (filter > 0)
            {
                ConfigureLowpass(filter, 1.0f);
            }
            return filter;
        }

        /// <summary>
        /// Set filter gain and HF cutoff.
        /// </summary>
        public static void SetFilterGains(int filter, float gain, float gainHF)
        {
            if (!IsAvailable || filter <= 0) return;

            try
            {
                // Set LowpassGain (0x0001)
                var efxType = filterFloatMethod?.DeclaringType;
                if (efxType != null)
                {
                    // Try to find LowpassGain enum value
                    object lowpassGain = null;
                    try
                    {
                        lowpassGain = Enum.Parse(filterFloatType, "LowpassGain");
                    }
                    catch
                    {
                        lowpassGain = Enum.ToObject(filterFloatType, 1); // LowpassGain = 1
                    }

                    if (lowpassGain != null)
                    {
                        filterFloatMethod.Invoke(null, new object[] { filter, lowpassGain, gain });
                    }
                }

                // Set LowpassGainHF
                SetLowpassGainHF(filter, gainHF);
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[EfxHelper] SetFilterGains failed: {ex.Message}");
            }
        }

        // Reverb parameter setters - Use EAX reverb enum names (our effects are EAX type)
        // Enum values from OpenTK dump:
        //   EaxReverbDecayTime = 0x0006, EaxReverbDecayHFRatio = 0x0007
        //   EaxReverbDensity = 0x0001, EaxReverbDiffusion = 0x0002
        //   EaxReverbGain = 0x0003, EaxReverbGainHF = 0x0004
        //   EaxReverbReflectionsGain = 0x0008, EaxReverbLateReverbGain = 0x000C
        public static void SetReverbDecayTime(int effect, float value) => SetEffectFloat(effect, "EaxReverbDecayTime", 0x0006, value);
        public static void SetReverbDecayHFRatio(int effect, float value) => SetEffectFloat(effect, "EaxReverbDecayHFRatio", 0x0007, value);
        public static void SetReverbDensity(int effect, float value) => SetEffectFloat(effect, "EaxReverbDensity", 0x0001, value);
        public static void SetReverbDiffusion(int effect, float value) => SetEffectFloat(effect, "EaxReverbDiffusion", 0x0002, value);
        public static void SetReverbGain(int effect, float value) => SetEffectFloat(effect, "EaxReverbGain", 0x0003, value);
        public static void SetReverbGainHF(int effect, float value) => SetEffectFloat(effect, "EaxReverbGainHF", 0x0004, value);
        public static void SetReverbReflectionsGain(int effect, float value) => SetEffectFloat(effect, "EaxReverbReflectionsGain", 0x0009, value);
        public static void SetReverbReflectionsDelay(int effect, float value) => SetEffectFloat(effect, "EaxReverbReflectionsDelay", 0x000A, value);
        public static void SetReverbLateReverbGain(int effect, float value) => SetEffectFloat(effect, "EaxReverbLateReverbGain", 0x000C, value);
        public static void SetReverbLateReverbDelay(int effect, float value) => SetEffectFloat(effect, "EaxReverbLateReverbDelay", 0x000D, value);
        public static void SetReverbAirAbsorptionGainHF(int effect, float value) => SetEffectFloat(effect, "EaxReverbAirAbsorptionGainHF", 0x0013, value);
        public static void SetReverbRoomRolloffFactor(int effect, float value) => SetEffectFloat(effect, "EaxReverbRoomRolloffFactor", 0x0016, value);

        private static void SetEffectFloat(int effect, string paramName, int paramValue, float value)
        {
            if (!_reverbInitialized || _effectFloatMethod == null || effect <= 0)
                return;

            try
            {
                // Clear any previous error
                GetALError();

                object enumValue = GetEffectFloatValue(paramName);
                bool usedNumeric = false;
                if (enumValue == null)
                {
                    enumValue = Enum.ToObject(_effectFloatType, paramValue);
                    usedNumeric = true;
                }

                _effectFloatMethod.Invoke(null, new object[] { effect, enumValue, value });

                int err = GetALError();
                if (err != 0)
                {
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] SetEffectFloat({paramName}={value}) OpenAL ERROR {err} (numeric={usedNumeric})");
                }
                // Success - no log needed (fires on every reverb param update)
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] SetEffectFloat({paramName}) EXCEPTION: {ex.Message}");
            }
        }

        private static object GetEffectIntegerValue(string name)
        {
            if (_effectIntegerType == null) return null;
            try { return Enum.Parse(_effectIntegerType, name); }
            catch { return null; }
        }

        private static object GetEffectFloatValue(string name)
        {
            if (_effectFloatType == null) return null;
            try { return Enum.Parse(_effectFloatType, name); }
            catch { return null; }
        }

        /// <summary>
        /// Initialize reflection for reverb-related EFX methods.
        /// </summary>
        private static bool InitializeReverbReflection()
        {
            if (_reverbInitialized) return true;
            if (!IsAvailable) return false;

            try
            {
                _efxType = filterFloatMethod?.DeclaringType;
                if (_efxType == null)
                {
                    SoundPhysicsAdaptedModSystem.Log("[EfxHelper] REVERB INIT FAILED: EFX type is null");
                    return false;
                }

                var asm = _efxType.Assembly;
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Searching for reverb methods in {asm.GetName().Name}...");

                // Find enum types needed for reverb
                foreach (var type in asm.GetTypes())
                {
                    if (type.Name == "EffectInteger" && type.IsEnum) _effectIntegerType = type;
                    else if (type.Name == "EffectFloat" && type.IsEnum) _effectFloatType = type;
                    else if (type.Name == "ALSource3i" && type.IsEnum) _alSource3iType = type;
                    else if (type.Name == "EffectSlotInteger" && type.IsEnum)
                    {
                        _effectSlotIntType = type;
                        // Get the Effect enum value for AttachEffectToAuxSlot
                        try
                        {
                            _effectSlotEffectValue = Enum.Parse(type, "Effect");
                        }
                        catch
                        {
                            // Fallback to numeric value (AL_EFFECTSLOT_EFFECT = 0x0001)
                            _effectSlotEffectValue = Enum.ToObject(type, 0x0001);
                        }
                        SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Found EffectSlotInteger enum, Effect={_effectSlotEffectValue}");
                    }
                }

                // GenAuxiliaryEffectSlot - try multiple signatures
                _genAuxSlotMethod = _efxType.GetMethod("GenAuxiliaryEffectSlot",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

                // DeleteAuxiliaryEffectSlot
                _deleteAuxSlotMethod = _efxType.GetMethod("DeleteAuxiliaryEffectSlot",
                    BindingFlags.Public | BindingFlags.Static);

                // AuxiliaryEffectSlot - try with EffectSlotInteger enum first
                Type effectSlotIntType = null;
                foreach (var type in asm.GetTypes())
                {
                    if (type.Name == "EffectSlotInteger" && type.IsEnum)
                    {
                        effectSlotIntType = type;
                        break;
                    }
                }

                if (effectSlotIntType != null)
                {
                    _auxSlotIntMethod = _efxType.GetMethod("AuxiliaryEffectSlot",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), effectSlotIntType, typeof(int) }, null);
                }

                // Fallback: try raw int signature
                if (_auxSlotIntMethod == null)
                {
                    _auxSlotIntMethod = _efxType.GetMethod("AuxiliaryEffectSlot",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), typeof(int), typeof(int) }, null);
                }

                // GenEffect
                _genEffectMethod = _efxType.GetMethod("GenEffect",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

                // DeleteEffect
                _deleteEffectMethod = _efxType.GetMethod("DeleteEffect",
                    BindingFlags.Public | BindingFlags.Static);

                // Effect(int, EffectInteger, int)
                if (_effectIntegerType != null)
                {
                    _effectIntMethod = _efxType.GetMethod("Effect",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), _effectIntegerType, typeof(int) }, null);
                }

                // Effect(int, EffectFloat, float)
                if (_effectFloatType != null)
                {
                    _effectFloatMethod = _efxType.GetMethod("Effect",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), _effectFloatType, typeof(float) }, null);

                    // Log all available EffectFloat enum values for debugging
                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] EffectFloat enum values:");
                    foreach (var val in Enum.GetNames(_effectFloatType))
                    {
                        int numericVal = (int)Enum.Parse(_effectFloatType, val);
                        SoundPhysicsAdaptedModSystem.Log($"[EfxHelper]   {val} = 0x{numericVal:X4}");
                    }
                }

                // Find AL.Source for connecting aux sends - CRITICAL for reverb to work
                // OpenTK uses AL.Source(int sid, ALSource3i param, int val1, int val2, int val3)
                foreach (var type in asm.GetTypes())
                {
                    if (type.Name == "AL" && type.Namespace?.Contains("OpenAL") == true)
                    {
                        // First try with ALSource3i enum type
                        if (_alSource3iType != null)
                        {
                            _source3iMethod = type.GetMethod("Source",
                                BindingFlags.Public | BindingFlags.Static, null,
                                new Type[] { typeof(int), _alSource3iType, typeof(int), typeof(int), typeof(int) }, null);

                            if (_source3iMethod != null)
                            {
                                // Get the AuxiliarySendFilter enum value
                                try
                                {
                                    _auxSendFilterValue = Enum.Parse(_alSource3iType, "AuxiliarySendFilter");
                                }
                                catch
                                {
                                    // Try numeric fallback: AL_AUXILIARY_SEND_FILTER = 0x20006
                                    try { _auxSendFilterValue = Enum.ToObject(_alSource3iType, 0x20006); }
                                    catch { _auxSendFilterValue = null; }
                                }
                                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Found AL.Source with ALSource3i enum, AuxSendFilter={_auxSendFilterValue}");
                            }
                        }

                        // Fallback: try with raw int parameters
                        if (_source3iMethod == null)
                        {
                            _source3iMethod = type.GetMethod("Source",
                                BindingFlags.Public | BindingFlags.Static, null,
                                new Type[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) }, null);
                        }

                        // List all Source methods for debugging
                        if (_source3iMethod == null)
                        {
                            SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Searching for Source methods in AL type...");
                            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            {
                                if (method.Name == "Source" && method.GetParameters().Length >= 4)
                                {
                                    var parms = string.Join(", ", Array.ConvertAll(method.GetParameters(), p => p.ParameterType.Name));
                                    SoundPhysicsAdaptedModSystem.Log($"[EfxHelper]   AL.Source({parms})");
                                }
                            }
                        }

                        break;
                    }
                }

                _reverbInitialized = true;

                // Always log initialization status (important for debugging)
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] Reverb reflection: " +
                    $"genAux={_genAuxSlotMethod != null}, auxSlotInt={_auxSlotIntMethod != null}, " +
                    $"genEffect={_genEffectMethod != null}, effectInt={_effectIntMethod != null}, " +
                    $"effectFloat={_effectFloatMethod != null}, source3i={_source3iMethod != null}");

                if (_genAuxSlotMethod == null)
                    SoundPhysicsAdaptedModSystem.Log("[EfxHelper] WARNING: GenAuxiliaryEffectSlot not found - reverb will not work!");
                if (_source3iMethod == null)
                    SoundPhysicsAdaptedModSystem.Log("[EfxHelper] WARNING: AL.Source3i not found - cannot connect aux sends!");

                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"[EfxHelper] InitializeReverbReflection FAILED: {ex.Message}");
                return false;
            }
        }
    }
}
