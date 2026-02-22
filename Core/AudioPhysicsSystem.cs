using System;
using System.Collections.Generic;
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

            // Throttle fade state
            // ThrottleFade: 1.0 = fully active, 0.0 = fully throttled/silent.
            // Steps toward 1 when unthrottled, toward 0 when throttled, using elapsed time.
            // CachedFilterForFade: last raycast-computed filter — used during fade-out so we
            // can lerp toward silence without needing to re-raytrace the throttled sound.
            public float ThrottleFade = 1.0f;
            public float CachedFilterForFade = 1.0f;
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
            soundphysicsadapted.Core.ExecutionTracer.Enter("AudioPhysicsSystem", "Update");
            try
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
            finally
            {
                soundphysicsadapted.Core.ExecutionTracer.Exit("AudioPhysicsSystem", "Update");
            }
        }

        /// <summary>
        /// Invalidate all cached results. Called on block change events.
        /// Doesn't force immediate raycast - just ensures the next interval
        /// check actually runs the raycast instead of returning stale data.
        /// </summary>
        public void InvalidateCache()
        {
            foreach (var kvp in soundCache)
            {
                // Reset BOTH timers so the very next tick:
                // 1. Passes the interval gate (LastUpdateTimeMs = 0)
                // 2. Bypasses the static cache (LastRaycastTimeMs = 0)
                kvp.Value.LastRaycastTimeMs = 0;
                kvp.Value.LastUpdateTimeMs = 0;
            }

            reverbCellCache?.Clear();

            SoundPhysicsAdaptedModSystem.DebugLog(
                $"ACOUSTICS: Cache invalidated ({soundCache.Count} entries, cell cache cleared)");
        }

        private void UpdateAllSounds(Vec3d playerPos, IBlockAccessor blockAccessor, long currentTimeMs)
        {
            soundphysicsadapted.Core.ExecutionTracer.Enter("AudioPhysicsSystem", "UpdateAllSounds");
            try
            {
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
                long interval = distance <= CLOSE_DISTANCE ? CLOSE_INTERVAL_MS
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

                if (cache.LastPlayerPos != null && cache.LastSoundPos != null && !isOverdue)
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
            for (int i = 0; i < _candidates.Count; i++)
            {
                var candidate = _candidates[i];

                // Budget check: overdue sounds get priority but are still capped
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

            updatedThisTick = processed;
            totalActive = count;
            CleanupCache();

            if (updatedThisTick > 0 || cachedThisTick > 0 || deferredThisTick > 0 || playerPosThisTick > 0)
            {
                string cellCacheInfo = reverbCellCache != null ? $" cellHits={cellCacheHitsThisTick} cells={reverbCellCache.CellCount}" : "";
                string throttleInfo = throttle != null ? $" throttle={throttle.ThrottledCount}" : "";
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"ACOUSTICS: updated={updatedThisTick} cached={cachedThisTick} " +
                    $"skipped={skippedThisTick} deferred={deferredThisTick} playerPos={playerPosThisTick} " +
                    $"total={totalActive} outdoor={isOutdoors}{cellCacheInfo}{throttleInfo}");
            }
            }
            finally
            {
                soundphysicsadapted.Core.ExecutionTracer.Exit("AudioPhysicsSystem", "UpdateAllSounds");
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
            soundphysicsadapted.Core.ExecutionTracer.Enter("AudioPhysicsSystem", "ProcessSoundRaycast", sound.Params?.Location?.ToShortString());
            try
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

            // Compute how much to step the fade based on real elapsed time.
            // Clamped to [0,1] so new sounds (LastUpdateTimeMs=0) don't get huge steps.
            long elapsedMs = cache.LastUpdateTimeMs > 0 ? currentTimeMs - cache.LastUpdateTimeMs : 50L;
            float fadeStep = fadeDurationMs > 0f ? Math.Min(1f, (float)elapsedMs / fadeDurationMs) : 1f;

            if (isThrottled)
            {
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
                cache.ThrottleFade = Math.Min(1f, cache.ThrottleFade + fadeStep);
            }

            float occlusion = OcclusionCalculator.Calculate(soundPos, playerPos, blockAccessor);
            float directFilter = occlusion <= 0 ? 1.0f : OcclusionCalculator.OcclusionToFilter(occlusion);

            int debugSourceId = AudioRenderer.GetSourceId(sound);

            SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                $"[RAY] {soundName} d={distance:F1} occ={occlusion:F2} pos=({soundPos.X:F0},{soundPos.Y:F0},{soundPos.Z:F0})");

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

                // === CELL CACHE CHECK ===
                // Skip cache for close sounds — reverb changes rapidly with small
                // player movements at short range, causing audible jumps when cache
                // expires and recomputes. Close sounds are cheap (typically 1-2) and
                // benefit most from per-tick fresh computation.
                double distToSound = soundPos.DistanceTo(playerPos);
                bool closeEnoughToSkipCache = distToSound < 10.0;

                if (reverbCellCache != null && config.EnableReverbCellCache && !closeEnoughToSkipCache)
                {
                    var cellEntry = reverbCellCache.TryGetCell(soundPos, playerPos, currentTimeMs, blockAccessor, out bool canStore);
                    if (cellEntry != null)
                    {
                        // CACHE HIT: Use cached reverb, resolve per-sound path from cached data
                        reverbResult = cellEntry.Reverb;
                        pathResult = AcousticRaytracer.ResolvePathFromCache(cellEntry, soundPos, playerPos, occlusion, config);
                        cellCacheHitsThisTick++;

                        SoundPhysicsAdaptedModSystem.DebugLog(
                            $"[CELL-CACHE] HIT uses={cellEntry.UseCount} age={currentTimeMs - cellEntry.CreatedTimeMs}ms");
                    }
                    else if (canStore)
                    {
                        // CACHE MISS (no entry): Full compute + store result
                        var (rv, pr) = AcousticRaytracer.CalculateWithPathsCacheable(
                            soundPos, playerPos, blockAccessor, occlusion,
                            out var bouncePoints, out int bounceCount,
                            out var openings, out int openingCount,
                            out float sharedAirspaceRatio, out float directOccOut, out bool hasDirectAirspaceOut);
                        reverbResult = rv;
                        pathResult = pr;

                        reverbCellCache.StoreCellIfEmpty(soundPos, playerPos, currentTimeMs,
                            reverbResult, bouncePoints, bounceCount,
                            openings, openingCount, sharedAirspaceRatio,
                            directOccOut, hasDirectAirspaceOut);
                    }
                    else
                    {
                        // CACHE MISS (wall between): Full compute, do NOT store (preserves existing entry)
                        var (rv, pr) = AcousticRaytracer.CalculateWithPaths(soundPos, playerPos, blockAccessor, occlusion);
                        reverbResult = rv;
                        pathResult = pr;
                    }
                }
                else
                {
                    // Cell cache disabled: original path
                    var (rv, pr) = AcousticRaytracer.CalculateWithPaths(soundPos, playerPos, blockAccessor, occlusion);
                    reverbResult = rv;
                    pathResult = pr;
                }

                // Apply reverb from path calculation (always — reverb is independent of repositioning)
                // CRITICAL: Validate sourceId to detect VS recycling source IDs.
                // When sound A finishes and sound B takes its sourceId, stale entries
                // could apply sound A's reverb to sound B.
                int? validatedSourceId = AudioRenderer.GetValidatedSourceId(sound);

                // DEBUG: Log which sound got which reverb result BEFORE applying
                if (config.DebugMode && config.DebugReverb)
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
                    // EMA smooth reverb gains to prevent abrupt jumps when crossing acoustic boundaries.
                    // Alpha 0.35 = ~3 ticks to converge to a step change (smooth enough to avoid pops,
                    // fast enough to still feel responsive during movement).
                    const float reverbAlpha = 0.35f;
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

                // SPR-STYLE LOS OVERRIDE: Skip repositioning when direct path is clear.
                // SPR: shouldEvaluateDirection() returns false when occlusion == 0 && redirectNonOccludedSounds.
                // We use < 1.0 threshold because VS has partial-block occlusion from plants/leaves
                // that are essentially still clear LOS. Above 1.0 = real wall between player and sound.
                // This prevents bounce rays from outvoting the direct path and shifting sound sideways
                // (the stone-throw panning bug: 40 bounce rays outvoted 1 direct path → 16° shift).
                bool skipRepositioning = occlusion < 1.0f;

                if (skipRepositioning)
                {
                    // Clear LOS: sound stays at original position, use direct filter.
                    // Reset any existing repositioning smoothly back to original.
                    AudioRenderer.ResetSoundPosition(sound, soundPos);
                    // ISSUE 4 FIX: Seed smoothed occ from direct path instead of resetting.
                    // This prevents abrupt jumps when transitioning back to occlusion.
                    cache.SmoothedBlendedOcc = occlusion;
                    cache.HasSmoothedOcc = true;

                    if (updatedThisTick == 0)
                    {
                        SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                            $"[4B-LOS] occ={occlusion:F2}<1.0 filt={directFilter:F3} (no repos)");
                    }
                }
                else if (pathResult.HasValue)
                {
                    // Occluded: full path resolution with opening probes.
                    // Position shifts toward openings, LPF uses blended occlusion.
                    bool applied = AudioRenderer.ApplySoundPath(sound, pathResult.Value, soundPos);

                    if (applied)
                    {
                        float blendedOcc = (float)pathResult.Value.BlendedOcclusion;
                        
                        // PATH CLARITY: Use open path ratio, not sharedAirspaceRatio.
                        // sharedAirspaceRatio is from sound-source fibonacci rays (wrong for this).
                        // Open path ratio comes from actual probe rays that found clear paths.
                        int openPaths = pathResult.Value.PathCount;
                        int totalPaths = pathResult.Value.TotalPathCount;
                        float pathClarity = totalPaths > 0 ? (float)openPaths / totalPaths : 0f;

                        // SPR-STYLE FLOOR: Two competing floors, take the MORE FAVORABLE (lower):
                        // 1. ISSUE 1 FIX: Shared airspace floor (SPR-style) - based on ray success, NOT wall thickness
                        //    SPR: floor = sqrt(sharedAirspaceRatio) * 0.2
                        //    Independent of wall thickness - only depends on how many rays found clear paths.
                        // 2. Clarity floor: Based on path clarity — high clarity = many clear paths
                        //    Higher clarity → lower floor → allows more recovery.

                        // Shared airspace floor (SPR formula)
                        float sharedAirspaceFilterFloor = MathF.Sqrt(pathResult.Value.SharedAirspaceRatio) * 0.2f;
                        sharedAirspaceFilterFloor = Math.Max(sharedAirspaceFilterFloor, 0.01f); // Avoid log(0)
                        float blockAbsorption = SoundPhysicsAdaptedModSystem.Config?.BlockAbsorption ?? 1.0f;
                        float sharedAirspaceFloor = -MathF.Log(sharedAirspaceFilterFloor) / blockAbsorption;

                        // Convert clarity floor to occlusion scale
                        // clarity floor filter = sqrt(pathClarity) * 0.35 (slightly higher than SPR's 0.2)
                        // Our filter formula: filter = exp(-occ * blockAbsorption)
                        // So: occ = -ln(filter) / blockAbsorption
                        float clarityFilterFloor = MathF.Sqrt(pathClarity) * 0.35f;
                        clarityFilterFloor = Math.Max(clarityFilterFloor, 0.01f); // Avoid log(0)
                        float clarityOccFloor = -MathF.Log(clarityFilterFloor) / blockAbsorption;

                        // Take the MORE FAVORABLE (lower) floor
                        float occlusionFloor = Math.Min(sharedAirspaceFloor, clarityOccFloor);
                        
                        if (blendedOcc < occlusionFloor)
                        {
                            blendedOcc = occlusionFloor;
                        }

                        // Temporal smoothing to dampen probe ray jitter
                        const float OCC_SMOOTH_FACTOR = 0.35f;
                        if (cache.HasSmoothedOcc)
                        {
                            cache.SmoothedBlendedOcc += (blendedOcc - cache.SmoothedBlendedOcc) * OCC_SMOOTH_FACTOR;
                        }
                        else
                        {
                            cache.SmoothedBlendedOcc = blendedOcc;
                            cache.HasSmoothedOcc = true;
                        }
                        float smoothedOcc = cache.SmoothedBlendedOcc;

                        float pathFilter = smoothedOcc <= 0 ? 1.0f
                            : OcclusionCalculator.OcclusionToFilter(smoothedOcc);

                        finalFilter = pathFilter;

                        SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                            $"[4B-LPF] dOcc={occlusion:F2} bOcc={blendedOcc:F2} smooth={smoothedOcc:F2} filt={pathFilter:F3} clarity={pathClarity:P0} airFloor={sharedAirspaceFloor:F2}");
                    }

                    if (updatedThisTick == 0 && pathResult.Value.RepositionOffset > 0.1)
                    {
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
            finally
            {
                soundphysicsadapted.Core.ExecutionTracer.Exit("AudioPhysicsSystem", "ProcessSoundRaycast");
            }
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
                soundCache.Remove(key);
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
