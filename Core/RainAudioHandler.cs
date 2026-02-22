using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Phase 5A: Manages replacement weather sounds (Layer 1) with OpenAL EFX lowpass filtering.
    ///
    /// LAYER 1 (Phase 5A): Non-positional ambient bed.
    ///   Uses TWO enclosure metrics from WeatherEnclosureCalculator:
    ///   - skyCoverage (0=outdoors, 1=fully roofed) → drives VOLUME
    ///   - occlusionFactor (0=all rain in air, 1=all behind material) → drives LPF
    ///   RelativePosition=true (listener-attached, stereo OK).
    ///
    /// Layer 2 (positional sources) moved to WeatherPositionalHandler + PositionalSourcePool.
    /// Per-type ducking: SetPositionalContributions() reduces Layer 1 when Layer 2 is active.
    ///
    /// Per-type LPF curves informed by real acoustic behavior:
    /// - Rain: Standard quadratic curve (full spectrum → bass rumble)
    /// - Hail: Steeper curve (high-freq ice sounds attenuate fast through walls)
    /// - Wind: LPF + volume driven by OcclusionFactor (muffled rumble through walls)
    /// - Tremble: Heaviest filtering (already sub-bass content)
    ///
    /// LPF implementation: OpenAL EFX LowpassGainHF is a gain value (0 = full muffling,
    /// 1 = no filter). We convert from Hz conceptual cutoff: gainHF = cutoffHz / 22000.
    /// </summary>
    public class RainAudioHandler : IDisposable
    {
        private readonly ICoreClientAPI capi;
        private SoundPhysicsConfig config;

        // Replacement sound loops — loaded from VS's own assets
        // Rain and wind use dual loops (leafy + leafless) playing simultaneously,
        // with volume split by leaviness (0-1). This matches vanilla VS which
        // runs both variants at once for smooth crossfade between biomes.
        private ILoadedSound rainLoopLeafy;
        private ILoadedSound rainLoopLeafless;
        private ILoadedSound windLoopLeafy;
        private ILoadedSound windLoopLeafless;
        private ILoadedSound trembleLoop;
        private ILoadedSound hailLoop;

        // Per-sound EFX lowpass filters
        // Rain and wind share one filter per type (leafy+leafless get same LPF)
        private int rainFilterId;
        private int windFilterId;
        private int trembleFilterId;
        private int hailFilterId;

        // Smoothed LPF values (weather changes slowly, smooth for clean transitions)
        private float smoothedRainGainHF = 1f;
        private float smoothedWindGainHF = 1f;
        private float smoothedTrembleGainHF = 1f;
        private float smoothedHailGainHF = 1f;

        // LPF smoothing factor — converge ~1.5s at 100ms tick to match enclosure smoothing
        // Aligned with WeatherEnclosureCalculator.SMOOTH_FACTOR (0.4 at 500ms)
        private const float LPF_SMOOTH_FACTOR = 0.25f;

        // Smoothed volume values — matches vanilla VS approach:
        //   curVol += (targetVol - curVol) * dt / smoothTime
        // Prevents abrupt cut-in/cut-out when weather starts/stops.
        // Vanilla uses dt/2 at 250ms tick. We use a factor per 100ms tick.
        // Factor 0.15 at 100ms → ~1.5s to converge (similar to vanilla's dt/2 at 250ms).
        // Smoothed vanilla weather intensities — only smooth weather on/off.
        // Enclosure metrics (skyCoverage, occlusionFactor) are applied AFTER
        // smoothing → indoor/outdoor volume changes are instant.
        private float smoothedRainIntensity = 0f;
        private float smoothedWindSpeed = 0f;
        private float smoothedHailIntensity = 0f;

        // Current per-type volumes (computed each tick, for debug display)
        private float currentRainVol = 0f;
        private float currentWindVol = 0f;
        private float currentHailVol = 0f;
        private float currentTrembleVol = 0f;

        // Max volume change per second (rate limiter, NOT exponential).
        // Linear ramp gives consistent movement regardless of gap size:
        //   0.8/s → full 0→1 transition takes 1.25s
        //   0.05→0.48 (0.43 delta) takes ~0.54s
        //   0.48→0 takes ~0.6s
        // Vanilla VS uses exponential dt*1 at 250ms → 25% per tick → ~1s converge.
        // 0.8/s rate limiter tracks vanilla closely without the big-gap jumps.
        private const float VOLUME_MAX_RATE = 0.8f;

        // Threshold below which we actually stop the sound (inaudible)
        private const float VOLUME_STOP_THRESHOLD = 0.005f;

        // Deep enclosure threshold — when both metrics exceed this, apply aggressive volume kill
        // Fixes: deep underground still at 40% volume with bass LPF (should be near-silent)
        private const float DEEP_ENCLOSURE_THRESHOLD = 0.92f;

        // Phase 5B: Per-type ducking from WeatherPositionalHandler
        // Each weather type's positional sources duck their own Layer 1 counterpart.
        // Set by WeatherAudioManager from WeatherPositionalHandler.XxxContribution.
        private float rainPositionalContribution = 0f;
        private float windPositionalContribution = 0f;
        private float hailPositionalContribution = 0f;

        // Track state for debug
        private bool rainLeafyPlaying, rainLeaflessPlaying;
        private bool windLeafyPlaying, windLeaflessPlaying;
        private bool tremblePlaying, hailPlaying;

        /// <summary>
        /// Set per-type positional contributions for granular Layer 1 ducking.
        /// Called by WeatherAudioManager from WeatherPositionalHandler each tick.
        /// Rain positional sources duck rain L1, wind ducks wind L1 (lightly), etc.
        /// </summary>
        public void SetPositionalContributions(float rainContrib, float windContrib, float hailContrib)
        {
            rainPositionalContribution = Math.Clamp(rainContrib, 0f, 1f);
            windPositionalContribution = Math.Clamp(windContrib, 0f, 1f);
            hailPositionalContribution = Math.Clamp(hailContrib, 0f, 1f);
        }



        public RainAudioHandler(ICoreClientAPI api)
        {
            capi = api;
        }

        public void Initialize()
        {
            config = SoundPhysicsAdaptedModSystem.Config;

            // Pre-create EFX filters for each weather type (Layer 1)
            rainFilterId = EfxHelper.GenFilter();
            windFilterId = EfxHelper.GenFilter();
            trembleFilterId = EfxHelper.GenFilter();
            hailFilterId = EfxHelper.GenFilter();

            if (rainFilterId > 0) EfxHelper.ConfigureLowpass(rainFilterId, 1.0f);
            if (windFilterId > 0) EfxHelper.ConfigureLowpass(windFilterId, 1.0f);
            if (trembleFilterId > 0) EfxHelper.ConfigureLowpass(trembleFilterId, 1.0f);
            if (hailFilterId > 0) EfxHelper.ConfigureLowpass(hailFilterId, 1.0f);

            WeatherAudioManager.WeatherDebugLog(
                $"RainAudioHandler init: filters rain={rainFilterId} wind={windFilterId} " +
                $"tremble={trembleFilterId} hail={hailFilterId}");
        }

        /// <summary>
        /// Main update — called every weather tick (~100ms).
        /// Starts/stops/updates replacement sounds based on current weather.
        /// </summary>
        /// <param name="skyCoverage">0=outdoors, 1=fully covered. Drives volume.</param>
        /// <param name="occlusionFactor">0=air path to rain (crisp), 1=behind material (muffled). Drives LPF.</param>
        /// <param name="leaviness">0=fully leafless, 1=fully leafy. Crossfade blend between variants.</param>
        public void Update(float dt, float rainIntensity, float skyCoverage, float occlusionFactor,
                          float leaviness, float windSpeed, float hailIntensity)
        {
            config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null) return;
            bool debug = config.DebugMode && config.DebugWeather;

            // Smooth vanilla weather intensities (weather start/stop ramps).
            // Enclosure factors applied AFTER → indoor/outdoor transitions are instant.
            // All types use raw values — no threshold gates.
            // Vanilla VS has no minimum wind threshold; even light breeze plays.
            smoothedRainIntensity = SmoothValue(smoothedRainIntensity, rainIntensity, dt);
            smoothedHailIntensity = SmoothValue(smoothedHailIntensity, hailIntensity, dt);
            smoothedWindSpeed = SmoothValue(smoothedWindSpeed, windSpeed, dt);

            // ── Rain ── (dual leafy/leafless, crossfade by leaviness)
            {
                float vol = 0f;
                if (smoothedRainIntensity > 0.01f)
                {
                    float targetGainHF = CalculateGainHF(occlusionFactor, config.WeatherLPFMinCutoff, config.WeatherLPFMaxCutoff, 2f);
                    smoothedRainGainHF = SmoothGainHF(smoothedRainGainHF, targetGainHF);
                    vol = CalculateRainVolume(smoothedRainIntensity, skyCoverage);
                    vol *= DeepEnclosureFactor(skyCoverage, occlusionFactor);
                    vol *= RainDuckFactor();
                }
                currentRainVol = vol;

                if (vol > VOLUME_STOP_THRESHOLD)
                {
                    // Split volume by leaviness (vanilla approach: both play simultaneously)
                    float leafyVol = vol * leaviness;
                    float leaflessVol = vol * Math.Max(0.3f, 1f - leaviness);

                    EnsureRainPlaying();
                    if (rainLoopLeafy != null)
                    {
                        rainLoopLeafy.SetVolume(leafyVol);
                        ApplyFilter(rainLoopLeafy, rainFilterId, smoothedRainGainHF);
                    }
                    if (rainLoopLeafless != null)
                    {
                        rainLoopLeafless.SetVolume(leaflessVol);
                        ApplyFilter(rainLoopLeafless, rainFilterId, smoothedRainGainHF);
                    }
                }
                else
                {
                    StopRain();
                    smoothedRainGainHF = 1f;
                }
            }

            // ── Hail ── (faster LPF ramp — high-freq content attenuates quickly)
            {
                float vol = 0f;
                if (smoothedHailIntensity > 0.01f)
                {
                    float targetGainHF = CalculateGainHF(occlusionFactor, config.HailLPFMinCutoff, config.WeatherLPFMaxCutoff, 1.5f);
                    smoothedHailGainHF = SmoothGainHF(smoothedHailGainHF, targetGainHF);
                    vol = CalculateHailVolume(smoothedHailIntensity, skyCoverage);
                    vol *= DeepEnclosureFactor(skyCoverage, occlusionFactor);
                    vol *= HailDuckFactor();
                }
                currentHailVol = vol;

                if (vol > VOLUME_STOP_THRESHOLD)
                {
                    EnsureHailPlaying();
                    if (hailLoop != null)
                    {
                        hailLoop.SetVolume(vol);
                        ApplyFilter(hailLoop, hailFilterId, smoothedHailGainHF);
                    }
                }
                else
                {
                    StopHail();
                    smoothedHailGainHF = 1f;
                }
            }

            // ── Wind ── (dual leafy/leafless, crossfade by leaviness)
            // Wind creates pressure waves that penetrate walls as low-frequency rumble.
            // In a storm you always hear SOME wind indoors - it's muffled, not silent.
            // OcclusionFactor drives LPF (primary muffling) + gentle volume reduction.
            // Wind should be MORE audible through walls than rain (long wavelength).
            {
                float vol = 0f;
                if (smoothedWindSpeed > 0.01f)
                {
                    // Volume: gentle sqrt curve with floor. LPF does the real muffling.
                    float occAttenuation = MathF.Max(0.12f, MathF.Sqrt(1f - occlusionFactor));
                    float skyFactor = 1f - skyCoverage * 0.3f;
                    vol = occAttenuation * skyFactor * smoothedWindSpeed;
                    vol = Math.Max(0f, vol);
                    vol *= DeepEnclosureFactor(skyCoverage, occlusionFactor);
                    vol *= WindDuckFactor();

                    float targetGainHF = CalculateGainHF(occlusionFactor, config.WindLPFMinCutoff, config.WeatherLPFMaxCutoff, 2f);
                    smoothedWindGainHF = SmoothGainHF(smoothedWindGainHF, targetGainHF);
                }
                currentWindVol = vol;

                if (vol > VOLUME_STOP_THRESHOLD)
                {
                    // Split volume by leaviness (vanilla: both play simultaneously)
                    // Vanilla uses nearbyLeaviness * 1.2f * wstr for leafy,
                    // (1-nearbyLeaviness) * 1.2f * wstr for leafless.
                    float leafyVol = vol * leaviness;
                    float leaflessVol = vol * (1f - leaviness);

                    EnsureWindPlaying();
                    if (windLoopLeafy != null)
                    {
                        windLoopLeafy.SetVolume(leafyVol);
                        ApplyFilter(windLoopLeafy, windFilterId, smoothedWindGainHF);
                    }
                    if (windLoopLeafless != null)
                    {
                        windLoopLeafless.SetVolume(leaflessVol);
                        ApplyFilter(windLoopLeafless, windFilterId, smoothedWindGainHF);
                    }

                    if (debug)
                    {
                        float occAttenuation = MathF.Max(0.12f, MathF.Sqrt(1f - occlusionFactor));
                        float skyFactor = 1f - skyCoverage * 0.3f;
                        WeatherAudioManager.WeatherDebugLog(
                            $"[WIND] vol={vol:F3} (leafy={leafyVol:F3} leafless={leaflessVol:F3} " +
                            $"leav={leaviness:F2} occAtt={occAttenuation:F3} sky={skyFactor:F3} " +
                            $"ws={windSpeed:F2}) lpf={smoothedWindGainHF:F3}");
                    }
                }
                else
                {
                    StopWind();
                    smoothedWindGainHF = 1f;
                }
            }

            // ── Tremble ── (keep audible, heavy LPF — bass rumble is the point)
            {
                float vol = 0f;
                if (smoothedRainIntensity > 0.5f) // VS: rainfall*1.6 - 0.8 threshold
                {
                    float targetGainHF = CalculateGainHF(occlusionFactor, config.TrembleLPFMinCutoff, 400f, 1f);
                    smoothedTrembleGainHF = SmoothGainHF(smoothedTrembleGainHF, targetGainHF);
                    vol = CalculateTrembleVolume(smoothedRainIntensity, skyCoverage);
                    vol *= DeepEnclosureFactor(skyCoverage, occlusionFactor);
                    vol *= RainDuckFactor(); // Tremble is rain-associated, duck with rain
                }
                currentTrembleVol = vol;

                if (vol > VOLUME_STOP_THRESHOLD)
                {
                    EnsureTremblePlaying();
                    if (trembleLoop != null)
                    {
                        trembleLoop.SetVolume(vol);
                        ApplyFilter(trembleLoop, trembleFilterId, smoothedTrembleGainHF);
                    }
                }
                else
                {
                    StopTremble();
                    smoothedTrembleGainHF = 1f;
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // LPF Curve Calculation
        // Convert Hz conceptual cutoff → OpenAL gainHF (0.001 - 1.0)
        // gainHF = cutoffHz / maxCutoffHz, clamped
        //
        // Per-type parameters (passed by callers in Update):
        //   Rain:    minHz=WeatherLPFMinCutoff, maxHz=WeatherLPFMaxCutoff, exp=2.0 (quadratic)
        //   Hail:    minHz=HailLPFMinCutoff,    maxHz=WeatherLPFMaxCutoff, exp=1.5 (steeper)
        //   Wind:    minHz=WindLPFMinCutoff,     maxHz=WeatherLPFMaxCutoff, exp=2.0 (quadratic)
        //   Tremble: minHz=TrembleLPFMinCutoff,  maxHz=400,                exp=1.0 (linear)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Parameterized LPF curve. Calculates gainHF from occlusion factor.
        /// Exponent controls curve shape: 1=linear, 1.5=steeper, 2=quadratic.
        /// </summary>
        private static float CalculateGainHF(float occl, float minHz, float maxHz, float exponent)
        {
            float t = MathF.Pow(occl, exponent);
            float cutoffHz = Lerp(maxHz, minHz, t);
            return HzToGainHF(cutoffHz);
        }

        // ════════════════════════════════════════════════════════════════
        // Per-Type Volume Curves
        // Driven by skyCoverage — how much of the sky is blocked overhead.
        // ════════════════════════════════════════════════════════════════

        private float CalculateRainVolume(float intensity, float skyCov)
        {
            float maxLoss = config.WeatherVolumeLossMax;
            float volumeLoss = skyCov * maxLoss;
            return Math.Max(0f, intensity * (1f - volumeLoss));
        }

        private float CalculateHailVolume(float intensity, float skyCov)
        {
            // More aggressive volume loss — hail clarity drops fast
            float volumeLoss = skyCov * 0.8f;
            return Math.Max(0f, intensity * (1f - volumeLoss));
        }

        private float CalculateTrembleVolume(float intensity, float skyCov)
        {
            // VS only reduces tremble 25% — keep it audible, LPF does the work
            float volumeLoss = skyCov * 0.25f;
            float baseVol = intensity * 1.6f - 0.8f;
            return Math.Max(0f, baseVol * (1f - volumeLoss));
        }

        // ════════════════════════════════════════════════════════════════
        // Deep Enclosure & Ambient Ducking
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Aggressive volume factor when deeply enclosed (both metrics > threshold).
        /// Fixes: deep underground still outputting 40% volume with max LPF.
        /// Returns 1.0 normally, ramps to 0.0 when SkyCoverage AND OcclusionFactor
        /// both exceed DEEP_ENCLOSURE_THRESHOLD (0.92).
        /// </summary>
        private static float DeepEnclosureFactor(float skyCov, float occl)
        {
            if (skyCov <= DEEP_ENCLOSURE_THRESHOLD || occl <= DEEP_ENCLOSURE_THRESHOLD)
                return 1f;

            // Both above threshold — ramp to 0 from 0.92 → 1.0
            float skyFactor = (skyCov - DEEP_ENCLOSURE_THRESHOLD) / (1f - DEEP_ENCLOSURE_THRESHOLD);
            float occlFactor = (occl - DEEP_ENCLOSURE_THRESHOLD) / (1f - DEEP_ENCLOSURE_THRESHOLD);
            float deepness = Math.Min(skyFactor, occlFactor); // Both must be deep
            deepness = deepness * deepness; // Quadratic falloff
            return Math.Max(0f, 1f - deepness);
        }

        /// <summary>
        /// Rain Layer 1 ducking when rain positional sources are active.
        /// Up to 50% volume reduction. Tremble uses this too (rain-associated).
        /// </summary>
        private float RainDuckFactor()
        {
            return 1f - (rainPositionalContribution * 0.5f);
        }

        /// <summary>
        /// Hail Layer 1 ducking when hail positional sources are active.
        /// Up to 50% volume reduction (hail is percussive, positional dominates).
        /// </summary>
        private float HailDuckFactor()
        {
            return 1f - (hailPositionalContribution * 0.5f);
        }

        /// <summary>
        /// Wind Layer 1 ducking when wind positional sources are active.
        /// Only 20% max reduction — wind penetration through walls is the whole point
        /// of Layer 1 wind. Positional wind is supplementary, not a replacement.
        /// </summary>
        private float WindDuckFactor()
        {
            return 1f - (windPositionalContribution * 0.2f);
        }

        // ════════════════════════════════════════════════════════════════
        // Sound Management — Start/Stop/Switch
        //
        // Shared helpers for dual-loop (leafy+leafless) and single-loop types.
        // Each type-specific method delegates with its own asset paths and flags.
        // ════════════════════════════════════════════════════════════════

        /// <summary>Ensure a dual-loop pair is playing. Loads if needed.</summary>
        private void EnsureDualPlaying(
            ref ILoadedSound leafy, ref ILoadedSound leafless,
            string leafyPath, string leaflessPath,
            ref bool leafyFlag, ref bool leaflessFlag,
            string debugName)
        {
            if (leafy == null || !leafy.IsPlaying)
            {
                leafy = LoadWeatherSound(leafyPath, debugName + "-leafy");
                leafy?.Start();
                leafyFlag = leafy?.IsPlaying == true;
            }

            if (leafless == null || !leafless.IsPlaying)
            {
                leafless = LoadWeatherSound(leaflessPath, debugName + "-leafless");
                leafless?.Start();
                leaflessFlag = leafless?.IsPlaying == true;

                if (leafyFlag || leaflessFlag)
                    WeatherAudioManager.WeatherDebugLog($"{debugName} started (dual leafy+leafless)");
            }
        }

        /// <summary>Stop and dispose a dual-loop pair.</summary>
        private void StopDual(
            ref ILoadedSound leafy, ref ILoadedSound leafless,
            ref bool leafyFlag, ref bool leaflessFlag,
            string debugName)
        {
            if (leafy != null)
            {
                try { leafy.Stop(); leafy.Dispose(); } catch { }
                leafy = null;
            }
            if (leafless != null)
            {
                try { leafless.Stop(); leafless.Dispose(); } catch { }
                leafless = null;
            }
            if (leafyFlag || leaflessFlag)
                WeatherAudioManager.WeatherDebugLog($"{debugName} stopped");
            leafyFlag = false;
            leaflessFlag = false;
        }

        /// <summary>Ensure a single loop is playing. Loads if needed.</summary>
        private void EnsureSinglePlaying(
            ref ILoadedSound loop, string path,
            ref bool flag, string debugName)
        {
            if (loop == null || !loop.IsPlaying)
            {
                loop = LoadWeatherSound(path, debugName);
                loop?.Start();
                flag = loop?.IsPlaying == true;

                if (flag)
                    WeatherAudioManager.WeatherDebugLog($"{debugName} started");
            }
        }

        /// <summary>Stop and dispose a single loop.</summary>
        private void StopSingle(
            ref ILoadedSound loop, ref bool flag,
            string debugName)
        {
            if (loop != null)
            {
                try { loop.Stop(); loop.Dispose(); } catch { }
                loop = null;
                if (flag)
                    WeatherAudioManager.WeatherDebugLog($"{debugName} stopped");
                flag = false;
            }
        }

        // ── Per-type wrappers (preserve existing call sites in Update) ──

        private void EnsureRainPlaying() =>
            EnsureDualPlaying(ref rainLoopLeafy, ref rainLoopLeafless,
                "sounds/weather/tracks/rain-leafy.ogg", "sounds/weather/tracks/rain-leafless.ogg",
                ref rainLeafyPlaying, ref rainLeaflessPlaying, "Rain");

        private void StopRain() =>
            StopDual(ref rainLoopLeafy, ref rainLoopLeafless,
                ref rainLeafyPlaying, ref rainLeaflessPlaying, "Rain");

        private void EnsureHailPlaying() =>
            EnsureSinglePlaying(ref hailLoop, "sounds/weather/tracks/hail.ogg",
                ref hailPlaying, "Hail");

        private void StopHail() =>
            StopSingle(ref hailLoop, ref hailPlaying, "Hail");

        private void EnsureWindPlaying() =>
            EnsureDualPlaying(ref windLoopLeafy, ref windLoopLeafless,
                "sounds/weather/wind-leafy.ogg", "sounds/weather/wind-leafless.ogg",
                ref windLeafyPlaying, ref windLeaflessPlaying, "Wind");

        private void StopWind() =>
            StopDual(ref windLoopLeafy, ref windLoopLeafless,
                ref windLeafyPlaying, ref windLeaflessPlaying, "Wind");

        private void EnsureTremblePlaying() =>
            EnsureSinglePlaying(ref trembleLoop, "sounds/weather/tracks/verylowtremble.ogg",
                ref tremblePlaying, "Tremble");

        private void StopTremble() =>
            StopSingle(ref trembleLoop, ref tremblePlaying, "Tremble");

        /// <summary>Stop all weather sounds (for runtime disable).</summary>
        public void StopAll()
        {
            StopRain();
            StopHail();
            StopWind();
            StopTremble();
            smoothedRainIntensity = 0f;
            smoothedWindSpeed = 0f;
            smoothedHailIntensity = 0f;
            currentRainVol = 0f;
            currentWindVol = 0f;
            currentHailVol = 0f;
            currentTrembleVol = 0f;
        }

        // ════════════════════════════════════════════════════════════════
        // Sound Loading
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Load a weather sound from VS assets using the same approach VS uses.
        /// RelativePosition=true: listener-attached (non-positional), stereo OK.
        /// SoundType=Weather: categorized properly.
        /// </summary>
        private ILoadedSound LoadWeatherSound(string assetPath, string debugName)
        {
            try
            {
                var soundParams = new SoundParams()
                {
                    Location = new AssetLocation(assetPath),
                    ShouldLoop = true,
                    DisposeOnFinish = false,
                    RelativePosition = true,   // Listener-attached, same as VS
                    Position = new Vintagestory.API.MathTools.Vec3f(0, 0, 0),
                    Volume = 0f,               // Start silent, our Update() sets volume
                    SoundType = EnumSoundType.Weather
                };

                var sound = capi.World.LoadSound(soundParams);

                if (sound == null)
                {
                    WeatherAudioManager.WeatherDebugLog($"LoadWeatherSound FAILED: {assetPath} returned null");
                    return null;
                }

                WeatherAudioManager.WeatherDebugLog($"LoadWeatherSound OK: {assetPath} (sound={sound.GetType().Name})");
                return sound;
            }
            catch (Exception ex)
            {
                WeatherAudioManager.WeatherDebugLog($"LoadWeatherSound EXCEPTION for {debugName}: {ex.Message}");
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // EFX Filter Application
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Apply lowpass filter to a weather sound's OpenAL source.
        /// Creates the filter attachment on first call, then updates gainHF.
        /// </summary>
        private void ApplyFilter(ILoadedSound sound, int filterId, float gainHF)
        {
            if (!EfxHelper.IsAvailable || filterId <= 0 || sound == null) return;

            try
            {
                int sourceId = AudioRenderer.GetSourceId(sound);
                if (sourceId <= 0) return;

                // Update the filter's gainHF value
                EfxHelper.SetLowpassGainHF(filterId, gainHF);

                // Attach filter to source (idempotent — re-attaching same filter is safe)
                AudioRenderer.AttachFilter(sourceId, filterId);
            }
            catch (Exception ex)
            {
                WeatherAudioManager.WeatherDebugLog($"ApplyFilter error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Utility
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Convert Hz cutoff to OpenAL LowpassGainHF (0.001 - 1.0).
        /// Simple linear mapping: gainHF = cutoffHz / 22000.
        /// Clamped to [0.001, 1.0] — never full silence, never beyond Nyquist.
        /// </summary>
        private static float HzToGainHF(float cutoffHz)
        {
            float gainHF = cutoffHz / 22000f;
            return Math.Clamp(gainHF, 0.001f, 1.0f);
        }

        /// <summary>Linear interpolation.</summary>
        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        /// <summary>
        /// Smooth gainHF toward target. Weather changes slowly — no snap needed.
        /// </summary>
        private static float SmoothGainHF(float current, float target)
        {
            float diff = target - current;
            if (MathF.Abs(diff) < 0.001f) return target;
            return current + diff * LPF_SMOOTH_FACTOR;
        }

        /// <summary>
        /// Rate-limited volume smoothing. Moves toward target at a fixed max speed
        /// (VOLUME_MAX_RATE units/sec), giving consistent linear ramps regardless of
        /// gap size. Prevents both abrupt cuts AND exponential "big jump first tick"
        /// behavior. At 0.4/s: a 0→1 ramp takes ~2.5s, matching natural weather feel.
        /// </summary>
        /// <summary>
        /// Rate-limited smoothing for vanilla weather intensities.
        /// Only smooths weather on/off transitions — enclosure factors are applied
        /// after smoothing so indoor/outdoor volume changes are instant.
        /// </summary>
        private static float SmoothValue(float current, float target, float dt)
        {
            float diff = target - current;
            if (MathF.Abs(diff) < 0.0001f) return target;

            float maxDelta = VOLUME_MAX_RATE * dt;
            if (MathF.Abs(diff) <= maxDelta) return target;
            return current + MathF.Sign(diff) * maxDelta;
        }

        /// <summary>Debug status for /soundphysics weather command.</summary>
        public string GetDebugStatus()
        {
            string duckStr = "";
            if (rainPositionalContribution > 0.01f || windPositionalContribution > 0.01f || hailPositionalContribution > 0.01f)
            {
                duckStr = $" Duck(r={rainPositionalContribution:F2} w={windPositionalContribution:F2} h={hailPositionalContribution:F2})";
            }
            return $"Rain:{(rainLeafyPlaying || rainLeaflessPlaying ? "ON" : "off")}(v={currentRainVol:F3} lpf={smoothedRainGainHF:F3}) " +
                   $"Hail:{(hailPlaying ? "ON" : "off")}(v={currentHailVol:F3} lpf={smoothedHailGainHF:F3}) " +
                   $"Wind:{(windLeafyPlaying || windLeaflessPlaying ? "ON" : "off")}(v={currentWindVol:F3} lpf={smoothedWindGainHF:F3}) " +
                   $"Tremble:{(tremblePlaying ? "ON" : "off")}(v={currentTrembleVol:F3} lpf={smoothedTrembleGainHF:F3})" +
                   duckStr;
        }

        public void Dispose()
        {
            StopAll();

            // Delete EFX filters (Layer 1)
            if (rainFilterId > 0) { EfxHelper.DeleteFilter(rainFilterId); rainFilterId = 0; }
            if (windFilterId > 0) { EfxHelper.DeleteFilter(windFilterId); windFilterId = 0; }
            if (trembleFilterId > 0) { EfxHelper.DeleteFilter(trembleFilterId); trembleFilterId = 0; }
            if (hailFilterId > 0) { EfxHelper.DeleteFilter(hailFilterId); hailFilterId = 0; }

            // Clear mono downmix cache
            AudioLoaderPatch.ClearMonoCache();

            WeatherAudioManager.WeatherDebugLog("RainAudioHandler disposed");
        }
    }
}
