using System;

namespace soundphysicsadapted
{
    /// <summary>
    /// Configuration for Sound Physics Adapted
    /// Loaded/saved as soundphysicsadapted.json
    /// </summary>
    public class SoundPhysicsConfig
    {
        // ============================================================
        // CONFIG VERSION
        // Used for migration. If this field is missing (= 0), the config
        // pre-dates the migration system and will be regenerated fresh.
        // Bump CurrentConfigVersion in SoundPhysicsAdaptedModSystem when
        // adding migrations that should apply to existing users.
        // ============================================================

        public int ConfigVersion { get; set; } = 0;

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

        /// <summary>
        /// Enable debug logging for occlusion raycasting.
        /// Shows per-sound occlusion results, ray hits, material absorption.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugOcclusion { get; set; } = false;

        /// <summary>
        /// Enable debug logging for reverb analysis.
        /// Shows ray hits, material contributions, and final values.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugReverb { get; set; } = false;

        /// <summary>
        /// Enable debug logging for sound path resolution.
        /// Shows path count, average occlusion, repositioning offset.
        /// Requires DebugMode=true to have any effect.
        /// Toggle with /soundphysics debugpaths
        /// </summary>
        public bool DebugSoundPaths { get; set; } = false;

        /// <summary>
        /// Enable debug logging for resonator features.
        /// Shows pause/resume events, Carry On pickup/placement, boombox state.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugResonator { get; set; } = false;

        /// <summary>
        /// Enable debug logging for weather audio system.
        /// Shows enclosure values, LPF gainHF, sound start/stop events.
        /// Requires DebugMode=true to have any effect.
        /// Toggle with /soundphysics weather-debug
        /// </summary>
        public bool DebugWeather { get; set; } = false;

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
        /// Enable debug logging for thunder audio events.
        /// Shows Layer 1/Layer 2 decisions, opening selection, bolt direction scoring.
        /// Requires DebugMode=true to have any effect.
        /// </summary>
        public bool DebugThunder { get; set; } = false;

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
        /// Maximum occlusion value (caps total block count).
        /// The DDA ray stops early once this threshold is reached.
        /// With BlockAbsorption=1.0 and ×2 internal multiplier, filter = exp(-MaxOcclusion * 2):
        ///   4.0 = exp(-8)  ≈ 0.03% (effectively silent at low configs)
        ///  32.0 = massive headroom for low-absorption configs
        /// SPR defaults to 64. We use 32 — enough headroom while limiting DDA cost.
        /// </summary>
        public float MaxOcclusion { get; set; } = 32.0f;

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
        /// [DEPRECATED] No longer used for skipping sounds.
        /// Reverb distance attenuation now uses per-sound SoundParams.Range from vanilla.
        /// Kept for config file backward compatibility — will be removed in a future version.
        /// </summary>
        public float MaxSoundDistance { get; set; } = 64.0f;

        /// <summary>
        /// [DEPRECATED] Unused. Kept for config backward compatibility.
        /// DDA step limit is now controlled by MaxDDASteps.
        /// </summary>
        public int MaxOcclusionRays { get; set; } = 16;

        /// <summary>
        /// Maximum DDA traversal steps per occlusion ray.
        /// Hard cap on how many blocks the ray walks regardless of sound distance.
        /// Prevents long-distance rays through open air from walking 60+ blocks.
        /// Default 32 covers ~20 blocks in any diagonal direction.
        /// 0 = unlimited (Manhattan distance bound only).
        /// </summary>
        public int MaxDDASteps { get; set; } = 32;

        /// <summary>
        /// Minimum lowpass filter value (0 = silent, 1 = no filter)
        /// Prevents sounds from being completely inaudible
        /// 0.001 = 0.1% minimum volume for max occluded sounds
        /// </summary>
        public float MinLowPassFilter { get; set; } = 0.001f;

        /// <summary>
        /// Maximum HF pass from diffraction floor (0 = disable, 1 = full pass).
        /// When bounce rays find viable indirect paths (e.g., around an L-shaped corridor),
        /// the diffraction floor allows more HF through than direct occlusion alone.
        /// 0.35 ≈ 9dB attenuation (realistic for one 90-degree corner bend).
        /// Based on Maekawa/UTD simplified diffraction models.
        /// </summary>
        public float MaxDiffractionFilter { get; set; } = 0.35f;

        /// <summary>
        /// Minimum occlusion applied to diffracted paths (in block units).
        /// Prevents diffraction from making sounds unrealistically clear.
        /// 0.3 ≈ 8dB loss per 90-degree bend (Wwise-style abstract diffraction coefficient).
        /// </summary>
        public float MinDiffractionOcclusion { get; set; } = 0.3f;

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
        /// </summary>
        public bool EnableSoundOverrides { get; set; } = true;

        /// <summary>
        /// Override vanilla beehive-wild.ogg with improved version.
        /// Requires EnableSoundOverrides=true.
        /// </summary>
        public bool OverrideBeehiveSound { get; set; } = false;

        /// <summary>
        /// Override vanilla lightning-nodistance.ogg with louder version.
        /// Requires EnableSoundOverrides=true.
        /// </summary>
        public bool OverrideLightningSound { get; set; } = false;

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
        // Replaces all vanilla bolt + ambient thunder with positioned audio.
        // Bolt: distance-based asset selection (verynear/near/distant) + delayed
        //       nodistance.ogg crack layer with realistic atmospheric falloff.
        // Indoor: Layer 1 (omnidirectional LPF rumble) + Layer 2 (crack at opening).
        // Outdoor: 3D positioned toward bolt/random sky direction.
        // Volume: vanilla per-tier linear curves + our enclosure attenuation.
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
        /// Minimum LPF cutoff for thunder Layer 1 when fully enclosed (Hz).
        /// Thunder is already low-frequency content; heavy filtering makes it
        /// a deep, barely-audible rumble. 200 Hz keeps some bass presence.
        /// </summary>
        public float ThunderLPFMinCutoff { get; set; } = 800f;

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
        /// Maximum positional thunder sources (one-shot pool for L2 cracks at openings).
        /// Only counts indoor L2 crack sources — outdoor cracks and rumbles are unlimited.
        /// </summary>
        public int MaxThunderSources { get; set; } = 20;

        /// <summary>
        /// Minimum pitch for nodistance.ogg crack at maximum distance (1000 blocks).
        /// At close range pitch=1.0 (bright crack), at max distance pitch drops to this value
        /// (deeper, bassier rumble). Simulates high-frequency atmospheric attenuation.
        /// </summary>
        public float ThunderCrackPitchMin { get; set; } = 0.5f;

        /// <summary>
        /// Random pitch variation applied to each thunder event (±this value).
        /// Adds natural variety so consecutive thunders don't sound identical.
        /// Applied to both crack (nodistance.ogg) and rumble (verynear/near/distant.ogg) sounds.
        /// </summary>
        public float ThunderPitchRandomness { get; set; } = 0.06f;

        // ============================================================
        // RAIN SURFACE IMPACTS
        // Plays localized rain impact sounds on specific block types
        // (anvils, metal blocks, etc.) when exposed to rain.
        // Uses VS's built-in ambient sound system — same clustering
        // mechanism as leaded glass panes (BlockRainAmbient).
        // Adjacent matching blocks merge into one louder source.
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _RainSurfaceImpactSystem { get; set; } = "--- Rain impact sounds on metal surfaces. Uses VS ambient clustering (same as glass panes). ---";

        /// <summary>
        /// Master toggle for rain surface impact sounds.
        /// When enabled: blocks matching RainSurfaceBlockPatterns play rain impact loops
        /// when exposed to rain. Adjacent blocks cluster into one sound source.
        /// </summary>
        public bool EnableRainSurfaceImpacts { get; set; } = true;

        /// <summary>
        /// Volume multiplier for rain surface impact sounds.
        /// Combined with rainfall intensity: final VolumeMul = rainfall * this.
        /// The per-block volume also scales with cluster size (VS AmbientBlockCount ratio).
        /// </summary>
        public float RainSurfaceVolume { get; set; } = 0.5f;

        /// <summary>
        /// Block code patterns that trigger rain surface impacts.
        /// Matched as prefix against block.Code.Path (e.g., "anvil" matches "anvil-copper").
        /// Add patterns for any block type you want rain impact sounds on.
        /// </summary>
        public string[] RainSurfaceBlockPatterns { get; set; } = new string[]
        {
            // --- Smithing / storage piles ---
            "anvil",            // game:anvil-{metal}, game:anvilpart-{base|top}-{metal}
            "ingotpile",        // game:ingotpile
            "platepile",        // game:platepile
            "metalpartpile",    // game:metalpartpile (scraps/parts pile)
            "metalsheet",       // game:metalsheet-{metal}-{facing}

            // --- Metal blocks / plates / sheets ---
            "metalblock",       // game:metalblock-{type}-{metal}
            "metalplate",       // game:metalplate-{metal}  (if used by mods)

            // --- Metal machines / containers ---
            "hopper",           // game:hopper-{metal}-{facing}
            "chute",            // game:chute, chute-cross, chute-straight, chute-t
            "verticalboiler",   // game:verticalboiler
            "condenser",        // game:condenser
            "cokeovendoor",     // game:cokeovendoor-{metal}

            // --- Metal furniture / decor ---
            "ironfence",        // game:ironfence-{metal}-{config}
            "supportchain",     // game:supportchain-{metal}-{facing}
            "supportbeam-tarnishedmetal", // game:supportbeam-tarnishedmetal-{config}
            "chandelier",       // game:chandelier-{metal}
            "lantern",          // game:lantern-{metal}-{facing}   (TODO: own sound?)
            "metaldoor",        // game:metaldoor-{metal}-{config}
            "trapdoor",         // game:trapdoor-{metal}-{config}
            "plaque",           // game:plaque-{metal}-{facing}
            "shingleblock",     // game:shingleblock-{metal}-{facing}  (metal roof shingles)
            "lightningrod",     // game:lightningrod-{metal}

            // Note: mechanics (angledgears, largegear3, helvehammerbase, transmission,
            // brake, crank, pulverizerframe etc.) are all wood — skip.
            // bloomerybase/bloomerychimney are clay/stone — skip.
            // forge is stone — skip.
            // Add any of these back when a rain-on-wood / rain-on-stone sound is available.
        };

        // ============================================================
        // TORCH AMBIENT
        // Adds ambient crackling sound to placed torches.
        // Uses VS's built-in ambient sound system for clustering.
        // Mono downmixed by our LoadSoundPatch (positional = auto mono).
        // ============================================================

        /// <summary>
        /// Section header visible in JSON config file.
        /// </summary>
        public string _TorchAmbientSystem { get; set; } = "--- Ambient crackling for placed torches. Uses VS ambient clustering. ---";

        /// <summary>
        /// Master toggle for torch ambient sounds.
        /// When enabled: placed lit torches emit a quiet crackling loop.
        /// Extinct/unlit torches are automatically excluded.
        /// </summary>
        public bool EnableTorchAmbient { get; set; } = true;

        /// <summary>
        /// Base volume for torch ambient sounds (returned by GetAmbientSoundStrength).
        /// Lower than held torch to avoid overwhelming nearby areas.
        /// </summary>
        public float TorchAmbientVolume { get; set; } = 0.35f;

        /// <summary>
        /// Sound asset path for torch ambient. Uses the same idle crackling
        /// sound that plays when a player holds a torch in hand.
        /// Format: "domain:path" (without .ogg extension).
        /// </summary>
        public string TorchAmbientSoundPath { get; set; } = "game:sounds/held/torch-idle";

        /// <summary>
        /// Block code patterns that are considered lit torches.
        /// Matched as prefix against block.Code.Path.
        /// Blocks matching these AND containing "extinct" or "burnedout" are excluded.
        /// </summary>
        public string[] TorchBlockPatterns { get; set; } = new string[]
        {
            "torch",
            "walltorch"
        };

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
        /// VS runs at 20 ticks/second — default 10 = up to 200 sounds/sec max throughput.
        /// The time budget (MaxTickBudgetMs) is the primary spike guard; this count cap
        /// is a secondary safety net that limits worst-case work even if each sound is cheap.
        /// Sounds exceeding the budget are deferred to the next tick.
        /// Close sounds are prioritized. Overdue sounds (>2s stale) get priority but are still capped.
        /// 0 = unlimited (no count cap, time budget only).
        /// </summary>
        public int MaxSoundsPerTick { get; set; } = 10;

        /// <summary>
        /// Additional overdue sounds that can process on top of MaxSoundsPerTick each tick.
        /// Overdue = new sounds or sounds not updated in >2s.
        /// Real max per tick = MaxSoundsPerTick + MaxOverdueSoundsPerTick (default 10+3=13).
        /// Prevents spikes when many sounds appear simultaneously (approaching a farm).
        /// 0 = overdue sounds obey normal budget (strictest). Default 3.
        /// </summary>
        public int MaxOverdueSoundsPerTick { get; set; } = 3;

        /// <summary>
        /// Maximum milliseconds to spend processing sounds per tick.
        /// When exceeded, remaining sounds are deferred to the next tick.
        /// This prevents lagspikes from complex environments where a single sound
        /// can take 50-100ms+ due to DDA traversals through dense geometry.
        /// 0 = unlimited (no time budget). Default 8ms (~half a 60fps frame).
        /// </summary>
        public float MaxTickBudgetMs { get; set; } = 8f;

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
        /// Enable static sound cache (skip raycasts when player and sound haven't moved).
        /// When true: sounds are only recalculated when something moves or changes.
        /// When false: sounds always recalculate when their interval is due, regardless of movement.
        /// Disabling reduces performance but ensures immediate response to all world changes.
        /// Block break/place and door interactions always bypass this cache automatically.
        /// </summary>
        public bool EnableStaticSoundCache { get; set; } = true;

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
        // DERIVED VALUES (not serialized)
        // ============================================================

        /// <summary>
        /// Pre-computed occlusion threshold beyond which sound is at MinLowPassFilter.
        /// DDA rays abort early when accumulated occlusion reaches this value.
        /// Derived from: -ln(MinLowPassFilter) / (BlockAbsorption * 2.0)
        /// Respects material-based accumulation since the check is on accumulated
        /// occlusion value, not block count. Includes 10% headroom.
        /// Not serialized to config — recalculated on load via RecalculateDerived().
        /// </summary>
        internal float InaudibleOcclusionThreshold { get; private set; } = 32.0f;

        /// <summary>
        /// Recalculate derived values after config load or parameter change.
        /// Must be called after deserialization and after any set command that
        /// changes BlockAbsorption, MinLowPassFilter, or MaxOcclusion.
        /// </summary>
        public void RecalculateDerived()
        {
            // OcclusionToFilter formula: filter = exp(-occ * BlockAbsorption * 2.0)
            // Solve for occ when filter = MinLowPassFilter:
            //   MinLowPassFilter = exp(-occ * BlockAbsorption * 2.0)
            //   occ = -ln(MinLowPassFilter) / (BlockAbsorption * 2.0)
            float absorption = Math.Max(BlockAbsorption * 2.0f, 0.001f);
            float rawThreshold = (float)(-Math.Log(Math.Max(MinLowPassFilter, 1e-6f)) / absorption);
            // 10% headroom so reverb (which uses x3 multiplier) still gets a
            // meaningful value before DDA abort, not an exact boundary clamp.
            InaudibleOcclusionThreshold = Math.Min(rawThreshold * 1.1f, MaxOcclusion);
        }

    }
}
