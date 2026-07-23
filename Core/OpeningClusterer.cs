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

        /// <summary>
        /// Wind source centroid: same X/Z as Centroid, but Y is derived from
        /// SkyOpeningY (ceiling height) for sky openings instead of floor level.
        /// For wall openings (EntryPos != null), matches Centroid exactly.
        /// Wind enters through openings, not at the floor where rain splashes.
        /// </summary>
        public Vec3d WindCentroid;
    }

    /// <summary>
    /// Greedy clustering algorithm for grouping nearby verified rain openings
    /// into positional audio source groups.
    ///
    /// Input: WeatherEnclosureCalculator.VerifiedOpenings (up to ~50 positions)
    /// Output: Up to MaxClusters groups, each becoming one positional audio source.
    ///
    /// Two phases:
    /// 1. Anchored — previous cycle's verified tracked openings seed clusters,
    ///    preserving cluster identity across cycles (stable centroids).
    /// 2. Greedy — pick strongest unclustered point as seed, absorb neighbors.
    ///
    /// Performance: O(n²) on small n = trivial; runs every ~100ms weather tick.
    /// </summary>
    public static class OpeningClusterer
    {
        /// <summary>
        /// Maximum horizontal distance (blocks) to merge openings into same cluster.
        /// 3.5 blocks ≈ typical door width or roof hole.
        /// Openings further apart become separate sources (different directions).
        /// </summary>
        private const float CLUSTER_RADIUS = 3.5f;
        private const float CLUSTER_RADIUS_SQ = CLUSTER_RADIUS * CLUSTER_RADIUS;

        // Reusable across calls to avoid GC pressure
        private static readonly List<OpeningCluster> resultClusters = new List<OpeningCluster>(8);
        private static bool[] consumed = new bool[32]; // Grows if needed

        /// <summary>
        /// Accumulates members into one cluster: centroid weights, aggregate stats,
        /// and the wind ceiling height — all in a single pass over the members.
        /// </summary>
        private struct ClusterBuilder
        {
            public List<Vec3d> MemberPositions;
            public List<Vec3d> MemberEntryPositions;
            public int MemberCount;
            private double centX, centY, centZ, centWeightSum;
            private float totalOcclusion, totalDistance, totalWeight;
            private double maxSkyY;

            public static ClusterBuilder Create()
            {
                return new ClusterBuilder
                {
                    MemberPositions = new List<Vec3d>(8),
                    MemberEntryPositions = new List<Vec3d>(8),
                    maxSkyY = double.NaN
                };
            }

            public void Add(VerifiedRainPosition op)
            {
                MemberPositions.Add(op.WorldPos);
                Vec3d entry = op.EntryPos ?? op.WorldPos;
                MemberEntryPositions.Add(entry);

                // Occlusion-weighted centroid: clarity² pulls toward least-occluded members
                float clarity = Math.Max(1f - Math.Min(op.Occlusion, 1f), 0.01f);
                float centW = clarity * clarity;
                centX += entry.X * centW;
                centY += entry.Y * centW;
                centZ += entry.Z * centW;
                centWeightSum += centW;
                totalOcclusion += op.Occlusion;
                totalDistance += op.Distance;
                totalWeight += (1f - Math.Min(op.Occlusion, 1f)) / (1f + op.Distance * 0.15f);
                MemberCount++;

                // Wind Y: highest known ceiling among sky-opening members
                // (edge columns know the roof height, interior columns inherit it)
                if (op.EntryPos == null && !double.IsNaN(op.SkyOpeningY)
                    && (double.IsNaN(maxSkyY) || op.SkyOpeningY > maxSkyY))
                {
                    maxSkyY = op.SkyOpeningY;
                }
            }

            public OpeningCluster Build()
            {
                var centroid = new Vec3d(
                    centX / centWeightSum, centY / centWeightSum, centZ / centWeightSum);
                return new OpeningCluster
                {
                    Centroid = centroid,
                    MemberCount = MemberCount,
                    AverageOcclusion = totalOcclusion / MemberCount,
                    AverageDistance = totalDistance / MemberCount,
                    TotalWeight = totalWeight,
                    MemberPositions = MemberPositions,
                    MemberEntryPositions = MemberEntryPositions,
                    WindCentroid = double.IsNaN(maxSkyY)
                        ? new Vec3d(centroid.X, centroid.Y, centroid.Z)
                        : new Vec3d(centroid.X, maxSkyY, centroid.Z)
                };
            }
        }

        /// <summary>
        /// Absorb all unconsumed openings within CLUSTER_RADIUS (horizontal) of the
        /// given center into a new ClusterBuilder, marking them consumed.
        /// </summary>
        private static ClusterBuilder AbsorbAround(
            IReadOnlyList<VerifiedRainPosition> openings, int count, double cx, double cz)
        {
            var builder = ClusterBuilder.Create();
            for (int i = 0; i < count; i++)
            {
                if (consumed[i]) continue;

                var candidate = openings[i];
                double dx = candidate.WorldPos.X - cx;
                double dz = candidate.WorldPos.Z - cz;
                if (dx * dx + dz * dz <= CLUSTER_RADIUS_SQ)
                {
                    builder.Add(candidate);
                    consumed[i] = true;
                }
            }
            return builder;
        }

        /// <summary>
        /// Cluster verified openings into positional source groups.
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

            if (count > consumed.Length)
            {
                consumed = new bool[count];
            }
            for (int i = 0; i < count; i++)
                consumed[i] = false;

            int clusterIdx = 0;

            // ── Phase 1: Anchored clustering ──
            // Only tracked openings verified last cycle are used as anchors
            // (not persisted behind-corner ones, which would pull front openings).
            if (anchors != null && anchors.Count > 0)
            {
                for (int a = 0; a < anchors.Count && clusterIdx < maxClusters; a++)
                {
                    var anchor = anchors[a];
                    if (!anchor.CurrentlyVerified) continue;

                    var builder = AbsorbAround(openings, count, anchor.WorldPos.X, anchor.WorldPos.Z);
                    if (builder.MemberCount > 0)
                    {
                        resultClusters.Add(builder.Build());
                        clusterIdx++;
                    }
                }
            }

            // ── Phase 2: Greedy clustering for remaining unassigned openings ──
            for (; clusterIdx < maxClusters; clusterIdx++)
            {
                // Find best unconsumed seed: clarity weighted by inverse distance
                int bestSeed = -1;
                float bestScore = -1f;

                for (int i = 0; i < count; i++)
                {
                    if (consumed[i]) continue;

                    var op = openings[i];
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

                // AbsorbAround picks up the seed itself (distance 0) plus neighbors
                var seedPos = openings[bestSeed].WorldPos;
                var builder = AbsorbAround(openings, count, seedPos.X, seedPos.Z);
                resultClusters.Add(builder.Build());
            }

            // Sort by TotalWeight descending — best clusters first
            resultClusters.Sort((a, b) => b.TotalWeight.CompareTo(a.TotalWeight));

            return resultClusters;
        }
    }
}
