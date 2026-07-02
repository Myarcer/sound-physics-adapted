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
            public Vec3f LastAppliedPos;     // Last position sent to OpenAL (dead zone filter)
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

        /// <summary>
        /// Position selector: (opening) → Vec3d world position for the source.
        /// When null (default), uses opening.WorldPos. Wind pool overrides this
        /// to use opening.WindWorldPos for ceiling-height placement on sky openings.
        /// </summary>
        public System.Func<TrackedOpening, Vec3d> PositionSelector { get; set; }

        // ── Tunable parameters (sensible defaults, overridable per type) ──

        /// <summary>
        /// Fade-in rate per tick (exponential). Higher = faster fade-in.
        /// Fast (~0.8s to 90%) because the Layer 1 bed-hold handover carries the
        /// missing energy until this source delivers — a slow spawn ramp here only
        /// prolongs the muffled hole, it no longer prevents any pop.
        /// </summary>
        public float FadeInRate { get; set; } = 0.25f;

        /// <summary>Fade-out rate per tick (exponential). Higher = faster fade-out.</summary>
        public float FadeOutRate { get; set; } = 0.10f;   // ~3s to silence

        /// <summary>
        /// Target-tracking rate for sources already established (above spawn threshold).
        /// Their target changes come from enclosure smoothing (~1s) which is already
        /// smooth — chasing it with the slow FadeInRate stacked a second lag on top,
        /// so Layer 2 rose 1.5-2s AFTER Layer 1 fell during indoor transitions (audible
        /// rain dropout in tunnel mouths). Fast tracking makes the enclosure smoother
        /// the single governing time constant for both layers.
        /// </summary>
        public float TrackRate { get; set; } = 0.35f;    // ~0.5s to 90%

        /// <summary>Below this volume a rising source uses FadeInRate (spawn ramp).</summary>
        private const float SPAWN_RAMP_THRESHOLD = 0.05f;

        /// <summary>
        /// Distance at which near-field gain compensation ends (full volume beyond).
        /// OpenAL barely attenuates within a few meters at Range=48, so a source at
        /// the tunnel mouth 1-2m from the ear played FAR louder than the ambient bed
        /// it replaced. Scale volume down linearly inside this radius instead.
        /// </summary>
        public float NearFieldRefDist { get; set; } = 6f;

        /// <summary>Gain floor at zero distance for near-field compensation.</summary>
        public float NearFieldMinGain { get; set; } = 0.4f;

        /// <summary>Volume below which a source is considered silent and can be stopped.</summary>
        public float MinVolume { get; set; } = 0.005f;

        /// <summary>
        /// Volume below which a fading-out slot can be evicted for reuse.
        /// Sources above this threshold are left to fade naturally, preventing
        /// audible hard-cuts when a new opening needs a slot.
        /// </summary>
        private const float EVICTION_VOLUME_THRESHOLD = 0.02f;

        /// <summary>
        /// Minimum squared distance an OpenAL source must move before SetPosition
        /// is called. Prevents panning jitter from sub-block centroid micro-shifts.
        /// 0.25 = 0.5 blocks minimum movement.
        /// </summary>
        private const float POSITION_UPDATE_MIN_DIST_SQ = 0.25f;

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

        /// <summary>
        /// Summed volume of active sources (capped at 1.5). Loudness proxy for the
        /// Layer 1 bed-hold handover: the bed only surrenders level that positional
        /// sources have ACTUALLY delivered, so detection/slot latency never leaves
        /// an audible hole.
        /// </summary>
        public float LoudnessSum { get; private set; }

        // ── Orphaned sounds: fading out detached from any slot ──
        // When a better opening takes over a busy slot, the old sound must not be
        // hard-cut, but waiting for it to fade inside the slot stalled new sources
        // for many seconds (Pass 1 kept re-arming the target). Instead the old
        // sound is moved here, fades out on its own, and the slot is free NOW.
        private class OrphanSound
        {
            public ILoadedSound Sound;
            public float Volume;
        }
        private readonly List<OrphanSound> orphans = new List<OrphanSound>(4);
        private const float ORPHAN_FADE_RATE = 0.20f; // per tick — ~1s to silence

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

            TickOrphans();

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
            // Per-slot opening score for eviction decisions (0 = unmatched/fading slot)
            Span<float> slotScores = stackalloc float[maxSlots];

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
                        var pos = PositionSelector != null ? PositionSelector(opening) : opening.WorldPos;
                        slot.WorldPos = pos;
                        float baseVol = CalculateVolume(opening, intensity, volumeMultiplier);
                        slot.TargetVolume = baseVol * ProximityFadeFactor(opening, earPos)
                                                    * NearFieldFactor(pos, earPos);
                        slotScores[s] = OpeningScore(opening, earPos);

                        if (slot.Sound != null && slot.Sound.IsPlaying)
                        {
                            // Dead zone: only update OpenAL position if moved significantly.
                            // Prevents panning jitter from sub-block centroid micro-shifts.
                            var newPos3f = new Vec3f((float)pos.X, (float)pos.Y, (float)pos.Z);
                            if (slot.LastAppliedPos == null || PositionDistSq(newPos3f, slot.LastAppliedPos) > POSITION_UPDATE_MIN_DIST_SQ)
                            {
                                slot.Sound.SetPosition(newPos3f);
                                slot.LastAppliedPos = newPos3f;
                            }
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

            // Pass 2: Assign unmatched openings to empty/fading-out slots.
            // Openings are processed best-score-first (weight/distance, verified bonus)
            // so nearby relevant openings win slots over distant persisted ones —
            // previously tracker insertion order let old far sources monopolize slots.
            Span<int> candidateOrder = stackalloc int[openingCount];
            int candidateCount = 0;
            for (int o = 0; o < openingCount; o++)
            {
                if (openingAssigned[o]) continue;
                if (trackedOpenings[o].Suppressed) continue; // Never assign slots to redundant openings
                candidateOrder[candidateCount++] = o;
            }
            // Insertion sort by descending score (candidateCount is tiny)
            for (int i = 1; i < candidateCount; i++)
            {
                int cur = candidateOrder[i];
                float curScore = OpeningScore(trackedOpenings[cur], earPos);
                int j = i - 1;
                while (j >= 0 && OpeningScore(trackedOpenings[candidateOrder[j]], earPos) < curScore)
                {
                    candidateOrder[j + 1] = candidateOrder[j];
                    j--;
                }
                candidateOrder[j + 1] = cur;
            }

            for (int c = 0; c < candidateCount; c++)
            {
                int o = candidateOrder[c];

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
                    // Only evict fading-out slots that are nearly silent.
                    // Sources above EVICTION_VOLUME_THRESHOLD fade naturally,
                    // preventing audible hard-cuts on slot reassignment.
                    if (slot.TargetVolume <= 0f && slot.CurrentVolume < EVICTION_VOLUME_THRESHOLD
                        && slot.CurrentVolume < lowestVolume)
                    {
                        lowestVolume = slot.CurrentVolume;
                        bestSlot = s;
                    }
                }

                if (bestSlot < 0)
                {
                    // All slots busy: if this opening clearly outclasses the weakest
                    // active source, take its slot NOW. The old sound is orphaned and
                    // fades out detached — waiting for an in-slot fade stalled better
                    // openings for many seconds (Pass 1 re-armed the target each tick).
                    float candidateScore = OpeningScore(trackedOpenings[o], earPos);
                    int weakest = -1;
                    float weakestScore = float.MaxValue;
                    for (int s = 0; s < maxSlots; s++)
                    {
                        if (slotScores[s] < weakestScore)
                        {
                            weakestScore = slotScores[s];
                            weakest = s;
                        }
                    }

                    if (weakest < 0 || candidateScore <= weakestScore * 2f) continue;

                    OrphanSlotSound(sources[weakest]);
                    slotScores[weakest] = float.MaxValue; // Claimed — not weakest anymore
                    bestSlot = weakest;
                    if (debug)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"[5B-{debugTag}] EVICT-SWAP slot={weakest} score={weakestScore:F2} " +
                            $"-> trackId={trackedOpenings[o].TrackingId} score={candidateScore:F2}");
                    }
                }

                var targetSlot = sources[bestSlot];
                var newOpening = trackedOpenings[o];

                if (targetSlot.Active && targetSlot.Sound != null)
                {
                    StopSource(targetSlot);
                }

                targetSlot.TrackingId = newOpening.TrackingId;
                var newPos = PositionSelector != null ? PositionSelector(newOpening) : newOpening.WorldPos;
                targetSlot.WorldPos = newPos;
                targetSlot.Active = true;
                targetSlot.CurrentVolume = 0f;
                float baseVol2 = CalculateVolume(newOpening, intensity, volumeMultiplier);
                targetSlot.TargetVolume = baseVol2 * ProximityFadeFactor(newOpening, earPos)
                                                   * NearFieldFactor(newPos, earPos);
                slotScores[bestSlot] = OpeningScore(newOpening, earPos);

                EnsureSourcePlaying(targetSlot);

                if (debug)
                {
                    WeatherAudioManager.WeatherDebugLog(
                        $"[5B-{debugTag}] ASSIGN slot={bestSlot} trackId={newOpening.TrackingId} " +
                        $"pos=({newPos.X:F0},{newPos.Y:F0},{newPos.Z:F0}) " +
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
                    // Spawn ramp (FadeInRate) only while the source is still quiet.
                    // Established sources track their target fast — the target is
                    // already smoothed upstream (enclosure EMA, cluster weight EMA),
                    // and stacking a second slow lag here made Layer 2 rise seconds
                    // after Layer 1 fell on indoor transitions.
                    float rate = slot.CurrentVolume < SPAWN_RAMP_THRESHOLD ? FadeInRate : TrackRate;
                    slot.CurrentVolume += diff * rate;
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

        public bool PlayOneShot(Vec3d worldPos, float volume, bool isLeafy, int preApplyFilterId)
        {
            return PlayOneShot(worldPos, volume, isLeafy, preApplyFilterId, 1.0f);
        }

        /// <summary>
        /// Play a one-shot positional sound at a world position (oneshot mode) with optional
        /// pre-applied LPF filter and pitch. The filter is attached BEFORE Start() to prevent transient
        /// bypass on sharp thunder cracks heard through walls.
        /// </summary>
        /// <param name="worldPos">World position to play at</param>
        /// <param name="volume">Initial volume (0-1)</param>
        /// <param name="isLeafy">Whether current biome is leafy</param>
        /// <param name="preApplyFilterId">OpenAL EFX filter ID to attach before Start(), 0 = none</param>
        /// <param name="pitch">Pitch multiplier (1.0 = normal)</param>
        public bool PlayOneShot(Vec3d worldPos, float volume, bool isLeafy, int preApplyFilterId, float pitch)
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
                    Pitch = pitch,
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
                    // Deliberately fading out (suppressed, evicted, or structurally
                    // zeroed) — must not refresh tracker persistence, or a whisper-
                    // volume far source keeps its opening alive forever.
                    if (slot.TargetVolume <= 0f) return false;

                    // Use EFFECTIVE occlusion (path-resolved when available),
                    // not raw direct DDA. A sound heard around a corner via
                    // bounce rays has low effective occlusion even when direct
                    // DDA is very high (wall between player and source).
                    float occ = audioPhysics.GetEffectiveOcclusion(slot.Sound);
                    if (occ < 0) return false; // Sound not in cache — unregistered or stale, not audible
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

        /// <summary>Squared distance between two Vec3f positions.</summary>
        private static float PositionDistSq(Vec3f a, Vec3f b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private float CalculateVolume(TrackedOpening opening, float intensity, float multiplier)
        {
            if (VolumeCalculator != null)
                return VolumeCalculator(opening, intensity, multiplier);

            // Weight near zero (structural shrink zeroed it) → volume must reach 0
            // so the source can fade out and be removed by audibility timeout.
            // The 0.35 floor below only applies to real (non-zeroed) openings to
            // prevent tiny 1-member clusters from being too quiet.
            if (opening.Suppressed || opening.SmoothedClusterWeight < 0.01f)
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
        /// Near-field gain compensation. OpenAL's distance model barely attenuates
        /// within a few meters at Range=48, so a source right at the ear plays at
        /// nearly full gain — far louder than the ambient bed it crossfades against.
        /// Linear ramp from NearFieldMinGain at 0m to 1.0 at NearFieldRefDist.
        /// </summary>
        private float NearFieldFactor(Vec3d sourcePos, Vec3d earPos)
        {
            if (earPos == null || sourcePos == null) return 1f;
            float dist = (float)sourcePos.DistanceTo(earPos);
            if (dist >= NearFieldRefDist) return 1f;
            return NearFieldMinGain + (1f - NearFieldMinGain) * (dist / NearFieldRefDist);
        }

        /// <summary>
        /// Slot-priority score: bigger and closer openings matter more, currently
        /// verified openings beat persisted ones, suppressed openings score zero.
        /// Used to order slot assignment and decide fade-swap evictions.
        /// </summary>
        private static float OpeningScore(TrackedOpening opening, Vec3d earPos)
        {
            if (opening.Suppressed) return 0f;
            float dist = earPos != null ? (float)opening.WorldPos.DistanceTo(earPos) : 0f;
            float score = (opening.SmoothedClusterWeight + 0.5f) / (1f + dist);
            if (opening.CurrentlyVerified) score *= 1.5f;
            return score;
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
            if (slot.Sound != null && slot.Sound.IsPlaying)
            {
                // Sound exists and playing — but check if it's actually registered
                // for occlusion processing. Unregistered sounds have N/A occlusion,
                // can't be tracked, and become immortal. Recreate them.
                if (AudioRenderer.IsRegistered(slot.Sound)) return;

                WeatherAudioManager.WeatherDebugLog(
                    $"[5B-{debugTag}] Stale unregistered source trackId={slot.TrackingId}, recreating");
            }
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
                    slot.LastAppliedPos = new Vec3f(
                        (float)slot.WorldPos.X,
                        (float)slot.WorldPos.Y,
                        (float)slot.WorldPos.Z);

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

        /// <summary>
        /// Detach a slot's sound into the orphan list (fades out on its own) and
        /// free the slot immediately for reassignment. No hard cut, no stall.
        /// </summary>
        private void OrphanSlotSound(PositionalSource slot)
        {
            if (slot.Sound != null)
            {
                orphans.Add(new OrphanSound { Sound = slot.Sound, Volume = slot.CurrentVolume });
                slot.Sound = null;
            }
            slot.Active = false;
            slot.CurrentVolume = 0f;
            slot.TargetVolume = 0f;
            slot.LastAppliedPos = null;
        }

        /// <summary>Fade out and dispose orphaned sounds. Called every update.</summary>
        private void TickOrphans()
        {
            for (int i = orphans.Count - 1; i >= 0; i--)
            {
                var orphan = orphans[i];
                orphan.Volume *= (1f - ORPHAN_FADE_RATE);

                if (orphan.Sound == null || !orphan.Sound.IsPlaying || orphan.Volume <= MinVolume)
                {
                    DisposeOrphan(orphan);
                    orphans.RemoveAt(i);
                }
                else
                {
                    orphan.Sound.SetVolume(orphan.Volume);
                }
            }
        }

        private static void DisposeOrphan(OrphanSound orphan)
        {
            if (orphan.Sound == null) return;
            try
            {
                AudioRenderer.UnregisterSound(orphan.Sound);
                orphan.Sound.Stop();
                orphan.Sound.Dispose();
            }
            catch { }
            orphan.Sound = null;
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
            slot.LastAppliedPos = null;
        }

        /// <summary>Set all active sources to fade out gracefully.</summary>
        public void FadeOutAll()
        {
            if (sources == null) return;

            TickOrphans();

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
            for (int i = orphans.Count - 1; i >= 0; i--)
            {
                DisposeOrphan(orphans[i]);
            }
            orphans.Clear();
            Contribution = 0f;
            LoudnessSum = 0f;
        }

        private void UpdateContribution()
        {
            if (sources == null)
            {
                Contribution = 0f;
                LoudnessSum = 0f;
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
            LoudnessSum = Math.Min(totalContribution, 1.5f);
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
                bool registered = AudioRenderer.IsRegistered(slot.Sound);
                bool audible = effectiveOcc >= 0 ? effectiveOcc <= AudibilityOccThreshold : false;
                string directStr = directOcc >= 0 ? $"{directOcc:F2}" : "N/A";
                string effectiveStr = effectiveOcc >= 0 ? $"{effectiveOcc:F2}" : "N/A";

                sb.AppendLine(
                    $"  [{debugTag}] Slot[{i}] id={slot.TrackingId} " +
                    $"pos=({slot.WorldPos?.X:F0},{slot.WorldPos?.Y:F0},{slot.WorldPos?.Z:F0}) " +
                    $"vol={slot.CurrentVolume:F3}/{slot.TargetVolume:F3} " +
                    $"directOcc={directStr} effOcc={effectiveStr} audible={audible} reg={registered} " +
                    $"playing={slot.Sound?.IsPlaying ?? false} " +
                    $"posMode={(PositionSelector != null ? "wind" : "default")}");
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
