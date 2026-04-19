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

            // Temporal smoothing for blended occlusion (dampens probe ray jitter)
            public float SmoothedBlendedOcc;    // EMA-smoothed blended occlusion value
            public bool HasSmoothedOcc;         // Whether SmoothedBlendedOcc has been seeded

            // Temporal smoothing for reverb send gains (dampens per-frame ray jitter)
            public float SmoothedG0, SmoothedG1, SmoothedG2, SmoothedG3;
            public bool HasSmoothedReverb;      // Whether reverb smoothing has been seeded

            // Acoustic boundary detection: when shared airspace is low, we're near an
            // acoustic edge (corner, doorway). These sounds need faster updates + convergence.
            public float LastSharedAirspaceRatio;  // 0 = fully occluded, 1 = full airspace
            public bool NearAcousticBoundary;      // true = treat as close-range priority

            // Throttle fade state
            // ThrottleFade: 1.0 = fully active, 0.0 = fully throttled/silent.
            // Steps toward 1 when unthrottled, toward 0 when throttled, using elapsed time.
            // CachedFilterForFade: last raycast-computed filter — used during fade-out so we
            // can lerp toward silence without needing to re-raytrace the throttled sound.
            public float ThrottleFade = 1.0f;
            public float CachedFilterForFade = 1.0f;

            // Throttle oscillation detection: prevents volume wobble when sounds
            // repeatedly cross the budget boundary (throttled↔unthrottled).
            // When 3+ transitions happen within 10s, fade is frozen at current level.
            // Unfreezes after 5s of stability (no transitions) in either state.
            public bool LastThrottledState;
            public long LastThrottleTransitionMs;
            public int ThrottleTransitionCount;
            public long ThrottleWindowStartMs;
            public bool ThrottleFrozen;

            // Face-sampled acoustic position for ambient volumes.
            // EMA-smoothed to prevent jitter; hysteresis prevents face-flip L/R panning.
            public Vec3d SmoothedAcousticPos;
            public bool HasSmoothedAcousticPos;
            public Vec3d CurrentBestFaceCenter; // For hysteresis: don't switch unless significantly better
        }

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

        // Stats
        private int updatedThisTick = 0;
        private int cachedThisTick = 0;
        private int playerPosThisTick = 0;  // Sounds at player position (fast path)
        private int skippedThisTick = 0;
        private int deferredThisTick = 0;
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
                reverbCellCache.Cleanup(currentTimeMs);
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

            reverbCellCache?.Clear();

            if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"ACOUSTICS: Cache invalidated ({soundCache.Count} entries, cell cache cleared)");
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

                // TIME BUDGET: stop processing when tick exceeds budget.
                // Always allow at least 1 sound per tick (prevents complete starvation).
                // Overdue sounds (new/stale) bypass the time budget to prevent indefinite deferral.
                if (timeBudgetMs > 0 && processed > 0 && !candidate.IsOverdue)
                {
                    float elapsedMs = (float)_tickStopwatch.Elapsed.TotalMilliseconds;
                    if (elapsedMs >= timeBudgetMs)
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

                // === Raycast this sound ===
                ProcessSoundRaycast(candidate.Sound, candidate.Cache, candidate.SoundPos,
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
                string throttleInfo = throttle != null ? $" throttle={throttle.ThrottledCount}" : "";
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

            // Calculate reverb at player position (no occlusion - it's from player to player)
            var (reverbResult, _) = AcousticRaytracer.CalculateWithPaths(playerPos, playerPos, blockAccessor, 0f);
            cachedPlayerReverb = reverbResult;

            if (SoundPhysicsAdaptedModSystem.IsReverbDebugEnabled)
                SoundPhysicsAdaptedModSystem.ReverbDebugLog(
                    $"[PLAYER-REVERB] g0={reverbResult.SendGain0:F2} g1={reverbResult.SendGain1:F2} g2={reverbResult.SendGain2:F2} g3={reverbResult.SendGain3:F2}");
        }

        /// <summary>
        /// Performs the expensive raycast + path resolution for a single sound.
        /// Extracted from UpdateAllSounds for the two-pass budget system.
        /// </summary>
        private void ProcessSoundRaycast(ILoadedSound sound, SoundCacheEntry cache, Vec3d soundPos,
            float distance, Vec3d playerPos, IBlockAccessor blockAccessor, long currentTimeMs)
        {
            string soundName = sound.Params?.Location?.ToShortString() ?? "unknown";

            // === PLAYER-POSITION FAST PATH ===
            // Sounds at player position (UI clicks, block breaking, bow draw) skip ALL calculations.
            // Source = listener means zero occlusion (no filter needed), only reverb applies.
            if (distance < PLAYER_POS_THRESHOLD)
            {
                playerPosThisTick++;

                // Apply cached player reverb only - no occlusion/filter needed
                int? sourceId = AudioRenderer.GetValidatedSourceId(sound);
                if (sourceId.HasValue && sourceId.Value > 0 && ReverbEffects.IsInitialized)
                {
                    ReverbEffects.ApplyToSource(sourceId.Value, cachedPlayerReverb);
                }

                cache.LastUpdateTimeMs = currentTimeMs;
                return;
            }
            // === THROTTLE FADE ===
            // Sounds near the eviction threshold can oscillate in/out of the budget (e.g. beehive
            // swarm at 40 blocks losing its slot every time a boar grunt fires nearby).
            // Instead of abrupt silence, we fade the filter gradually using elapsed time.
            //
            // Fade-out: throttled sound lerps from last good filter → minFilter over ThrottleFadeSeconds.
            //           No raycast needed — uses CachedFilterForFade from the last active update.
            // Fade-in:  newly unthrottled sound runs the full raycast, then lerps minFilter → computed.
            //
            // Both paths always return here (throttled) or continue below (active/fading-in).
            var config = SoundPhysicsAdaptedModSystem.Config;
            var throttle = SoundPhysicsAdaptedModSystem.Throttle;
            bool isThrottled = throttle != null && throttle.IsThrottled(sound);
            float minFilter = config?.MinLowPassFilter ?? 0.001f;
            float fadeDurationMs = (config?.ThrottleFadeSeconds ?? 0.5f) * 1000f;

            // === THROTTLE OSCILLATION DETECTION ===
            // When a sound repeatedly crosses the budget boundary (e.g. beehive at 40 blocks
            // losing its slot every time a nearby boar grunts), the fade direction keeps
            // reversing, causing audible volume wobble. Detect this and freeze the fade.
            bool throttleStateChanged = (isThrottled != cache.LastThrottledState);
            cache.LastThrottledState = isThrottled;

            if (throttleStateChanged)
            {
                cache.LastThrottleTransitionMs = currentTimeMs;

                // Reset tracking window if expired (>10s since window start)
                if (currentTimeMs - cache.ThrottleWindowStartMs > 10000)
                {
                    cache.ThrottleTransitionCount = 0;
                    cache.ThrottleWindowStartMs = currentTimeMs;
                }
                cache.ThrottleTransitionCount++;

                // 3+ transitions in 10s = oscillating → freeze fade at current level
                if (cache.ThrottleTransitionCount >= 3 && !cache.ThrottleFrozen)
                {
                    cache.ThrottleFrozen = true;
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog(
                            $"[THROTTLE] Froze fade for {soundName} at {cache.ThrottleFade:F2} " +
                            $"({cache.ThrottleTransitionCount} transitions in {(currentTimeMs - cache.ThrottleWindowStartMs) / 1000f:F1}s)");
                }
            }

            // Unfreeze when stable: no transitions for 5+ seconds means the budget settled
            if (cache.ThrottleFrozen && cache.LastThrottleTransitionMs > 0
                && currentTimeMs - cache.LastThrottleTransitionMs > 5000)
            {
                cache.ThrottleFrozen = false;
                cache.ThrottleTransitionCount = 0;
                cache.ThrottleWindowStartMs = currentTimeMs;
                // ThrottleFade stays at current value — normal fade logic resumes
                // from here, smoothly converging to the correct final state.
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[THROTTLE] Unfroze fade for {soundName} (stable 5s, fade={cache.ThrottleFade:F2})");
            }

            // Compute how much to step the fade based on real elapsed time.
            // Clamped to [0,1] so new sounds (LastUpdateTimeMs=0) don't get huge steps.
            long elapsedMs = cache.LastUpdateTimeMs > 0 ? currentTimeMs - cache.LastUpdateTimeMs : 50L;
            float fadeStep = fadeDurationMs > 0f ? Math.Min(1f, (float)elapsedMs / fadeDurationMs) : 1f;

            if (isThrottled)
            {
                if (!cache.ThrottleFrozen)
                    cache.ThrottleFade = Math.Max(0f, cache.ThrottleFade - fadeStep);

                float fadedFilter = minFilter + (cache.CachedFilterForFade - minFilter) * cache.ThrottleFade;
                AudioRenderer.SetOcclusion(sound, fadedFilter, soundPos, soundName);
                cache.LastUpdateTimeMs = currentTimeMs;
                cache.LastRaycastTimeMs = currentTimeMs;
                cache.LastPlayerPos = playerPos.Clone();
                cache.LastSoundPos = soundPos.Clone();
                return;
            }
            else
            {
                // Step fade up (fading in from a previous throttle, or already at 1 — no-op).
                if (!cache.ThrottleFrozen)
                    cache.ThrottleFade = Math.Min(1f, cache.ThrottleFade + fadeStep);
            }

            // NOTE: Sound occlusion uses OcclusionCalculator.Calculate() (multi-ray with voting).
            // All blocks (including doors) are treated uniformly — AABB collision geometry
            // determines occlusion naturally. No special door handling.

            // AMBIENT FACE-SAMPLED OCCLUSION: For ambient volume sounds (beehives, water, lava,
            // rainwindow), VS positions the sound at the nearest bbox surface — which may land
            // on an occluded face. We multi-sample all player-facing faces to determine:
            //   1. Acoustic position = face center with highest total clarity (ON the surface)
            //   2. Occlusion = derived directly from sample clarity (no second DDA needed)
            // This avoids the interior-point bug where averaging face centers produces a point
            // inside the bbox volume that always hits the volume's own blocks.
            // NOTE: Rainwindow uses SoundType.Weather, not Ambient — must include both.
            var soundType = sound.Params?.SoundType;
            bool isAmbientVolume = soundType == EnumSoundType.Ambient
                                || soundType == EnumSoundType.AmbientGlitchunaffected
                                || soundType == EnumSoundType.Weather;
            Vec3d acousticPos = soundPos;
            float ambientDerivedOcclusion = -1f; // -1 = not computed (use normal Calculate path)

            if (isAmbientVolume)
            {
                var samples = AmbientSoundPatches.GetFaceSamples(sound, out int sampleCount, out bool playerInside);
                var volBboxes = AmbientSoundPatches.GetBboxes(sound, out int volBboxCount);

                if (playerInside)
                {
                    // Player is inside the volume — center the sound on the player.
                    // This eliminates L/R panning entirely, creating an immersive enveloping effect.
                    // The proximity blend below will smooth the transition as the player enters.
                    acousticPos = playerPos;
                    ambientDerivedOcclusion = 0f;

                    if (updatedThisTick == 0 && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                        SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                            $"[AMBIENT-INSIDE] {soundName} playerInside=true, occ=0, centered on player");
                }
                else if (samples != null && sampleCount > 0)
                {
                    // Multi-ray voted occlusion per face center.
                    // Previously: per-sample single-ray DDA → averaged per face. This was
                    // unstable: DDA edge-clipping on a single wall produced occ=1/2/3 depending
                    // on exact ray angle through block corners. Now: one multi-ray convergent
                    // (9-ray voted) call per face center with bbox exclusion. The voting system
                    // stabilizes wall-counting: center ray is authoritative, offsets detect thin
                    // walls via supermajority. Early exits make this efficient: occluded walls
                    // return after 1 ray, clear paths after 1 ray, only ambiguous 0.3-0.5 range
                    // spawns all 9.
                    //
                    // KEY DESIGN: Occlusion = bestFaceOcc (least-occluded path), NOT average
                    // of all faces. Back-faces traverse the entire volume and would drag the
                    // average up, causing false muffling when standing right next to a clear face.
                    //
                    // Position = best face center with hysteresis to prevent L/R flip-flop.

                    // Hysteresis threshold: don't switch face unless new one is this much better.
                    const double FACE_SWITCH_THRESHOLD = 0.15;

                    Vec3d bestFaceCenter = null;
                    double bestFaceClarity = -1;
                    double bestFaceRawOcc = 0;
                    double bestFaceDist = double.MaxValue;
                    int facesTested = 0;

                    // Also track clarity + raw occ for the current (hysteresis) face
                    double currentLockedFaceClarity = -1;
                    double currentLockedFaceRawOcc = 0;

                    // Extract unique face centers from samples (grouped by face from AddFaceSamples)
                    Vec3d prevFaceCenter = null;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        var fc = samples[i].FaceCenter;
                        if (prevFaceCenter != null && fc == prevFaceCenter)
                            continue; // Same face, skip — we only need one DDA per face center
                        prevFaceCenter = fc;

                        // Multi-ray voted DDA from face center to player, excluding volume bboxes
                        float faceOcc = (volBboxes != null && volBboxCount > 0)
                            ? OcclusionCalculator.CalculateExcludingBboxes(
                                fc, playerPos, blockAccessor, volBboxes, volBboxCount)
                            : OcclusionCalculator.Calculate(fc, playerPos, blockAccessor);
                        float clarity = Math.Max(0f, 1f - faceOcc);
                        double faceDist = fc.DistanceTo(playerPos);
                        facesTested++;

                        // Prefer: 1) higher clarity, 2) lower raw occ (>0.01 margin),
                        // 3) closest face to player. The distance tiebreaker is critical
                        // for multi-bbox volumes (e.g. beehives with 2 bboxes): when all
                        // faces have identical clarity/occ in open air, without it the
                        // first-in-iteration face wins — often a far bbox face. This causes
                        // the proximity blend to miss (distance > 2.5 blocks) and walking
                        // L/R across the far bbox center flips the face → instant L/R pan.
                        if (clarity > bestFaceClarity ||
                            (clarity == bestFaceClarity && faceOcc < bestFaceRawOcc - 0.01f) ||
                            (clarity == bestFaceClarity && Math.Abs(faceOcc - bestFaceRawOcc) <= 0.01f
                             && faceDist < bestFaceDist))
                        {
                            bestFaceClarity = clarity;
                            bestFaceRawOcc = faceOcc;
                            bestFaceCenter = fc;
                            bestFaceDist = faceDist;
                        }

                        // Track if this is the locked face from last tick
                        if (cache.CurrentBestFaceCenter != null &&
                            fc.X == cache.CurrentBestFaceCenter.X &&
                            fc.Y == cache.CurrentBestFaceCenter.Y &&
                            fc.Z == cache.CurrentBestFaceCenter.Z)
                        {
                            currentLockedFaceClarity = clarity;
                            currentLockedFaceRawOcc = faceOcc;
                        }
                    }

                    if (bestFaceCenter != null && facesTested > 0)
                    {
                        // Face hysteresis: keep current face unless new one is significantly better.
                        // Prevents L/R oscillation when two faces have similar clarity.
                        Vec3d chosenFace = bestFaceCenter;
                        double chosenClarity = bestFaceClarity;
                        double chosenRawOcc = bestFaceRawOcc;

                        if (cache.CurrentBestFaceCenter != null && currentLockedFaceClarity >= 0)
                        {
                            double clarityDelta = bestFaceClarity - currentLockedFaceClarity;

                            if (clarityDelta < FACE_SWITCH_THRESHOLD)
                            {
                                // Clarity is similar — check tiebreakers.

                                // 1. RAW OCCLUSION tiebreaker: when all faces are occluded
                                // (clarity=0), prefer lower raw occ. Side faces send diagonal
                                // rays through walls (occ=2+), player-facing face goes
                                // perpendicular (occ=1). Use >0.3 threshold to avoid jitter.
                                double occDelta = currentLockedFaceRawOcc - bestFaceRawOcc;
                                if (occDelta > 0.3)
                                {
                                    // New face has significantly lower occ — switch to it
                                    // chosenFace already = bestFaceCenter
                                }
                                // 2. DISTANCE tiebreaker: when clarities AND occ are similar,
                                // switch to closer face if meaningfully nearer.
                                // Low threshold (0.3) because EMA smoothing (alpha=0.15)
                                // already prevents position jumps. Heavy hysteresis here
                                // blocks face tracking along multi-bbox volumes, causing
                                // the acoustic pos to lock to a wrong bbox's face.
                                else
                                {
                                    double bestDist = bestFaceCenter.DistanceTo(playerPos);
                                    double lockedDist = cache.CurrentBestFaceCenter.DistanceTo(playerPos);

                                    if (bestDist < lockedDist - 0.3)
                                    {
                                        // New face is >1.5 blocks closer — override hysteresis
                                        // chosenFace already = bestFaceCenter
                                    }
                                    else
                                    {
                                        // Keep locked face
                                        chosenFace = cache.CurrentBestFaceCenter;
                                        chosenClarity = currentLockedFaceClarity;
                                        chosenRawOcc = currentLockedFaceRawOcc;
                                    }
                                }
                            }
                        }
                        cache.CurrentBestFaceCenter = chosenFace;

                        // EMA temporal smoothing on position.
                        // Alpha 0.15 = ~300ms convergence at 50ms ticks.
                        const float ACOUSTIC_POS_EMA = 0.15f;
                        if (cache.HasSmoothedAcousticPos)
                        {
                            var prev = cache.SmoothedAcousticPos;
                            acousticPos = new Vec3d(
                                prev.X + (chosenFace.X - prev.X) * ACOUSTIC_POS_EMA,
                                prev.Y + (chosenFace.Y - prev.Y) * ACOUSTIC_POS_EMA,
                                prev.Z + (chosenFace.Z - prev.Z) * ACOUSTIC_POS_EMA);
                        }
                        else
                        {
                            acousticPos = chosenFace;
                        }

                        cache.SmoothedAcousticPos = acousticPos;
                        cache.HasSmoothedAcousticPos = true;

                        // Use the raw (unclamped) avg occlusion of the best face.
                        // WHY: clarity = max(0, 1-sampleOcc) clamps to 0 for sampleOcc > 1.
                        // Using (1 - chosenClarity) caps at 1.0, but OcclusionToFilter is
                        // exponential and expects accumulated values (e.g. 6.0 for 6 stone
                        // blocks). Capping at 1.0 massively under-muffles vs regular sounds
                        // that pass the full accumulated DDA value through the same formula.
                        ambientDerivedOcclusion = (float)chosenRawOcc;
                    }
                    else
                    {
                        // All samples fully occluded — use VS position as fallback
                        acousticPos = soundPos;
                        ambientDerivedOcclusion = 1f;
                    }

                    if (updatedThisTick == 0)
                    {
                        if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                            SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                                $"[AMBIENT-BLEND] {soundName} tested {facesTested} faces (multi-ray voted), bestFaceClarity={bestFaceClarity:F2} bestRawOcc={bestFaceRawOcc:F2} " +
                                $"derivedOcc={ambientDerivedOcclusion:F2} pos=({acousticPos.X:F2},{acousticPos.Y:F2},{acousticPos.Z:F2})");
                    }
                }
                else if (volBboxes != null && volBboxCount > 0)
                {
                    // No face samples (e.g., rainwindow) but we have volume bboxes.
                    // VS positioned the sound at bbox boundary — use standard DDA but exclude
                    // the volume's own blocks so they don't self-occlude.
                    acousticPos = soundPos; // Keep VS positioning
                    ambientDerivedOcclusion = OcclusionCalculator.CalculatePathOcclusionExcludingBboxes(
                        acousticPos, playerPos, blockAccessor,
                        volBboxes, volBboxCount);

                    if (updatedThisTick == 0 && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                        SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                            $"[AMBIENT-FALLBACK] {soundName} no samples, using bbox-excluded DDA, occ={ambientDerivedOcclusion:F2}");
                }

                // Point-source ambients (resonators, etc.) have SoundType.Ambient but no
                // bbox volumes and no face samples. They should NOT skip repositioning —
                // treat them as regular sounds so probe rays can reposition around walls.
                if (ambientDerivedOcclusion < 0f)
                {
                    isAmbientVolume = false;

                    if (updatedThisTick == 0 && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                        SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                            $"[AMBIENT-DOWNGRADE] {soundName} has SoundType.Ambient but no bbox/samples — treating as point source");
                }
            }

            // PROXIMITY CENTER BLEND (Steam Audio approach):
            // As the player approaches an ambient volume, blend the acoustic position
            // toward the player's own position. This progressively reduces stereo panning,
            // preventing L/R flip-flop at bbox boundaries and adjacent bbox transitions.
            // When fully inside: sound is centered (zero panning = immersive envelope).
            // When >BLEND_START blocks away: full directional positioning from face-sampling.
            if (isAmbientVolume && ambientDerivedOcclusion >= 0f)
            {
                const float BLEND_START = 2.5f; // Start blending when within 2.5 blocks of surface
                float distToSound = (float)playerPos.DistanceTo(acousticPos);

                if (distToSound < BLEND_START)
                {
                    // t=0 at surface (fully centered) → t=1 at BLEND_START (fully directional)
                    float t = distToSound / BLEND_START;
                    // Ease-in: panning ramps up slowly near the volume, preserving centering longer
                    t = t * t;
                    acousticPos = new Vec3d(
                        playerPos.X + (acousticPos.X - playerPos.X) * t,
                        playerPos.Y + (acousticPos.Y - playerPos.Y) * t,
                        playerPos.Z + (acousticPos.Z - playerPos.Z) * t);
                }
            }

            // For ambient volumes with sample-derived occlusion, skip the redundant DDA.
            // The sample data already accounts for all paths from surface to player.
            float occlusion = ambientDerivedOcclusion >= 0f
                ? ambientDerivedOcclusion
                : OcclusionCalculator.Calculate(acousticPos, playerPos, blockAccessor);

            // Per-sound penetration override: gameplay-critical sounds (bells, temporal rifts)
            // get reduced occlusion so they remain audible through thick walls.
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

            float directFilter = occlusion <= 0 ? 1.0f : OcclusionCalculator.OcclusionToFilter(occlusion);

            // Interval compensation factor for EMA smoothing below.
            // Alpha values are tuned for 50ms (CLOSE_INTERVAL) ticks. Far sounds update
            // at 200-500ms intervals, so each update must converge proportionally more.
            // Math: α_compensated = 1 - (1-α)^(interval/baseInterval)
            // This ensures convergence TIME is consistent regardless of update rate.
            float intervalRatio = (cache.NearAcousticBoundary || distance <= CLOSE_DISTANCE) ? 1f
                                : distance <= NEAR_DISTANCE ? NEAR_INTERVAL_MS / (float)CLOSE_INTERVAL_MS
                                : FAR_INTERVAL_MS / (float)CLOSE_INTERVAL_MS;

            // Read per-sound range from vanilla SoundParams for reverb distance attenuation.
            // Default 32 blocks = vanilla default. Used instead of the old global MaxSoundDistance config.
            float soundRange = sound.Params?.Range ?? 32f;

            int debugSourceId = AudioRenderer.GetSourceId(sound);

            bool isForceRefresh = cache.LastRaycastTimeMs > 0 && (currentTimeMs - cache.LastRaycastTimeMs) >= FORCE_REFRESH_MS;
            if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"[RAY] {soundName} d={distance:F1} occ={occlusion:F2} snd=({soundPos.X:F2},{soundPos.Y:F2},{soundPos.Z:F2}) plr=({playerPos.X:F2},{playerPos.Y:F2},{playerPos.Z:F2}) startBlk=({(int)Math.Floor(soundPos.X)},{(int)Math.Floor(soundPos.Y)},{(int)Math.Floor(soundPos.Z)})", 
                    force: isForceRefresh);

            // Default to direct occlusion filter; path resolution may override below
            float finalFilter = directFilter;

            // --- PHASE 4B: SPR-style Sound Path Resolution ---
            // SPR's redirectNonOccludedSounds (default: true) = skip repositioning for clear LOS.
            // We match this: when directOcclusion < 1.0 (essentially clear LOS through air/plants),
            // skip repositioning entirely. Sound plays at original position with direct filter.
            // Reverb rays still run regardless — reverb is always useful.
            // When occluded (>= 1.0 block), full opening probe system with dedup + diffraction kicks in.
            if (config != null && config.EnableSoundRepositioning)
            {
                ReverbResult reverbResult;
                SoundPathResult? pathResult;
                bool didFullRaytrace = false;

                // === CELL CACHE CHECK ===
                // Composite key = (soundCell, playerCell) — cache auto-invalidates
                // when player moves to a new cell. No skip threshold needed.
                // Close sounds use 2-block player cells (responsive), far sounds use
                // 8-block player cells (stable). Cache is purely for deduplication:
                // 50 entities in same pen share one reverb computation.
                if (reverbCellCache != null && config.EnableReverbCellCache)
                {
                    var cellEntry = reverbCellCache.TryGetCell(soundPos, playerPos, currentTimeMs, blockAccessor, out bool canStore);
                    if (cellEntry != null)
                    {
                        // CACHE HIT: Use cached reverb, resolve per-sound path from cached data
                        reverbResult = cellEntry.Reverb;
                        pathResult = AcousticRaytracer.ResolvePathFromCache(cellEntry, soundPos, playerPos, occlusion, config);
                        cellCacheHitsThisTick++;

                        if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                            SoundPhysicsAdaptedModSystem.DebugLog(
                                $"[CELL-CACHE] HIT uses={cellEntry.UseCount} age={currentTimeMs - cellEntry.CreatedTimeMs}ms");
                    }
                    else if (canStore)
                    {
                        // CACHE MISS (no entry): Full compute + store result
                        var (rv, pr) = AcousticRaytracer.CalculateWithPathsCacheable(
                            soundPos, playerPos, blockAccessor, occlusion, soundRange,
                            out var bouncePoints, out int bounceCount,
                            out var openings, out int openingCount,
                            out float sharedAirspaceRatio, out float directOccOut, out bool hasDirectAirspaceOut);
                        reverbResult = rv;
                        pathResult = pr;
                        didFullRaytrace = true;

                        reverbCellCache.StoreCellIfEmpty(soundPos, playerPos, currentTimeMs,
                            reverbResult, bouncePoints, bounceCount,
                            openings, openingCount, sharedAirspaceRatio,
                            directOccOut, hasDirectAirspaceOut);
                    }
                    else
                    {
                        // CACHE MISS (wall between): Full compute, do NOT store (preserves existing entry)
                        var (rv, pr) = AcousticRaytracer.CalculateWithPaths(soundPos, playerPos, blockAccessor, occlusion, soundRange);
                        reverbResult = rv;
                        pathResult = pr;
                        didFullRaytrace = true;
                    }
                }
                else
                {
                    // Cell cache disabled: original path
                    var (rv, pr) = AcousticRaytracer.CalculateWithPaths(soundPos, playerPos, blockAccessor, occlusion, soundRange);
                    reverbResult = rv;
                    pathResult = pr;
                    didFullRaytrace = true;
                }

                // Apply reverb from path calculation (always — reverb is independent of repositioning)
                // CRITICAL: Validate sourceId to detect VS recycling source IDs.
                // When sound A finishes and sound B takes its sourceId, stale entries
                // could apply sound A's reverb to sound B.

                // === VIZ CAPTURE: accumulate all raytraced sounds this tick ===
                var viz = DebugVisualization.Instance;
                if (viz != null && viz.AnyAcousticVizActive && didFullRaytrace)
                {
                    viz.CaptureFromRaytracer(
                        AcousticRaytracer.CacheableBouncePoints, AcousticRaytracer.CacheableBounceCount,
                        AcousticRaytracer.CacheableOpenings, AcousticRaytracer.CacheableOpeningCount,
                        pathResult, soundPos, playerPos, occlusion);
                }

                int? validatedSourceId = AudioRenderer.GetValidatedSourceId(sound);

                // DEBUG: Log which sound got which reverb result BEFORE applying
                if (SoundPhysicsAdaptedModSystem.IsReverbDebugEnabled)
                {
                    string srcDbg = validatedSourceId.HasValue ? validatedSourceId.Value.ToString() : "STALE";
                    SoundPhysicsAdaptedModSystem.ReverbDebugLog(
                        $"[REVERB-FOR] {soundName} src={srcDbg} -> g0={reverbResult.SendGain0:F2} g1={reverbResult.SendGain1:F2} g2={reverbResult.SendGain2:F2} g3={reverbResult.SendGain3:F2}");
                }

                // Wind sounds are exempt from reverb — wind is a broad atmospheric phenomenon
                // that doesn't reflect off walls like rain/footsteps/impacts do.
                // Reverb on wind positional sources sounds unnatural.
                bool isWindSound = soundName.Contains("wind-leaf");

                if (validatedSourceId.HasValue && validatedSourceId.Value > 0 && ReverbEffects.IsInitialized && !isWindSound)
                {
                    // ADAPTIVE EMA smooth reverb gains — same logic as occlusion smoothing.
                    // Large reverb changes (crossing acoustic boundary) converge fast.
                    // Small changes (probe jitter) smooth heavily.
                    float maxGainDelta = 0f;
                    if (cache.HasSmoothedReverb)
                    {
                        maxGainDelta = Math.Max(maxGainDelta, Math.Abs(reverbResult.SendGain0 - cache.SmoothedG0));
                        maxGainDelta = Math.Max(maxGainDelta, Math.Abs(reverbResult.SendGain1 - cache.SmoothedG1));
                        maxGainDelta = Math.Max(maxGainDelta, Math.Abs(reverbResult.SendGain2 - cache.SmoothedG2));
                        maxGainDelta = Math.Max(maxGainDelta, Math.Abs(reverbResult.SendGain3 - cache.SmoothedG3));
                    }
                    // Reverb is less perceptible than LPF, can converge a bit faster.
                    // Gains are 0-1 scale, so thresholds are smaller than occlusion.
                    float reverbAlpha = maxGainDelta > 0.3f ? 0.65f   // big reverb change: ~3 ticks
                                      : maxGainDelta > 0.1f ? 0.45f   // medium: ~5 ticks
                                      : 0.30f;                        // jitter: smooth

                    if (!cache.HasSmoothedReverb)
                    {
                        cache.SmoothedG0 = reverbResult.SendGain0;
                        cache.SmoothedG1 = reverbResult.SendGain1;
                        cache.SmoothedG2 = reverbResult.SendGain2;
                        cache.SmoothedG3 = reverbResult.SendGain3;
                        cache.HasSmoothedReverb = true;
                    }
                    else
                    {
                        cache.SmoothedG0 += (reverbResult.SendGain0 - cache.SmoothedG0) * reverbAlpha;
                        cache.SmoothedG1 += (reverbResult.SendGain1 - cache.SmoothedG1) * reverbAlpha;
                        cache.SmoothedG2 += (reverbResult.SendGain2 - cache.SmoothedG2) * reverbAlpha;
                        cache.SmoothedG3 += (reverbResult.SendGain3 - cache.SmoothedG3) * reverbAlpha;
                    }

                    var smoothedReverb = new ReverbResult(
                        cache.SmoothedG0, cache.SmoothedG1, cache.SmoothedG2, cache.SmoothedG3,
                        reverbResult.SendCutoff0, reverbResult.SendCutoff1, reverbResult.SendCutoff2, reverbResult.SendCutoff3);
                    ReverbEffects.ApplyToSource(validatedSourceId.Value, smoothedReverb);
                }

                // SPR-STYLE LOS OVERRIDE: Skip repositioning when direct path is essentially clear.
                // SPR: shouldEvaluateDirection() returns false when occlusion == 0 && redirectNonOccludedSounds.
                // Threshold must sit in the gap between foliage and actual walls:
                //   plant=0.02, leaves=0.05, leavesbranchy=0.12 → clear/foliage (skip reposition)
                //   gravel=0.4, wood=0.6, stone=1.0           → real obstruction (allow reposition)
                // 0.3 cleanly separates these: covers all foliage (≤ 0.12) with margin,
                // but lets gravel/wood/stone trigger repositioning toward openings.
                // This prevents bounce rays from outvoting the direct path and shifting sound sideways
                // (the stone-throw panning bug: 40 bounce rays outvoted 1 direct path → 16° shift).
                bool skipRepositioning = occlusion < 0.3f;

                // OPTION E: Ambient volume sounds (beehives, water, lava) skip probes entirely.
                // VS plays these as dynamic bounding-box volumes whose position tracks the player
                // (nearest point on bbox). Face-sampling (above) picks the best acoustic origin
                // from all player-facing bbox faces, so the direct ray already has the correct
                // occlusion. Probes still run inside the raytracer (reverb shares the same call)
                // but pathResult is discarded here.
                // isAmbientVolume is detected earlier for face-sampling.
                if (isAmbientVolume)
                    skipRepositioning = true;

                if (skipRepositioning)
                {
                    // Clear LOS or ambient volume: sound stays at original position.
                    // For ambient volumes: use face-sampled acousticPos (stable, EMA-smoothed)
                    // instead of vanilla soundPos which flip-flops between bbox faces at edges.
                    Vec3d resetPos = isAmbientVolume ? acousticPos : soundPos;
                    AudioRenderer.ResetSoundPosition(sound, resetPos);

                    // SMOOTH TRANSITION: When switching from occluded→clear, don't snap
                    // the filter. Instead, EMA-smooth toward the direct occlusion value.
                    // This prevents the audible brightness pop when crossing the occ<0.3
                    // threshold (filter could jump 2-3x in one tick otherwise).
                    // For ambient volumes, this gives smooth occlusion as player moves around walls.
                    if (cache.HasSmoothedOcc && cache.SmoothedBlendedOcc > occlusion + 0.3f)
                    {
                        // Still converging from previous occlusion — smooth toward clear
                        float clearDelta = cache.SmoothedBlendedOcc - occlusion;
                        float clearAlpha = clearDelta > 1.5f ? 0.55f : clearDelta > 0.5f ? 0.40f : 0.30f;
                        // Compensate for update interval (far sounds update less often)
                        if (intervalRatio > 1f)
                            clearAlpha = 1f - MathF.Pow(1f - clearAlpha, intervalRatio);
                        cache.SmoothedBlendedOcc += (occlusion - cache.SmoothedBlendedOcc) * clearAlpha;
                        // Use smoothed filter instead of raw direct filter for continuity
                        finalFilter = cache.SmoothedBlendedOcc <= 0 ? 1.0f
                            : OcclusionCalculator.OcclusionToFilter(cache.SmoothedBlendedOcc);
                    }
                    else
                    {
                        cache.SmoothedBlendedOcc = occlusion;
                    }
                    cache.HasSmoothedOcc = true;

                    // Clear LOS with occlusion near 0.3 threshold = near acoustic boundary.
                    // Values above 0.15 suggest growing foliage/obstruction — keep update interval high.
                    // Ambient volumes always get responsive updates (position changes every tick).
                    cache.NearAcousticBoundary = isAmbientVolume || occlusion > 0.15f;

                    if (updatedThisTick == 0)
                    {
                        if (isAmbientVolume)
                        {
                            bool usedFace = acousticPos != soundPos;
                            if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                                    $"[4B-AMBIENT] occ={occlusion:F2} filt={directFilter:F3} " +
                                    $"({(usedFace ? "face-sampled" : "vanilla pos")}, probe skip)");
                        }
                        else
                        {
                            if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                                    $"[4B-LOS] occ={occlusion:F2}<0.3 filt={directFilter:F3} (no repos)");
                        }
                    }
                }
                else if (pathResult.HasValue)
                {
                    // PERMEATED-ONLY CHECK: When ALL paths are through-wall (zero open paths),
                    // repositioning is meaningless — there's no opening to localize toward.
                    // The weighted direction from random wall-bleed rays is unstable, causing
                    // offset to jump 5m→19m→37m between ticks (beehive flutter bug).
                    // SPR doesn't reposition permeated-only sounds either.
                    // Keep LPF filtering (below) but skip the position shift.
                    bool allPermeated = pathResult.Value.PathCount == 0 && pathResult.Value.PermeatedPathCount > 0;
                    if (allPermeated)
                    {
                        AudioRenderer.ResetSoundPosition(sound, soundPos);
                    }

                    // Occluded: full path resolution with opening probes.
                    // Position shifts toward openings, LPF uses DIRECT occlusion (SPR-style).
                    // bOcc provides a DIFFRACTION FLOOR: when bounce rays find viable indirect
                    // paths (L-corridors, around corners), allows more HF than direct occlusion
                    // alone. Capped at ~9dB (MaxDiffractionFilter) with entombment guards.
                    bool applied = allPermeated || AudioRenderer.ApplySoundPath(sound, pathResult.Value, soundPos);

                    if (applied)
                    {
                        float airspaceRatio = pathResult.Value.SharedAirspaceRatio;

                        // SPR-STYLE LPF: Direct occlusion drives the base filter. Always.
                        // Shared airspace floor prevents over-muffling of repositioned sounds.
                        // Diffraction floor (from bOcc bounce data) adds relief for L-corridors
                        // and around-corner scenarios where indirect paths are viable.

                        // SPR: directCutoff = max(directCutoff, sqrt(sharedAirspace) * 0.2)
                        float sharedAirspaceFloor = MathF.Sqrt(airspaceRatio) * 0.2f;

                        // ACOUSTIC BOUNDARY DETECTION:
                        cache.LastSharedAirspaceRatio = airspaceRatio;
                        cache.NearAcousticBoundary = (airspaceRatio > 0.02f && airspaceRatio < 0.5f)
                            || (airspaceRatio < 0.02f && occlusion < 3.0f);

                        // EMA SMOOTHING on direct occlusion for smooth transitions.
                        // Without smoothing, direct occlusion can jump when multi-ray
                        // sampling hits slightly different blocks between ticks.
                        float targetOcc = occlusion;
                        float delta = cache.HasSmoothedOcc ? Math.Abs(targetOcc - cache.SmoothedBlendedOcc) : 0f;
                        float occSmoothFactor = delta > 3.0f ? 0.70f
                                              : delta > 1.5f ? 0.55f
                                              : delta > 0.5f ? 0.40f
                                              : 0.25f;

                        if (soundName != null && soundName.Contains("weather/"))
                        {
                            occSmoothFactor *= 0.5f;
                        }

                        if (intervalRatio > 1f)
                            occSmoothFactor = 1f - MathF.Pow(1f - occSmoothFactor, intervalRatio);

                        if (cache.HasSmoothedOcc)
                        {
                            cache.SmoothedBlendedOcc += (targetOcc - cache.SmoothedBlendedOcc) * occSmoothFactor;
                        }
                        else
                        {
                            cache.SmoothedBlendedOcc = targetOcc;
                            cache.HasSmoothedOcc = true;
                        }
                        float smoothedOcc = cache.SmoothedBlendedOcc;

                        // Direct filter from smoothed occlusion
                        float smoothedDirectFilter = smoothedOcc <= 0 ? 1.0f
                            : OcclusionCalculator.OcclusionToFilter(smoothedOcc);

                        // SPR-style: airspace floor prevents over-muffling
                        finalFilter = Math.Max(smoothedDirectFilter, sharedAirspaceFloor);

                        // DIFFRACTION FLOOR (bOcc reintegration):
                        // When bounce rays find viable indirect paths (L-corridors, around corners),
                        // allow more HF through than direct occlusion + airspace floor alone.
                        // Based on UTD/Maekawa simplified diffraction: ~8-10dB loss per 90° bend.
                        // Guards prevent entombed sounds from benefiting (requires meaningful
                        // shared airspace, open path count, and moderate direct occlusion).
                        float diffractionFloor = 0f;
                        float reposOffset = (float)pathResult.Value.RepositionOffset;
                        int openPaths = pathResult.Value.PathCount;

                        bool hasDiffractionEvidence =
                            openPaths >= 2 &&              // at least 2 open bounce paths
                            airspaceRatio >= 0.05f &&      // >5% shared airspace (not entombed)
                            smoothedOcc > 0.5f;            // direct path must be meaningfully occluded

                        if (hasDiffractionEvidence)
                        {
                            // Diffraction floor: use BETTER of measured indirect path vs.
                            // guaranteed minimum from physics (single 90° bend, ~8dB).
                            // Max applied on filter (pick less muffled), NOT on occlusion.
                            float minDiffOcc = config.MinDiffractionOcclusion;
                            float rawBOccFilter = OcclusionCalculator.OcclusionToFilter((float)pathResult.Value.BlendedOcclusion);
                            float minDiffFilter = OcclusionCalculator.OcclusionToFilter(minDiffOcc);
                            float bOccFilter = Math.Max(rawBOccFilter, minDiffFilter);

                            // Confidence from multiple evidence sources:
                            // - airspace: 25%+ → full confidence (strong shared volume)
                            // - paths: 4+ open → full confidence (consistent indirect routing)
                            // - repositioning: 3m+ offset → bonus (sound visibly bends around corner)
                            float airspaceConf = Math.Min(airspaceRatio * 4f, 1f);
                            float pathConf = Math.Min(openPaths / 4f, 1f);
                            float reposConf = Math.Clamp(reposOffset / 3f, 0f, 1f);
                            float confidence = Math.Min(1f, airspaceConf * pathConf + reposConf * 0.3f);

                            diffractionFloor = Math.Min(bOccFilter * confidence, config.MaxDiffractionFilter);

                            // Take max: diffraction floor overrides if higher than current filter
                            finalFilter = Math.Max(finalFilter, diffractionFloor);
                        }

                        // DIFFRACTION ANGLE DARKENING:
                        // Sounds bending around corners lose HF proportional to bend angle.
                        float bendRatio = distance > 0.1f ? Math.Clamp(reposOffset / distance, 0f, 1f) : 0f;
                        float diffractionDarkening = 1f - bendRatio * 0.3f;
                        finalFilter *= diffractionDarkening;

                        if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                            SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                                $"[4B-LPF] dOcc={occlusion:F2} smooth={smoothedOcc:F2} filt={finalFilter:F3} bend={bendRatio:F2} diffFilt={diffractionDarkening:F2} airFloor={sharedAirspaceFloor:F2} diffFloor={diffractionFloor:F3} air={airspaceRatio:F2} open={openPaths} alpha={occSmoothFactor:F2}{(cache.NearAcousticBoundary ? " BOUNDARY" : "")}");
                    }

                    if (updatedThisTick == 0 && pathResult.Value.RepositionOffset > 0.1)
                    {
                        if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                            SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                                $"[4B-Path] off={pathResult.Value.RepositionOffset:F1}m bOcc={pathResult.Value.BlendedOcclusion:F2} paths={pathResult.Value.PathCount}/{pathResult.Value.TotalPathCount} perm={pathResult.Value.PermeatedPathCount}");
                    }
                }
                else
                {
                    // No paths found (rays cancelled out or no paths at all).
                    // Let position smoothly return to original via SmoothAll().
                    AudioRenderer.ResetSoundPosition(sound, soundPos);
                }
            }

            // Cache filter for potential future fade-out (throttle eviction).
            cache.CachedFilterForFade = finalFilter;

            // Per-sound penetration floor: guarantee minimum audibility for gameplay-critical sounds.
            if (penetrationFloor > 0f)
                finalFilter = Math.Max(finalFilter, penetrationFloor);

            // Apply throttle fade-in lerp: if ThrottleFade < 1 (just got a slot back),
            // blend from minFilter toward the computed filter.
            // At ThrottleFade=1 (fully active) this is a no-op (effectiveFilter == finalFilter).
            float effectiveFilter = cache.ThrottleFade >= 1f
                ? finalFilter
                : minFilter + (finalFilter - minFilter) * cache.ThrottleFade;

            // Single SetOcclusion call with the final chosen filter value.
            // This avoids the target flip-flop that happened when SetOcclusion was
            // called first with direct filter, then overridden by path filter.
            AudioRenderer.SetOcclusion(sound, effectiveFilter, soundPos, soundName);

            cache.LastSoundPos = soundPos.Clone();
            cache.LastPlayerPos = playerPos.Clone();
            cache.CachedOcclusion = occlusion;
            cache.LastUpdateTimeMs = currentTimeMs;
            cache.LastRaycastTimeMs = currentTimeMs;
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
            isOutdoors = (skyHits == SKY_PROBE_RAY_COUNT);
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
            if (soundCache.TryGetValue(sound, out var cache))
            {
                // If path resolution has produced a smoothed blended value, use it.
                // This is the actual occlusion being applied to the sound's LPF.
                if (cache.HasSmoothedOcc)
                    return cache.SmoothedBlendedOcc;
                // Otherwise fall back to direct DDA occlusion
                return cache.CachedOcclusion;
            }
            return -1f;
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
                return cache.HasSmoothedOcc;
            }
            return false;
        }

        /// <summary>
        /// Provides access to the spatial reverb cell cache for block-change invalidation.
        /// </summary>
        public ReverbCellCache CellCache => reverbCellCache;

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
        }
    }
}
