using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Controls when occlusion raycasts fire for each active sound.
    ///
    /// Design: simple distance-based intervals + static cache + block change invalidation.
    /// Inspired by Sound Physics Remastered (Minecraft) which ships successfully
    /// with just distance culling + rate caps + a 1-second stale cache.
    ///
    /// Every tick (50ms), all sounds are iterated (cheap). The expensive raycast
    /// only fires when a sound's interval is due and something actually changed.
    /// </summary>
    public class AudioPhysicsSystem
    {
        // === Distance Buckets ===
        private const float CLOSE_DISTANCE = 10f;    // 0-10m: every tick
        private const float NEAR_DISTANCE = 30f;     // 10-30m: every 200ms
        // >30m: Far, every 500ms

        private const long CLOSE_INTERVAL_MS = 50;    // Every tick - corners, doors, fire
        private const long NEAR_INTERVAL_MS = 200;    // 4 ticks - still responsive
        private const long FAR_INTERVAL_MS = 500;     // 10 ticks - background sounds

        // === Static Cache ===
        // Skip raycast if nothing moved. Force refresh on timer OR on block change.
        private const double MOVE_THRESHOLD = 0.25;    // Blocks - below this = "didn't move"
        private const long FORCE_REFRESH_MS = 2000;    // 2s - catch block changes we missed

        // === Block Change Grace Window ===
        // After a block change (door open/close), the static cache would re-engage immediately
        // because neither player nor sound moved. This causes audible "step-down" artifacts:
        // one raycast fires, then 2s of silence until FORCE_REFRESH. The grace window keeps
        // the static cache bypassed for long enough that sounds reconverge smoothly.
        private const long BLOCK_CHANGE_GRACE_MS = 1000; // 1s of unrestricted updates after block change
        private long lastBlockChangeInvalidationMs = 0;

        // === Sky Probe ===
        private const int SKY_PROBE_RAY_COUNT = 5;
        private const float SKY_PROBE_DISTANCE = 64f;
        private const long SKY_PROBE_INTERVAL_MS = 500;

        private class SoundCacheEntry
        {
            public Vec3d LastSoundPos;
            public Vec3d LastPlayerPos;
            public float CachedOcclusion;
            public long LastUpdateTimeMs;       // Interval gating (resets on cache hit + raycast)
            public long LastRaycastTimeMs;      // Force-refresh gating (resets ONLY on raycast)
            public float Distance;

            // NOTE: this entry holds no smoothed value of any kind. Filter gain, reverb
            // sends and position are converged once, in AudioRenderer.SmoothAll on the
            // fixed 25 ms tick (audit item A4). This tick writes raw targets only.

            // Acoustic boundary detection: when shared airspace is low, we're near an
            // acoustic edge (corner, doorway). These sounds need faster updates + convergence.
            public float LastSharedAirspaceRatio;  // 0 = fully occluded, 1 = full airspace
            public bool NearAcousticBoundary;      // true = treat as close-range priority

            // True only when the last raycast actually applied a repositioned path
            // (occluded sound routed toward an opening via ApplySoundPath). Clear-LOS
            // and permeated-only sounds are NOT repositioned. Exposed via
            // IsSoundRepositioned.
            public bool IsRepositioned;

            // Face memory of an ambient volume sound (locked face, stabilized position).
            // Created on the first resolve; stays null for every other sound.
            // The throttle envelope lives on the renderer entry, next to the filter it
            // multiplies — a throttled sound is not processed here at all.
            public AmbientVolumeState Ambient;

            // Set true on first detection via ConsumeLocalPlayerOcclusionPosition.
            // Persists for this sound's lifetime; cleared when sound exits active set.
            // Means: this specific sound was triggered by the local player — no occlusion, reverb only.
            public bool IsLocalPlayerSound;

            // ISSUE 20: cached entombment verdict (BFS pre-check). Re-verified every
            // ENTOMB_RECHECK_MS or after a block change, whichever comes first.
            public bool IsEntombed;
            public long LastEntombCheckMs;
        }

        // ISSUE 20: how long a BFS entombment verdict (either way) stays valid.
        // Covers the player physically entering the cavity; digging is covered by
        // block-change invalidation.
        private const long ENTOMB_RECHECK_MS = 1000;

        private Dictionary<ILoadedSound, SoundCacheEntry> soundCache = new Dictionary<ILoadedSound, SoundCacheEntry>();

        // === Spatial Reverb Cell Cache ===
        private ReverbCellCache reverbCellCache;
        private long lastCellCacheCleanupMs = 0;
        private const long CELL_CACHE_CLEANUP_INTERVAL_MS = 5000;
        private int cellCacheHitsThisTick = 0;

        // Sky probe
        private bool isOutdoors = false;
        private long lastSkyProbeTimeMs = 0;
        private Vec3d lastSkyProbePos = null;

        // === OPTIMIZATION: Cached reverb for player-position sounds ===
        // Sounds at player position (menu clicks, block breaking, bow draw) don't need:
        // - Occlusion (they're AT the listener, always 0)
        // - Path resolution (no repositioning needed)
        // - Individual reverb calc (share cached player-environment reverb)
        private const float PLAYER_POS_THRESHOLD = 1.0f;  // Sounds within 1m of player
        private ReverbResult cachedPlayerReverb = ReverbResult.None;
        private long lastPlayerReverbTimeMs = 0;
        private const long PLAYER_REVERB_INTERVAL_MS = 250;  // Recalc every 250ms
        // Listener position of the last computed player reverb. The raytrace reads no
        // clock and seeds its probe RNG from the two positions only, so for a listener
        // that has not moved in a world that has not changed it returns the value the
        // cache already holds.
        private Vec3d lastPlayerReverbPos = null;

        // === Pre-allocated reusable objects (AlconDevTest optimization) ===
        // Reduces GC pressure in hot paths that run every tick
        private static readonly Vec3d[] skyProbeDiagonals = new Vec3d[]
        {
            new Vec3d(0.707, 0.707, 0),
            new Vec3d(-0.707, 0.707, 0),
            new Vec3d(0, 0.707, 0.707),
            new Vec3d(0, 0.707, -0.707)
        };
        private static readonly Vec3d skyProbeUp = new Vec3d(0, 1, 0);
        private BlockPos _reusableSkyProbePos = new BlockPos(0, 0, 0, 0);
        private List<ILoadedSound> _cleanupRemoveList = new List<ILoadedSound>();
        private HashSet<ILoadedSound> _cleanupActiveSet = new HashSet<ILoadedSound>();
        private Vec3d _reusableSoundPos = new Vec3d();

        // === Per-tick budget candidate list (pre-allocated, reused each tick) ===
        private struct RaycastCandidate
        {
            public ILoadedSound Sound;
            public SoundCacheEntry Cache;
            public Vec3d SoundPos;
            public float Distance;
            public bool IsOverdue; // >FORCE_REFRESH_MS since last raycast
        }
        private List<RaycastCandidate> _candidates = new List<RaycastCandidate>();

        // === Sound throttle: collects all positional sound distances per tick ===
        private Dictionary<ILoadedSound, float> _soundDistances = new Dictionary<ILoadedSound, float>();

        // Time budget: stops processing sounds when tick exceeds budget to prevent lagspikes
        private readonly Stopwatch _tickStopwatch = new Stopwatch();
        private int budgetExceededThisTick = 0;

        // An overdue sound passes the normal time budget, so a new oneshot (a footstep,
        // an impact) is never deferred and never plays unfiltered. It still needs a
        // ceiling: InvalidateCache marks EVERY sound overdue at once, so after a block
        // change the whole per-tick allowance could run with no time limit at all. This
        // factor sets that ceiling as a multiple of MaxTickBudgetMs — 20 ms at the
        // default 8 ms. A sound deferred at that point comes back on the next tick,
        // still overdue, and jumps the queue.
        private const float OVERDUE_BUDGET_FACTOR = 2.5f;

        // Stats
        private int updatedThisTick = 0;
        private int cachedThisTick = 0;
        private int playerPosThisTick = 0;  // Sounds at player position (fast path)
        private int skippedThisTick = 0;
        private int deferredThisTick = 0;
        private int throttledThisTick = 0;
        private int totalActive = 0;

        public bool IsOutdoors => isOutdoors;

        public int SuggestedReverbRayCount
        {
            get
            {
                var config = SoundPhysicsAdaptedModSystem.Config;
                if (config == null) return 32;
                return isOutdoors ? Math.Min(8, config.ReverbRayCount) : config.ReverbRayCount;
            }
        }

        /// <summary>
        /// Called every 50ms from game tick handler.
        /// </summary>
        public void Update(Vec3d playerPos, IBlockAccessor blockAccessor, long currentTimeMs)
        {
            if (playerPos == null || blockAccessor == null) return;

            updatedThisTick = 0;
            cachedThisTick = 0;
            playerPosThisTick = 0;
            skippedThisTick = 0;
            deferredThisTick = 0;
            throttledThisTick = 0;
            cellCacheHitsThisTick = 0;

            // Initialize cell cache on first use
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (reverbCellCache == null && config != null && config.EnableReverbCellCache)
            {
                reverbCellCache = new ReverbCellCache();
            }

            // Periodic cell cache cleanup
            if (reverbCellCache != null && currentTimeMs - lastCellCacheCleanupMs > CELL_CACHE_CLEANUP_INTERVAL_MS)
            {
                reverbCellCache.Cleanup(currentTimeMs, playerPos);
                lastCellCacheCleanupMs = currentTimeMs;
            }

            // Update cached player-position reverb if stale
            UpdatePlayerPositionReverb(playerPos, blockAccessor, currentTimeMs);

            UpdateSkyProbe(playerPos, blockAccessor, currentTimeMs);
            UpdateAllSounds(playerPos, blockAccessor, currentTimeMs);
        }

        /// <summary>
        /// Invalidate all cached results. Called on block change events.
        /// Doesn't force immediate raycast - just ensures the next interval
        /// check actually runs the raycast instead of returning stale data.
        /// Also starts a grace window where the static cache is bypassed,
        /// preventing the "one raycast then 2s freeze" step-down artifact.
        /// </summary>
        public void InvalidateCache(long currentTimeMs = 0)
        {
            foreach (var kvp in soundCache)
            {
                // Reset BOTH timers so the very next tick:
                // 1. Passes the interval gate (LastUpdateTimeMs = 0)
                // 2. Bypasses the static cache (LastRaycastTimeMs = 0)
                kvp.Value.LastRaycastTimeMs = 0;
                kvp.Value.LastUpdateTimeMs = 0;
            }

            // Start grace window — static cache stays bypassed for BLOCK_CHANGE_GRACE_MS
            if (currentTimeMs > 0)
                lastBlockChangeInvalidationMs = currentTimeMs;

            // NOTE: cell cache is NOT cleared here. The block-change path already does
            // targeted invalidation (OnBlockChanged -> CellCache.InvalidateCellAt for the
            // changed cell + boundary neighbors). A blanket Clear() on every debounced
            // block change threw away every unrelated cell — mining/combat degraded the
            // dedupe cache to ~0% hit rate exactly when raycast load peaks.

            if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"ACOUSTICS: Cache invalidated ({soundCache.Count} entries, cell cache untouched — targeted invalidation)");
        }

        private void UpdateAllSounds(Vec3d playerPos, IBlockAccessor blockAccessor, long currentTimeMs)
        {
            _tickStopwatch.Restart();
            budgetExceededThisTick = 0;

            // Reset viz nearest-sound tracking for this tick
            DebugVisualization.Instance?.ResetTickCapture();

            var activeSounds = AudioRenderer.GetActiveSounds();
            int count = 0;
            _candidates.Clear();
            _soundDistances.Clear();

            var config = SoundPhysicsAdaptedModSystem.Config;
            int maxPerTick = config?.MaxSoundsPerTick ?? 25;

            // === PASS 1: Iterate all sounds, apply cheap gates, collect raycast candidates ===
            foreach (var sound in activeSounds)
            {
                count++;

                if (!soundCache.TryGetValue(sound, out var cache))
                {
                    cache = new SoundCacheEntry();
                    soundCache[sound] = cache;
                }

                // Resolve position: prefer stored (from SetPosition patches), fall back to Params
                Vec3d soundPos = AudioRenderer.GetStoredPosition(sound);
                if (soundPos == null)
                {
                    try
                    {
                        var pos = sound.Params?.Position;
                        if (pos != null && (pos.X != 0 || pos.Y != 0 || pos.Z != 0))
                        {
                            _reusableSoundPos.Set(pos.X, pos.Y, pos.Z);
                            soundPos = _reusableSoundPos;
                        }
                    }
                    catch { }
                }
                if (soundPos == null) continue;

                // Adjust sound position for multi-block sources (e.g. doors)
                soundPos = SoundSourceAdjuster.Adjust(soundPos, blockAccessor);

                float distance = (float)playerPos.DistanceTo(soundPos);
                cache.Distance = distance;

                // Track distance for throttle evaluation (every sound, not just candidates)
                _soundDistances[sound] = distance;

                // --- Interval gate: skip if not due yet ---
                // Sounds near acoustic boundaries (low shared airspace = near a corner/doorway)
                // get promoted to CLOSE_INTERVAL regardless of distance. The boundary between
                // "muffled behind wall" and "clear through opening" is a few blocks wide —
                // any player movement there flips the sound dramatically. Must be responsive.
                bool atBoundary = cache.NearAcousticBoundary;
                long interval = (atBoundary || distance <= CLOSE_DISTANCE) ? CLOSE_INTERVAL_MS
                              : distance <= NEAR_DISTANCE  ? NEAR_INTERVAL_MS
                              : FAR_INTERVAL_MS;

                long timeSinceUpdate = currentTimeMs - cache.LastUpdateTimeMs;
                if (timeSinceUpdate < interval)
                {
                    skippedThisTick++;
                    continue;
                }

                // --- Static cache: skip if nothing moved ---
                long timeSinceRaycast = currentTimeMs - cache.LastRaycastTimeMs;
                // New sounds (LastRaycastTimeMs==0) are ALWAYS overdue to ensure immediate processing.
                // Oneshot sounds like footsteps/impacts must not be deferred or they'll play wrong.
                bool isOverdue = cache.LastRaycastTimeMs == 0 || timeSinceRaycast >= FORCE_REFRESH_MS;

                // Block change grace window: after a door/block change, keep the static cache
                // bypassed so sounds reconverge smoothly instead of freezing for 2s.
                bool inGraceWindow = lastBlockChangeInvalidationMs > 0
                    && (currentTimeMs - lastBlockChangeInvalidationMs) < BLOCK_CHANGE_GRACE_MS;

                bool staticCacheEnabled = config?.EnableStaticSoundCache ?? true;

                // No throttle bypass is needed here any more. The fade envelope runs in
                // AudioRenderer.SmoothAll on the 25 ms tick, so it keeps moving while a
                // sound sits in this cache or waits out its interval.
                if (staticCacheEnabled && !inGraceWindow && cache.LastPlayerPos != null && cache.LastSoundPos != null && !isOverdue)
                {
                    double playerMoved = playerPos.DistanceTo(cache.LastPlayerPos);
                    double soundMoved = soundPos.DistanceTo(cache.LastSoundPos);

                    if (playerMoved < MOVE_THRESHOLD && soundMoved < MOVE_THRESHOLD)
                    {
                        cachedThisTick++;
                        cache.LastUpdateTimeMs = currentTimeMs;
                        continue;
                    }
                }

                // Sound passed all cheap gates — needs raycasting.
                // Clone soundPos if it's the reusable instance (will be overwritten next iteration).
                Vec3d candidatePos = (soundPos == _reusableSoundPos) ? soundPos.Clone() : soundPos;

                _candidates.Add(new RaycastCandidate
                {
                    Sound = sound,
                    Cache = cache,
                    SoundPos = candidatePos,
                    Distance = distance,
                    IsOverdue = isOverdue
                });
            }

            // === THROTTLE EVALUATION ===
            // Decide which sounds get full processing vs heavy muting.
            // Must run BEFORE Pass 2 so throttled sounds can be cheaply skipped.
            var throttle = SoundPhysicsAdaptedModSystem.Throttle;
            throttle?.EvaluateThrottle(_soundDistances);

            // === PASS 2: Sort candidates by priority, process up to budget ===
            // Priority: overdue sounds first (starvation prevention), then by distance ascending (close sounds first)
            if (_candidates.Count > 1)
            {
                _candidates.Sort((a, b) =>
                {
                    // Overdue sounds always come first
                    if (a.IsOverdue != b.IsOverdue)
                        return a.IsOverdue ? -1 : 1;
                    // Within same priority tier, closer sounds first
                    return a.Distance.CompareTo(b.Distance);
                });
            }

            int processed = 0;
            int overdueProcessed = 0;
            int maxOverdue = Math.Max(4, (config?.MaxOverdueSoundsPerTick ?? 6));
            float timeBudgetMs = config?.MaxTickBudgetMs ?? 8f;
            for (int i = 0; i < _candidates.Count; i++)
            {
                var candidate = _candidates[i];

                // A throttled sound is not measured at all. Its filter target stays where
                // the last measurement left it, and the renderer fades it out from there.
                // It costs nothing until it gets its slot back, and then it is overdue and
                // jumps the queue.
                if (throttle != null && throttle.IsThrottled(candidate.Sound))
                {
                    throttledThisTick++;
                    continue;
                }

                // TIME BUDGET: stop processing when tick exceeds budget.
                // Always allow at least 1 sound per tick (prevents complete starvation).
                // Overdue sounds (new/stale) get the wider ceiling, so a new oneshot is
                // not deferred, but the tick still ends. See OVERDUE_BUDGET_FACTOR.
                if (timeBudgetMs > 0 && processed > 0)
                {
                    float limitMs = candidate.IsOverdue ? timeBudgetMs * OVERDUE_BUDGET_FACTOR : timeBudgetMs;
                    float elapsedMs = (float)_tickStopwatch.Elapsed.TotalMilliseconds;
                    if (elapsedMs >= limitMs)
                    {
                        budgetExceededThisTick++;
                        deferredThisTick++;
                        continue;
                    }
                }

                // COUNT BUDGET: overdue sounds get priority but are still capped
                if (maxPerTick > 0 && processed >= maxPerTick)
                {
                    // Over normal budget — only allow overdue, up to maxOverdue extra
                    if (!candidate.IsOverdue || overdueProcessed >= maxOverdue)
                    {
                        deferredThisTick++;
                        continue;
                    }
                    overdueProcessed++;
                }

                UpdateSoundAcoustics(candidate.Sound, candidate.Cache, candidate.SoundPos,
                    candidate.Distance, playerPos, blockAccessor, currentTimeMs);
                processed++;
            }
            _tickStopwatch.Stop();

            updatedThisTick = processed;
            totalActive = count;
            CleanupCache();

            if (SoundPhysicsAdaptedModSystem.IsDebugEnabled && (updatedThisTick > 0 || cachedThisTick > 0 || deferredThisTick > 0 || playerPosThisTick > 0))
            {
                string cellCacheInfo = reverbCellCache != null ? $" cellHits={cellCacheHitsThisTick} cells={reverbCellCache.CellCount}" : "";
                string throttleInfo = throttle != null ? $" throttle={throttle.ThrottledCount} thrSkip={throttledThisTick}" : "";
                string budgetInfo = budgetExceededThisTick > 0 ? $" budgetDeferred={budgetExceededThisTick} tickMs={_tickStopwatch.Elapsed.TotalMilliseconds:F1}" : "";
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"ACOUSTICS: updated={updatedThisTick} cached={cachedThisTick} " +
                    $"skipped={skippedThisTick} deferred={deferredThisTick} playerPos={playerPosThisTick} " +
                    $"total={totalActive} outdoor={isOutdoors}{cellCacheInfo}{throttleInfo}{budgetInfo}");
            }

            // Per-tick viz diagnostic: log when viz wanted data but no raytrace fired
            var vizTick = DebugVisualization.Instance;
            if (SoundPhysicsAdaptedModSystem.IsDebugEnabled && vizTick != null && vizTick.AnyAcousticVizActive && !vizTick.HasCapturedThisTick)
            {
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[VIZ-TICK] No capture this tick: updated={updatedThisTick} cached={cachedThisTick} skipped={skippedThisTick} total={totalActive}");
            }
        }

        /// <summary>
        /// Updates reverb cache for player position every PLAYER_REVERB_INTERVAL_MS.
        /// Player-position sounds (UI, block breaking, bow draw) reuse this cached reverb.
        /// </summary>
        private void UpdatePlayerPositionReverb(Vec3d playerPos, IBlockAccessor blockAccessor, long currentTimeMs)
        {
            if (currentTimeMs - lastPlayerReverbTimeMs < PLAYER_REVERB_INTERVAL_MS) return;
            lastPlayerReverbTimeMs = currentTimeMs;

            // Static listener, unchanged world: the raytrace would return what the cache
            // already holds, so skip it. The block-change grace window lets a door or a
            // wall reach the player reverb on the next cadence step.
            bool inGraceWindow = lastBlockChangeInvalidationMs > 0
                && (currentTimeMs - lastBlockChangeInvalidationMs) < BLOCK_CHANGE_GRACE_MS;
            if (!inGraceWindow && lastPlayerReverbPos != null
                && playerPos.DistanceTo(lastPlayerReverbPos) < MOVE_THRESHOLD)
                return;

            // Calculate reverb at player position (no occlusion - it's from player to player)
            var (reverbResult, _) = AcousticRaytracer.CalculateWithPaths(playerPos, playerPos, blockAccessor, 0f);
            cachedPlayerReverb = reverbResult;
            lastPlayerReverbPos = playerPos.Clone();

            if (SoundPhysicsAdaptedModSystem.IsReverbDebugEnabled)
                SoundPhysicsAdaptedModSystem.ReverbDebugLog(
                    $"[PLAYER-REVERB] g0={reverbResult.SendGain0:F2} g1={reverbResult.SendGain1:F2} g2={reverbResult.SendGain2:F2} g3={reverbResult.SendGain3:F2}");
        }

        /// <summary>
        /// Measures one sound and writes its TARGETS: filter gain, reverb sends, position.
        ///
        /// This method never smooths and never ramps. Every temporal move belongs to
        /// <see cref="AudioRenderer.SmoothAll"/> on the fixed 25 ms tick, because this one
        /// runs at 50, 200 or 500 ms depending on distance (audit item A4).
        ///
        /// Order of work:
        ///   1. fast path for sounds at the player, and for sounds the player caused
        ///   2. ambient volumes: face sampling gives position and occlusion at once
        ///   3. direct occlusion, plus the per-sound penetration override
        ///   4. paths: entombment check, cell cache, raytrace — reverb and opening data
        ///   5. repositioning decision and the gain that goes with it
        /// </summary>
        private void UpdateSoundAcoustics(ILoadedSound sound, SoundCacheEntry cache, Vec3d soundPos,
            float distance, Vec3d playerPos, IBlockAccessor blockAccessor, long currentTimeMs)
        {
            string soundName = sound.Params?.Location?.ToShortString() ?? "unknown";
            var config = SoundPhysicsAdaptedModSystem.Config;
            bool firstLogOfTick = updatedThisTick == 0;

            // === 1. PLAYER-POSITION FAST PATH ===
            // Sounds at the player (UI clicks, block breaking, bow draw) skip every
            // calculation: source equals listener means no occlusion, only reverb.
            //
            // This also catches world sounds the LOCAL PLAYER caused at any distance,
            // tagged by LoadSoundPatch.MarkLocalPlayerSoundPosition. The player knows what
            // they did; muffling their own block sounds because of a slab or some cattails
            // next to them is jarring. The reverb still describes the room correctly.
            //
            // The verdict is taken once and kept for the lifetime of the sound. Consuming
            // the queued position makes sure no other sound at the same block inherits it.
            if (!cache.IsLocalPlayerSound)
                cache.IsLocalPlayerSound = soundphysicsadapted.LoadSoundPatch
                    .ConsumeLocalPlayerOcclusionPosition(soundPos);
            bool isLocalPlayerSound = cache.IsLocalPlayerSound;

            if (distance < PLAYER_POS_THRESHOLD || isLocalPlayerSound)
            {
                playerPosThisTick++;
                cache.IsRepositioned = false;

                int? sourceId = AudioRenderer.GetValidatedSourceId(sound);
                if (sourceId.HasValue && sourceId.Value > 0)
                {
                    if (isLocalPlayerSound && distance >= PLAYER_POS_THRESHOLD)
                    {
                        // A local-player sound some blocks away (breaking a slab): clear any
                        // low-pass that landed on it before the tag arrived.
                        AudioRenderer.SetOcclusion(sound, 1.0f, soundPos, soundName);
                    }
                    AudioRenderer.SetReverbTarget(sound, cachedPlayerReverb);
                }

                cache.LastUpdateTimeMs = currentTimeMs;
                return;
            }

            // === 2. AMBIENT VOLUMES ===
            // Beehives, water and lava are box volumes whose vanilla position tracks the
            // player and often lands on a face that is blocked. AmbientVolumeResolver picks
            // the clearest player-facing face and returns its occlusion, so no second DDA
            // is needed. A point source with the Ambient type (a resonator) is downgraded
            // and follows the normal path, so probe rays can still route it around a wall.
            bool isAmbientVolume = AmbientVolumeResolver.IsVolumeSoundType(sound.Params?.SoundType);
            Vec3d acousticPos = soundPos;
            float derivedOcclusion = -1f;

            if (isAmbientVolume)
            {
                cache.Ambient ??= new AmbientVolumeState();
                var ambient = AmbientVolumeResolver.Resolve(sound, soundPos, playerPos, blockAccessor,
                    cache.Ambient, soundName, firstLogOfTick);
                isAmbientVolume = ambient.IsVolume;
                acousticPos = ambient.AcousticPos;
                derivedOcclusion = ambient.DerivedOcclusion;
            }

            // === 3. DIRECT OCCLUSION ===
            // All blocks are handled the same way — the AABB collision geometry decides.
            // Doors need no special case.
            float occlusion = derivedOcclusion >= 0f
                ? derivedOcclusion
                : OcclusionCalculator.Calculate(acousticPos, playerPos, blockAccessor);

            // Per-sound penetration override: a sound the player must hear (a bell, a
            // temporal rift) is occluded less and gets a floor under its gain.
            var materialConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;
            var penetrationOverride = materialConfig?.GetSoundPenetration(soundName);
            float penetrationFloor = -1f;
            if (penetrationOverride != null && penetrationOverride.OcclusionMultiplier < 1.0f)
            {
                float rawOcclusion = occlusion;
                occlusion *= penetrationOverride.OcclusionMultiplier;
                penetrationFloor = penetrationOverride.MinFilterFloor;
                if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"[PENETRATION] {soundName} rawOcc={rawOcclusion:F2} x{penetrationOverride.OcclusionMultiplier:F2} = {occlusion:F2} floor={penetrationFloor:F2}");
            }

            float targetGain = FilterPipeline.DirectGain(occlusion);

            bool isForceRefresh = cache.LastRaycastTimeMs > 0 && (currentTimeMs - cache.LastRaycastTimeMs) >= FORCE_REFRESH_MS;
            if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"[RAY] {soundName} d={distance:F1} occ={occlusion:F2} snd=({soundPos.X:F2},{soundPos.Y:F2},{soundPos.Z:F2}) plr=({playerPos.X:F2},{playerPos.Y:F2},{playerPos.Z:F2}) startBlk=({(int)Math.Floor(soundPos.X)},{(int)Math.Floor(soundPos.Y)},{(int)Math.Floor(soundPos.Z)})",
                    force: isForceRefresh);

            // === 4 + 5. PATHS, REVERB, REPOSITIONING ===
            if (config != null && config.EnableSoundRepositioning)
            {
                // Read the per-sound range from the vanilla parameters for the reverb
                // distance falloff. 32 blocks is the vanilla default.
                float soundRange = sound.Params?.Range ?? 32f;

                var paths = ResolveAcousticPaths(cache, soundPos, playerPos, blockAccessor,
                    occlusion, soundRange, currentTimeMs, soundName);

                PushReverbTarget(sound, paths.Reverb, soundName);

                targetGain = ResolveRepositioning(sound, cache, paths.Path, soundPos, acousticPos,
                    occlusion, distance, isAmbientVolume, targetGain, config, soundName, firstLogOfTick);
            }

            targetGain = FilterPipeline.ApplyPenetrationFloor(targetGain, penetrationFloor);

            // One write, one target. The renderer ramps toward it and multiplies the
            // throttle envelope in — nothing here knows about ramps.
            AudioRenderer.SetOcclusion(sound, targetGain, soundPos, soundName);

            cache.LastSoundPos = soundPos.Clone();
            cache.LastPlayerPos = playerPos.Clone();
            cache.CachedOcclusion = occlusion;
            cache.LastUpdateTimeMs = currentTimeMs;
            cache.LastRaycastTimeMs = currentTimeMs;
        }

        /// <summary>Reverb and path data for one sound this tick.</summary>
        private struct AcousticPaths
        {
            public ReverbResult Reverb;
            public SoundPathResult? Path;
        }

        /// <summary>
        /// Gets the reverb and the indirect paths for one sound, by the cheapest route
        /// that still answers the question: entombment first, then the cell cache, then a
        /// full raytrace.
        /// </summary>
        private AcousticPaths ResolveAcousticPaths(SoundCacheEntry cache, Vec3d soundPos, Vec3d playerPos,
            IBlockAccessor blockAccessor, float occlusion, float soundRange, long currentTimeMs, string soundName)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            var result = new AcousticPaths();
            bool didFullRaytrace = false;

            // ISSUE 20: BFS ENTOMBMENT PRE-CHECK.
            // A heavily occluded sound can sit in a sealed cavity (a cave below the player,
            // a walled cellar). A cheap flood fill proves that no air path exists before the
            // raytrace spends 32x4 rays to find the same. Entombed treatment: dry, no
            // repositioning, direct occlusion with no floors.
            bool entombed = false;
            if (occlusion >= EntombmentChecker.MIN_OCCLUSION_TO_CHECK)
            {
                bool verdictValid = cache.LastEntombCheckMs > 0
                    && currentTimeMs - cache.LastEntombCheckMs < ENTOMB_RECHECK_MS
                    && cache.LastEntombCheckMs > lastBlockChangeInvalidationMs;

                if (verdictValid)
                {
                    entombed = cache.IsEntombed;
                }
                else
                {
                    entombed = EntombmentChecker.Check(soundPos, playerPos, blockAccessor)
                        == EntombmentChecker.Result.Entombed;
                    cache.IsEntombed = entombed;
                    cache.LastEntombCheckMs = currentTimeMs;

                    if (entombed && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                        SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                            $"[ENTOMB] {soundName} occ={occlusion:F1} sealed cavity — raytrace skipped");
                }
            }
            else
            {
                cache.IsEntombed = false;
                cache.LastEntombCheckMs = 0;
            }

            if (entombed)
            {
                result.Reverb = ReverbResult.None;
                result.Path = null;
            }
            // CELL CACHE.
            // The key is (soundCell, playerCell), so it drops itself when the player moves
            // to another cell and needs no distance threshold. Close sounds use 2-block
            // player cells (responsive), far sounds 8-block cells (stable). The cache only
            // removes duplicate work: 50 entities in one pen share a single computation.
            else if (reverbCellCache != null && config.EnableReverbCellCache)
            {
                var cellEntry = reverbCellCache.TryGetCell(soundPos, playerPos, currentTimeMs, blockAccessor, out bool canStore);
                if (cellEntry != null)
                {
                    result.Reverb = cellEntry.Reverb;
                    result.Path = AcousticRaytracer.ResolvePathFromCache(cellEntry, soundPos, playerPos, occlusion, config);
                    cellCacheHitsThisTick++;

                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog(
                            $"[CELL-CACHE] HIT uses={cellEntry.UseCount} age={currentTimeMs - cellEntry.CreatedTimeMs}ms");
                }
                else if (canStore)
                {
                    var (rv, pr) = AcousticRaytracer.CalculateWithPathsCacheable(
                        soundPos, playerPos, blockAccessor, occlusion, soundRange,
                        out var bouncePoints, out int bounceCount,
                        out var openings, out int openingCount,
                        out float sharedAirspaceRatio, out float directOccOut, out bool hasDirectAirspaceOut);
                    result.Reverb = rv;
                    result.Path = pr;
                    didFullRaytrace = true;

                    reverbCellCache.StoreCellIfEmpty(soundPos, playerPos, currentTimeMs,
                        result.Reverb, bouncePoints, bounceCount,
                        openings, openingCount, sharedAirspaceRatio,
                        directOccOut, hasDirectAirspaceOut);
                }
                else
                {
                    // A wall runs through this cell — compute, but do not store, so the
                    // entry of the other zone survives.
                    var (rv, pr) = AcousticRaytracer.CalculateWithPaths(soundPos, playerPos, blockAccessor, occlusion, soundRange);
                    result.Reverb = rv;
                    result.Path = pr;
                    didFullRaytrace = true;
                }
            }
            else
            {
                var (rv, pr) = AcousticRaytracer.CalculateWithPaths(soundPos, playerPos, blockAccessor, occlusion, soundRange);
                result.Reverb = rv;
                result.Path = pr;
                didFullRaytrace = true;
            }

            var viz = DebugVisualization.Instance;
            if (viz != null && viz.AnyAcousticVizActive && didFullRaytrace)
            {
                viz.CaptureFromRaytracer(
                    AcousticRaytracer.CacheableBouncePoints, AcousticRaytracer.CacheableBounceCount,
                    AcousticRaytracer.CacheableOpenings, AcousticRaytracer.CacheableOpeningCount,
                    result.Path, soundPos, playerPos, occlusion);
            }

            return result;
        }

        /// <summary>
        /// Hands the raw reverb to the renderer, which converges and applies it.
        ///
        /// The source id is validated first: Vintage Story reuses ids, and a stale entry
        /// would put the reverb of a finished sound onto its successor.
        /// </summary>
        private void PushReverbTarget(ILoadedSound sound, ReverbResult reverb, string soundName)
        {
            int? validatedSourceId = AudioRenderer.GetValidatedSourceId(sound);

            if (SoundPhysicsAdaptedModSystem.IsReverbDebugEnabled)
            {
                string srcDbg = validatedSourceId.HasValue ? validatedSourceId.Value.ToString() : "STALE";
                SoundPhysicsAdaptedModSystem.ReverbDebugLog(
                    $"[REVERB-FOR] {soundName} src={srcDbg} -> g0={reverb.SendGain0:F2} g1={reverb.SendGain1:F2} g2={reverb.SendGain2:F2} g3={reverb.SendGain3:F2}");
            }

            // Wind has no reverb: it is a broad atmospheric sound, it does not reflect off
            // walls the way rain, footsteps and impacts do.
            bool isWindSound = soundName.Contains("wind-leaf");
            if (isWindSound) return;

            if (validatedSourceId.HasValue && validatedSourceId.Value > 0 && ReverbEffects.IsInitialized)
                AudioRenderer.SetReverbTarget(sound, reverb);
        }

        /// <summary>
        /// Decides whether the sound moves toward an opening, and returns the gain that
        /// belongs to that decision.
        /// </summary>
        private float ResolveRepositioning(ILoadedSound sound, SoundCacheEntry cache, SoundPathResult? pathResult,
            Vec3d soundPos, Vec3d acousticPos, float occlusion, float distance, bool isAmbientVolume,
            float directGain, SoundPhysicsConfig config, string soundName, bool firstLogOfTick)
        {
            // SPR skips direction work when the direct path is clear
            // (shouldEvaluateDirection returns false at occlusion 0 with
            // redirectNonOccludedSounds). The threshold must sit in the gap between foliage
            // and real walls:
            //   plant 0.02, leaves 0.05, leavesbranchy 0.12 -> clear, keep the position
            //   gravel 0.4, wood 0.6, stone 1.0             -> obstruction, allow the move
            // 0.3 separates them with margin. Without it, 40 bounce rays outvote the one
            // direct path and pan a thrown stone 16 degrees sideways.
            bool skipRepositioning = occlusion < 0.3f;

            // Ambient volumes never use the probes. Face sampling already gave the correct
            // origin, and the probe result would fight it. The raytrace still ran — reverb
            // shares the call — only the path is dropped here.
            if (isAmbientVolume)
                skipRepositioning = true;

            if (skipRepositioning)
            {
                // Clear line of sight, or an ambient volume: the sound keeps its place.
                // An ambient volume uses the face-sampled position, which is stable, rather
                // than the vanilla one that flips between box faces at the edges.
                cache.IsRepositioned = false;
                AudioRenderer.ResetSoundPosition(sound, isAmbientVolume ? acousticPos : soundPos);

                // Occlusion near the 0.3 threshold means foliage is building up, so keep
                // the fast update rate. Ambient volumes always update fast: their position
                // moves with the player.
                cache.NearAcousticBoundary = isAmbientVolume || occlusion > 0.15f;

                if (firstLogOfTick && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                {
                    if (isAmbientVolume)
                        SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                            $"[4B-AMBIENT] occ={occlusion:F2} gain={directGain:F3} " +
                            $"({(acousticPos != soundPos ? "face-sampled" : "vanilla pos")}, probe skip)");
                    else
                        SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                            $"[4B-LOS] occ={occlusion:F2}<0.3 gain={directGain:F3} (no repos)");
                }

                return directGain;
            }

            if (!pathResult.HasValue)
            {
                // No paths at all, or the rays cancelled out. The position returns on its
                // own through the renderer.
                cache.IsRepositioned = false;
                AudioRenderer.ResetSoundPosition(sound, soundPos);
                return directGain;
            }

            var path = pathResult.Value;

            // PERMEATED ONLY: every path goes through a wall, none through an opening.
            // Moving the sound is meaningless then — the weighted direction of random
            // wall-bleed rays jumps from 5 m to 19 m to 37 m between ticks (the beehive
            // flutter). SPR does not move such a sound either. The low-pass still applies.
            bool allPermeated = path.PathCount == 0 && path.PermeatedPathCount > 0;
            if (allPermeated)
                AudioRenderer.ResetSoundPosition(sound, soundPos);

            bool applied = allPermeated || AudioRenderer.ApplySoundPath(sound, path, soundPos);
            cache.IsRepositioned = applied && !allPermeated;

            float gain = directGain;
            if (applied)
            {
                cache.LastSharedAirspaceRatio = path.SharedAirspaceRatio;
                cache.NearAcousticBoundary = FilterPipeline.NearAcousticBoundary(path.SharedAirspaceRatio, occlusion);

                gain = FilterPipeline.OccludedGain(occlusion, path, distance, config, out var diag);

                if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"[4B-LPF] dOcc={occlusion:F2} gain={gain:F3} bend={diag.BendRatio:F2} " +
                        $"dark={diag.DiffractionDarkening:F2} airFloor={diag.AirspaceFloor:F2} " +
                        $"diffFloor={diag.DiffractionFloor:F3} air={diag.AirspaceRatio:F2} " +
                        $"openOcc={(diag.BestOpeningOcclusion < float.MaxValue ? diag.BestOpeningOcclusion.ToString("F1") : "-")} " +
                        $"open={diag.OpenPaths}{(cache.NearAcousticBoundary ? " BOUNDARY" : "")}");
            }

            if (firstLogOfTick && path.RepositionOffset > 0.1
                && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
            {
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"[4B-Path] off={path.RepositionOffset:F1}m bOcc={path.BlendedOcclusion:F2} " +
                    $"paths={path.PathCount}/{path.TotalPathCount} perm={path.PermeatedPathCount}");
            }

            return gain;
        }



        private void UpdateSkyProbe(Vec3d playerPos, IBlockAccessor blockAccessor, long currentTimeMs)
        {
            if (currentTimeMs - lastSkyProbeTimeMs < SKY_PROBE_INTERVAL_MS &&
                lastSkyProbePos != null &&
                playerPos.DistanceTo(lastSkyProbePos) < 2.0)
                return;

            lastSkyProbeTimeMs = currentTimeMs;
            lastSkyProbePos = playerPos.Clone();

            // PRIORITY: Use weather enclosure when weather system is active.
            // WeatherEnclosureCalculator casts 84 hemisphere rays every 100ms —
            // strictly better than our 5-ray binary probe. SmoothedOcclusionFactor < 0.1
            // means nearly all rays escape (outdoors). Only fall back to the cheap
            // 5-ray probe when weather is inactive (no rain/hail/wind).
            var weather = SoundPhysicsAdaptedModSystem.Weather;
            if (weather != null && weather.OcclusionFactor >= 0f)
            {
                // Weather system provides continuous 0-1 enclosure.
                // OcclusionFactor is already the smoothed value.
                // RawSkyCoverage/RawOcclusionFactor are unsmoothed.
                float smoothedOccl = weather.OcclusionFactor;
                float rawSky = weather.RawSkyCoverage;
                float rawOccl = weather.RawOcclusionFactor;

                // If weather system ran recently (any metric is non-zero),
                // use its superior enclosure data
                if (rawSky > 0f || rawOccl > 0f || smoothedOccl > 0f)
                {
                    bool was = isOutdoors;
                    isOutdoors = smoothedOccl < 0.1f;
                    if (isOutdoors != was)
                        if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                            SoundPhysicsAdaptedModSystem.DebugLog($"SKY PROBE (weather): {(isOutdoors ? "OUTDOORS" : "INDOORS")} (occl={smoothedOccl:F2})");
                    return;
                }
            }

            // FALLBACK: Cheap 5-ray sky probe when weather system is inactive.
            int skyHits = 0;
            // Use pre-allocated static directions (AlconDevTest optimization)
            if (!RayHitsBlock(playerPos, skyProbeUp, SKY_PROBE_DISTANCE, blockAccessor))
                skyHits++;

            for (int i = 0; i < skyProbeDiagonals.Length; i++)
            {
                if (!RayHitsBlock(playerPos, skyProbeDiagonals[i], SKY_PROBE_DISTANCE, blockAccessor))
                    skyHits++;
            }

            bool was2 = isOutdoors;
            // 4 of 5 is enough: a single blocked diagonal (tree branch, eave, cave
            // entrance lip) must not flip the player to "indoors".
            isOutdoors = (skyHits >= SKY_PROBE_RAY_COUNT - 1);
            if (isOutdoors != was2)
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"SKY PROBE (fallback): {(isOutdoors ? "OUTDOORS" : "INDOORS")} ({skyHits}/{SKY_PROBE_RAY_COUNT})");
        }

        private bool RayHitsBlock(Vec3d origin, Vec3d dir, float maxDist, IBlockAccessor ba)
        {
            // Use reusable BlockPos to reduce allocations (AlconDevTest optimization)
            for (float dist = 1f; dist <= maxDist; dist += 1f)
            {
                _reusableSkyProbePos.Set(
                    (int)Math.Floor(origin.X + dir.X * dist),
                    (int)Math.Floor(origin.Y + dir.Y * dist),
                    (int)Math.Floor(origin.Z + dir.Z * dist));
                Block b = ba.GetBlock(_reusableSkyProbePos);
                if (b != null && b.Id != 0 &&
                    b.BlockMaterial != EnumBlockMaterial.Air &&
                    b.BlockMaterial != EnumBlockMaterial.Plant &&
                    b.BlockMaterial != EnumBlockMaterial.Leaves)
                    return true;
            }
            return false;
        }

        private void CleanupCache()
        {
            if (soundCache.Count <= totalActive + 10) return;

            // Reuse pre-allocated collections (AlconDevTest optimization)
            _cleanupRemoveList.Clear();
            _cleanupActiveSet.Clear();

            foreach (var sound in AudioRenderer.GetActiveSounds())
                _cleanupActiveSet.Add(sound);

            foreach (var kvp in soundCache)
            {
                if (!_cleanupActiveSet.Contains(kvp.Key))
                    _cleanupRemoveList.Add(kvp.Key);
            }
            foreach (var key in _cleanupRemoveList)
            {
                soundCache.Remove(key);
                AmbientSoundPatches.RemoveSound(key);
            }
        }

        /// <summary>
        /// Query the cached DIRECT occlusion for a specific sound.
        /// Returns the raw DDA occlusion (0=clear, higher=more occluded).
        /// This is the direct line-of-sight occlusion, NOT the path-resolved value.
        /// Returns -1 if the sound is not in the cache (never processed).
        /// Used by weather system for audibility-based persistence.
        /// </summary>
        public float GetSoundOcclusion(ILoadedSound sound)
        {
            if (sound == null) return -1f;
            if (soundCache.TryGetValue(sound, out var cache))
            {
                return cache.CachedOcclusion;
            }
            return -1f;
        }

        /// <summary>
        /// Query the EFFECTIVE occlusion for a specific sound.
        /// Returns the path-resolved (blended + smoothed) occlusion when available,
        /// otherwise falls back to the raw direct DDA occlusion.
        /// 
        /// When a sound is repositioned via bounce rays, the effective occlusion
        /// is much lower than the direct DDA value (e.g., direct=8.0 but
        /// path-resolved=1.2 because sound reaches player around a corner).
        /// 
        /// Returns -1 if the sound is not in the cache (never processed).
        /// Used by weather system for audibility checks — a sound that's heard
        /// through indirect paths should not be considered inaudible.
        /// </summary>
        public float GetEffectiveOcclusion(ILoadedSound sound)
        {
            if (sound == null) return -1f;
            if (!soundCache.TryGetValue(sound, out var cache)) return -1f;

            // Read back what the sound actually sounds like: the renderer holds the gain
            // that is on the OpenAL filter right now, with every floor already in it
            // (shared airspace, probe opening, diffraction). Converting it back to
            // occlusion units gives the true effective value, not the direct one.
            float gain = AudioRenderer.GetCurrentFilterGain(sound);
            if (gain > 0f)
                return OcclusionCalculator.FilterToOcclusion(gain);

            // Not tracked by the renderer (yet) — fall back to the direct DDA value.
            return cache.CachedOcclusion;
        }

        /// <summary>
        /// Check if a sound is currently being repositioned via path resolution.
        /// Returns true when the sound is occluded (direct DDA >= 1.0) and
        /// AudioPhysicsSystem has found an indirect path (bounce rays) to route
        /// the sound around the obstacle to the player.
        /// 
        /// Used by weather system: repositioned sounds should persist (player
        /// walked behind corner but sound is heard through opening). Non-repositioned
        /// sounds should fall back to timeout-based persistence.
        /// </summary>
        public bool IsSoundRepositioned(ILoadedSound sound)
        {
            if (sound == null) return false;
            if (soundCache.TryGetValue(sound, out var cache))
            {
                // Dedicated flag set only when ApplySoundPath actually ran for the last
                // raycast. The old check (HasSmoothedOcc) was set in the clear-LOS branch
                // too, so every processed sound reported "repositioned" after one pass.
                return cache.IsRepositioned;
            }
            return false;
        }

        /// <summary>
        /// Provides access to the spatial reverb cell cache for block-change invalidation.
        /// </summary>
        public ReverbCellCache CellCache => reverbCellCache;

        /// <summary>
        /// Reverb at the player's position, refreshed every 250ms. Used as the cheap
        /// first-frame approximation for newly started sounds (SoundStartPostfix) so
        /// their attack is never dry — the physics tick EMA-corrects within 50ms.
        /// </summary>
        public ReverbResult CachedPlayerReverb => cachedPlayerReverb;

        public string GetStats()
        {
            var cellStats = reverbCellCache?.GetStats();
            return $"Active={totalActive}, Updated={updatedThisTick}, " +
                   $"Cached={cachedThisTick}, Skipped={skippedThisTick}, " +
                   $"Deferred={deferredThisTick}, PlayerPos={playerPosThisTick}, Outdoor={isOutdoors}, ReverbRays={SuggestedReverbRayCount}" +
                   (cellStats != null ? $", CellCache=[{cellStats}]" : "");
        }

        public void Dispose()
        {
            soundCache.Clear();
            reverbCellCache?.Clear();
            reverbCellCache = null;
            lastPlayerReverbPos = null;
        }
    }
}
