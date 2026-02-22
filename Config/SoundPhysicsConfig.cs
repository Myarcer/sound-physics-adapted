namespace soundphysicsadapted
{
    /// <summary>
    /// Configuration for Sound Physics Adapted
    /// Loaded/saved as soundphysicsadapted.json
    /// </summary>
    public class SoundPhysicsConfig
    {
        // ============================================================
        // GENERAL
        // ============================================================

        /// <summary>
        /// Master enable/disable switch
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Enable debug logging for testing.
        /// Shows occlusion results, path resolution, filter values.
        /// Toggle with /soundphysics debug
        /// </summary>
        public bool DebugMode { get; set; } = false;

        /// <summary>
        /// Enable verbose per-block DDA raycast logging.
        /// WARNING: Generates massive log output (300+ lines per sound per update).
        /// Only enable briefly for debugging specific occlusion issues.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugVerbose { get; set; } = false;

        // ============================================================
        // OCCLUSION
        // Raycast-based sound muffling through blocks.
        // Uses DDA grid traversal with material-based absorption.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _OcclusionSystem { get; set; } = "--- Raycast occlusion through blocks. Muffles sounds behind walls based on material. ---";

        /// <summary>
        /// Enable debug logging for occlusion raycasting.
        /// Shows per-sound occlusion results, ray hits, material absorption.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugOcclusion { get; set; } = false;

        /// <summary>
        /// Maximum occlusion value (caps total block count)
        /// Higher = more muffling possible
        /// </summary>
        public float MaxOcclusion { get; set; } = 10.0f;

        /// <summary>
        /// Occlusion value per solid block
        /// Higher = each block muffles more
        /// </summary>
        public float OcclusionPerSolidBlock { get; set; } = 1.0f;

        /// <summary>
        /// Absorption coefficient for filter calculation.
        /// Higher = more aggressive lowpass filter per occlusion.
        /// 1.0 = each block significantly muffles.
        /// </summary>
        public float BlockAbsorption { get; set; } = 1.0f;

        /// <summary>
        /// Maximum distance to process sounds
        /// Sounds further than this use default (no occlusion)
        /// </summary>
        public float MaxSoundDistance { get; set; } = 64.0f;

        /// <summary>
        /// Maximum raycast iterations to find blocks
        /// Higher = more accurate but slower
        /// </summary>
        public int MaxOcclusionRays { get; set; } = 16;

        /// <summary>
        /// Minimum lowpass filter value (0 = silent, 1 = no filter)
        /// Prevents sounds from being completely inaudible
        /// 0.001 = 0.1% minimum volume for max occluded sounds
        /// </summary>
        public float MinLowPassFilter { get; set; } = 0.001f;

        /// <summary>
        /// Offset distance for multi-ray occlusion (soft edges).
        /// Shoots 9 rays with offset positions to detect thin walls at perpendicular angles.
        /// 0 = single ray (strict mode), 0.3-0.5 = recommended for soft occlusion.
        /// </summary>
        public float OcclusionVariation { get; set; } = 0.35f;

        // ============================================================
        // REVERB
        // Custom multi-slot reverb replacing the default system.
        // Uses 4 EAX reverb slots with different decay times.
        // Supports all materials (wood, glass, soil, stone).
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _ReverbSystem { get; set; } = "--- Custom multi-slot EAX reverb. Set EnableCustomReverb=false to disable. ---";

        /// <summary>
        /// Master toggle for custom reverb system.
        /// When enabled, the multi-slot reverb system handles all reverb.
        /// </summary>
        public bool EnableCustomReverb { get; set; } = true;

        /// <summary>
        /// Enable debug logging for reverb analysis.
        /// Shows ray hits, material contributions, and final values.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugReverb { get; set; } = false;

        /// <summary>
        /// Disable the default reverb system entirely.
        /// Required for custom reverb to work without interference.
        /// When true: default SetReverb() is disabled.
        /// When false: both systems run simultaneously (not recommended).
        /// </summary>
        public bool DisableVanillaReverb { get; set; } = true;

        /// <summary>
        /// Number of rays to cast for reverb calculation.
        /// More rays = more accurate but slower. Default 32.
        /// </summary>
        public int ReverbRayCount { get; set; } = 32;

        /// <summary>
        /// Number of times rays bounce off surfaces.
        /// More bounces = longer reverb tails. Default 4.
        /// </summary>
        public int ReverbBounces { get; set; } = 4;

        /// <summary>
        /// Maximum distance for reverb rays (blocks).
        /// Affects how far reverb can detect surfaces. Default 256.
        /// </summary>
        public float ReverbMaxDistance { get; set; } = 256f;

        /// <summary>
        /// Master reverb gain multiplier (0-2).
        /// 1.0 = normal, 0.5 = half reverb, 2.0 = double reverb.
        /// </summary>
        public float ReverbGain { get; set; } = 1.0f;

        // ============================================================
        // SUBMERSION AUDIO
        // Replaces default submersion audio (lowpass + pitch) for water and lava.
        // Stacks properly with occlusion and is fully configurable.
        // Lava uses separate, heavier values (denser medium = more muffling).
        // NOTE: Reverb is NOT affected — it uses a separate EFX system.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _UnderwaterSystem { get; set; } = "--- Replaces default submersion lowpass and pitch for water and lava. Set ReplaceVanillaLowpass=false to use default instead. ---";

        /// <summary>
        /// Replace default underwater/lava lowpass and pitch.
        /// When true: applies configurable values that stack with occlusion.
        /// When false: default underwater audio plays (may conflict with occlusion).
        /// </summary>
        public bool ReplaceVanillaLowpass { get; set; } = true;

        /// <summary>
        /// Lowpass filter value when fully underwater (0 = silent, 1 = no effect).
        /// Multiplies with the occlusion filter.
        /// Example: occlusion=0.3, underwater=0.08 → final=0.024 (very muffled).
        /// </summary>
        public float UnderwaterFilterValue { get; set; } = 0.08f;

        /// <summary>
        /// Whether underwater filter affects music sounds.
        /// When true: music gets muffled underwater.
        /// When false: music plays at full volume underwater.
        /// </summary>
        public bool UnderwaterFilterAffectsMusic { get; set; } = false;

        /// <summary>
        /// Pitch offset applied when underwater (-1 to 1).
        /// 0 = no pitch change, negative = lower pitch.
        /// </summary>
        public float UnderwaterPitchOffset { get; set; } = -0.15f;

        /// <summary>
        /// Whether underwater pitch offset affects music sounds.
        /// When true: music pitch drops underwater.
        /// When false: music plays at normal pitch underwater.
        /// </summary>
        public bool UnderwaterPitchAffectsMusic { get; set; } = false;

        /// <summary>
        /// Reverb high-frequency cutoff multiplier when underwater (0-1).
        /// Lower = duller reverb underwater. Default 0.4.
        /// </summary>
        public float UnderwaterReverbCutoff { get; set; } = 0.4f;

        /// <summary>
        /// Reverb gain multiplier when player is underwater (0-1).
        /// 0.0 = no reverb underwater, 1.0 = full reverb.
        /// Default 0.3 = 70% reduction (reverb doesn't work the same in water).
        /// </summary>
        public float UnderwaterReverbMultiplier { get; set; } = 0.3f;

        // --- LAVA SUBMERSION ---

        /// <summary>
        /// Enable separate lava submersion filter.
        /// When true: lava uses its own heavier filter/pitch values below.
        /// When false: lava uses the same values as water.
        /// </summary>
        public bool EnableLavaFilter { get; set; } = true;

        /// <summary>
        /// Lowpass filter value when submerged in lava (0 = silent, 1 = no effect).
        /// Much heavier than water — lava is extremely dense and viscous.
        /// Default 0.02 vs water's 0.08.
        /// </summary>
        public float LavaFilterValue { get; set; } = 0.02f;

        /// <summary>
        /// Pitch offset when submerged in lava (-1 to 1).
        /// Deeper shift than water — thick, sluggish medium.
        /// Default -0.30 vs water's -0.15.
        /// </summary>
        public float LavaPitchOffset { get; set; } = -0.30f;

        /// <summary>
        /// Reverb high-frequency cutoff multiplier when in lava (0-1).
        /// Near-zero — almost no high-frequency reverb in molten rock.
        /// Default 0.1 vs water's 0.4.
        /// </summary>
        public float LavaReverbCutoff { get; set; } = 0.1f;

        /// <summary>
        /// Reverb gain multiplier when in lava (0-1).
        /// Near-zero — sound doesn't reverberate in dense molten material.
        /// Default 0.05 vs water's 0.3.
        /// </summary>
        public float LavaReverbMultiplier { get; set; } = 0.05f;

        // ============================================================
        // SOUND PATH RESOLUTION
        // Repositions sounds to appear from openings (doors, windows).
        // Uses permeation weighting for natural blending.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _SoundPathSystem { get; set; } = "--- Sound repositioning toward openings (doors, windows). ---";

        /// <summary>
        /// Enable sound repositioning toward openings.
        /// When true: sounds behind walls appear to come from doors/windows.
        /// When false: sounds stay at original position (occlusion only).
        /// </summary>
        public bool EnableSoundRepositioning { get; set; } = true;

        /// <summary>
        /// Enable debug logging for sound path resolution.
        /// Shows path count, average occlusion, repositioning offset.
        /// Requires DebugMode=true to have any effect.
        /// Toggle with /soundphysics debugpaths
        /// </summary>
        public bool DebugSoundPaths { get; set; } = false;

        /// <summary>
        /// Enable path-based muffle (LPF from weighted average occlusion).
        /// When true: sounds through openings have additional muffling based on path.
        /// When false: use only direct-path occlusion for LPF.
        /// Default true - provides more realistic muffling through openings.
        /// </summary>
        public bool EnablePathMuffle { get; set; } = true;

        /// <summary>
        /// Permeation base for exponential falloff through materials.
        /// Lower = more attenuation per unit of occlusion.
        /// 0.4 = 40% transmission per block.
        /// </summary>
        public float PermeationBase { get; set; } = 0.4f;

        /// <summary>
        /// Minimum repositioning offset to apply (in blocks).
        /// Below this threshold, keep original position to avoid jitter.
        /// 0.5 = sound must be at least half a block away from original to reposition.
        /// </summary>
        public float MinRepositionOffset { get; set; } = 0.5f;

        /// <summary>
        /// Occlusion threshold to split paths into OPEN (for position) vs PERMEATED (for through-wall).
        /// Paths with occlusion below this threshold contribute to repositioned direction.
        /// Paths above this are "through-wall" — contribute to BlendedOcclusion muffle but not position.
        /// 1.5 = ~1.5 blocks of occlusion; paths through thicker walls are treated as permeated.
        /// </summary>
        public float PermeationOcclusionThreshold { get; set; } = 1.5f;

        // ============================================================
        // SOUND OVERRIDES
        // Optional replacement of vanilla sounds with custom versions.
        // Changes require game restart to take effect.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _SoundOverrideSystem { get; set; } = "--- Optional sound file overrides. Replace vanilla sounds with improved versions. Currently: beehive-wild.ogg sound. ---";

        /// <summary>
        /// Master toggle for sound file overrides.
        /// When false: all sounds use vanilla files.
        /// When true: enabled overrides replace vanilla sounds.
        /// Default false - opt-in feature.
        /// </summary>
        public bool EnableSoundOverrides { get; set; } = false;

        /// <summary>
        /// Override vanilla beehive-wild.ogg with improved version.
        /// Requires EnableSoundOverrides=true.
        /// </summary>
        public bool OverrideBeehiveSound { get; set; } = true;

        // ============================================================
        // RESONATOR ENHANCEMENTS
        // Improved resonator (music block) functionality.
        // Includes multi-client sync, pause/resume, and Carry On compatibility.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _ResonatorSystem { get; set; } = "--- Resonator enhancements: pause/resume, multi-client sync, Carry On boombox. ---";

        /// <summary>
        /// Master toggle for resonator enhancements.
        /// When true: enables pause/resume (Shift/Ctrl+RMB), multi-client playback sync.
        /// When false: resonator uses vanilla behavior only.
        /// </summary>
        public bool EnableResonatorFix { get; set; } = true;

        /// <summary>
        /// Enable debug logging for resonator features.
        /// Shows pause/resume events, Carry On pickup/placement, boombox state.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugResonator { get; set; } = false;

        /// <summary>
        /// Enable Carry On mod compatibility (boombox feature).
        /// When true: music continues playing while carrying a resonator.
        /// When false: music stops when picked up (vanilla behavior).
        /// Requires EnableResonatorFix=true and Carry On mod to be installed.
        /// </summary>
        public bool EnableCarryOnCompat { get; set; } = true;

        // ============================================================
        // WEATHER AUDIO
        // Replaces default weather sounds with managed loops using
        // OpenAL EFX lowpass filtering based on enclosure level.
        // "Rain on the roof" instead of just quieter rain.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _WeatherSystem { get; set; } = "--- Weather audio with lowpass filtering based on enclosure. Set EnableWeatherEnhancement=false to disable. ---";

        /// <summary>
        /// Master toggle for weather audio enhancement.
        /// When enabled: default weather loops are replaced with
        /// managed versions using lowpass filtering.
        /// When disabled: default weather sounds play normally.
        /// </summary>
        public bool EnableWeatherEnhancement { get; set; } = true;

        /// <summary>
        /// Enable debug logging for weather audio system.
        /// Shows enclosure values, LPF gainHF, sound start/stop events.
        /// Requires DebugMode=true to have any effect.
        /// Toggle with /soundphysics weather-debug
        /// </summary>
        public bool DebugWeather { get; set; } = false;

        /// <summary>
        /// Minimum LPF cutoff for rain when fully enclosed (Hz).
        /// Lower = more muffled. 300 Hz = bass rumble only.
        /// This is converted to OpenAL gainHF internally.
        /// </summary>
        public float WeatherLPFMinCutoff { get; set; } = 300f;

        /// <summary>
        /// Maximum LPF cutoff outdoors (Hz). 22000 = full spectrum.
        /// </summary>
        public float WeatherLPFMaxCutoff { get; set; } = 22000f;

        /// <summary>
        /// Maximum volume reduction for rain at full enclosure (0-1).
        /// LPF does most of the work; this is supplementary.
        /// 0.6 = 60% max volume reduction.
        /// </summary>
        public float WeatherVolumeLossMax { get; set; } = 0.6f;

        /// <summary>
        /// Minimum LPF cutoff for hail when fully enclosed (Hz).
        /// Hail is high-frequency — attenuates faster through walls than rain.
        /// </summary>
        public float HailLPFMinCutoff { get; set; } = 250f;

        /// <summary>
        /// Minimum LPF cutoff for wind when fully enclosed (Hz).
        /// Wind is broadband — bass persists more than rain highs.
        /// </summary>
        public float WindLPFMinCutoff { get; set; } = 600f;

        /// <summary>
        /// Minimum LPF cutoff for tremble when fully enclosed (Hz).
        /// Tremble is already sub-bass content. Very narrow band.
        /// </summary>
        public float TrembleLPFMinCutoff { get; set; } = 80f;

        // ============================================================
        // POSITIONAL WEATHER
        // Places directional rain/hail/wind sources at detected openings.
        // Creates "rain from the doorway" effect with automatic
        // occlusion and repositioning around corners.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _PositionalWeatherSystem { get; set; } = "--- Positional weather at openings. Rain/wind/hail from doors/roof holes. Set EnablePositionalWeather=false to disable. ---";

        /// <summary>
        /// Master toggle for positional weather sources at detected openings.
        /// When enabled: rain/wind/hail sources placed at verified openings.
        /// When disabled: only the non-positional ambient bed plays.
        /// Requires EnableWeatherEnhancement=true.
        /// </summary>
        public bool EnablePositionalWeather { get; set; } = true;

        /// <summary>
        /// Enable positional wind sources at detected openings.
        /// Wind enters through doors/holes/windows just like rain.
        /// Uses the same openings — "openings that let rain in almost always let wind in."
        /// Requires EnablePositionalWeather=true.
        /// </summary>
        public bool EnablePositionalWind { get; set; } = true;

        /// <summary>
        /// Enable positional hail sources at detected openings.
        /// Hail follows same physics as rain (falls vertically, same blocking).
        /// Requires EnablePositionalWeather=true.
        /// </summary>
        public bool EnablePositionalHail { get; set; } = true;

        /// <summary>
        /// Enable debug logging for positional weather sources.
        /// Shows opening tracking, clustering, source placement, persistence state.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugPositionalWeather { get; set; } = false;

        /// <summary>
        /// Enable visual block highlights showing DDA weather enclosure detection.
        /// Colors: Green=verified opening, Yellow=exposed candidate, Red=blocked,
        /// Blue=covered (roof), Cyan=neighbor find, Orange=partial (triggers neighbor search).
        /// Toggle with /soundphysics weather-viz
        /// </summary>
        public bool DebugWeatherVisualization { get; set; } = false;

        /// <summary>
        /// Maximum positional rain sources (per-type budget).
        /// Each source is an OpenAL voice with per-source occlusion/repositioning.
        /// 4 is typically enough to cover all openings in a building.
        /// </summary>
        public int MaxPositionalRainSources { get; set; } = 4;

        /// <summary>
        /// Maximum positional wind sources (per-type budget).
        /// Wind uses the same openings as rain with different audio assets.
        /// </summary>
        public int MaxPositionalWindSources { get; set; } = 4;

        /// <summary>
        /// Maximum positional hail sources (per-type budget).
        /// Hail uses the same openings as rain with different audio assets.
        /// </summary>
        public int MaxPositionalHailSources { get; set; } = 4;

        /// <summary>
        /// How long tracked openings persist after last verification (seconds).
        /// While persisted, positional sources stay active even when the
        /// opening is out of direct line-of-sight.
        /// Higher = openings survive longer around corners.
        /// Lower = faster cleanup of abandoned openings.
        /// </summary>
        public float OpeningPersistenceSeconds { get; set; } = 10f;

        /// <summary>
        /// Minimum sky coverage before positional sources activate (0-1).
        /// This number represents at once both SmoothedSkyCoverage and SmoothedOcclusionFactor
        /// When outdoors (low sky coverage), positional sources are unnecessary.
        /// 0.15 = sources only activate when at least 15% of sky is blocked.
        /// </summary>
        public float PositionalMinSkyCoverage { get; set; } = 0.15f;

        /// <summary>
        /// Volume multiplier for positional rain sources.
        /// 1.0 = full calculated volume. Reduce if rain sources are too loud
        /// relative to the ambient bed.
        /// </summary>
        public float PositionalWeatherVolume { get; set; } = 1.0f;

        /// <summary>
        /// Volume multiplier for positional wind sources.
        /// Slightly softer than rain since the ambient wind bed is always present.
        /// </summary>
        public float PositionalWindVolume { get; set; } = 0.8f;

        /// <summary>
        /// Volume multiplier for positional hail sources.
        /// Hail should be directionally prominent (percussive impacts).
        /// </summary>
        public float PositionalHailVolume { get; set; } = 1.0f;

        // ============================================================
        // THUNDER POSITIONING
        // Two-layer positioned thunder audio.
        // Layer 1: omnidirectional rumble with LPF (heard through walls).
        // Layer 2: positional one-shots at detected openings (crack from doorway).
        // Outdoors: thunder positioned toward bolt direction.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _ThunderSystem { get; set; } = "--- Thunder positioning. Two-layer system with positional cracks at openings. ---";

        /// <summary>
        /// Master toggle for thunder positioning system.
        /// When enabled: thunder is replaced with positioned audio,
        /// bolt strikes get directional cracks at detected openings.
        /// When disabled: default thunder plays normally.
        /// Requires EnableWeatherEnhancement=true.
        /// </summary>
        public bool EnableThunderPositioning { get; set; } = true;

        /// <summary>
        /// Enable debug logging for thunder audio events.
        /// Shows Layer 1/Layer 2 decisions, opening selection, bolt direction scoring.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugThunder { get; set; } = false;

        /// <summary>
        /// Minimum LPF cutoff for thunder Layer 1 when fully enclosed (Hz).
        /// Thunder is already low-frequency content; heavy filtering makes it
        /// a deep, barely-audible rumble. 200 Hz keeps some bass presence.
        /// </summary>
        public float ThunderLPFMinCutoff { get; set; } = 200f;

        /// <summary>
        /// Volume multiplier for thunder Layer 1 (indoor rumble).
        /// Scales the base volume of the omnidirectional muffled thunder.
        /// </summary>
        public float ThunderLayer1Volume { get; set; } = 1.0f;

        /// <summary>
        /// Volume multiplier for thunder Layer 2 (positional crack at openings).
        /// Scales the directional component heard through doors/roof holes.
        /// </summary>
        public float ThunderLayer2Volume { get; set; } = 1.0f;

        /// <summary>
        /// Maximum positional thunder sources (one-shot pool size).
        /// Thunder events are discrete and don't stack heavily.
        /// </summary>
        public int MaxThunderSources { get; set; } = 10;

        // ============================================================
        // PERFORMANCE
        // Per-tick processing budget to prevent frame drops during
        // spike scenarios (teleport, block break mass invalidation).
        // Sound playback throttle limits concurrent OpenAL sources.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _PerformanceSystem { get; set; } = "--- Per-tick budget cap and sound playback throttle. Prevents overload from dense areas. ---";

        /// <summary>
        /// Maximum number of sounds that can run full raycasting per tick.
        /// VS runs at 20 ticks/second — default 25 = up to 500 sounds/sec max throughput.
        /// Protects against spikes when many sounds become eligible at once
        /// (teleport, block break cache invalidation, entering dense areas).
        /// Sounds exceeding the budget are deferred to the next tick.
        /// Close sounds are prioritized. Overdue sounds (>2s stale) get priority but are still capped.
        /// 0 = unlimited (no budget cap).
        /// </summary>
        public int MaxSoundsPerTick { get; set; } = 25;

        /// <summary>
        /// Additional overdue sounds that can process on top of MaxSoundsPerTick each tick.
        /// Overdue = new sounds or sounds not updated in >2s.
        /// Real max per tick = MaxSoundsPerTick + MaxOverdueSoundsPerTick (default 25+6=31).
        /// Prevents spikes when many sounds appear simultaneously (approaching a farm).
        /// 0 = overdue sounds obey normal budget (strictest). Default 6.
        /// </summary>
        public int MaxOverdueSoundsPerTick { get; set; } = 6;

        /// <summary>
        /// Enable spatial reverb cell caching.
        /// Sounds in the same 4x4x4 block area share reverb calculations.
        /// Dramatically reduces CPU usage when many entities are clustered.
        /// </summary>
        public bool EnableReverbCellCache { get; set; } = true;

        /// <summary>
        /// Enable the sound playback throttle.
        /// Limits concurrent positional sounds to save OpenAL mixing overhead.
        /// When the budget is full, farthest sounds are blocked; closer sounds always win.
        /// </summary>
        public bool EnableSoundThrottle { get; set; } = true;

        /// <summary>
        /// Maximum concurrent positional sounds allowed to play simultaneously.
        /// Sounds beyond this limit are silently blocked based on distance.
        /// 0 = no limit (vanilla behavior, same as disabling the throttle). Default 40.
        /// </summary>
        public int MaxConcurrentSounds { get; set; } = 40;

        /// <summary>
        /// Fade duration in seconds when a sound is throttled (evicted) or unthrottled (admitted).
        /// Instead of abrupt silence, sounds smoothly fade to/from minimum volume.
        /// Prevents audible mute/unmute clicks when sounds near the budget threshold oscillate.
        /// 5.0 = very smooth fade. 0 = instant (original behavior).
        /// </summary>
        public float ThrottleFadeSeconds { get; set; } = 5.0f;

        /// <summary>
        /// Weather audio tick update interval in milliseconds.
        /// Weather state changes slowly; 100ms is sufficient.
        /// Lower = smoother indoor/outdoor transitions but more CPU overhead.
        /// </summary>
        public int WeatherTickIntervalMs { get; set; } = 100;

        // ============================================================
        // EXECUTION TRACE
        // Developer tools for Call Graph generation.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _ExecutionTraceSystem { get; set; } = "--- Developer Execution Tracer. Do not enable during normal gameplay. ---";

        /// <summary>
        /// Enable the performance execution tracer.
        /// Writes method entry/exit timestamps to trace.csv for Call Graph generation.
        /// Disable during normal gameplay for best performance.
        /// </summary>
        public bool EnableExecutionTracer { get; set; } = false;


    }
}
