using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Result of sound path resolution - contains repositioned location and blended occlusion.
    /// Immutable struct for efficient passing without heap allocation.
    /// </summary>
    public readonly struct SoundPathResult
    {
        /// <summary>
        /// Repositioned sound location (weighted average direction from ALL paths).
        /// SPR-style: direct path always contributes. When direct LOS is clear,
        /// it dominates and position stays near original. When occluded, indirect
        /// paths through openings pull the position toward the opening.
        /// </summary>
        public readonly Vec3d ApparentPosition;

        /// <summary>
        /// Weighted average occlusion across ALL paths — for LPF calculation.
        /// Lower = clearer sound. Direct path contributes its occlusion weighted
        /// by its permeation, so clear LOS naturally pulls this toward 0.
        /// </summary>
        public readonly double AverageOcclusion;

        /// <summary>
        /// Weighted average path distance (for debugging/analysis).
        /// </summary>
        public readonly double AverageDistance;

        /// <summary>
        /// Number of open (low-occlusion) paths contributing.
        /// </summary>
        public readonly int PathCount;

        /// <summary>
        /// Offset distance from original position to apparent position.
        /// Small offset = sound barely moved (direct dominates).
        /// Large offset = sound shifted toward opening (indirect dominates).
        /// </summary>
        public readonly double RepositionOffset;

        /// <summary>
        /// Total number of paths (for diagnostics).
        /// </summary>
        public readonly int TotalPathCount;

        /// <summary>
        /// Average occlusion of high-occlusion (through-wall) paths only (diagnostics).
        /// </summary>
        public readonly double PermeatedOcclusion;

        /// <summary>
        /// Total weight of high-occlusion paths (diagnostics).
        /// </summary>
        public readonly double PermeatedWeight;

        /// <summary>
        /// Number of high-occlusion (through-wall) paths.
        /// </summary>
        public readonly int PermeatedPathCount;

        /// <summary>
        /// Average occlusion of low-occlusion (open) paths only (diagnostics).
        /// </summary>
        public readonly double OpenAverageOcclusion;

        /// <summary>
        /// Blended occlusion: weighted average of ALL paths.
        /// This is the primary value used to drive the LPF on the sound source.
        /// </summary>
        public readonly double BlendedOcclusion;

        /// <summary>
        /// Ratio of rays that found shared airspace (0-1).
        /// Used for SPR-style floor calculation: higher ratio = more clear paths = lower floor.
        /// </summary>
        public readonly float SharedAirspaceRatio;

        public SoundPathResult(Vec3d apparentPos, Vec3d originalPos, double avgOcclusion, double avgDist,
            int openPathCount, int totalPathCount,
            double permeatedOcclusion, double permeatedWeight, int permeatedPathCount,
            double openAvgOcclusion, double blendedOcclusion, float sharedAirspaceRatio)
        {
            ApparentPosition = apparentPos;
            AverageOcclusion = avgOcclusion;
            AverageDistance = avgDist;
            PathCount = openPathCount;
            RepositionOffset = originalPos.DistanceTo(apparentPos);
            TotalPathCount = totalPathCount;
            PermeatedOcclusion = permeatedOcclusion;
            PermeatedWeight = permeatedWeight;
            PermeatedPathCount = permeatedPathCount;
            OpenAverageOcclusion = openAvgOcclusion;
            BlendedOcclusion = blendedOcclusion;
            SharedAirspaceRatio = sharedAirspaceRatio;
        }
    }

    /// <summary>
    /// SPR-style unified weighted direction resolver.
    /// 
    /// ALL paths (direct + indirect bounces) contribute to a single weighted direction average.
    /// The direct path is always included as a contributor. When LOS is clear, the direct path
    /// dominates (high permeation = high weight) and position stays near original. When LOS is
    /// blocked, direct weight drops and indirect paths through openings pull position toward
    /// the opening. The transition is inherently smooth — no binary state switch.
    /// 
    /// This matches SPR's evaluateSoundPosition() approach:
    ///   sum = directDir.normalize() * directWeight
    ///   sum += Sigma(indirectDir.normalize() * indirectWeight)
    ///   finalDir = normalize(sum)
    ///   position = playerPos + finalDir * distance
    /// </summary>
    public class SoundPathResolver
    {
        private struct PathEntry
        {
            public Vec3d Direction;       // Normalized direction from player toward path point
            public double TotalDistance;   // Total acoustic path distance
            public double Weight;          // Contribution weight (permeation / dist²)
            public double PathOcclusion;   // Occlusion along this path segment to player
        }

        // Single unified list — all paths contribute to direction
        private List<PathEntry> allPaths = new List<PathEntry>(128);

        // Track open vs permeated counts for diagnostics only
        private int openCount = 0;
        private int permeatedCount = 0;
        private float occlusionThreshold = 1.5f;

        /// <summary>
        /// Reset for new sound calculation. Reuses internal storage.
        /// </summary>
        public void Clear()
        {
            allPaths.Clear();
            openCount = 0;
            permeatedCount = 0;
        }

        /// <summary>
        /// No-op: kept for API compatibility.
        /// Direct path is now added via AddPath() in AcousticRaytracer.
        /// </summary>
        public void SetDirectPathOcclusion(double occlusion)
        {
            // SPR-style: direct path is just another AddPath() contributor.
        }

        /// <summary>
        /// Add a path to the resolution calculation.
        /// ALL paths contribute to the weighted direction average (SPR-style).
        /// Open vs permeated classification is tracked for diagnostics only.
        /// </summary>
        public void AddPath(Vec3d directionFromPlayer, double totalDistance, double weight, double pathOcclusion, float occThreshold = 1.5f)
        {
            if (weight < 0.00001) return;

            occlusionThreshold = occThreshold;

            Vec3d normalizedDir = directionFromPlayer.Clone();
            normalizedDir.Normalize();

            allPaths.Add(new PathEntry
            {
                Direction = normalizedDir,
                TotalDistance = totalDistance,
                Weight = weight,
                PathOcclusion = pathOcclusion
            });

            // Diagnostic tracking only
            if (pathOcclusion < occThreshold)
                openCount++;
            else
                permeatedCount++;
        }

        /// <summary>
        /// SPR-style unified evaluation: ALL paths contribute to direction.
        /// No binary split, no shortcut. Direct path is just another contributor.
        /// 
        /// Returns null only if:
        /// - No paths at all
        /// - Weighted directions cancel out
        /// </summary>
        public SoundPathResult? Evaluate(Vec3d soundPos, Vec3d playerPos, SoundPhysicsConfig config, float sharedAirspaceRatio = 0f)
        {
            if (allPaths.Count == 0) return null;

            // === UNIFIED WEIGHTED DIRECTION ===
            double weightedX = 0, weightedY = 0, weightedZ = 0;
            double totalWeight = 0;
            double weightedOcclusion = 0;
            double weightedDistance = 0;

            // Diagnostic accumulators
            double openWeightedOcc = 0, openTotalWeight = 0;
            double permWeightedOcc = 0, permTotalWeight = 0;

            foreach (var path in allPaths)
            {
                weightedX += path.Direction.X * path.Weight;
                weightedY += path.Direction.Y * path.Weight;
                weightedZ += path.Direction.Z * path.Weight;
                totalWeight += path.Weight;

                weightedOcclusion += path.PathOcclusion * path.Weight;
                weightedDistance += path.TotalDistance * path.Weight;

                // Diagnostic split
                if (path.PathOcclusion < occlusionThreshold)
                {
                    openWeightedOcc += path.PathOcclusion * path.Weight;
                    openTotalWeight += path.Weight;
                }
                else
                {
                    permWeightedOcc += path.PathOcclusion * path.Weight;
                    permTotalWeight += path.Weight;
                }
            }

            if (totalWeight < 0.0001) return null;

            // ISSUE 5 FIX: Use weighted percentile instead of weighted mean.
            // The weighted mean is diluted by many through-wall bounce rays even though they have low weight.
            // Using the 25th percentile (best quarter of paths by weight) gives the sound character
            // of the ACTUAL best paths, not a mixture dominated by volume of poor paths.
            //
            // SPR avoids this by not averaging occlusion at all - it uses directCutoff with a floor.
            // SPP avoids this by using separate sound instances for permeated vs open paths.
            // We use weighted percentile as a middle ground.

            // Sort paths by occlusion (ascending - best paths first)
            allPaths.Sort((a, b) => a.PathOcclusion.CompareTo(b.PathOcclusion));

            // Find 25th percentile by weight
            const double PERCENTILE = 0.25;
            double targetWeight = totalWeight * PERCENTILE;
            double accumulatedWeight = 0;
            double blendedOcclusion = allPaths[0].PathOcclusion; // Default to best path

            foreach (var path in allPaths)
            {
                accumulatedWeight += path.Weight;
                if (accumulatedWeight >= targetWeight)
                {
                    blendedOcclusion = path.PathOcclusion;
                    break;
                }
            }

            // Still compute weighted mean for diagnostics/comparison
            double avgOcclusion = weightedOcclusion / totalWeight;
            double avgDistance = weightedDistance / totalWeight;
            double openAvgOcc = openTotalWeight > 0.0001 ? openWeightedOcc / openTotalWeight : 0;
            double permAvgOcc = permTotalWeight > 0.0001 ? permWeightedOcc / permTotalWeight : 0;

            // Compute apparent direction from weighted sum of all paths
            double dirLength = Math.Sqrt(weightedX * weightedX + weightedY * weightedY + weightedZ * weightedZ);
            if (dirLength < 0.0001) return null; // Directions cancel out

            Vec3d apparentDir = new Vec3d(
                weightedX / dirLength,
                weightedY / dirLength,
                weightedZ / dirLength
            );

            double originalDist = soundPos.DistanceTo(playerPos);
            Vec3d apparentPos = new Vec3d(
                playerPos.X + apparentDir.X * originalDist,
                playerPos.Y + apparentDir.Y * originalDist,
                playerPos.Z + apparentDir.Z * originalDist
            );

            double offset = soundPos.DistanceTo(apparentPos);
            // No MinRepositionOffset threshold — always return a result.
            // When offset is tiny, position naturally stays near-original
            // and position smoothing handles the rest. A threshold here
            // causes null flickering and resets smoothing state.

            return new SoundPathResult(
                apparentPos, soundPos, avgOcclusion, avgDistance,
                openCount, allPaths.Count,
                permAvgOcc, permTotalWeight, permeatedCount,
                openAvgOcc, blendedOcclusion, // ISSUE 5 FIX: Use percentile-based blended occlusion
                sharedAirspaceRatio
            );
        }

        /// <summary>
        /// Get debug info about current paths.
        /// </summary>
        public string GetDebugInfo()
        {
            if (allPaths.Count == 0) return "No paths";

            double totalWeight = 0;
            double minOcc = double.MaxValue;

            foreach (var p in allPaths)
            {
                totalWeight += p.Weight;
                if (p.PathOcclusion < minOcc) minOcc = p.PathOcclusion;
            }

            return $"total={allPaths.Count} open={openCount} perm={permeatedCount} w={totalWeight:F4} minOcc={minOcc:F2}";
        }
    }
}
