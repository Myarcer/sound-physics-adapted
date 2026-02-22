using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Phase 5B: Orchestrates positional weather source pools (rain, wind, hail).
    /// All three types share the same TrackedOpening positions from OpeningTracker.
    ///
    /// Each pool gets its own PositionalSourcePool instance with type-specific:
    /// - Audio assets (rain-leafy.ogg, wind-leafy.ogg, hail.ogg)
    /// - Volume calculation (rain=intensity, wind=windSpeed, hail=hailIntensity)
    /// - Source budget (configurable per type, default 4 each)
    /// - Fade rates (wind slightly faster fade-in for responsiveness)
    ///
    /// Combined contribution from all pools feeds back to RainAudioHandler
    /// for Layer 1 ambient ducking (per-type: rain ducks rain L1, etc.)
    /// </summary>
    public class WeatherPositionalHandler : IDisposable
    {
        private readonly ICoreClientAPI capi;

        private PositionalSourcePool rainPool;
        private PositionalSourcePool windPool;
        private PositionalSourcePool hailPool;

        private bool initialized = false;
        private bool disposed = false;

        // Per-type contribution for granular Layer 1 ducking
        public float RainContribution => rainPool?.Contribution ?? 0f;
        public float WindContribution => windPool?.Contribution ?? 0f;
        public float HailContribution => hailPool?.Contribution ?? 0f;

        /// <summary>Combined active count across all pools.</summary>
        public int TotalActiveCount =>
            (rainPool?.ActiveCount ?? 0) +
            (windPool?.ActiveCount ?? 0) +
            (hailPool?.ActiveCount ?? 0);

        public WeatherPositionalHandler(ICoreClientAPI api)
        {
            capi = api;
        }

        /// <summary>
        /// Initialize all source pools with config-driven budgets and per-type tuning.
        /// </summary>
        public void Initialize()
        {
            if (initialized) return;

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null) return;

            // ── Rain pool ──
            rainPool = new PositionalSourcePool(
                capi,
                config.MaxPositionalRainSources,
                "RAIN");

            rainPool.AssetResolver = (isLeafy) => new AssetLocation(
                isLeafy ? "sounds/weather/tracks/rain-leafy.ogg"
                        : "sounds/weather/tracks/rain-leafless.ogg");

            rainPool.VolumeCalculator = CalculateRainVolume;

            // ── Wind pool ──
            windPool = new PositionalSourcePool(
                capi,
                config.MaxPositionalWindSources,
                "WIND");

            windPool.AssetResolver = (isLeafy) => new AssetLocation(
                isLeafy ? "sounds/weather/wind-leafy.ogg"
                        : "sounds/weather/wind-leafless.ogg");

            windPool.VolumeCalculator = CalculateWindVolume;

            // Wind: slightly faster fade-in for responsiveness (wind gusts are sudden)
            windPool.FadeInRate = 0.15f;  // ~1.2s to 90%
            windPool.FadeOutRate = 0.08f; // ~3.5s fade (wind lingers)

            // ── Hail pool ──
            hailPool = new PositionalSourcePool(
                capi,
                config.MaxPositionalHailSources,
                "HAIL");

            hailPool.AssetResolver = (_) => new AssetLocation("sounds/weather/tracks/hail.ogg");

            hailPool.VolumeCalculator = CalculateHailVolume;

            // Hail: faster fade-in (percussive onset), standard fade-out
            hailPool.FadeInRate = 0.15f;

            initialized = true;
            WeatherAudioManager.WeatherDebugLog(
                $"WeatherPositionalHandler init: rain={config.MaxPositionalRainSources} " +
                $"wind={config.MaxPositionalWindSources} hail={config.MaxPositionalHailSources} sources");
        }

        /// <summary>
        /// Update all positional source pools with the shared tracked openings.
        /// Called from WeatherAudioManager each tick after OpeningTracker.Update().
        /// </summary>
        /// <param name="trackedOpenings">Shared openings from OpeningTracker</param>
        /// <param name="rainIntensity">Current rain intensity (0-1)</param>
        /// <param name="hailIntensity">Current hail intensity (0-1)</param>
        /// <param name="windSpeed">Current wind speed (0-1+)</param>
        /// <param name="isLeafy">Whether current biome is leafy</param>
        /// <param name="earPos">Player ear position for proximity fade</param>
        /// <param name="skyCoverage">Current sky coverage (0=outdoors, 1=fully roofed)</param>
        /// <param name="occlusionFactor">Current occlusion (0=clear air, 1=behind walls)</param>
        public void UpdateAll(
            IReadOnlyList<TrackedOpening> trackedOpenings,
            float rainIntensity,
            float hailIntensity,
            float windSpeed,
            bool isLeafy,
            Vec3d earPos,
            float skyCoverage,
            float occlusionFactor)
        {
            if (!initialized || disposed) return;

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null) return;

            // Outdoor attenuation: when player is outdoors (low sky coverage AND low occlusion),
            // Layer 2 positional sources should defer to Layer 1 ambient bed.
            // Under trees: skyC=0.5, occl=0.1 → outdoorness=0.45 → attenuation=0.62
            // Indoors:     skyC=0.95, occl=0.8 → outdoorness=0.01 → attenuation=0.99
            // Cave opening: skyC=0.9, occl=0.2 → outdoorness=0.08 → attenuation=0.93
            float outdoorness = (1f - skyCoverage) * (1f - occlusionFactor);
            float outdoorAttenuation = 1f - (outdoorness * 0.85f); // Max 85% reduction when fully outdoors
            outdoorAttenuation = Math.Max(outdoorAttenuation, 0.15f); // Floor at 15% volume

            // Rain: always update when raining
            if (config.EnablePositionalWeather)
            {
                rainPool?.UpdateSources(
                    trackedOpenings,
                    rainIntensity,
                    isLeafy,
                    config.PositionalWeatherVolume * outdoorAttenuation,
                    earPos);
            }
            else
            {
                rainPool?.FadeOutAll();
            }

            // Wind: update when wind is blowing — no threshold gate.
            // Even light breeze (0.1) should produce gentle wind sound.
            // SmoothValue in RainAudioHandler handles ramp-up/down.
            if (config.EnablePositionalWind && windSpeed > 0.01f)
            {
                windPool?.UpdateSources(
                    trackedOpenings,
                    windSpeed,   // Wind uses windSpeed as its "intensity"
                    isLeafy,
                    config.PositionalWindVolume * outdoorAttenuation,
                    earPos);
            }
            else
            {
                windPool?.FadeOutAll();
            }

            // Hail: update when hailing
            if (config.EnablePositionalHail && hailIntensity > 0.01f)
            {
                hailPool?.UpdateSources(
                    trackedOpenings,
                    hailIntensity,
                    isLeafy,
                    config.PositionalHailVolume * outdoorAttenuation,
                    earPos);
            }
            else
            {
                hailPool?.FadeOutAll();
            }
        }

        /// <summary>
        /// Fade out all sources across all pools (outdoor gate, feature disable, etc.)
        /// </summary>
        public void FadeOutAll()
        {
            rainPool?.FadeOutAll();
            windPool?.FadeOutAll();
            hailPool?.FadeOutAll();
        }

        /// <summary>
        /// Stop all sources immediately across all pools.
        /// </summary>
        public void StopAll()
        {
            rainPool?.StopAll();
            windPool?.StopAll();
            hailPool?.StopAll();
        }

        // ════════════════════════════════════════════════════════════════
        // Multi-pool audibility (for OpeningTracker persistence)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if ANY pool has an audible source at the given TrackingId.
        /// If rain OR wind OR hail source at that opening is still audible,
        /// the opening stays alive in OpeningTracker.
        /// </summary>
        public bool IsSourceAudible(int trackingId)
        {
            if (rainPool?.IsSourceAudible(trackingId) == true) return true;
            if (windPool?.IsSourceAudible(trackingId) == true) return true;
            if (hailPool?.IsSourceAudible(trackingId) == true) return true;
            return false;
        }

        /// <summary>
        /// Check if ANY pool has a repositioned source at the given TrackingId.
        /// Repositioned = AudioPhysicsSystem is routing the sound through indirect
        /// paths (bounce rays) because direct LOS is occluded. This means the player
        /// is hearing the sound around a corner, NOT through a sealed opening.
        /// </summary>
        public bool IsSourceRepositioned(int trackingId)
        {
            if (rainPool?.IsSourceRepositioned(trackingId) == true) return true;
            if (windPool?.IsSourceRepositioned(trackingId) == true) return true;
            if (hailPool?.IsSourceRepositioned(trackingId) == true) return true;
            return false;
        }

        // ════════════════════════════════════════════════════════════════
        // Per-type volume calculators
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Rain positional volume: intensity * sqrt(openingSize) * multiplier.
        /// sqrt curve: 1 col=0.35, 2=0.50, 4=0.71, 8=1.0.
        /// Floor 0.35 — even single-column openings should be audible.
        /// </summary>
        private static float CalculateRainVolume(TrackedOpening opening, float intensity, float multiplier)
        {
            // Zero weight (structural shrink) = zero volume, don't apply floor
            if (opening.SmoothedClusterWeight < 0.01f) return 0f;

            float sizeWeight = MathF.Sqrt(Math.Min(opening.SmoothedClusterWeight / 8f, 1f));
            sizeWeight = Math.Max(sizeWeight, 0.35f);
            return Math.Clamp(intensity * sizeWeight * multiplier, 0f, 1f);
        }

        /// <summary>
        /// Wind positional volume: windSpeed * sqrt(openingSize) * multiplier.
        /// Wind is broader — every opening contributes more evenly.
        /// Floor 0.30, divisor 6 instead of 8 (wind fills openings more).
        /// </summary>
        private static float CalculateWindVolume(TrackedOpening opening, float windSpeed, float multiplier)
        {
            // Zero weight (structural shrink) = zero volume, don't apply floor
            if (opening.SmoothedClusterWeight < 0.01f) return 0f;

            float sizeWeight = MathF.Sqrt(Math.Min(opening.SmoothedClusterWeight / 6f, 1f));
            sizeWeight = Math.Max(sizeWeight, 0.30f);
            return Math.Clamp(windSpeed * sizeWeight * multiplier, 0f, 1f);
        }

        /// <summary>
        /// Hail positional volume: hailIntensity * sqrt(openingSize) * multiplier.
        /// Hail is percussive — slightly louder per-source than rain.
        /// Floor 0.40, same divisor as rain.
        /// </summary>
        private static float CalculateHailVolume(TrackedOpening opening, float hailIntensity, float multiplier)
        {
            // Zero weight (structural shrink) = zero volume, don't apply floor
            if (opening.SmoothedClusterWeight < 0.01f) return 0f;

            float sizeWeight = MathF.Sqrt(Math.Min(opening.SmoothedClusterWeight / 8f, 1f));
            sizeWeight = Math.Max(sizeWeight, 0.40f);
            return Math.Clamp(hailIntensity * sizeWeight * multiplier, 0f, 1f);
        }

        // ════════════════════════════════════════════════════════════════
        // Debug
        // ════════════════════════════════════════════════════════════════

        public string GetDebugStatus()
        {
            if (TotalActiveCount == 0)
                return "  No active positional weather sources";

            var sb = new System.Text.StringBuilder();
            int rainActive = rainPool?.ActiveCount ?? 0;
            int windActive = windPool?.ActiveCount ?? 0;
            int hailActive = hailPool?.ActiveCount ?? 0;

            sb.AppendLine($"  Positional: rain={rainActive} wind={windActive} hail={hailActive} " +
                         $"(duck: rain={RainContribution:F2} wind={WindContribution:F2} hail={HailContribution:F2})");

            if (rainActive > 0) sb.AppendLine(rainPool.GetDebugStatus());
            if (windActive > 0) sb.AppendLine(windPool.GetDebugStatus());
            if (hailActive > 0) sb.AppendLine(hailPool.GetDebugStatus());

            return sb.ToString().TrimEnd();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            rainPool?.Dispose();
            windPool?.Dispose();
            hailPool?.Dispose();

            rainPool = null;
            windPool = null;
            hailPool = null;

            initialized = false;
            WeatherAudioManager.WeatherDebugLog("WeatherPositionalHandler disposed");
        }
    }
}
