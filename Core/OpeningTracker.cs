using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// A tracked opening with persistence beyond the verification window.
    /// Once discovered, an opening stays tracked even when the player moves
    /// behind a corner and it leaves VerifiedOpenings. The positional audio
    /// source remains active — AudioPhysicsSystem repositions it around corners.
    /// </summary>
    public class TrackedOpening
    {
        /// <summary>
        /// World-space position of the opening (cluster centroid).
        /// Set on creation, may be updated if the cluster moves slightly between scans.
        /// </summary>
        public Vec3d WorldPos;

        /// <summary>
        /// Number of opening columns in the cluster (for volume weighting).
        /// Updated when the opening re-appears in VerifiedOpenings with a new cluster.
        /// </summary>
        public int ClusterWeight;

        /// <summary>
        /// Smoothed cluster weight for stable volume. Fast attack to snap up
        /// when more members found, slow linear decay (1.5/sec) to prevent
        /// abrupt volume drops when DDA finds fewer columns.
        /// Reset instantly on structural changes (block placement).
        /// Drives volume calculation directly.
        /// </summary>
        public float SmoothedClusterWeight;

        /// <summary>
        /// Average path occlusion at last verification (for initial LPF hint).
        /// Note: actual per-frame LPF is handled by AudioPhysicsSystem.
        /// </summary>
        public float LastKnownOcclusion;

        /// <summary>
        /// Game time (ms) when this opening was first discovered.
        /// </summary>
        public long CreatedTimeMs;

        /// <summary>
        /// Game time (ms) when this opening was last seen in VerifiedOpenings.
        /// Used for persistence timeout — opening removed when too long without verification.
        /// </summary>
        public long LastVerifiedTimeMs;

        /// <summary>
        /// Whether this opening is currently in VerifiedOpenings (direct LOS from player).
        /// When false, the opening is persisted but may be behind a corner.
        /// </summary>
        public bool CurrentlyVerified;

        /// <summary>
        /// Unique ID for matching with positional sound source slots.
        /// </summary>
        public int TrackingId;

        /// <summary>
        /// World-space positions of each opening column (from cluster members).
        /// Used for structural integrity checks: DDA from each stored position to
        /// LastVerifiedPlayerPos detects blocks placed inside the opening,
        /// without false-triggering when the player walks around a corner.
        /// </summary>
        public List<Vec3d> MemberPositions;

        /// <summary>
        /// Player ear position at last verification time.
        /// DDA from each MemberPosition to THIS position detects structural changes
        /// at the opening itself. Using the stored (not current) player position means
        /// walking around corners doesn't trigger removal — only placing/removing
        /// blocks in the opening changes these paths.
        /// </summary>
        public Vec3d LastVerifiedPlayerPos;

        /// <summary>
        /// Wall-face entry positions for each member (parallel to MemberPositions).
        /// These are where rain sound enters the cave/room — the actual wall face blocks.
        /// Used for block-event invalidation: when a block is placed within 1 block of
        /// any entry point, ForceReverify is set.
        /// </summary>
        public List<Vec3d> MemberEntryPositions;

        /// <summary>
        /// When true, a block was placed/removed near this opening's entry points.
        /// On the next update cycle, the opening is forced to re-verify: CurrentlyVerified
        /// and LastVerifiedTimeMs are reset so the enclosure calculator must re-find it.
        /// If the opening is genuinely sealed, it won't be re-found → persistence timeout → removal.
        /// </summary>
        public bool ForceReverify;
    }

    /// <summary>
    /// Manages tracked openings with persistence and hysteresis.
    /// 
    /// Discovery: Every scan cycle, clusters from VerifiedOpenings are matched
    /// against existing tracked openings. New clusters create new tracked entries.
    /// Existing entries are updated when their cluster re-appears.
    ///
    /// Persistence: Tracked openings survive when they leave VerifiedOpenings
    /// (player moved behind corner). AudioPhysicsSystem handles repositioning
    /// the sound around corners via bounce rays.
    ///
    /// Removal conditions:
    /// 1. Persistence timeout: opening hasn't been re-verified for N seconds
    /// 2. Distance: player moved too far from the opening
    /// 3. Weather stopped: no rain/hail
    /// 4. Structural change: heightmap now covers the opening column
    ///
    /// This solves the L-shaped room problem: the door opening stays tracked
    /// even when occluded from the new player position.
    /// </summary>
    public class OpeningTracker
    {
        private readonly ICoreClientAPI capi;
        private readonly List<TrackedOpening> trackedOpenings = new List<TrackedOpening>(8);
        private int nextTrackingId = 1;

        /// <summary>
        /// Callback to check if a tracked opening's positional source is still audible.
        /// Takes TrackingId, returns true if the sound is being heard by the player
        /// (even if occluded/repositioned by AudioPhysicsSystem).
        /// When null, falls back to flat persistence timeout.
        /// </summary>
        public System.Func<int, bool> IsSourceAudible { get; set; }



        /// <summary>
        /// Current tracked openings (read by RainAudioHandler for source placement).
        /// Includes both currently-verified and persisted openings.
        /// </summary>
        public IReadOnlyList<TrackedOpening> TrackedOpenings => trackedOpenings;

        /// <summary>
        /// Number of currently tracked openings (for debug display).
        /// </summary>
        public int Count => trackedOpenings.Count;

        /// <summary>
        /// Number of openings currently verified (direct LOS to player).
        /// </summary>
        public int VerifiedCount
        {
            get
            {
                int c = 0;
                for (int i = 0; i < trackedOpenings.Count; i++)
                    if (trackedOpenings[i].CurrentlyVerified) c++;
                return c;
            }
        }

        // Match radius: how close a new cluster must be to an existing tracked opening
        // to be considered the "same" opening (avoids duplicates when clusters shift slightly)
        private const float MATCH_RADIUS = 4.0f;
        private const float MATCH_RADIUS_SQ = MATCH_RADIUS * MATCH_RADIUS;

        // Position lerp: smooth centroid updates to prevent per-tick panning jitter.
        // 0.3 per 100ms tick → ~90% convergence in 700ms (7 ticks).
        // Combined with Schmitt trigger hysteresis on the input side,
        // this handles residual centroid oscillation from member count changes.
        private const float POSITION_LERP_FACTOR = 0.3f;

        // Minimum centroid shift to apply lerp (below this, snap to avoid float drift)
        private const float POSITION_SNAP_THRESHOLD_SQ = 0.01f; // 0.1 blocks

        // Maximum centroid shift before snapping (large jump = new opening, not jitter)
        private const float POSITION_SNAP_MAX_SQ = 9.0f; // 3.0 blocks

        // Merge radius: tracked openings closer than this are merged to prevent
        // duplicate sources from cluster split/merge oscillation
        private const float MERGE_RADIUS = 3.0f;
        private const float MERGE_RADIUS_SQ = MERGE_RADIUS * MERGE_RADIUS;

        // Hysteresis for removal: opening must be this far beyond scan radius to be removed
        // Prevents flicker at scan boundary
        private const float REMOVAL_DISTANCE_PADDING = 4f;

        /// <summary>
        /// Maximum DDA occlusion for a member column to count as "still open" in the
        /// structural integrity check. Verified openings pass at occ &lt; 0.3 (DDA threshold).
        /// Using 0.5 gives headroom so normal scan variance doesn't cause false positives,
        /// while still catching placed blocks (~1.0 occ each).
        /// </summary>
        private const float STRUCTURAL_OCC_THRESHOLD = 0.5f;

        public OpeningTracker(ICoreClientAPI api)
        {
            capi = api;
        }

        // Height offsets for structural integrity checks.
        // WeatherEnclosureCalculator samples at heights: 1.01f, 4f, 8f, 12f.
        // We check integer offsets: +1, +2, +4, +8, +12 to cover the full
        // vertical span where rain could enter (multi-height openings).
        private static readonly int[] STRUCTURAL_HEIGHT_OFFSETS = { 1, 2, 4, 8, 12 };

        /// <summary>
        /// Update tracked openings from the latest clustering results.
        /// Called every scan cycle (~500ms) from WeatherAudioManager.
        ///
        /// 1. Mark all existing openings as unverified
        /// 2. Match new clusters to existing tracked openings
        /// 3. Create new tracked openings for unmatched clusters
        /// 4. Remove stale openings past persistence timeout
        /// </summary>
        /// <param name="clusters">Latest cluster results from OpeningClusterer</param>
        /// <param name="playerEarPos">Current player ear position</param>
        /// <param name="gameTimeMs">Current game time in milliseconds</param>
        /// <param name="scanRadius">The scan radius from WeatherEnclosureCalculator (for distance removal)</param>
        public void Update(
            IReadOnlyList<OpeningCluster> clusters,
            Vec3d playerEarPos,
            long gameTimeMs,
            int scanRadius)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            float persistenceMs = (config?.OpeningPersistenceSeconds ?? 10f) * 1000f;
            bool debug = config?.DebugMode == true && config?.DebugPositionalWeather == true;

            // Step 1: Mark all existing as unverified this cycle
            MarkAllUnverified();

            // Step 2: Match clusters to existing tracked openings
            int clusterCount = clusters.Count;
            Span<bool> clusterConsumed = stackalloc bool[clusterCount];

            MatchAndReverify(clusters, playerEarPos, gameTimeMs, clusterConsumed, debug);

            // Step 3: Create new tracked openings for unmatched clusters
            CreateNewOpenings(clusters, clusterConsumed, playerEarPos, gameTimeMs, debug);

            // Step 3B: Merge nearby tracked openings to prevent duplicate sources
            MergeNearbyOpenings(debug);

            // Step 4: Remove stale/invalid openings
            var blockAccessor = capi.World.BlockAccessor;
            RemoveStaleOpenings(playerEarPos, gameTimeMs, persistenceMs, scanRadius, blockAccessor, debug);
        }

        /// <summary>Mark all existing openings as unverified for this cycle.</summary>
        private void MarkAllUnverified()
        {
            for (int i = 0; i < trackedOpenings.Count; i++)
            {
                trackedOpenings[i].CurrentlyVerified = false;
            }
        }

        /// <summary>
        /// Match new clusters to existing tracked openings by proximity.
        /// Re-verifies matches, smooths positions, and updates peak/smoothed weights.
        /// </summary>
        private void MatchAndReverify(
            IReadOnlyList<OpeningCluster> clusters,
            Vec3d playerEarPos,
            long gameTimeMs,
            Span<bool> clusterConsumed,
            bool debug)
        {
            int clusterCount = clusters.Count;

            for (int t = 0; t < trackedOpenings.Count; t++)
            {
                var tracked = trackedOpenings[t];
                float bestDistSq = MATCH_RADIUS_SQ;
                int bestCluster = -1;

                for (int c = 0; c < clusterCount; c++)
                {
                    if (clusterConsumed[c]) continue;

                    double dx = clusters[c].Centroid.X - tracked.WorldPos.X;
                    double dz = clusters[c].Centroid.Z - tracked.WorldPos.Z;
                    float distSq = (float)(dx * dx + dz * dz);

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestCluster = c;
                    }
                }

                if (bestCluster >= 0)
                {
                    // Re-verified: update stats and smooth position
                    var cluster = clusters[bestCluster];

                    // Position smoothing: lerp toward new centroid to prevent per-tick panning jitter.
                    // Small shifts (<snap threshold) snap immediately (float precision).
                    // Large shifts (> 3 blocks) snap immediately (new geometry, not jitter).
                    // Medium shifts get exponential smoothing.
                    double pdx = cluster.Centroid.X - tracked.WorldPos.X;
                    double pdy = cluster.Centroid.Y - tracked.WorldPos.Y;
                    double pdz = cluster.Centroid.Z - tracked.WorldPos.Z;
                    double shiftSq = pdx * pdx + pdy * pdy + pdz * pdz;

                    if (shiftSq < POSITION_SNAP_THRESHOLD_SQ)
                    {
                        // Trivially small shift — snap to avoid float drift
                        tracked.WorldPos = cluster.Centroid;
                    }
                    else if (shiftSq > POSITION_SNAP_MAX_SQ)
                    {
                        // Large shift (>3 blocks) — likely new geometry, snap
                        tracked.WorldPos = cluster.Centroid;
                    }
                    else
                    {
                        // Velocity-aware lerp: larger shifts get dampened more heavily.
                        // Small jitter converges quickly, large centroid jumps (from member
                        // set changes) are near-frozen until they persist directionally.
                        //   < 1 block:  normal lerp (0.3) — ~700ms to 90%
                        //   1-2 blocks: slow lerp (0.12) — ~1.8s to 90%
                        //   2-3 blocks: very slow lerp (0.05) — ~4.5s to 90%
                        float lerpFactor = shiftSq < 1.0f ? POSITION_LERP_FACTOR
                                         : shiftSq < 4.0f ? POSITION_LERP_FACTOR * 0.4f
                                         : POSITION_LERP_FACTOR * 0.15f;

                        tracked.WorldPos = new Vec3d(
                            tracked.WorldPos.X + pdx * lerpFactor,
                            tracked.WorldPos.Y + pdy * lerpFactor,
                            tracked.WorldPos.Z + pdz * lerpFactor);
                    }

                    tracked.ClusterWeight = cluster.MemberCount;

                    // Peak weight hold: remember highest verified member count.
                    // When player walks back into cave, DDA can't reach overhead
                    // columns anymore → members drop to 1. Without peak hold,
                    // volume would crash instantly. Peak decays slowly instead.
                    // Asymmetric smoothing: fast attack, slow linear decay.
                    // Snap up immediately when more members found.
                    // Decay slowly (1.5 members/sec) to prevent abrupt volume drops
                    // when DDA finds fewer columns (player moving away from overhead openings).
                    if (cluster.MemberCount >= tracked.SmoothedClusterWeight)
                    {
                        // Fast attack: snap to new higher value
                        tracked.SmoothedClusterWeight = cluster.MemberCount;
                    }
                    else
                    {
                        // Slow linear decay: 0.15/tick at ~10Hz = 1.5 members/sec
                        tracked.SmoothedClusterWeight = Math.Max(
                            tracked.SmoothedClusterWeight - 0.15f,
                            (float)cluster.MemberCount);
                    }
                    tracked.LastKnownOcclusion = cluster.AverageOcclusion;
                    tracked.LastVerifiedTimeMs = gameTimeMs;
                    tracked.CurrentlyVerified = true;
                    tracked.MemberPositions = new List<Vec3d>(cluster.MemberPositions);
                    tracked.MemberEntryPositions = cluster.MemberEntryPositions != null
                        ? new List<Vec3d>(cluster.MemberEntryPositions) : null;
                    tracked.LastVerifiedPlayerPos = new Vec3d(playerEarPos.X, playerEarPos.Y, playerEarPos.Z);
                    clusterConsumed[bestCluster] = true;

                    if (debug)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"[5B-TRACK] RE-VERIFIED id={tracked.TrackingId} pos=({tracked.WorldPos.X:F0},{tracked.WorldPos.Y:F0},{tracked.WorldPos.Z:F0}) " +
                            $"members={cluster.MemberCount} occl={cluster.AverageOcclusion:F2}");
                    }
                }
            }
        }

        /// <summary>Create new tracked openings for unmatched clusters.</summary>
        private void CreateNewOpenings(
            IReadOnlyList<OpeningCluster> clusters,
            Span<bool> clusterConsumed,
            Vec3d playerEarPos,
            long gameTimeMs,
            bool debug)
        {
            for (int c = 0; c < clusters.Count; c++)
            {
                if (clusterConsumed[c]) continue;

                var cluster = clusters[c];
                var newOpening = new TrackedOpening
                {
                    WorldPos = cluster.Centroid,
                    ClusterWeight = cluster.MemberCount,
                    SmoothedClusterWeight = Math.Min(cluster.MemberCount, 1f),  // Start at 1, ramp up
                    LastKnownOcclusion = cluster.AverageOcclusion,
                    CreatedTimeMs = gameTimeMs,
                    LastVerifiedTimeMs = gameTimeMs,
                    CurrentlyVerified = true,
                    TrackingId = nextTrackingId++,
                    MemberPositions = new List<Vec3d>(cluster.MemberPositions),
                    MemberEntryPositions = cluster.MemberEntryPositions != null
                        ? new List<Vec3d>(cluster.MemberEntryPositions) : null,
                    LastVerifiedPlayerPos = new Vec3d(playerEarPos.X, playerEarPos.Y, playerEarPos.Z)
                };

                trackedOpenings.Add(newOpening);

                if (debug)
                {
                    WeatherAudioManager.WeatherDebugLog(
                        $"[5B-TRACK] NEW OPENING id={newOpening.TrackingId} pos=({newOpening.WorldPos.X:F0},{newOpening.WorldPos.Y:F0},{newOpening.WorldPos.Z:F0}) " +
                        $"members={cluster.MemberCount} occl={cluster.AverageOcclusion:F2}");
                }
            }
        }

        /// <summary>
        /// Merge nearby tracked openings to prevent duplicate sources
        /// from cluster split/merge oscillation. When clusters split one cycle
        /// and merge the next, two tracked openings can end up 1-2 blocks apart.
        /// The higher-weight one absorbs the other.
        /// </summary>
        private void MergeNearbyOpenings(bool debug)
        {
            for (int i = trackedOpenings.Count - 1; i >= 0; i--)
            {
                for (int j = i - 1; j >= 0; j--)
                {
                    double mdx = trackedOpenings[i].WorldPos.X - trackedOpenings[j].WorldPos.X;
                    double mdz = trackedOpenings[i].WorldPos.Z - trackedOpenings[j].WorldPos.Z;
                    double mergeDist = mdx * mdx + mdz * mdz;

                    if (mergeDist < MERGE_RADIUS_SQ)
                    {
                        // Merge: keep the one with higher smoothed weight
                        TrackedOpening keeper, absorbed;
                        int absorbedIdx;
                        if (trackedOpenings[j].SmoothedClusterWeight >= trackedOpenings[i].SmoothedClusterWeight)
                        {
                            keeper = trackedOpenings[j];
                            absorbed = trackedOpenings[i];
                            absorbedIdx = i;
                        }
                        else
                        {
                            keeper = trackedOpenings[i];
                            absorbed = trackedOpenings[j];
                            absorbedIdx = j;
                        }

                        // Transfer verification status
                        if (absorbed.CurrentlyVerified)
                            keeper.CurrentlyVerified = true;
                        if (absorbed.LastVerifiedTimeMs > keeper.LastVerifiedTimeMs)
                            keeper.LastVerifiedTimeMs = absorbed.LastVerifiedTimeMs;

                        if (debug)
                        {
                            WeatherAudioManager.WeatherDebugLog(
                                $"[5B-TRACK] MERGED id={absorbed.TrackingId} into id={keeper.TrackingId} " +
                                $"(dist={Math.Sqrt(mergeDist):F1} < {MERGE_RADIUS:F1})");
                        }

                        trackedOpenings.RemoveAt(absorbedIdx);
                        if (absorbedIdx < i) i--; // Adjust outer index if removed below
                        break; // Only merge one pair per outer iteration
                    }
                }
            }
        }

        /// <summary>
        /// Remove stale/invalid openings based on distance, structural changes,
        /// and audibility persistence.
        /// </summary>
        private void RemoveStaleOpenings(
            Vec3d playerEarPos, long gameTimeMs,
            float persistenceMs, int scanRadius,
            IBlockAccessor blockAccessor, bool debug)
        {
            for (int i = trackedOpenings.Count - 1; i >= 0; i--)
            {
                var tracked = trackedOpenings[i];
                string removeReason = null;

                // ── BLOCK-EVENT INVALIDATION (entry-point proximity trigger) ──
                // When a block was placed/removed near an entry point, IMMEDIATELY REMOVE
                // the opening. If it's still genuinely open, the enclosure calculator will
                // re-discover it within ~500ms and create a fresh tracked opening.
                // We can't soft-reset here because 4d (audibility persistence) would
                // resurrect the opening: the sound is still playing → IsSourceAudible=true
                // → LastVerifiedTimeMs gets reset → opening survives forever.
                if (tracked.ForceReverify)
                {
                    if (debug)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"[5B-TRACK] BLOCK-EVENT REMOVE id={tracked.TrackingId} " +
                            $"pos=({tracked.WorldPos.X:F0},{tracked.WorldPos.Y:F0},{tracked.WorldPos.Z:F0})");
                    }
                    trackedOpenings.RemoveAt(i);
                    continue;
                }

                // ── STRUCTURAL CHECKS FIRST (ground truth, cannot be overridden) ──

                // 4a: Distance — player too far from opening
                if (removeReason == null && playerEarPos != null)
                {
                    double dx = tracked.WorldPos.X - playerEarPos.X;
                    double dz = tracked.WorldPos.Z - playerEarPos.Z;
                    float horizDist = (float)Math.Sqrt(dx * dx + dz * dz);
                    float removalDist = scanRadius + REMOVAL_DISTANCE_PADDING;

                    if (horizDist > removalDist)
                    {
                        removeReason = $"too far ({horizDist:F1} > {removalDist})";
                    }
                }

                // 4b: Structural change — heightmap now covers opening columns
                // Check EACH member column's rain height (not just the centroid,
                // which may not correspond to any real column and gets stale as
                // members are removed). If ALL member columns are now roofed over
                // the player, the opening is sealed.
                if (removeReason == null && blockAccessor != null
                    && tracked.MemberPositions != null && tracked.MemberPositions.Count > 0)
                {
                    try
                    {
                        int playerY = (int)Math.Floor(playerEarPos.Y);
                        bool allSealed = true;

                        for (int m = 0; m < tracked.MemberPositions.Count; m++)
                        {
                            var memberPos = tracked.MemberPositions[m];
                            int mx = (int)Math.Floor(memberPos.X);
                            int mz = (int)Math.Floor(memberPos.Z);
                            int memberY = (int)Math.Floor(memberPos.Y);
                            int currentRainH = blockAccessor.GetRainMapHeightAt(mx, mz);

                            // A member is sealed if rain now hits above it AND above the player.
                            // No +2 tolerance — a single block placed at memberY+1 should count.
                            if (currentRainH <= memberY || playerY >= currentRainH)
                            {
                                allSealed = false;
                                break;
                            }
                        }

                        if (allSealed)
                        {
                            removeReason = "structural change (all member columns roofed)";
                        }
                    }
                    catch { /* BlockAccessor may fail at chunk boundaries */ }
                }

                // 4c: DDA structural integrity check for persisted openings
                if (removeReason == null && !tracked.CurrentlyVerified
                    && tracked.MemberPositions != null && tracked.MemberPositions.Count > 0
                    && tracked.LastVerifiedPlayerPos != null && blockAccessor != null)
                {
                    CheckStructuralIntegrity(tracked, blockAccessor, debug);
                }

                // ── AUDIBILITY PERSISTENCE (soft hint, only after structural checks pass) ──

                // 4d: Audibility-based persistence.
                // As long as the source is physically intact (structural checks passed)
                // AND audible to the player, keep it alive. This allows natural fade-out
                // via OpenAL distance attenuation + acoustics occlusion as the player
                // walks away, instead of abruptly killing sources.
                //
                // Pool slots are stable: each source keeps its slot by TrackingId match.
                // No competition/oscillation between sources at similar volumes.
                // Sources only free their slot when removed from tracker here.
                //
                // When no longer audible: full persistence timeout before removal,
                // giving time for the player to turn back / opening to re-verify.
                if (removeReason == null && !tracked.CurrentlyVerified)
                {
                    bool stillAudible = false;
                    try { stillAudible = IsSourceAudible?.Invoke(tracked.TrackingId) == true; }
                    catch { /* callback failure = not audible */ }

                    if (stillAudible)
                    {
                        // Sound is audible — keep alive regardless of fresh openings.
                        // Natural fade-out via distance attenuation handles the transition.
                        tracked.LastVerifiedTimeMs = gameTimeMs;
                        if (debug)
                        {
                            WeatherAudioManager.WeatherDebugLog(
                                $"[5B-TRACK] AUDIBLE-PERSIST id={tracked.TrackingId}");
                        }
                    }
                    else
                    {
                        // Not audible — use full persistence timeout
                        long timeSinceVerified = gameTimeMs - tracked.LastVerifiedTimeMs;
                        if (timeSinceVerified > persistenceMs)
                        {
                            removeReason = $"inaudible timeout ({timeSinceVerified}ms > {persistenceMs}ms)";
                        }
                    }
                }

                if (removeReason != null)
                {
                    if (debug)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"[5B-TRACK] REMOVED id={tracked.TrackingId} reason={removeReason}");
                    }
                    trackedOpenings.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Block-event invalidation: check if a changed block is near any tracked
        /// opening's entry points (wall faces). If so, flag for re-verification.
        /// Called from WeatherAudioManager.NotifyBlockChanged(), driven by the
        /// existing BlockChanged event. Event-driven, cheap, no polling needed.
        /// </summary>
        public void OnBlockChanged(BlockPos changedPos)
        {
            bool debug = SoundPhysicsAdaptedModSystem.Config?.DebugMode == true
                      && SoundPhysicsAdaptedModSystem.Config?.DebugPositionalWeather == true;

            for (int i = 0; i < trackedOpenings.Count; i++)
            {
                var tracked = trackedOpenings[i];
                if (tracked.ForceReverify) continue; // Already flagged

                // Check entry positions first (wall faces — precise)
                if (tracked.MemberEntryPositions != null)
                {
                    for (int m = 0; m < tracked.MemberEntryPositions.Count; m++)
                    {
                        Vec3d entry = tracked.MemberEntryPositions[m];
                        int dx = Math.Abs(changedPos.X - (int)Math.Floor(entry.X));
                        int dy = Math.Abs(changedPos.Y - (int)Math.Floor(entry.Y));
                        int dz = Math.Abs(changedPos.Z - (int)Math.Floor(entry.Z));

                        if (dx <= 1 && dy <= 1 && dz <= 1)
                        {
                            tracked.ForceReverify = true;
                            if (debug)
                            {
                                WeatherAudioManager.WeatherDebugLog(
                                    $"[5B-TRACK] BLOCK-EVENT HIT (entry) id={tracked.TrackingId} " +
                                    $"block=({changedPos.X},{changedPos.Y},{changedPos.Z}) " +
                                    $"entry=({entry.X:F0},{entry.Y:F0},{entry.Z:F0}) d=({dx},{dy},{dz})");
                            }
                            break;
                        }
                    }
                }

                // Also check member positions (rain columns — fallback for sky openings
                // and cases where entry points didn't cover the sealed spot)
                if (!tracked.ForceReverify && tracked.MemberPositions != null)
                {
                    for (int m = 0; m < tracked.MemberPositions.Count; m++)
                    {
                        Vec3d member = tracked.MemberPositions[m];
                        int dx = Math.Abs(changedPos.X - (int)Math.Floor(member.X));
                        int dz = Math.Abs(changedPos.Z - (int)Math.Floor(member.Z));

                        // Only XZ for member positions (rain columns span vertically)
                        if (dx <= 1 && dz <= 1)
                        {
                            tracked.ForceReverify = true;
                            if (debug)
                            {
                                WeatherAudioManager.WeatherDebugLog(
                                    $"[5B-TRACK] BLOCK-EVENT HIT (member) id={tracked.TrackingId} " +
                                    $"block=({changedPos.X},{changedPos.Y},{changedPos.Z}) " +
                                    $"member=({member.X:F0},{member.Y:F0},{member.Z:F0})");
                            }
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// DDA structural integrity check for persisted openings.
        /// Detects blocks placed IN the opening. Two checks per member column:
        ///   1. Direct block check at the member position itself (bypasses DDA skipFirst)
        ///   2. DDA from member to stored player position (catches blocks between)
        /// Uses stored player pos (not current) so walking around corners doesn't
        /// false-trigger — only placing/removing blocks changes these paths.
        /// </summary>
        private void CheckStructuralIntegrity(TrackedOpening tracked, IBlockAccessor blockAccessor, bool debug)
        {
            try
            {
                int openCount = 0;
                for (int m = 0; m < tracked.MemberPositions.Count; m++)
                {
                    var memberPos = tracked.MemberPositions[m];
                    bool columnBlocked = false;

                    // Check 1: Block AT the member position itself.
                    // DDA always skips the starting block (skipFirst=true), so a block
                    // placed exactly at the opening position would be invisible to DDA.
                    // This explicit check catches that case.
                    int bx = (int)Math.Floor(memberPos.X);
                    int by = (int)Math.Floor(memberPos.Y);
                    int bz = (int)Math.Floor(memberPos.Z);
                    var memberBlockPos = new BlockPos(bx, by, bz, 0);

                    Block blockAtMember = blockAccessor.GetBlock(memberBlockPos);
                    if (blockAtMember != null && blockAtMember.Id != 0 && IsBlockSolid(blockAtMember))
                    {
                        columnBlocked = true;
                        if (debug)
                        {
                            WeatherAudioManager.WeatherDebugLog(
                                $"[5B-TRACK] STRUCTURAL-BLOCK id={tracked.TrackingId} member[{m}] " +
                                $"block={blockAtMember.Code} at ({bx},{by},{bz})");
                        }
                    }

                    // Check blocks above at structural height offsets
                    if (!columnBlocked)
                    {
                        for (int h = 0; h < STRUCTURAL_HEIGHT_OFFSETS.Length && !columnBlocked; h++)
                        {
                            var aboveBlockPos = new BlockPos(bx, by + STRUCTURAL_HEIGHT_OFFSETS[h], bz, 0);
                            Block blockAbove = blockAccessor.GetBlock(aboveBlockPos);
                            if (blockAbove != null && blockAbove.Id != 0 && IsBlockSolid(blockAbove))
                            {
                                columnBlocked = true;
                                if (debug)
                                {
                                    WeatherAudioManager.WeatherDebugLog(
                                        $"[5B-TRACK] STRUCTURAL-BLOCK+{STRUCTURAL_HEIGHT_OFFSETS[h]} id={tracked.TrackingId} member[{m}] " +
                                        $"block={blockAbove.Code} at ({bx},{by + STRUCTURAL_HEIGHT_OFFSETS[h]},{bz})");
                                }
                            }
                        }
                    }

                    // Check 2: DDA from member to stored player position.
                    // Catches blocks placed BETWEEN the member and the player
                    // (not at the exact member position).
                    if (!columnBlocked)
                    {
                        float occ = OcclusionCalculator.CalculateWeatherPathOcclusion(
                            memberPos, tracked.LastVerifiedPlayerPos, blockAccessor);
                        if (occ >= STRUCTURAL_OCC_THRESHOLD)
                            columnBlocked = true;
                    }

                    if (!columnBlocked)
                        openCount++;
                }

                // Structural change: update weight immediately (no smoothing —
                // block placement response should be instant)
                if (openCount != tracked.MemberPositions.Count)
                {
                    tracked.SmoothedClusterWeight = openCount;

                    // NOTE: Never set removeReason here. The DDA check
                    // (member→stored player pos) false-triggers on existing
                    // walls when rounding corners inside houses. Instead:
                    //  - Weight goes to 0 → volume drops to 0 (instant silence)
                    //  - 4d (audibility check) handles actual removal:
                    //    · If audible through walls → stays alive (repositioned/muffled)
                    //    · If truly inaudible → persistence timeout removes it
                    // This preserves block-placement responsiveness (volume=0 immediately)
                    // while preventing false-kills on corner navigation.
                    if (debug)
                    {
                        WeatherAudioManager.WeatherDebugLog(
                            $"[5B-TRACK] STRUCTURAL-SHRINK id={tracked.TrackingId} " +
                            $"open={openCount}/{tracked.MemberPositions.Count}");
                    }
                }
            }
            catch { /* BlockAccessor may fail at chunk boundaries */ }
        }

        /// <summary>
        /// Check if a block is structurally solid (not air, plant, or leaves).
        /// Used by structural integrity checks to determine if an opening column is blocked.
        /// </summary>
        private static bool IsBlockSolid(Block block)
        {
            return block.BlockMaterial != EnumBlockMaterial.Air
                && block.BlockMaterial != EnumBlockMaterial.Plant
                && block.BlockMaterial != EnumBlockMaterial.Leaves;
        }

        /// <summary>
        /// Remove all tracked openings (weather stopped, feature disabled).
        /// </summary>
        public void Clear()
        {
            trackedOpenings.Clear();
        }

        /// <summary>
        /// Get debug status string.
        /// </summary>
        public string GetDebugStatus()
        {
            int verified = VerifiedCount;
            int persisted = trackedOpenings.Count - verified;
            return $"Tracked={trackedOpenings.Count}(verified={verified} persisted={persisted})";
        }

        /// <summary>
        /// Detailed per-opening debug for /soundphysics weather command.
        /// Shows three states: VERIFIED (in current scan), repositioned (persisted
        /// but heard via indirect path), persisted (persisted, normal timeout).
        /// </summary>
        public string GetDetailedDebugStatus(long currentGameTimeMs)
        {
            if (trackedOpenings.Count == 0)
                return "  No tracked openings";

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < trackedOpenings.Count; i++)
            {
                var t = trackedOpenings[i];
                long ageSec = (currentGameTimeMs - t.CreatedTimeMs) / 1000;
                long sinceLast = (currentGameTimeMs - t.LastVerifiedTimeMs) / 1000;

                string verStr;
                if (t.CurrentlyVerified)
                {
                    verStr = "VERIFIED";
                }
                else
                {
                    verStr = $"persisted({sinceLast}s ago)";
                }

                sb.AppendLine(
                    $"  Opening[{i}] id={t.TrackingId} " +
                    $"pos=({t.WorldPos.X:F0},{t.WorldPos.Y:F0},{t.WorldPos.Z:F0}) " +
                    $"weight={t.ClusterWeight} occ={t.LastKnownOcclusion:F2} " +
                    $"age={ageSec}s {verStr}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
