using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Result of clustering verified rain openings into positional source groups.
    /// Each cluster represents one positional audio source placement.
    /// </summary>
    public struct OpeningCluster
    {
        /// <summary>
        /// World-space centroid of the cluster (occlusion-weighted average of member positions).
        /// Weighted by clarity² (clarity = 1 - occlusion), so the centroid is pulled toward
        /// the least-occluded members. Prevents muffling when a small clear opening feeds a
        /// large cluster of occluded positions behind walls.
        /// This is where the positional audio source will be placed.
        /// </summary>
        public Vec3d Centroid;

        /// <summary>
        /// Number of verified opening positions contributing to this cluster.
        /// More members = more rain entering through this area = louder source.
        /// </summary>
        public int MemberCount;

        /// <summary>
        /// Average occlusion of member openings (from VerifiedRainPosition).
        /// Lower = clearer air path to player. Used for per-source LPF hint.
        /// Note: actual LPF is applied by AudioPhysicsSystem, not RainAudioHandler.
        /// </summary>
        public float AverageOcclusion;

        /// <summary>
        /// Average distance from cluster centroid to player.
        /// Used for distance-based volume falloff.
        /// </summary>
        public float AverageDistance;

        /// <summary>
        /// Total weight of the cluster: sum of (1 - occlusion) * distanceWeight for each member.
        /// Higher = more audible opening group. Used for source volume calculation.
        /// </summary>
        public float TotalWeight;

        /// <summary>
        /// World-space positions of each member opening in this cluster.
        /// Used by OpeningTracker for structural integrity checks:
        /// DDA from each member position to the stored player position
        /// detects when blocks are placed inside the opening.
        /// </summary>
        public List<Vec3d> MemberPositions;

        /// <summary>
        /// Wall-face entry positions for each member (parallel to MemberPositions).
        /// Used for block-event invalidation: when a block is placed near an entry point,
        /// the opening is flagged for re-verification. Falls back to WorldPos for sky openings.
        /// </summary>
        public List<Vec3d> MemberEntryPositions;
    }

    /// <summary>
    /// Greedy clustering algorithm for grouping nearby verified rain openings
    /// into positional audio source groups.
    ///
    /// Input: WeatherEnclosureCalculator.VerifiedOpenings (up to ~15 positions)
    /// Output: Up to MaxClusters groups, each becoming one positional audio source.
    ///
    /// Algorithm: Simple greedy — pick strongest unclustered point as seed,
    /// absorb all points within CLUSTER_RADIUS. Repeat until max clusters reached
    /// or no points remain.
    ///
    /// Performance: O(n²) on ≤15 input points = trivial.
    /// </summary>
    public static class OpeningClusterer
    {
        /// <summary>
        /// Maximum horizontal distance (blocks) to merge openings into same cluster.
        /// 3 blocks ≈ typical door width or roof hole.
        /// Openings further apart become separate sources (different directions).
        /// </summary>
        private const float CLUSTER_RADIUS = 3.5f;
        private const float CLUSTER_RADIUS_SQ = CLUSTER_RADIUS * CLUSTER_RADIUS;

        // Reusable list to avoid GC pressure (called every 500ms)
        private static readonly List<OpeningCluster> resultClusters = new List<OpeningCluster>(8);
        private static bool[] consumed = new bool[32]; // Grows if needed

        /// <summary>
        /// Cluster verified openings into positional source groups.
        /// 
        /// Greedy algorithm:
        /// 1. Score each opening: clarity * inverse distance (best audible opening)
        /// 2. Pick highest-scoring unconsumed opening as cluster seed
        /// 3. Absorb all unconsumed openings within CLUSTER_RADIUS of seed
        /// 4. Compute centroid and aggregate stats
        /// 5. Repeat until maxClusters reached or no openings remain
        /// </summary>
        /// <param name="openings">Verified rain positions from WeatherEnclosureCalculator</param>
        /// <param name="maxClusters">Maximum clusters to produce (= max positional sources)</param>
        /// <param name="anchors">Previous cycle's tracked openings for centroid stability (optional).
        /// When provided, verified openings are first assigned to the nearest anchor within
        /// CLUSTER_RADIUS, preserving cluster identity across cycles. Unassigned openings
        /// fall through to greedy seeding. On the first cycle (no anchors), pure greedy.</param>
        /// <returns>List of clusters sorted by TotalWeight descending. Reused internal list — do NOT cache across calls.</returns>
        public static IReadOnlyList<OpeningCluster> Cluster(
            IReadOnlyList<VerifiedRainPosition> openings,
            int maxClusters,
            IReadOnlyList<TrackedOpening> anchors = null)
        {
            resultClusters.Clear();

            int count = openings.Count;
            if (count == 0 || maxClusters <= 0) return resultClusters;

            // Reset consumed flags (grow array if needed)
            if (count > consumed.Length)
            {
                consumed = new bool[count];
            }
            for (int i = 0; i < count; i++)
                consumed[i] = false;

            int clusterIdx = 0;

            // ── Phase 1: Anchored clustering ──
            // Use previous cycle's tracked opening positions as cluster seeds.
            // This prevents centroid re-seeding instability: the same openings
            // end up in the same cluster each tick, producing stable centroids.
            // Only tracked openings verified last cycle are used as anchors
            // (not persisted behind-corner ones, which would pull front openings).
            if (anchors != null && anchors.Count > 0)
            {
                for (int a = 0; a < anchors.Count && clusterIdx < maxClusters; a++)
                {
                    var anchor = anchors[a];
                    // Only use anchors that were verified last cycle
                    if (!anchor.CurrentlyVerified) continue;

                    var memberPositions = new List<Vec3d>(8);
                    var memberEntryPositions = new List<Vec3d>(8);
                    double centX = 0, centY = 0, centZ = 0;
                    double centWeightSum = 0;
                    float totalOcclusion = 0, totalDistance = 0, totalWeight = 0;
                    int memberCount = 0;

                    for (int i = 0; i < count; i++)
                    {
                        if (consumed[i]) continue;

                        var candidate = openings[i];
                        // Distance from candidate to ANCHOR position (stable center)
                        double dx = candidate.WorldPos.X - anchor.WorldPos.X;
                        double dz = candidate.WorldPos.Z - anchor.WorldPos.Z;
                        double distSq = dx * dx + dz * dz;

                        if (distSq <= CLUSTER_RADIUS_SQ)
                        {
                            memberPositions.Add(candidate.WorldPos);
                            Vec3d entry = candidate.EntryPos ?? candidate.WorldPos;
                            memberEntryPositions.Add(entry);
                            // Occlusion-weighted centroid: clarity² pulls toward least-occluded members
                            float clarity = Math.Max(1f - Math.Min(candidate.Occlusion, 1f), 0.01f);
                            float centW = clarity * clarity;
                            centX += entry.X * centW;
                            centY += entry.Y * centW;
                            centZ += entry.Z * centW;
                            centWeightSum += centW;
                            totalOcclusion += candidate.Occlusion;
                            totalDistance += candidate.Distance;
                            totalWeight += (1f - Math.Min(candidate.Occlusion, 1f)) / (1f + candidate.Distance * 0.15f);
                            memberCount++;
                            consumed[i] = true;
                        }
                    }

                    if (memberCount > 0)
                    {
                        resultClusters.Add(new OpeningCluster
                        {
                            Centroid = new Vec3d(centX / centWeightSum, centY / centWeightSum, centZ / centWeightSum),
                            MemberCount = memberCount,
                            AverageOcclusion = totalOcclusion / memberCount,
                            AverageDistance = totalDistance / memberCount,
                            TotalWeight = totalWeight,
                            MemberPositions = memberPositions,
                            MemberEntryPositions = memberEntryPositions
                        });
                        clusterIdx++;
                    }
                }
            }

            // ── Phase 2: Greedy clustering for remaining unassigned openings ──
            // Handles openings too far from any anchor, and the first cycle
            // when no anchors exist yet.
            for (; clusterIdx < maxClusters; clusterIdx++)
            {
                // Find best unconsumed seed (highest weight = most audible)
                int bestSeed = -1;
                float bestScore = -1f;

                for (int i = 0; i < count; i++)
                {
                    if (consumed[i]) continue;

                    var op = openings[i];
                    // Score: clarity (inverse occlusion) weighted by inverse distance
                    float clarity = 1f - Math.Min(op.Occlusion, 1f);
                    float distWeight = 1f / (1f + op.Distance * 0.1f);
                    float score = clarity * distWeight;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestSeed = i;
                    }
                }

                if (bestSeed < 0) break; // All consumed

                // Build cluster around seed
                var seed = openings[bestSeed];
                var memberPositions = new List<Vec3d>(8);
                var memberEntryPositions = new List<Vec3d>(8);
                memberPositions.Add(seed.WorldPos);
                // Use entry point for centroid (source placement) — fall back to rain pos for sky openings
                Vec3d seedEntry = seed.EntryPos ?? seed.WorldPos;
                memberEntryPositions.Add(seedEntry);
                // Occlusion-weighted centroid: clarity² pulls toward least-occluded members
                float seedClarity = Math.Max(1f - Math.Min(seed.Occlusion, 1f), 0.01f);
                float seedCentW = seedClarity * seedClarity;
                double centX = seedEntry.X * seedCentW;
                double centY = seedEntry.Y * seedCentW;
                double centZ = seedEntry.Z * seedCentW;
                double centWeightSum = seedCentW;
                float totalOcclusion = seed.Occlusion;
                float totalDistance = seed.Distance;
                float totalWeight = (1f - Math.Min(seed.Occlusion, 1f)) / (1f + seed.Distance * 0.15f);
                int memberCount = 1;
                consumed[bestSeed] = true;

                // Absorb nearby unconsumed openings
                for (int i = 0; i < count; i++)
                {
                    if (consumed[i]) continue;

                    var candidate = openings[i];

                    // Horizontal distance check (ignore Y — rain at different heights can cluster)
                    double dx = candidate.WorldPos.X - seed.WorldPos.X;
                    double dz = candidate.WorldPos.Z - seed.WorldPos.Z;
                    double distSq = dx * dx + dz * dz;

                    if (distSq <= CLUSTER_RADIUS_SQ)
                    {
                        memberPositions.Add(candidate.WorldPos);
                        Vec3d candEntry = candidate.EntryPos ?? candidate.WorldPos;
                        memberEntryPositions.Add(candEntry);
                        // Occlusion-weighted centroid contribution
                        float candClarity = Math.Max(1f - Math.Min(candidate.Occlusion, 1f), 0.01f);
                        float candCentW = candClarity * candClarity;
                        centX += candEntry.X * candCentW;
                        centY += candEntry.Y * candCentW;
                        centZ += candEntry.Z * candCentW;
                        centWeightSum += candCentW;
                        totalOcclusion += candidate.Occlusion;
                        totalDistance += candidate.Distance;
                        totalWeight += (1f - Math.Min(candidate.Occlusion, 1f)) / (1f + candidate.Distance * 0.15f);
                        memberCount++;
                        consumed[i] = true;
                    }
                }

                // Compute occlusion-weighted centroid
                resultClusters.Add(new OpeningCluster
                {
                    Centroid = new Vec3d(centX / centWeightSum, centY / centWeightSum, centZ / centWeightSum),
                    MemberCount = memberCount,
                    AverageOcclusion = totalOcclusion / memberCount,
                    AverageDistance = totalDistance / memberCount,
                    TotalWeight = totalWeight,
                    MemberPositions = memberPositions,
                    MemberEntryPositions = memberEntryPositions
                });
            }

            // Sort by TotalWeight descending — best clusters first
            resultClusters.Sort((a, b) => b.TotalWeight.CompareTo(a.TotalWeight));

            return resultClusters;
        }
    }
}
