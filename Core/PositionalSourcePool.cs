using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Reusable pool of positional mono OpenAL sources placed at tracked openings.
    /// Each weather type (rain, wind, hail) gets its own pool instance sharing
    /// the same TrackedOpening data from OpeningTracker.
    ///
    /// Lifecycle: WeatherPositionalHandler creates pool instances and calls
    /// UpdateSources() each tick with the shared tracked openings. The pool
    /// manages source creation/disposal, position updates, volume smoothing,
    /// and fade in/out. AudioPhysicsSystem handles occlusion, LPF, and
    /// repositioning around corners automatically via LoadSoundPatch registration.
    ///
    /// Supports two modes:
    /// - Looping (rain/wind/hail): continuous loops matched by TrackingId
    /// - OneShot (thunder): fire-and-forget positional sounds (Phase 5C)
    /// </summary>
    public class PositionalSourcePool : IDisposable
    {
        /// <summary>Pool operation mode.</summary>
        public enum PoolMode
        {
            /// <summary>Sources loop continuously, matched to openings by TrackingId.</summary>
            Looping,
            /// <summary>Sources play once and auto-recycle. No TrackingId matching.</summary>
            OneShot
        }

        /// <summary>
        /// A single positional weather sound source at a detected opening.
        /// Lifecycle managed by the pool; occlusion/repositioning by AudioPhysicsSystem.
        /// </summary>
        private class PositionalSource
        {
            public ILoadedSound Sound;       // World-positioned mono loop/oneshot
            public Vec3d WorldPos;            // Current placement position
            public int TrackingId;            // Matched to TrackedOpening.TrackingId (looping mode)
            public bool Active;              // Whether this slot is in use
            public float TargetVolume;       // What we're fading toward
            public float CurrentVolume;      // What's currently set (for smooth fading)
        }

        private readonly ICoreClientAPI capi;
        private readonly string debugTag;      // e.g. "RAIN", "WIND", "HAIL"
        private readonly PoolMode mode;
        private PositionalSource[] sources;
        private bool initialized = false;

        // ── Delegates for per-type customization ──

        /// <summary>
        /// Volume calculator: (opening, intensity, configMultiplier) → volume [0-1].
        /// Each weather type provides its own volume logic.
        /// </summary>
        public System.Func<TrackedOpening, float, float, float> VolumeCalculator { get; set; }

        /// <summary>
        /// Asset resolver: (isLeafy) → AssetLocation for the sound to play.
        /// Handles leafy/leafless variants (rain, wind) or static assets (hail).
        /// </summary>
        public System.Func<bool, AssetLocation> AssetResolver { get; set; }

        // ── Tunable parameters (sensible defaults, overridable per type) ──

        /// <summary>Fade-in rate per tick (exponential). Higher = faster fade-in.</summary>
        public float FadeInRate { get; set; } = 0.12f;   // ~1.5s to 90%

        /// <summary>Fade-out rate per tick (exponential). Higher = faster fade-out.</summary>
        public float FadeOutRate { get; set; } = 0.10f;   // ~3s to silence

        /// <summary>Volume below which a source is considered silent and can be stopped.</summary>
        public float MinVolume { get; set; } = 0.005f;

        /// <summary>
        /// Direct DDA occlusion above this = sound is inaudible.
        /// At occ=5.0: filter = exp(-5) = 0.007 = nearly silent.
        /// </summary>
        public float AudibilityOccThreshold { get; set; } = 5.0f;

        /// <summary>Sound range for OpenAL 3D distance model.</summary>
        public float SoundRange { get; set; } = 48f;

        /// <summary>
        /// Minimum cluster weight at which proximity fade applies.
        /// Below this threshold (small openings like doorways), no proximity fade.
        /// Above this (large open areas), sources fade when player walks through them.
        /// </summary>
        public float ProximityFadeMinClusterWeight { get; set; } = 4f;

        /// <summary>
        /// Distance at which proximity fade starts (full volume outside this range).
        /// </summary>
        public float ProximityFadeStartDist { get; set; } = 3.5f;

        /// <summary>
        /// Distance at which proximity fade reaches zero (player on top of source).
        /// </summary>
        public float ProximityFadeEndDist { get; set; } = 0.5f;

        // ── State ──

        /// <summary>Currently loaded asset variant (tracks leafy state for reload).</summary>
        private bool currentIsLeafy = true;

        /// <summary>
        /// Average volume of active sources (0-1). Used for Layer 1 ambient ducking.
        /// </summary>
        public float Contribution { get; private set; }

        /// <summary>Number of active source slots.</summary>
        public int ActiveCount
        {
            get
            {
                if (sources == null) return 0;
                int count = 0;
                for (int i = 0; i < sources.Length; i++)
                    if (sources[i].Active) count++;
                return count;
            }
        }

        public PositionalSourcePool(ICoreClientAPI api, int maxSources, string debugTag, PoolMode mode = PoolMode.Looping)
        {
            capi = api;
            this.debugTag = debugTag;
            this.mode = mode;

            sources = new PositionalSource[maxSources];
            for (int i = 0; i < maxSources; i++)
            {
                sources[i] = new PositionalSource();
            }
            initialized = true;
        }

        // ════════════════════════════════════════════════════════════════
        // Looping Mode: Continuous sources matched to tracked openings
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Update positional sources based on tracked openings (looping mode).
        /// Called from WeatherPositionalHandler after OpeningTracker.Update().
        ///
        /// Sets WORLD POSITION and BASE VOLUME only.
        /// AudioPhysicsSystem handles occlusion, LPF, and repositioning.
        /// </summary>
        /// <param name="trackedOpenings">Current tracked openings from OpeningTracker</param>
        /// <param name="intensity">Current weather intensity for this type (0-1)</param>
        /// <param name="isLeafy">Whether current biome is leafy (for asset variant)</param>
        /// <param name="volumeMultiplier">Config volume multiplier for this type</param>
        /// <param name="earPos">Player ear position for proximity fade</param>
        public void UpdateSources(
            IReadOnlyList<TrackedOpening> trackedOpenings,
            float intensity,
            bool isLeafy,
            float volumeMultiplier,
            Vec3d earPos)
        {
            if (!initialized || sources == null || mode != PoolMode.Looping) return;

            bool debug = SoundPhysicsAdaptedModSystem.Config?.DebugMode == true
                      && SoundPhysicsAdaptedModSystem.Config?.DebugPositionalWeather == true;

            // Track leafy state for asset reloads
            currentIsLeafy = isLeafy;

            // Disable conditions: no intensity, no openings
            if (intensity < 0.01f || trackedOpenings == null || trackedOpenings.Count == 0)
            {
                FadeOutAll();
                UpdateContribution();
                return;
            }

            int maxSlots = sources.Length;
            int openingCount = trackedOpenings.Count;
            Span<bool> openingAssigned = stackalloc bool[openingCount];

            // Pass 1: Update existing slot assignments (matched by TrackingId)
            for (int s = 0; s < maxSlots; s++)
            {
                var slot = sources[s];
                if (!slot.Active) continue;

                bool found = false;
                for (int o = 0; o < openingCount; o++)
                {
                    if (openingAssigned[o]) continue;
                    if (trackedOpenings[o].TrackingId == slot.TrackingId)
                    {
                        var opening = trackedOpenings[o];
                        slot.WorldPos = opening.WorldPos;
                        float baseVol = CalculateVolume(opening, intensity, volumeMultiplier);
                        slot.TargetVolume = baseVol * ProximityFadeFactor(opening, earPos);

                        if (slot.Sound != null && slot.Sound.IsPlaying)
                        {
                            slot.Sound.SetPosition(new Vec3f(
                                (float)opening.WorldPos.X,
                                (float)opening.WorldPos.Y,
                                (float)opening.WorldPos.Z));
                        }

                        openingAssigned[o] = true;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    slot.TargetVolume = 0f;
                }
            }

            // Pass 2: Assign unmatched openings to empty/fading-out slots
            for (int o = 0; o < openingCount; o++)
            {
                if (openingAssigned[o]) continue;

                int bestSlot = -1;
                float lowestVolume = float.MaxValue;

                for (int s = 0; s < maxSlots; s++)
                {
                    var slot = sources[s];
                    if (!slot.Active)
                    {
                        bestSlot = s;
                        break;
                    }
                    if (slot.TargetVolume <= 0f && slot.CurrentVolume < lowestVolume)
                    {
                        lowestVolume = slot.CurrentVolume;
                        bestSlot = s;
                    }
                }

                if (bestSlot < 0) break;

                var targetSlot = sources[bestSlot];
                var newOpening = trackedOpenings[o];

                if (targetSlot.Active && targetSlot.Sound != null)
                {
                    StopSource(targetSlot);
                }

                targetSlot.TrackingId = newOpening.TrackingId;
                targetSlot.WorldPos = newOpening.WorldPos;
                targetSlot.Active = true;
                targetSlot.CurrentVolume = 0f;
                float baseVol2 = CalculateVolume(newOpening, intensity, volumeMultiplier);
                targetSlot.TargetVolume = baseVol2 * ProximityFadeFactor(newOpening, earPos);

                EnsureSourcePlaying(targetSlot);

                if (debug)
                {
                    WeatherAudioManager.WeatherDebugLog(
                        $"[5B-{debugTag}] ASSIGN slot={bestSlot} trackId={newOpening.TrackingId} " +
                        $"pos=({newOpening.WorldPos.X:F0},{newOpening.WorldPos.Y:F0},{newOpening.WorldPos.Z:F0}) " +
                        $"targetVol={targetSlot.TargetVolume:F3}");
                }
            }

            // Pass 3: Apply volume smoothing to all active slots
            for (int s = 0; s < maxSlots; s++)
            {
                var slot = sources[s];
                if (!slot.Active) continue;

                float diff = slot.TargetVolume - slot.CurrentVolume;
                if (MathF.Abs(diff) < MinVolume)
                {
                    slot.CurrentVolume = slot.TargetVolume;
                }
                else if (diff > 0)
                {
                    slot.CurrentVolume += diff * FadeInRate;
                }
                else
                {
                    slot.CurrentVolume += diff * FadeOutRate;
                }

                if (slot.Sound != null && slot.Sound.IsPlaying)
                {
                    slot.Sound.SetVolume(Math.Max(0f, slot.CurrentVolume));
                }

                if (slot.TargetVolume <= 0f && slot.CurrentVolume <= MinVolume)
                {
                    StopSource(slot);

                    if (debug)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"[5B-{debugTag}] FADED OUT slot={s} trackId={slot.TrackingId}");
                    }
                }
            }

            UpdateContribution();
        }

        // ════════════════════════════════════════════════════════════════
        // OneShot Mode: Fire-and-forget positional sounds (Phase 5C thunder)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Play a one-shot positional sound at a world position (oneshot mode).
        /// Grabs an available slot, plays the sound, slot auto-recycles when done.
        /// Returns true if a slot was available and sound was started.
        /// </summary>
        /// <param name="worldPos">World position to play at</param>
        /// <param name="volume">Initial volume (0-1)</param>
        /// <param name="isLeafy">Whether current biome is leafy</param>
        public bool PlayOneShot(Vec3d worldPos, float volume, bool isLeafy)
        {
            return PlayOneShot(worldPos, volume, isLeafy, 0);
        }

        /// <summary>
        /// Play a one-shot positional sound at a world position (oneshot mode) with optional
        /// pre-applied LPF filter. The filter is attached BEFORE Start() to prevent transient
        /// bypass on sharp thunder cracks heard through walls.
        /// </summary>
        /// <param name="worldPos">World position to play at</param>
        /// <param name="volume">Initial volume (0-1)</param>
        /// <param name="isLeafy">Whether current biome is leafy</param>
        /// <param name="preApplyFilterId">OpenAL EFX filter ID to attach before Start(), 0 = none</param>
        public bool PlayOneShot(Vec3d worldPos, float volume, bool isLeafy, int preApplyFilterId)
        {
            if (!initialized || sources == null || mode != PoolMode.OneShot) return false;
            if (AssetResolver == null) return false;

            // Find an available slot
            int bestSlot = -1;
            for (int s = 0; s < sources.Length; s++)
            {
                if (!sources[s].Active)
                {
                    bestSlot = s;
                    break;
                }
            }

            if (bestSlot < 0) return false; // All slots busy

            var slot = sources[bestSlot];
            slot.WorldPos = worldPos;
            slot.Active = true;
            slot.CurrentVolume = volume;
            slot.TargetVolume = volume;

            try
            {
                AudioLoaderPatch.ForceMonoNextLoad = true;

                var soundParams = new SoundParams()
                {
                    Location = AssetResolver(isLeafy),
                    ShouldLoop = false,
                    DisposeOnFinish = false,  // We manage disposal
                    RelativePosition = false,
                    Position = new Vec3f((float)worldPos.X, (float)worldPos.Y, (float)worldPos.Z),
                    Volume = volume,
                    SoundType = EnumSoundType.Weather,
                    Range = SoundRange
                };

                slot.Sound = capi.World.LoadSound(soundParams);
                AudioLoaderPatch.ForceMonoNextLoad = false;

                if (slot.Sound != null)
                {
                    // Pre-apply LPF filter if provided (prevents transient bypass on thunder cracks)
                    if (preApplyFilterId > 0 && EfxHelper.IsAvailable)
                    {
                        int sourceId = AudioRenderer.GetSourceId(slot.Sound);
                        if (sourceId > 0)
                        {
                            AudioRenderer.AttachFilter(sourceId, preApplyFilterId);
                        }
                    }

                    slot.Sound.Start();
                    return true;
                }
            }
            catch (Exception ex)
            {
                AudioLoaderPatch.ForceMonoNextLoad = false;
                WeatherAudioManager.WeatherDebugLog($"[5C-{debugTag}] OneShot failed: {ex.Message}");
            }

            slot.Active = false;
            return false;
        }

        /// <summary>
        /// Tick one-shot sources: check if finished playing, recycle slots.
        /// Call each weather tick for oneshot mode pools.
        /// </summary>
        public void TickOneShotSources()
        {
            if (!initialized || sources == null || mode != PoolMode.OneShot) return;

            for (int s = 0; s < sources.Length; s++)
            {
                var slot = sources[s];
                if (!slot.Active) continue;

                // Check if sound finished playing
                if (slot.Sound == null || !slot.Sound.IsPlaying)
                {
                    StopSource(slot);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Audibility Check (for OpeningTracker persistence)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if a source at the given TrackingId is still audible to the player.
        /// Queries AudioPhysicsSystem's cached occlusion for the sound.
        /// </summary>
        public bool IsSourceAudible(int trackingId)
        {
            if (sources == null) return false;

            var audioPhysics = SoundPhysicsAdaptedModSystem.Acoustics;
            if (audioPhysics == null) return false;

            for (int i = 0; i < sources.Length; i++)
            {
                var slot = sources[i];
                if (slot.Active && slot.TrackingId == trackingId && slot.Sound != null)
                {
                    if (slot.CurrentVolume <= MinVolume) return false;

                    // Use EFFECTIVE occlusion (path-resolved when available),
                    // not raw direct DDA. A sound heard around a corner via
                    // bounce rays has low effective occlusion even when direct
                    // DDA is very high (wall between player and source).
                    float occ = audioPhysics.GetEffectiveOcclusion(slot.Sound);
                    if (occ < 0) return true; // Not yet in cache = just spawned, assume audible
                    bool audible = occ <= AudibilityOccThreshold;

                    var cfg = SoundPhysicsAdaptedModSystem.Config;
                    if (cfg?.DebugMode == true && cfg?.DebugPositionalWeather == true)
                    {
                        float directOcc = audioPhysics.GetSoundOcclusion(slot.Sound);
                        WeatherAudioManager.WeatherDebugLog(
                            $"[5B-{debugTag}-AUDIBLE] trackId={trackingId} slot={i} directOcc={directOcc:F2} " +
                            $"effectiveOcc={occ:F2} vol={slot.CurrentVolume:F3} audible={audible}");
                    }

                    return audible;
                }
            }
            return false;
        }

        /// <summary>
        /// Check if a source at the given TrackingId is currently being repositioned
        /// by AudioPhysicsSystem (heard through indirect paths via bounce rays).
        /// When true, the sound is occluded but a path around the obstacle was found.
        /// When false, the sound either has clear LOS or no indirect path exists.
        /// </summary>
        public bool IsSourceRepositioned(int trackingId)
        {
            if (sources == null) return false;

            var audioPhysics = SoundPhysicsAdaptedModSystem.Acoustics;
            if (audioPhysics == null) return false;

            for (int i = 0; i < sources.Length; i++)
            {
                var slot = sources[i];
                if (slot.Active && slot.TrackingId == trackingId && slot.Sound != null)
                {
                    return audioPhysics.IsSoundRepositioned(slot.Sound);
                }
            }
            return false;
        }

        // ════════════════════════════════════════════════════════════════
        // Source Lifecycle
        // ════════════════════════════════════════════════════════════════

        private float CalculateVolume(TrackedOpening opening, float intensity, float multiplier)
        {
            if (VolumeCalculator != null)
                return VolumeCalculator(opening, intensity, multiplier);

            // Weight near zero (structural shrink zeroed it) → volume must reach 0
            // so the source can fade out and be removed by audibility timeout.
            // The 0.35 floor below only applies to real (non-zeroed) openings to
            // prevent tiny 1-member clusters from being too quiet.
            if (opening.SmoothedClusterWeight < 0.01f)
                return 0f;

            // Default: same as original rain formula
            float sizeWeight = MathF.Sqrt(Math.Min(opening.SmoothedClusterWeight / 8f, 1f));
            sizeWeight = Math.Max(sizeWeight, 0.35f);
            return Math.Clamp(intensity * sizeWeight * multiplier, 0f, 1f);
        }

        /// <summary>
        /// Proximity fade: when player walks right through a positional source
        /// in a wide-open area, fade it out to prevent left-right panning artifacts.
        /// Only applies to large openings (cluster weight >= threshold), where
        /// the player is basically outdoors with rain/wind all around.
        /// Small openings (doorways, windows) keep full volume at any distance
        /// because that's the whole point of positional audio through openings.
        /// </summary>
        private float ProximityFadeFactor(TrackedOpening opening, Vec3d earPos)
        {
            // Skip proximity fade for small openings (doorways, windows)
            if (opening.SmoothedClusterWeight < ProximityFadeMinClusterWeight)
                return 1f;

            if (earPos == null) return 1f;

            float dist = (float)opening.WorldPos.DistanceTo(earPos);

            if (dist >= ProximityFadeStartDist) return 1f;
            if (dist <= ProximityFadeEndDist) return 0f;

            // Linear fade between end and start distances
            return (dist - ProximityFadeEndDist) / (ProximityFadeStartDist - ProximityFadeEndDist);
        }

        /// <summary>
        /// Ensure a source slot has a playing mono sound.
        /// Uses ForceMonoNextLoad flag to trigger stereo->mono downmix.
        /// NOTE: ForceMonoNextLoad is consumed synchronously during LoadSound.
        /// Multiple pools calling this sequentially in the same tick is safe
        /// because each LoadSound call is synchronous and consumes the flag.
        /// </summary>
        private void EnsureSourcePlaying(PositionalSource slot)
        {
            if (slot.Sound != null && slot.Sound.IsPlaying) return;
            if (AssetResolver == null) return;

            if (slot.Sound != null)
            {
                try
                {
                    AudioRenderer.UnregisterSound(slot.Sound);
                    slot.Sound.Stop();
                    slot.Sound.Dispose();
                }
                catch { }
                slot.Sound = null;
            }

            try
            {
                AudioLoaderPatch.ForceMonoNextLoad = true;

                var soundParams = new SoundParams()
                {
                    Location = AssetResolver(currentIsLeafy),
                    ShouldLoop = true,
                    DisposeOnFinish = false,
                    RelativePosition = false,
                    Position = new Vec3f(
                        (float)slot.WorldPos.X,
                        (float)slot.WorldPos.Y,
                        (float)slot.WorldPos.Z),
                    Volume = 0f,
                    SoundType = EnumSoundType.Weather,
                    Range = SoundRange
                };

                slot.Sound = capi.World.LoadSound(soundParams);
                AudioLoaderPatch.ForceMonoNextLoad = false;

                if (slot.Sound != null)
                {
                    slot.Sound.Start();

                    int channels = -1;
                    try { channels = slot.Sound.Channels; } catch { }
                    WeatherAudioManager.WeatherDebugLog(
                        $"[5B-{debugTag}] Created source trackId={slot.TrackingId} " +
                        $"channels={channels} " +
                        $"pos=({slot.WorldPos.X:F0},{slot.WorldPos.Y:F0},{slot.WorldPos.Z:F0})");
                }
            }
            catch (Exception ex)
            {
                AudioLoaderPatch.ForceMonoNextLoad = false;
                WeatherAudioManager.WeatherDebugLog($"[5B-{debugTag}] Failed to create source: {ex.Message}");
            }
        }

        private void StopSource(PositionalSource slot)
        {
            if (slot.Sound != null)
            {
                try
                {
                    AudioRenderer.UnregisterSound(slot.Sound);
                    slot.Sound.Stop();
                    slot.Sound.Dispose();
                }
                catch { }
                slot.Sound = null;
            }
            slot.Active = false;
            slot.CurrentVolume = 0f;
            slot.TargetVolume = 0f;
        }

        /// <summary>Set all active sources to fade out gracefully.</summary>
        public void FadeOutAll()
        {
            if (sources == null) return;

            for (int i = 0; i < sources.Length; i++)
            {
                var slot = sources[i];
                if (!slot.Active) continue;

                slot.TargetVolume = 0f;

                float diff = -slot.CurrentVolume;
                slot.CurrentVolume += diff * FadeOutRate;

                if (slot.Sound != null && slot.Sound.IsPlaying)
                {
                    slot.Sound.SetVolume(Math.Max(0f, slot.CurrentVolume));
                }

                if (slot.CurrentVolume <= MinVolume)
                {
                    StopSource(slot);
                }
            }

            UpdateContribution();
        }

        /// <summary>Stop all sources immediately (for disposal or feature toggle).</summary>
        public void StopAll()
        {
            if (sources == null) return;

            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i].Active)
                {
                    StopSource(sources[i]);
                }
            }
            Contribution = 0f;
        }

        private void UpdateContribution()
        {
            if (sources == null)
            {
                Contribution = 0f;
                return;
            }

            float totalContribution = 0f;
            int activeCount = 0;

            for (int i = 0; i < sources.Length; i++)
            {
                var slot = sources[i];
                if (slot.Active && slot.CurrentVolume > MinVolume)
                {
                    totalContribution += slot.CurrentVolume;
                    activeCount++;
                }
            }

            Contribution = activeCount > 0 ? Math.Min(totalContribution / activeCount, 1f) : 0f;
        }

        // ════════════════════════════════════════════════════════════════
        // Debug
        // ════════════════════════════════════════════════════════════════

        /// <summary>Per-source debug info for /soundphysics weather command.</summary>
        public string GetDebugStatus()
        {
            if (sources == null || ActiveCount == 0)
                return $"  [{debugTag}] No active positional sources";

            var audioPhysics = SoundPhysicsAdaptedModSystem.Acoustics;
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < sources.Length; i++)
            {
                var slot = sources[i];
                if (!slot.Active) continue;

                float directOcc = audioPhysics?.GetSoundOcclusion(slot.Sound) ?? -1f;
                float effectiveOcc = audioPhysics?.GetEffectiveOcclusion(slot.Sound) ?? -1f;
                bool audible = effectiveOcc >= 0 ? effectiveOcc <= AudibilityOccThreshold : true;
                string directStr = directOcc >= 0 ? $"{directOcc:F2}" : "N/A";
                string effectiveStr = effectiveOcc >= 0 ? $"{effectiveOcc:F2}" : "N/A";

                sb.AppendLine(
                    $"  [{debugTag}] Slot[{i}] id={slot.TrackingId} " +
                    $"pos=({slot.WorldPos?.X:F0},{slot.WorldPos?.Y:F0},{slot.WorldPos?.Z:F0}) " +
                    $"vol={slot.CurrentVolume:F3}/{slot.TargetVolume:F3} " +
                    $"directOcc={directStr} effOcc={effectiveStr} audible={audible} " +
                    $"playing={slot.Sound?.IsPlaying ?? false}");
            }
            return sb.ToString().TrimEnd();
        }

        public void Dispose()
        {
            StopAll();
            sources = null;
            initialized = false;
        }
    }
}
