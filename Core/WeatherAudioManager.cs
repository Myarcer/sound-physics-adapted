using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using soundphysicsadapted.Patches;

namespace soundphysicsadapted
{
    /// <summary>
    /// Phase 5A+5B coordinator: manages weather audio replacement with LPF
    /// and positional sources at detected openings.
    ///
    /// Architecture:
    /// - WeatherSoundPatches (Harmony) suppresses VS's 6 global weather loops
    /// - This manager reads VS's weather state each tick via reflection
    /// - WeatherEnclosureCalculator replaces roomVolumePitchLoss with two metrics:
    ///     SkyCoverage (heightmap sampling) -> drives VOLUME
    ///     OcclusionFactor (DDA air-path checks) -> drives LPF
    /// - RainAudioHandler Layer 1: non-positional ambient bed with EFX lowpass
    ///     Per-type ducking when Layer 2 sources are active
    /// - WeatherPositionalHandler Layer 2: positional mono sources at detected openings
    ///     Manages 3 independent pools (rain, wind, hail) sharing TrackedOpenings
    /// - ThunderAudioHandler (Phase 5C skeleton): bolt-direction weighted one-shots
    /// </summary>
    public class WeatherAudioManager : IDisposable
    {
        private readonly ICoreClientAPI capi;
        private RainAudioHandler rainHandler;
        private WeatherPositionalHandler positionalHandler;
        private ThunderAudioHandler thunderHandler;
        private WeatherEnclosureCalculator enclosureCalculator;
        private OpeningTracker openingTracker;

        private bool initialized = false;
        private bool disposed = false;

        // ── Debug Visualization (tracked openings layer) ──
        private const int HIGHLIGHT_SLOT_TRACKED = 92;
        private long lastTrackedVizUpdateMs = 0;
        private const long TRACKED_VIZ_UPDATE_MS = 500;
        private bool trackedVizActive = false;

        // Enclosure outputs (from WeatherEnclosureCalculator)
        /// <summary>Smoothed 0=outdoors, 1=fully covered. Used for positional outdoor gate.</summary>
        public float SkyCoverage => enclosureCalculator?.SmoothedSkyCoverage ?? 1f;
        /// <summary>Smoothed 0=all rain in air, 1=all behind walls. Used for positional outdoor gate.</summary>
        public float OcclusionFactor => enclosureCalculator?.SmoothedOcclusionFactor ?? 1f;

        /// <summary>Raw (unsmoothed) sky coverage. For Layer 1 volume — instant indoor/outdoor response.</summary>
        public float RawSkyCoverage => enclosureCalculator?.SkyCoverage ?? 0f;
        /// <summary>Raw (unsmoothed) occlusion factor. For Layer 1 LPF — instant indoor/outdoor response.</summary>
        public float RawOcclusionFactor => enclosureCalculator?.OcclusionFactor ?? 0f;

        // Vanilla fallback (still read for comparison/debug)
        public float RoomVolumePitchLoss { get; private set; }

        // ── Spawn fade-in ──
        // Prevents full-volume weather blast when spawning into a world with active rain/wind.
        // Enclosure calculator needs ~1s to compute correct indoor values; this fade covers
        // that window so sounds ramp up gently instead of slamming in at outdoor volume.
        private float spawnFadeMultiplier = 0f;
        /// <summary>Seconds from first tick to reach full volume. 2s covers enclosure convergence.</summary>
        private const float SPAWN_FADE_DURATION = 2.0f;
        private const float SPAWN_FADE_RATE = 1.0f / SPAWN_FADE_DURATION; // per second

        // ── Warmup period ──
        // Hard guarantee: produce ZERO audio for the first N ticks after player appears.
        // This covers ALL timing edge cases: chunk loading delays, enclosure calc rate limiter,
        // vanilla sound suppression gap, and initialization ordering.
        // 3 ticks * 100ms = 300ms silence on spawn (imperceptible vs a loud spike).
        private int warmupTicksRemaining = 3;
        private bool warmupComplete = false;

        // Faded intensities (real intensity * spawnFadeMultiplier), used by both OnGameTick and UpdatePositionalWeather
        private float fadedRainIntensity;
        private float fadedHailIntensity;
        private float fadedWindSpeed;

        // Cached weather state (updated each tick from VS)
        // These are pre-smoothed: VS precipitation (clientClimateCond.Rainfall) is
        // jittery — fluctuates 0.03-0.07 between ticks during light rain. The slow
        // EMA pre-smoother stabilizes the signal before it reaches Layer 1/2 volume.
        public float RainIntensity { get; private set; }
        public float HailIntensity { get; private set; }
        public float WindSpeed { get; private set; }
        public bool IsLeafy { get; private set; }
        /// <summary>Raw leaviness 0-1 for crossfade blending (leafy/leafless).</summary>
        public float Leaviness { get; private set; }

        // ── VS precipitation pre-smoother ──
        // Raw VS weather values jump per-tick (especially Rainfall during light/ending rain).
        // This slow EMA smooths the raw signal BEFORE it reaches any handler.
        // Layer 1/2 volume smoothing (RainAudioHandler.SmoothValue, PositionalSourcePool)
        // remains untouched — those handle start/stop ramps and volume fading.
        private float smoothedVSRain, smoothedVSHail, smoothedVSWind;
        private bool vsWeatherSeeded;
        /// <summary>
        /// EMA factor per tick for VS weather pre-smoothing. Lower = smoother/slower.
        /// At 0.08 with 100ms ticks: ~12 ticks (1.2s) to reach 63% of a step change.
        /// Prevents audible jumps from VS's noisy Rainfall signal (0→0.065→0→0.065).
        /// </summary>
        private const float VS_WEATHER_SMOOTH_FACTOR = 0.08f;
        // Raw VS values (for debug comparison)
        private float rawVSRain, rawVSHail, rawVSWind;

        public WeatherAudioManager(ICoreClientAPI api)
        {
            capi = api;
        }

        /// <summary>
        /// Initialize after Harmony patches are applied.
        /// Creates handler instances and preloads sound assets.
        /// </summary>
        public bool Initialize()
        {
            if (initialized) return true;

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableWeatherEnhancement)
            {
                SoundPhysicsAdaptedModSystem.Log("WeatherAudioManager: disabled by config");
                return false;
            }

            if (!WeatherSoundPatches.IsActive)
            {
                SoundPhysicsAdaptedModSystem.Log("WeatherAudioManager: patches not active, cannot initialize");
                return false;
            }

            try
            {
                enclosureCalculator = new WeatherEnclosureCalculator(capi);
                openingTracker = new OpeningTracker(capi);

                // Layer 1: non-positional ambient bed with LPF
                rainHandler = new RainAudioHandler(capi);
                rainHandler.Initialize();

                // Layer 2: positional sources at detected openings (rain + wind + hail)
                positionalHandler = new WeatherPositionalHandler(capi);
                positionalHandler.Initialize();

                // Phase 5C: thunder one-shots with Layer 1 rumble + Layer 2 at openings
                thunderHandler = new ThunderAudioHandler(capi);
                thunderHandler.Initialize();

                // Wire thunder handler to Harmony patches so lightning events get routed to us
                WeatherSoundPatches.SetThunderHandler(thunderHandler, this);

                // Wire up audibility-based persistence: OpeningTracker asks ALL positional pools
                // if any source is still being heard before removing the opening
                openingTracker.IsSourceAudible = (trackingId) => positionalHandler.IsSourceAudible(trackingId);



                initialized = true;
                SoundPhysicsAdaptedModSystem.Log("WeatherAudioManager initialized (L1 ambient + L2 positional rain/wind/hail + 5C thunder)");
                return true;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.Log($"WeatherAudioManager init FAILED: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Called from mod system tick handler (~100ms interval).
        /// Reads VS weather state and delegates to handlers.
        /// </summary>
        /// <summary>
        /// Forward a block-change event to the opening tracker for entry-point
        /// proximity invalidation. Called from SoundPhysicsAdaptedModSystem.OnBlockChanged.
        /// </summary>
        public void NotifyBlockChanged(BlockPos pos)
        {
            openingTracker?.OnBlockChanged(pos);
            enclosureCalculator?.InvalidateNearbyColumns(pos);
        }

        public void OnGameTick(float dt)
        {
            if (!initialized || disposed) return;

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableWeatherEnhancement)
            {
                // Feature disabled at runtime — stop all replacement sounds
                rainHandler?.StopAll();
                positionalHandler?.StopAll();
                openingTracker?.Clear();
                return;
            }

            try
            {
                // Read weather state from VS
                UpdateWeatherState();

                // Update enclosure calculator (rate-limited internally to 100ms)
                // IMPORTANT: All audio logic gated on player existence.
                // During loading, player is null but ticks fire — don't update audio
                // until we can calculate proper enclosure values.
                var player = capi.World.Player?.Entity;
                if (player == null)
                {
                    // Player not loaded yet — don't start any sound processing.
                    // Spawn fade will start fresh once player exists.
                    return;
                }

                long gameTimeMs = capi.World.ElapsedMilliseconds;
                // Pass eye/ear position -- consistent with occlusion system
                Vec3d earPos = player.Pos.XYZ.Add(player.LocalEyePos);

                enclosureCalculator?.Update(earPos, gameTimeMs);

                // ── Warmup period: absolute silence for first N ticks ──
                // Covers ALL timing edge cases: chunk loading, enclosure calc rate limiter,
                // vanilla suppression gap, and initialization ordering.
                // Enclosure calculator still runs during warmup (converges in background).
                if (!warmupComplete)
                {
                    warmupTicksRemaining--;
                    if (warmupTicksRemaining <= 0)
                    {
                        warmupComplete = true;
                        // Reset spawn fade to start clean AFTER warmup
                        spawnFadeMultiplier = 0f;
                        WeatherDebugLog($"[WARMUP] Complete — enclosure converged, starting spawn fade. " +
                            $"sky={SkyCoverage:F2} occl={OcclusionFactor:F2}");
                    }
                    else
                    {
                        // Force all intensities to zero — no audio during warmup
                        fadedRainIntensity = 0f;
                        fadedHailIntensity = 0f;
                        fadedWindSpeed = 0f;
                        return;
                    }
                }

                // Spawn fade-in: ramp from silence to full over SPAWN_FADE_DURATION.
                // Warmup already reset spawnFadeMultiplier to 0 when it completed.
                // Cap dt to prevent huge jumps when first tick after loading has accumulated time.
                float cappedDt = Math.Min(dt, 0.2f); // Cap to 200ms max to prevent jumps
                spawnFadeMultiplier = Math.Min(1f, spawnFadeMultiplier + cappedDt * SPAWN_FADE_RATE);

                // Apply spawn fade to weather intensities — affects both Layer 1 volume
                // and Layer 2 positional volume, but NOT LPF/enclosure calculations
                // (those run on real values so they converge correctly during the fade).
                fadedRainIntensity = RainIntensity * spawnFadeMultiplier;
                fadedHailIntensity = HailIntensity * spawnFadeMultiplier;
                fadedWindSpeed = WindSpeed * spawnFadeMultiplier;

                // Phase 5B: Cluster verified openings and update tracker
                if (config.EnablePositionalWeather && enclosureCalculator != null && openingTracker != null)
                {
                    UpdatePositionalWeather(earPos, gameTimeMs, config);
                }

                // Delegate to handlers — pass smoothed enclosure metrics (Layer 1)
                // Use faded intensities so volume ramps up gently on spawn.
                // Enclosure calculator now converges in ~0.5-1s (100ms ticks, factor 0.2)
                // Fast enough for responsive indoor/outdoor, smooth enough to avoid jitter.
                rainHandler?.Update(dt, fadedRainIntensity, SkyCoverage, OcclusionFactor,
                    Leaviness, fadedWindSpeed, fadedHailIntensity);

                // Phase 5C: tick thunder one-shot cleanup + indoor→outdoor L2 transition
                thunderHandler?.OnGameTick(SkyCoverage, OcclusionFactor);

                // Debug logging
                if (config.DebugMode && config.DebugWeather)
                {
                    string trackerStatus = openingTracker?.GetDebugStatus() ?? "";
                    WeatherDebugLog(
                        $"WEATHER TICK: rain={RainIntensity:F3}(raw={rawVSRain:F3}) hail={HailIntensity:F3}(raw={rawVSHail:F3}) " +
                        $"wind={WindSpeed:F3}(raw={rawVSWind:F3}) sky={SkyCoverage:F2} occl={OcclusionFactor:F2} " +
                        $"spawnFade={spawnFadeMultiplier:F2} " +
                        $"vanillaLoss={RoomVolumePitchLoss:F2} leaviness={Leaviness:F2} " +
                        $"{enclosureCalculator?.GetDebugStatus() ?? ""} " +
                        $"{trackerStatus} " +
                        $"handler={rainHandler?.GetDebugStatus() ?? "null"}");
                }
            }
            catch (Exception ex)
            {
                WeatherDebugLog($"OnGameTick EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>
        /// Phase 5B: Cluster verified openings, update tracker, and feed to positional handler.
        /// Called each tick but clustering is only recalculated when enclosure calculator updates
        /// (its VerifiedOpenings change at most every 500ms).
        /// </summary>
        private void UpdatePositionalWeather(Vec3d earPos, long gameTimeMs, SoundPhysicsConfig config)
        {
            // Outdoor gate: when player is outdoors (low sky coverage + low occlusion),
            // positional sources are pointless — Layer 1 ambient bed provides full rain.
            // Clear tracker to fade out and remove all positional sources.
            // Uses SmoothedSkyCoverage to avoid flicker at transition boundaries.
            float minSky = config.PositionalMinSkyCoverage;
            float currentSky = enclosureCalculator.SmoothedSkyCoverage;
            float currentOccl = enclosureCalculator.SmoothedOcclusionFactor;

            if (currentSky < minSky && currentOccl < minSky)
            {
                // Player is outdoors — kill all positional sources
                if (openingTracker.Count > 0)
                {
                    if (config.DebugMode && config.DebugPositionalWeather)
                    {
                        WeatherDebugLog(
                            $"[5B] OUTDOOR GATE: sky={currentSky:F2} occl={currentOccl:F2} < {minSky:F2} — clearing {openingTracker.Count} tracked openings");
                    }
                    openingTracker.Clear();
                }

                // Clear tracked opening viz when outdoors
                UpdateTrackedOpeningViz(capi.World.ElapsedMilliseconds);

                // Feed empty tracker to positional handler (triggers fade-out on all pools)
                // Use faded intensities for spawn fade-in consistency
                positionalHandler?.UpdateAll(
                    openingTracker.TrackedOpenings,
                    fadedRainIntensity,
                    fadedHailIntensity,
                    fadedWindSpeed,
                    IsLeafy,
                    earPos,
                    currentSky,
                    currentOccl);

                // Reset ducking — no positional sources active
                rainHandler?.SetPositionalContributions(0f, 0f, 0f);
                return;
            }

            // Cluster the calculator's verified openings
            var openings = enclosureCalculator.VerifiedOpenings;
            // Use max of all per-type budgets for clustering (tracker serves all pools)
            int maxTracked = Math.Max(config.MaxPositionalRainSources,
                Math.Max(config.MaxPositionalWindSources, config.MaxPositionalHailSources));

            var clusters = OpeningClusterer.Cluster(openings, maxTracked, openingTracker.TrackedOpenings);

            // Update tracker with new clusters (handles persistence, matching, removal)
            openingTracker.Update(clusters, earPos, gameTimeMs, 12); // scanRadius=12 matches WeatherEnclosureCalculator.SCAN_RADIUS

            // Feed tracked openings to all positional pools (rain, wind, hail)
            // Use faded intensities for spawn fade-in consistency
            positionalHandler?.UpdateAll(
                openingTracker.TrackedOpenings,
                fadedRainIntensity,
                fadedHailIntensity,
                fadedWindSpeed,
                IsLeafy,
                earPos,
                currentSky,
                currentOccl);

            // Feed per-type ducking contributions back to Layer 1
            rainHandler?.SetPositionalContributions(
                positionalHandler?.RainContribution ?? 0f,
                positionalHandler?.WindContribution ?? 0f,
                positionalHandler?.HailContribution ?? 0f);

            // ── Tracked opening visualization (slot 92) ──
            UpdateTrackedOpeningViz(capi.World.ElapsedMilliseconds);

            if (config.DebugMode && config.DebugPositionalWeather)
            {
                WeatherDebugLog(
                    $"[5B] openings={openings.Count} clusters={clusters.Count} " +
                    $"tracked={openingTracker.Count} active={positionalHandler?.TotalActiveCount ?? 0} " +
                    $"duck: rain={positionalHandler?.RainContribution ?? 0:F2} wind={positionalHandler?.WindContribution ?? 0:F2} hail={positionalHandler?.HailContribution ?? 0:F2}");
            }
        }

        /// <summary>
        /// Render tracked openings as block highlights.
        /// Bright magenta = currently confirmed (audio source active, verified this cycle).
        /// Dark magenta = persisted (still playing but not re-confirmed this cycle).
        /// </summary>
        private void UpdateTrackedOpeningViz(long gameTimeMs)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            bool viz = config?.DebugWeatherVisualization == true;

            if (!viz)
            {
                // Just turned off — clear the slot
                if (trackedVizActive)
                {
                    var p = capi.World.Player;
                    if (p != null)
                    {
                        capi.World.HighlightBlocks(p, HIGHLIGHT_SLOT_TRACKED,
                            new List<BlockPos>(), new List<int>());
                    }
                    trackedVizActive = false;
                }
                return;
            }

            if (gameTimeMs - lastTrackedVizUpdateMs < TRACKED_VIZ_UPDATE_MS) return;
            lastTrackedVizUpdateMs = gameTimeMs;

            var player = capi.World.Player;
            if (player == null || openingTracker == null) return;

            var tracked = openingTracker.TrackedOpenings;
            var positions = new List<BlockPos>();
            var colors = new List<int>();

            // Bright magenta for verified, dark magenta for persisted
            // Magenta = "audio source location" (distinct from white=detection pipeline)
            int colorVerified  = ColorUtil.ColorFromRgba(255, 0, 255, 200);   // Bright magenta - verified centroid
            int colorPersisted = ColorUtil.ColorFromRgba(180, 0, 180, 200);   // Dark magenta - persisted centroid
            int colorMember    = ColorUtil.ColorFromRgba(255, 128, 255, 160); // Light magenta - member (rain impact)

            for (int i = 0; i < tracked.Count; i++)
            {
                var opening = tracked[i];
                int centroidColor = opening.CurrentlyVerified ? colorVerified : colorPersisted;

                // Show centroid block (where positional source is placed)
                positions.Add(new BlockPos(
                    (int)Math.Floor(opening.WorldPos.X),
                    (int)Math.Floor(opening.WorldPos.Y),
                    (int)Math.Floor(opening.WorldPos.Z)));
                colors.Add(centroidColor);

                // Show member positions (rain impact points) - LIGHT MAGENTA
                if (opening.MemberPositions != null)
                {
                    for (int m = 0; m < opening.MemberPositions.Count; m++)
                    {
                        var mp = opening.MemberPositions[m];
                        positions.Add(new BlockPos(
                            (int)Math.Floor(mp.X),
                            (int)Math.Floor(mp.Y),
                            (int)Math.Floor(mp.Z)));
                        colors.Add(colorMember);
                    }
                }
            }

            capi.World.HighlightBlocks(player, HIGHLIGHT_SLOT_TRACKED,
                positions, colors,
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
            trackedVizActive = positions.Count > 0;
        }

        /// <summary>
        /// Read all weather values from VS via reflection/statics.
        /// VS computes these in WeatherSimulationSound.updateSounds() which runs
        /// before our postfix, so values are always fresh.
        /// </summary>
        private void UpdateWeatherState()
        {
            // Still read vanilla for debug comparison (zero cost, public static)
            RoomVolumePitchLoss = WeatherSoundPatches.ReadRoomVolumePitchLoss();

            // Read raw values from VS (jittery — Rainfall fluctuates per tick)
            rawVSRain = WeatherSoundPatches.ReadRainIntensity();
            rawVSHail = WeatherSoundPatches.ReadHailIntensity();
            rawVSWind = WeatherSoundPatches.ReadWindSpeed();

            // Seed on first read to avoid cold-start ramp (spawn fade handles audio ramp)
            if (!vsWeatherSeeded)
            {
                smoothedVSRain = rawVSRain;
                smoothedVSHail = rawVSHail;
                smoothedVSWind = rawVSWind;
                vsWeatherSeeded = true;
            }
            else
            {
                // Slow EMA: stabilize VS precipitation jitter before feeding to handlers.
                // Handler-level smoothing (SmoothValue rate limiter) remains for start/stop ramps.
                smoothedVSRain += (rawVSRain - smoothedVSRain) * VS_WEATHER_SMOOTH_FACTOR;
                smoothedVSHail += (rawVSHail - smoothedVSHail) * VS_WEATHER_SMOOTH_FACTOR;
                smoothedVSWind += (rawVSWind - smoothedVSWind) * VS_WEATHER_SMOOTH_FACTOR;
            }

            RainIntensity = smoothedVSRain;
            HailIntensity = smoothedVSHail;
            WindSpeed = smoothedVSWind;

            IsLeafy = WeatherSoundPatches.IsLeafy();
            Leaviness = WeatherSoundPatches.GetLeaviness();
        }

        /// <summary>
        /// Get status string for /soundphysics weather command.
        /// </summary>
        public string GetStatus()
        {
            if (!initialized)
                return "Weather audio: NOT INITIALIZED";

            var config = SoundPhysicsAdaptedModSystem.Config;
            string enabledStr = config?.EnableWeatherEnhancement == true ? "ENABLED" : "DISABLED";
            string posStr = config?.EnablePositionalWeather == true ? "ENABLED" : "DISABLED";

            string positionalDebug = positionalHandler?.GetDebugStatus() ?? "No positional handler";
            string thunderStr = config?.EnableThunderPositioning == true ? "ENABLED" : "DISABLED";
            string thunderDebug = thunderHandler?.GetDebugStatus() ?? "No thunder handler";

            return $"Weather audio: {enabledStr}\n" +
                   $"  Spawn Fade: {spawnFadeMultiplier:F2} (1.0 = fully faded in) Warmup: {(warmupComplete ? "complete" : $"{warmupTicksRemaining} ticks remaining")}\n" +
                   $"  Sky Coverage: {SkyCoverage:F2} (drives volume)\n" +
                   $"  Occlusion Factor: {OcclusionFactor:F2} (drives LPF)\n" +
                   $"  Vanilla roomVolumePitchLoss: {RoomVolumePitchLoss:F2} (reference)\n" +
                   $"  {enclosureCalculator?.GetDebugStatus() ?? "No calculator"}\n" +
                   $"  Rain: {RainIntensity:F2}, Hail: {HailIntensity:F2}, Wind: {WindSpeed:F2}\n" +
                   $"  Leaviness: {Leaviness:F2} (leafy={IsLeafy})\n" +
                   $"  Positional Weather: {posStr}\n" +
                   $"  {openingTracker?.GetDebugStatus() ?? "No tracker"}\n" +
                   $"{openingTracker?.GetDetailedDebugStatus(capi.World.ElapsedMilliseconds) ?? ""}\n" +
                   $"  Active positional sources: {positionalHandler?.TotalActiveCount ?? 0}\n" +
                   $"  {positionalDebug}\n" +
                   $"  Layer 1: {rainHandler?.GetDebugStatus() ?? "No handler"}\n" +
                   $"  Thunder: {thunderStr} — {thunderDebug}";
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            rainHandler?.Dispose();
            rainHandler = null;

            positionalHandler?.Dispose();
            positionalHandler = null;

            thunderHandler?.Dispose();
            thunderHandler = null;

            openingTracker?.Clear();
            openingTracker = null;

            // Clear tracked opening viz slot
            try
            {
                var player = capi?.World?.Player;
                if (player != null)
                {
                    capi.World.HighlightBlocks(player, HIGHLIGHT_SLOT_TRACKED,
                        new List<BlockPos>(), new List<int>());
                }
            }
            catch { /* world may be unloading */ }

            enclosureCalculator?.Dispose();
            enclosureCalculator = null;

            initialized = false;
            WeatherDebugLog("WeatherAudioManager disposed");
        }

        // ── Static debug logging (shared pattern) ──

        /// <summary>
        /// Weather-specific debug logging. Uses DebugWeather flag.
        /// Requires DebugMode=true as master gate.
        /// Rate-limited via shared counter in SoundPhysicsAdaptedModSystem.
        /// Callable from WeatherSoundPatches and RainAudioHandler.
        /// </summary>
        public static void WeatherDebugLog(string message)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config?.DebugMode != true || config?.DebugWeather != true) return;

            // Use the main mod system's debug log which includes rate limiting
            // Prefix with [Weather] for filtering
            SoundPhysicsAdaptedModSystem.DebugLog($"[Weather] {message}");
        }
    }
}
