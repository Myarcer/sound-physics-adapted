using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Unified acoustic raytracer - single ray pass drives multiple sound systems.
    ///
    /// Fires fibonacci-sphere distributed rays from sound sources, bouncing off surfaces.
    /// The shared ray loop simultaneously feeds:
    ///   1. REVERB (Phase 4A) - SPR-style multi-slot reverb with shared airspace detection
    ///   2. SOUND PATHS (Phase 4B) - Permeation-weighted repositioning toward openings
    ///   3. ROOM STATS - Debug display of acoustic environment metrics
    ///
    /// Key design: one raycast pass, multiple consumers. Splitting would double ray cost.
    ///
    /// Reverb approach (vs vanilla's logarithmic distance formula):
    /// - Flat energy contribution based on material reflectivity
    /// - Ray bouncing for realistic multi-reflection paths
    /// - Distributes energy to 4 reverb slots by reflection delay
    /// - Shared airspace check - only audible reflections count
    ///
    /// Path resolution feeds SoundPathResolver with permeation-weighted directions,
    /// creating smooth blending rather than binary snap-to-opening behavior.
    /// </summary>
    public static class AcousticRaytracer
    {
        // Golden ratio for fibonacci sphere distribution
        private const float PHI = 1.618033988f;

        // PHASE 4B: Reusable path resolver to avoid allocation per sound
        private static readonly SoundPathResolver pathResolver = new SoundPathResolver();
        private static int probeLogCount = 0;

        // Pre-allocated neighbor offsets for opening detection (6 face-adjacent blocks)
        private static readonly int[][] neighborOffsets = new int[][]
        {
            new[] { 1, 0, 0 }, new[] { -1, 0, 0 },
            new[] { 0, 1, 0 }, new[] { 0, -1, 0 },
            new[] { 0, 0, 1 }, new[] { 0, 0, -1 }
        };

        // Block filter predicate for reverb raycasting (cached delegate to avoid allocation)
        private static readonly System.Func<Block, bool> _reverbBlockFilter = BlockClassification.IsSolidForReverb;

        // === Pre-allocated reusable objects (GC pressure reduction) ===
        // Safe because VS mod ticks are single-threaded (all on client game tick).
        private static float[] _reusableBounceReflectivity = new float[8]; // Max bounces
        private static Queue<(int x, int y, int z, int depth)> _probeSearchQueue = new();
        private static HashSet<long> _probeVisited = new();
        private static BlockPos _probeBlockPos = new BlockPos(0, 0, 0, 0);
        private static BlockPos _adjacentAirBlockPos = new BlockPos(0, 0, 0, 0);
        private static Vec3d _reusableBouncePoint = new Vec3d();
        private static Vec3d _reusableFromPlayerToBounce = new Vec3d();
        private static Vec3d _reusableDirNorm = new Vec3d();
        private static Vec3d _reusableRayDir = new Vec3d();
        private static BlockPos _decorCheckPos = new BlockPos(0, 0, 0, 0);

        /// <summary>
        /// Calculate reverb parameters for a sound at given position.
        /// Backward-compatible wrapper - delegates to CalculateWithPathsCacheable, discards path results.
        /// </summary>
        public static ReverbResult Calculate(Vec3d soundPos, Vec3d playerPos, IBlockAccessor blockAccessor)
        {
            var (reverb, _) = CalculateWithPathsCacheable(
                soundPos, playerPos, blockAccessor, -1f,
                out _, out _, out _, out _, out _, out _, out _);
            return reverb;
        }

        /// <summary>
        /// Calculate reverb AND sound path for repositioning (Phase 4B).
        /// Wrapper that delegates to CalculateWithPathsCacheable, discarding cache capture data.
        /// </summary>
        public static (ReverbResult reverb, SoundPathResult? path) CalculateWithPaths(
            Vec3d soundPos, Vec3d playerPos, IBlockAccessor blockAccessor, float multiRayOcclusion = -1f)
        {
            return CalculateWithPathsCacheable(
                soundPos, playerPos, blockAccessor, multiRayOcclusion,
                out _, out _, out _, out _, out _, out _, out _);
        }

        // === Pre-allocated output arrays for cacheable variant ===
        private static BouncePoint[] _cacheableBouncePoints = new BouncePoint[256];
        private static int _cacheableBounceCount = 0;
        private static OpeningData[] _cacheableOpenings = new OpeningData[24];
        private static int _cacheableOpeningCount = 0;

        /// <summary>
        /// Cache-aware version of CalculateWithPaths.
        /// On top of the normal reverb + path calculation, captures BouncePoint[] and 
        /// OpeningData[] arrays for storage in the ReverbCellCache.
        /// Returns the bounce/opening data via out parameters.
        /// </summary>
        public static (ReverbResult reverb, SoundPathResult? path) CalculateWithPathsCacheable(
            Vec3d soundPos, Vec3d playerPos, IBlockAccessor blockAccessor, float multiRayOcclusion,
            out BouncePoint[] bouncePoints, out int bounceCount,
            out OpeningData[] openings, out int openingCount,
            out float sharedAirspaceRatioOut, out float directOcclusionOut, out bool hasDirectAirspaceOut)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableCustomReverb)
            {
                bouncePoints = _cacheableBouncePoints;
                bounceCount = 0;
                openings = _cacheableOpenings;
                openingCount = 0;
                sharedAirspaceRatioOut = 0f;
                directOcclusionOut = 0f;
                hasDirectAirspaceOut = false;
                return (ReverbResult.None, null);
            }

            float soundDistance = (float)playerPos.DistanceTo(soundPos);

            var acoustics = SoundPhysicsAdaptedModSystem.Acoustics;
            int baseRayCount = acoustics != null ? acoustics.SuggestedReverbRayCount : config.ReverbRayCount;

            int numRays;
            if (soundDistance < 15f)
                numRays = baseRayCount;
            else if (soundDistance < 30f)
                numRays = Math.Max(12, baseRayCount / 2);
            else
                numRays = Math.Max(8, baseRayCount / 4);

            int bounces = config.ReverbBounces;
            float maxDistance = config.ReverbMaxDistance;

            float sendGain0 = 0f, sendGain1 = 0f, sendGain2 = 0f, sendGain3 = 0f;
            float rcpTotalRays = 1f / (numRays * bounces);

            if (_reusableBounceReflectivity.Length < bounces)
                _reusableBounceReflectivity = new float[bounces];
            else
                Array.Clear(_reusableBounceReflectivity, 0, bounces);
            float[] bounceReflectivity = _reusableBounceReflectivity;

            int raysHit = 0;
            int raysEscaped = 0;
            int totalBouncePoints = 0;
            int sharedAirspaceCount = 0;

            // Reset cacheable bounce point capture
            _cacheableBounceCount = 0;

            pathResolver.Clear();

            float goldenAngle = PHI * MathF.PI * 2f;

            float singleRayOcclusion = OcclusionCalculator.CalculatePathOcclusion(soundPos, playerPos, blockAccessor);
            bool hasDirectAirspace = singleRayOcclusion < 0.1f;
            float directOcclusion = multiRayOcclusion >= 0 ? multiRayOcclusion : singleRayOcclusion;

            for (int i = 0; i < numRays; i++)
            {
                float fiN = (float)i / numRays;
                float longitude = goldenAngle * i;
                float latitude = MathF.Asin(fiN * 2f - 1f);

                Vec3d rayDir = new Vec3d(
                    Math.Cos(latitude) * Math.Cos(longitude),
                    Math.Sin(latitude),
                    Math.Cos(latitude) * Math.Sin(longitude)
                );

                var hit = RaycastToSurface(soundPos, rayDir, maxDistance, blockAccessor);
                if (!hit.HasValue)
                {
                    raysEscaped++;
                    continue;
                }
                raysHit++;

                float totalDistance = hit.Value.distance;
                Vec3d lastHitPos = hit.Value.position;
                Vec3d lastHitNormal = hit.Value.normal;
                Vec3d lastRayDir = rayDir;
                BlockPos lastHitBlock = hit.Value.blockPos;

                for (int bounce = 0; bounce < bounces; bounce++)
                {
                    totalBouncePoints++;

                    float reflectivity = GetBlockReflectivity(hit.Value.block, lastHitBlock, lastHitNormal, blockAccessor);
                    bounceReflectivity[bounce] += reflectivity;

                    // Calculate bounce point offset from surface
                    double bpX = lastHitPos.X + lastHitNormal.X * 0.15;
                    double bpY = lastHitPos.Y + lastHitNormal.Y * 0.15;
                    double bpZ = lastHitPos.Z + lastHitNormal.Z * 0.15;

                    _reusableBouncePoint.Set(bpX, bpY, bpZ);

                    float pathOcclusion = OcclusionCalculator.CalculatePathOcclusion(_reusableBouncePoint, playerPos, blockAccessor);
                    float permeation = (float)Math.Pow(config.PermeationBase, pathOcclusion);

                    float bounceToPlayerDist = (float)_reusableBouncePoint.DistanceTo(playerPos);
                    float pathTotalDist = totalDistance + bounceToPlayerDist;
                    float weight = permeation / (pathTotalDist * pathTotalDist + 0.01f);

                    // CAPTURE bounce point for cache
                    if (_cacheableBounceCount < _cacheableBouncePoints.Length)
                    {
                        _cacheableBouncePoints[_cacheableBounceCount] = new BouncePoint
                        {
                            PosX = bpX,
                            PosY = bpY,
                            PosZ = bpZ,
                            NormalX = lastHitNormal.X,
                            NormalY = lastHitNormal.Y,
                            NormalZ = lastHitNormal.Z,
                            Reflectivity = reflectivity,
                            PathOcclusion = pathOcclusion,
                            TotalDistance = totalDistance,
                            BounceIndex = bounce,
                            Permeation = permeation
                        };
                        _cacheableBounceCount++;
                    }

                    if (config.EnableSoundRepositioning)
                    {
                        _reusableFromPlayerToBounce.Set(bpX - playerPos.X, bpY - playerPos.Y, bpZ - playerPos.Z);
                        double dirLen = _reusableFromPlayerToBounce.Length();
                        if (dirLen > 0.01)
                        {
                            _reusableDirNorm.Set(
                                _reusableFromPlayerToBounce.X / dirLen,
                                _reusableFromPlayerToBounce.Y / dirLen,
                                _reusableFromPlayerToBounce.Z / dirLen);
                            pathResolver.AddPath(_reusableDirNorm, pathTotalDist, weight, pathOcclusion, config.PermeationOcclusionThreshold);
                        }
                    }

                    bool hasAirspace = pathOcclusion < 0.5f;
                    float airspaceWeight;
                    if (hasAirspace)
                    {
                        sharedAirspaceCount++;
                        airspaceWeight = 1.0f;
                    }
                    else if (hasDirectAirspace)
                    {
                        airspaceWeight = 0.5f;
                    }
                    else
                    {
                        airspaceWeight = 0f;
                    }

                    if (airspaceWeight > 0f)
                    {
                        float energyTowardsPlayer = 0.25f * (reflectivity * 0.75f + 0.25f);
                        // Slot selection is purely distance-based (reflectivity doesn't change speed of sound).
                        // Reflectivity affects energy amount (above) and bounce attenuation (below), not timing.
                        float reflectionDelay = totalDistance * 0.12f;

                        float cross0 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 0f), 0f, 1f);
                        float cross1 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 1f), 0f, 1f);
                        float cross2 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 2f), 0f, 1f);
                        float cross3 = Math.Clamp(reflectionDelay - 2f, 0f, 1f);

                        sendGain0 += cross0 * energyTowardsPlayer * airspaceWeight * 6.4f * rcpTotalRays;
                        sendGain1 += cross1 * energyTowardsPlayer * airspaceWeight * 12.8f * rcpTotalRays;
                        sendGain2 += cross2 * energyTowardsPlayer * airspaceWeight * 12.8f * rcpTotalRays;
                        sendGain3 += cross3 * energyTowardsPlayer * airspaceWeight * 12.8f * rcpTotalRays;
                    }

                    if (bounce < bounces - 1)
                    {
                        Vec3d newRayDir = Reflect(lastRayDir, lastHitNormal);
                        var nextHit = RaycastToSurface(lastHitPos, newRayDir, maxDistance, blockAccessor, lastHitBlock);
                        if (!nextHit.HasValue) break;

                        totalDistance += nextHit.Value.distance;
                        lastHitPos = nextHit.Value.position;
                        lastHitNormal = nextHit.Value.normal;
                        lastRayDir = newRayDir;
                        lastHitBlock = nextHit.Value.blockPos;
                        hit = nextHit;
                    }
                }
            }

            for (int i = 0; i < bounces; i++)
                bounceReflectivity[i] /= numRays;

            if (bounces >= 2 && bounceReflectivity[1] > 0)
                sendGain1 *= bounceReflectivity[1];
            if (bounces >= 3 && bounceReflectivity[2] > 0)
                sendGain2 *= MathF.Pow(bounceReflectivity[2], 3f);
            if (bounces >= 4 && bounceReflectivity[3] > 0)
                sendGain3 *= MathF.Pow(bounceReflectivity[3], 4f);

            float totalRays = raysHit + raysEscaped;
            float sharedAirspaceRatio = totalBouncePoints > 0
                ? (float)sharedAirspaceCount / totalBouncePoints
                : 0f;

            sendGain0 = Math.Clamp(sendGain0, 0f, 1f);
            sendGain1 = Math.Clamp(sendGain1, 0f, 1f);
            sendGain2 = Math.Clamp(sendGain2 * 1.05f - 0.05f, 0f, 1f);
            sendGain3 = Math.Clamp(sendGain3 * 1.05f - 0.05f, 0f, 1f);

            float maxSoundDistance = config.MaxSoundDistance;
            float distanceAtten = 1f - Math.Min(soundDistance / maxSoundDistance, 1f);

            sendGain0 *= distanceAtten;
            sendGain1 *= distanceAtten;
            sendGain2 *= distanceAtten;
            sendGain3 *= distanceAtten;

            // Reset opening capture
            _cacheableOpeningCount = 0;

            SoundPathResult? pathResult = null;
            if (config.EnableSoundRepositioning)
            {
                {
                    float directPermeation = (float)Math.Pow(config.PermeationBase, directOcclusion);
                    float directDist = (float)soundPos.DistanceTo(playerPos);
                    float directWeight = directPermeation / (directDist * directDist + 0.01f);

                    Vec3d directDir = new Vec3d(
                        soundPos.X - playerPos.X,
                        soundPos.Y - playerPos.Y,
                        soundPos.Z - playerPos.Z
                    );
                    double ddirLen = directDir.Length();
                    if (ddirLen > 0.01)
                    {
                        Vec3d dNorm = new Vec3d(
                            directDir.X / ddirLen,
                            directDir.Y / ddirLen,
                            directDir.Z / ddirLen
                        );
                        pathResolver.AddPath(dNorm, directDist, directWeight, directOcclusion, config.PermeationOcclusionThreshold);
                    }
                }

                bool skipProbes = soundDistance > 25f || directOcclusion < 1.0f;
                if (!skipProbes)
                {
                    ProbeForOpenings(soundPos, playerPos, blockAccessor, config);
                }

                pathResult = pathResolver.Evaluate(soundPos, playerPos, config, sharedAirspaceRatio);

                // Log probe results for diagnostics (first 20 calculations only)
                if (probeLogCount < 20 && config.DebugSoundPaths)
                {
                    probeLogCount++;
                    string pathInfo = pathResult.HasValue
                        ? $"off={pathResult.Value.RepositionOffset:F1}m occ={pathResult.Value.AverageOcclusion:F2} open={pathResult.Value.PathCount} perm={pathResult.Value.PermeatedPathCount}"
                        : "null (LOS/cancelled)";
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[4B-Resolve] #{probeLogCount}: {pathInfo} directOcc={directOcclusion:F1}");
                }
            }

            if (config.DebugMode && (config.DebugReverb || config.DebugSoundPaths))
            {
                bool isOutdoor = acoustics?.IsOutdoors ?? false;
                string pathInfo = pathResult.HasValue
                    ? $"reposDist={pathResult.Value.RepositionOffset:F1} avgOcc={pathResult.Value.AverageOcclusion:F2}"
                    : "noRepos";

                SoundPhysicsAdaptedModSystem.ReverbDebugLog(
                    $"REVERB+PATH: g0={sendGain0:F2} g1={sendGain1:F2} " +
                    $"shared={sharedAirspaceCount}/{totalBouncePoints} ({sharedAirspaceRatio:P0}) " +
                    $"direct={hasDirectAirspace} {pathInfo}");
            }

            var reverbResult = new ReverbResult(sendGain0, sendGain1, sendGain2, sendGain3);

            // Output cached data
            bouncePoints = _cacheableBouncePoints;
            bounceCount = _cacheableBounceCount;
            openings = _cacheableOpenings;
            openingCount = _cacheableOpeningCount;
            sharedAirspaceRatioOut = sharedAirspaceRatio;
            directOcclusionOut = directOcclusion;
            hasDirectAirspaceOut = hasDirectAirspace;

            return (reverbResult, pathResult);
        }

        /// <summary>
        /// Resolve per-sound path from cached cell data without any raycasting.
        /// Uses cached bounce points and openings to compute path direction and
        /// occlusion blending for a specific sound position.
        /// Cost: ~50 float ops per bounce point vs ~8 DDA traversals per bounce point.
        /// </summary>
        public static SoundPathResult? ResolvePathFromCache(
            ReverbCellEntry cell, Vec3d soundPos, Vec3d playerPos,
            float directOcclusion, SoundPhysicsConfig config)
        {
            pathResolver.Clear();

            // Add direct path (per-sound - unique angle)
            float directPermeation = (float)Math.Pow(config.PermeationBase, directOcclusion);
            float directDist = (float)soundPos.DistanceTo(playerPos);
            float directWeight = directPermeation / (directDist * directDist + 0.01f);

            Vec3d directDir = new Vec3d(
                soundPos.X - playerPos.X,
                soundPos.Y - playerPos.Y,
                soundPos.Z - playerPos.Z
            );
            double dirLen = directDir.Length();
            if (dirLen > 0.01)
            {
                Vec3d dirNorm = new Vec3d(
                    directDir.X / dirLen,
                    directDir.Y / dirLen,
                    directDir.Z / dirLen
                );
                pathResolver.AddPath(dirNorm, directDist, directWeight, directOcclusion, config.PermeationOcclusionThreshold);
            }

            // Add cached bounce paths (shared geometry, per-sound weighting)
            for (int i = 0; i < cell.BouncePointCount; i++)
            {
                var bp = cell.BouncePoints[i];
                // Direction from THIS sound's player perspective (unique per sound)
                double bpDirX = bp.PosX - playerPos.X;
                double bpDirY = bp.PosY - playerPos.Y;
                double bpDirZ = bp.PosZ - playerPos.Z;
                double bpDirLen = Math.Sqrt(bpDirX * bpDirX + bpDirY * bpDirY + bpDirZ * bpDirZ);
                if (bpDirLen < 0.01) continue;

                _reusableDirNorm.Set(bpDirX / bpDirLen, bpDirY / bpDirLen, bpDirZ / bpDirLen);

                // Distance: sound->bounce + bounce->player
                double sBpDist = Math.Sqrt(
                    (soundPos.X - bp.PosX) * (soundPos.X - bp.PosX) +
                    (soundPos.Y - bp.PosY) * (soundPos.Y - bp.PosY) +
                    (soundPos.Z - bp.PosZ) * (soundPos.Z - bp.PosZ));
                float pathDist = (float)(sBpDist + bpDirLen);

                // Weight uses cached permeation (bounce->player occlusion doesn't change per sound)
                float weight = bp.Permeation / (pathDist * pathDist + 0.01f);
                pathResolver.AddPath(_reusableDirNorm, pathDist, weight, bp.PathOcclusion, config.PermeationOcclusionThreshold);
            }

            // Add cached openings (shared, but weight includes per-sound distance)
            for (int i = 0; i < cell.OpeningCount; i++)
            {
                var op = cell.Openings[i];

                double opDirX = op.PosX - playerPos.X;
                double opDirY = op.PosY - playerPos.Y;
                double opDirZ = op.PosZ - playerPos.Z;
                double opDirLen = Math.Sqrt(opDirX * opDirX + opDirY * opDirY + opDirZ * opDirZ);
                if (opDirLen < 0.01) continue;

                _reusableDirNorm.Set(opDirX / opDirLen, opDirY / opDirLen, opDirZ / opDirLen);

                float distPlayerToOp = (float)opDirLen;
                float distOpToSound = (float)Math.Sqrt(
                    (op.PosX - soundPos.X) * (op.PosX - soundPos.X) +
                    (op.PosY - soundPos.Y) * (op.PosY - soundPos.Y) +
                    (op.PosZ - soundPos.Z) * (op.PosZ - soundPos.Z));
                float totalDist = distPlayerToOp + distOpToSound;

                float soundSidePermeation = (float)Math.Pow(config.PermeationBase, op.OccToCell);
                float playerSidePermeation = (float)Math.Pow(config.PermeationBase, op.OccToPlayer);
                float weight = soundSidePermeation * playerSidePermeation * op.OpeningBoost
                    / (totalDist * totalDist + 0.01f);

                float totalOcc = op.OccToPlayer + op.DiffractionPenalty + op.OccToCell;
                pathResolver.AddPath(_reusableDirNorm, totalDist, weight, totalOcc, config.PermeationOcclusionThreshold);
            }

            return pathResolver.Evaluate(soundPos, playerPos, config, cell.SharedAirspaceRatio);
        }

        /// <summary>
        /// Fire probe rays from player toward sound area to find wall openings.
        /// When a probe ray hits a wall, check adjacent blocks for air gaps (openings).
        /// Clear paths through openings get high weight in the path resolver.
        ///
        /// This solves the fundamental problem: uniform fibonacci sphere rays from the sound
        /// almost never find small openings (1-block airhole ≈ 0.4% of sphere).
        /// Probe rays from the player side directly SEARCH for openings.
        /// </summary>
        // Reusable dedup set for opening detection (cleared each call, avoids allocation).
        private static readonly HashSet<long> _openingDedup = new HashSet<long>();

        /// <summary>
        /// Pack block coordinates into a single long for fast deduplication.
        /// </summary>
        private static long PackBlockPos(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0xFFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        /// <summary>
        /// Count face-adjacent air blocks around an opening to estimate its size.
        /// Returns 0 (isolated 1-block hole) to 6 (completely surrounded by air).
        /// Cost: 6 block lookups — trivial.
        /// </summary>
        private static int CountAdjacentAir(int bx, int by, int bz, IBlockAccessor blockAccessor)
        {
            int airCount = 0;
            for (int n = 0; n < neighborOffsets.Length; n++)
            {
                int ax = bx + neighborOffsets[n][0];
                int ay = by + neighborOffsets[n][1];
                int az = bz + neighborOffsets[n][2];
                _adjacentAirBlockPos.Set(ax, ay, az);
                Block b = blockAccessor.GetBlock(_adjacentAirBlockPos);
                if (b == null || b.Id == 0 ||
                    b.BlockMaterial == EnumBlockMaterial.Air ||
                    b.BlockMaterial == EnumBlockMaterial.Plant)
                {
                    airCount++;
                }
            }
            return airCount;
        }

        /// <summary>
        /// Fire probe rays from player toward sound area to find wall openings.
        /// When a probe ray hits a wall, check adjacent blocks for air gaps (openings).
        /// Clear paths through openings get high weight in the path resolver.
        /// Also captures OpeningData into _cacheableOpenings for cell cache storage.
        /// </summary>
        private static void ProbeForOpenings(Vec3d soundPos, Vec3d playerPos, IBlockAccessor blockAccessor, SoundPhysicsConfig config)
        {
            Vec3d toSound = new Vec3d(
                soundPos.X - playerPos.X,
                soundPos.Y - playerPos.Y,
                soundPos.Z - playerPos.Z
            );
            double dist = toSound.Length();
            if (dist < 1.0) return;


            Vec3d dirNorm = new Vec3d(toSound.X / dist, toSound.Y / dist, toSound.Z / dist);

            int probeCount = 12;
            int openingsFound = 0;
            int posHash = (int)(soundPos.X * 73856093 + soundPos.Y * 19349663 + soundPos.Z * 83492791
                         + playerPos.X * 37139213 + playerPos.Y * 57853711 + playerPos.Z * 29475827);
            Random rng = new Random(posHash);

            _openingDedup.Clear();

            for (int i = 0; i < probeCount; i++)
            {
                Vec3d probeDir;
                if (i == 0)
                {
                    probeDir = dirNorm;
                }
                else
                {
                    double jitterAngle = 0.785;
                    double theta = rng.NextDouble() * Math.PI * 2;
                    double phi = rng.NextDouble() * jitterAngle;

                    Vec3d perp1, perp2;
                    if (Math.Abs(dirNorm.Y) < 0.9)
                        perp1 = new Vec3d(-dirNorm.Z, 0, dirNorm.X);
                    else
                        perp1 = new Vec3d(1, 0, 0);

                    double p1Len = perp1.Length();
                    if (p1Len > 0.001)
                    {
                        perp1.X /= p1Len; perp1.Y /= p1Len; perp1.Z /= p1Len;
                    }
                    perp2 = new Vec3d(
                        dirNorm.Y * perp1.Z - dirNorm.Z * perp1.Y,
                        dirNorm.Z * perp1.X - dirNorm.X * perp1.Z,
                        dirNorm.X * perp1.Y - dirNorm.Y * perp1.X
                    );

                    double sinPhi = Math.Sin(phi);
                    double cosPhi = Math.Cos(phi);
                    probeDir = new Vec3d(
                        dirNorm.X * cosPhi + (perp1.X * Math.Cos(theta) + perp2.X * Math.Sin(theta)) * sinPhi,
                        dirNorm.Y * cosPhi + (perp1.Y * Math.Cos(theta) + perp2.Y * Math.Sin(theta)) * sinPhi,
                        dirNorm.Z * cosPhi + (perp1.Z * Math.Cos(theta) + perp2.Z * Math.Sin(theta)) * sinPhi
                    );
                }

                var hit = RaycastToSurface(playerPos, probeDir, (float)dist, blockAccessor);
                if (!hit.HasValue) continue;

                BlockPos wallPos = hit.Value.blockPos;

                _probeSearchQueue.Clear();
                _probeVisited.Clear();

                for (int n = 0; n < neighborOffsets.Length; n++)
                {
                    int nx = wallPos.X + neighborOffsets[n][0];
                    int ny = wallPos.Y + neighborOffsets[n][1];
                    int nz = wallPos.Z + neighborOffsets[n][2];
                    _probeSearchQueue.Enqueue((nx, ny, nz, 1));
                    _probeVisited.Add(PackBlockPos(nx, ny, nz));
                }

                while (_probeSearchQueue.Count > 0)
                {
                    var (cx, cy, cz, depth) = _probeSearchQueue.Dequeue();

                    long packedPos = PackBlockPos(cx, cy, cz);
                    if (!_openingDedup.Add(packedPos))
                        continue;

                    _probeBlockPos.Set(cx, cy, cz);
                    Block neighborBlock = blockAccessor.GetBlock(_probeBlockPos);

                    if (neighborBlock == null || neighborBlock.Id == 0 ||
                        neighborBlock.BlockMaterial == EnumBlockMaterial.Air ||
                        neighborBlock.BlockMaterial == EnumBlockMaterial.Plant)
                    {
                        Vec3d openingCenter = new Vec3d(cx + 0.5, cy + 0.5, cz + 0.5);

                        float occToPlayer = OcclusionCalculator.CalculatePathOcclusion(openingCenter, playerPos, blockAccessor);
                        if (occToPlayer > 2.0f) continue;

                        float occToSound = OcclusionCalculator.CalculatePathOcclusion(openingCenter, soundPos, blockAccessor);

                        int adjacentAir = CountAdjacentAir(cx, cy, cz, blockAccessor);

                        float openingBoost;
                        if (adjacentAir <= 1) openingBoost = 1.0f;
                        else if (adjacentAir <= 2) openingBoost = 1.5f;
                        else if (adjacentAir <= 3) openingBoost = 2.5f;
                        else openingBoost = 4.0f;

                        float diffractionPenalty;
                        if (adjacentAir <= 1) diffractionPenalty = 3.0f;
                        else if (adjacentAir <= 2) diffractionPenalty = 1.5f;
                        else if (adjacentAir <= 3) diffractionPenalty = 0.5f;
                        else diffractionPenalty = 0.0f;

                        float permeation = (float)Math.Pow(config.PermeationBase, occToSound);
                        float distPlayerToOpening = (float)openingCenter.DistanceTo(playerPos);
                        float distOpeningToSound = (float)openingCenter.DistanceTo(soundPos);
                        float totalPathDist = distPlayerToOpening + distOpeningToSound;

                        float playerSidePermeation = (float)Math.Pow(config.PermeationBase, occToPlayer);
                        float combinedPermeation = playerSidePermeation * permeation;
                        float weight = combinedPermeation * openingBoost / (totalPathDist * totalPathDist + 0.01f);

                        Vec3d dirToOpening = new Vec3d(
                            openingCenter.X - playerPos.X,
                            openingCenter.Y - playerPos.Y,
                            openingCenter.Z - playerPos.Z
                        );
                        double opDirLen = dirToOpening.Length();
                        if (opDirLen > 0.01)
                        {
                            Vec3d normalizedDir = new Vec3d(
                                dirToOpening.X / opDirLen,
                                dirToOpening.Y / opDirLen,
                                dirToOpening.Z / opDirLen
                            );

                            float totalOcclusion = occToPlayer + diffractionPenalty + occToSound;
                            pathResolver.AddPath(normalizedDir, totalPathDist, weight, totalOcclusion, config.PermeationOcclusionThreshold);
                            openingsFound++;

                            if (probeLogCount < 20 && config.DebugSoundPaths)
                            {
                                SoundPhysicsAdaptedModSystem.DebugLog(
                                    $"[4B-Open] ({cx},{cy},{cz}) d={depth} air={adjacentAir} occ={totalOcclusion:F1} w={weight:F5}");
                            }

                            // CAPTURE opening for cache
                            if (_cacheableOpeningCount < _cacheableOpenings.Length)
                            {
                                _cacheableOpenings[_cacheableOpeningCount] = new OpeningData
                                {
                                    PosX = cx + 0.5,
                                    PosY = cy + 0.5,
                                    PosZ = cz + 0.5,
                                    OccToPlayer = occToPlayer,
                                    OccToCell = occToSound,
                                    AdjacentAir = adjacentAir,
                                    OpeningBoost = openingBoost,
                                    DiffractionPenalty = diffractionPenalty
                                };
                                _cacheableOpeningCount++;
                            }
                        }
                    }
                    else if (depth < 3)
                    {
                        for (int n = 0; n < neighborOffsets.Length; n++)
                        {
                            int nextX = cx + neighborOffsets[n][0];
                            int nextY = cy + neighborOffsets[n][1];
                            int nextZ = cz + neighborOffsets[n][2];
                            long nextPacked = PackBlockPos(nextX, nextY, nextZ);

                            if (!_probeVisited.Contains(nextPacked))
                            {
                                _probeSearchQueue.Enqueue((nextX, nextY, nextZ, depth + 1));
                                _probeVisited.Add(nextPacked);
                            }
                        }
                    }
                }
            }

            if (probeLogCount < 20 && openingsFound > 0 && config.DebugSoundPaths)
            {
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[4B-Probe] {openingsFound} openings (12 probes, dedup={_openingDedup.Count})");
            }
        }

        /// <summary>
        /// Calculate room reverb statistics at player's current position for display.
        /// Returns a formatted string with all relevant reverb metrics.
        /// </summary>
        public static string GetRoomStats(Vec3d playerPos, IBlockAccessor blockAccessor)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableCustomReverb)
                return "Reverb disabled";

            int numRays = config.ReverbRayCount;
            int bounces = config.ReverbBounces;
            float maxDistance = config.ReverbMaxDistance;

            float sendGain0 = 0f, sendGain1 = 0f, sendGain2 = 0f, sendGain3 = 0f;
            float rcpTotalRays = 1f / (numRays * bounces);

            // Pre-allocated, cleared
            if (_reusableBounceReflectivity.Length < bounces)
                _reusableBounceReflectivity = new float[bounces];
            else
                Array.Clear(_reusableBounceReflectivity, 0, bounces);
            float[] bounceReflectivity = _reusableBounceReflectivity;
            int raysHit = 0;
            int raysEscaped = 0;
            int totalBouncePoints = 0;
            int sharedAirspaceCount = 0;
            float avgReflectivity = 0f;
            float totalDistance = 0f;
            int distanceSamples = 0;

            float goldenAngle = PHI * MathF.PI * 2f;

            for (int i = 0; i < numRays; i++)
            {
                float fiN = (float)i / numRays;
                float longitude = goldenAngle * i;
                float latitude = MathF.Asin(fiN * 2f - 1f);

                Vec3d rayDir = new Vec3d(
                    Math.Cos(latitude) * Math.Cos(longitude),
                    Math.Sin(latitude),
                    Math.Cos(latitude) * Math.Sin(longitude)
                );

                var hit = RaycastToSurface(playerPos, rayDir, maxDistance, blockAccessor);
                if (!hit.HasValue)
                {
                    raysEscaped++;
                    continue;
                }
                raysHit++;

                float rayDistance = hit.Value.distance;
                totalDistance += rayDistance;
                distanceSamples++;

                Vec3d lastHitPos = hit.Value.position;
                Vec3d lastHitNormal = hit.Value.normal;
                Vec3d lastRayDir = rayDir;
                BlockPos lastHitBlock = hit.Value.blockPos;

                for (int bounce = 0; bounce < bounces; bounce++)
                {
                    totalBouncePoints++;

                    float reflectivity = GetBlockReflectivity(hit.Value.block, lastHitBlock, lastHitNormal, blockAccessor);
                    bounceReflectivity[bounce] += reflectivity;
                    avgReflectivity += reflectivity;

                    bool hasAirspace = HasSharedAirspace(lastHitPos, lastHitNormal, playerPos, blockAccessor);
                    // GetRoomStats: player is both source and listener, so direct airspace
                    // is always true. Use full weight for LOS hits, partial for failures.
                    float airspaceWeight = hasAirspace ? 1.0f : 0.5f;
                    if (hasAirspace) sharedAirspaceCount++;

                    {
                        float energyTowardsPlayer = 0.25f * (reflectivity * 0.75f + 0.25f);
                        // Slot selection is purely distance-based (matches CalculateWithPathsCacheable)
                        float reflectionDelay = rayDistance * 0.12f;

                        float cross0 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 0f), 0f, 1f);
                        float cross1 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 1f), 0f, 1f);
                        float cross2 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 2f), 0f, 1f);
                        float cross3 = Math.Clamp(reflectionDelay - 2f, 0f, 1f);

                        sendGain0 += cross0 * energyTowardsPlayer * airspaceWeight * 6.4f * rcpTotalRays;
                        sendGain1 += cross1 * energyTowardsPlayer * airspaceWeight * 12.8f * rcpTotalRays;
                        sendGain2 += cross2 * energyTowardsPlayer * airspaceWeight * 12.8f * rcpTotalRays;
                        sendGain3 += cross3 * energyTowardsPlayer * airspaceWeight * 12.8f * rcpTotalRays;
                    }

                    if (bounce < bounces - 1)
                    {
                        Vec3d newRayDir = Reflect(lastRayDir, lastHitNormal);
                        var nextHit = RaycastToSurface(lastHitPos, newRayDir, maxDistance, blockAccessor, lastHitBlock);
                        if (!nextHit.HasValue) break;

                        rayDistance += nextHit.Value.distance;
                        lastHitPos = nextHit.Value.position;
                        lastHitNormal = nextHit.Value.normal;
                        lastRayDir = newRayDir;
                        lastHitBlock = nextHit.Value.blockPos;
                        hit = nextHit;
                    }
                }
            }

            // Scale gains by bounce reflectivity
            for (int i = 0; i < bounces; i++)
                bounceReflectivity[i] /= numRays;

            if (bounces >= 2 && bounceReflectivity[1] > 0)
                sendGain1 *= bounceReflectivity[1];
            if (bounces >= 3 && bounceReflectivity[2] > 0)
                sendGain2 *= MathF.Pow(bounceReflectivity[2], 3f);
            if (bounces >= 4 && bounceReflectivity[3] > 0)
                sendGain3 *= MathF.Pow(bounceReflectivity[3], 4f);

            sendGain0 = Math.Clamp(sendGain0, 0f, 1f);
            sendGain1 = Math.Clamp(sendGain1, 0f, 1f);
            sendGain2 = Math.Clamp(sendGain2 * 1.05f - 0.05f, 0f, 1f);
            sendGain3 = Math.Clamp(sendGain3 * 1.05f - 0.05f, 0f, 1f);

            float totalRays = raysHit + raysEscaped;
            float enclosureFactor = totalRays > 0 ? (float)raysHit / totalRays : 0f;
            float sharedAirspaceRatio = totalBouncePoints > 0
                ? (float)sharedAirspaceCount / totalBouncePoints
                : 0f;
            avgReflectivity = totalBouncePoints > 0 ? avgReflectivity / totalBouncePoints : 0f;
            float avgDistance = distanceSamples > 0 ? totalDistance / distanceSamples : 0f;

            float totalReverb = sendGain0 + sendGain1 + sendGain2 + sendGain3;

            return $"Room Reverb Stats:\n" +
                   $"  Total Reverb: {totalReverb:F2}\n" +
                   $"  Send Gains: [{sendGain0:F2}, {sendGain1:F2}, {sendGain2:F2}, {sendGain3:F2}]\n" +
                   $"  Enclosure: {enclosureFactor:P0} ({raysHit}/{(int)totalRays} rays hit)\n" +
                   $"  Audible Reflections: {sharedAirspaceRatio:P0} ({sharedAirspaceCount}/{totalBouncePoints})\n" +
                   $"  Avg Reflectivity: {avgReflectivity:F2}\n" +
                   $"  Avg Distance: {avgDistance:F1} blocks";
        }

        /// <summary>
        /// Get material reflectivity for reverb, with decor awareness.
        /// If the hit face has a decor block (carpet, moss, rug), the decor's
        /// reflectivity fully overrides the base block — simulating sound absorption
        /// on the carpeted face while other faces remain reflective.
        /// </summary>
        private static float GetBlockReflectivity(Block block, BlockPos hitBlockPos, Vec3d hitNormal, IBlockAccessor blockAccessor)
        {
            if (block == null) return 0.5f;

            var materialConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;
            float baseReflectivity;
            if (materialConfig != null)
            {
                baseReflectivity = materialConfig.GetReflectivity(block);
            }
            else
            {
                // Fallback based on material type
                baseReflectivity = block.BlockMaterial switch
                {
                    EnumBlockMaterial.Stone => 1.5f,
                    EnumBlockMaterial.Ore => 1.5f,
                    EnumBlockMaterial.Metal => 1.25f,
                    EnumBlockMaterial.Brick => 1.3f,
                    EnumBlockMaterial.Ceramic => 1.1f,
                    EnumBlockMaterial.Ice => 0.9f,
                    EnumBlockMaterial.Glass => 0.75f,
                    EnumBlockMaterial.Wood => 0.4f,
                    EnumBlockMaterial.Soil => 0.6f,
                    EnumBlockMaterial.Gravel => 0.5f,
                    EnumBlockMaterial.Sand => 0.35f,
                    EnumBlockMaterial.Cloth => 0.1f,
                    EnumBlockMaterial.Snow => 0.15f,
                    EnumBlockMaterial.Leaves => 0.2f,
                    EnumBlockMaterial.Plant => 0.1f,
                    _ => 0.5f
                };
            }

            // Check for decor (carpet, moss, rug) on the hit face.
            // The ray normal tells us which face was hit — the decor on that face
            // overrides reflectivity (e.g. carpet on stone floor absorbs from above).
            int faceIndex = NormalToFaceIndex(hitNormal);
            if (faceIndex >= 0 && hitBlockPos != null)
            {
                _decorCheckPos.Set(hitBlockPos.X, hitBlockPos.Y, hitBlockPos.Z);
                Block decor = blockAccessor.GetDecor(_decorCheckPos, faceIndex);
                if (decor != null && decor.Id != 0)
                {
                    // Decor fully overrides reflectivity on this face
                    float decorReflectivity = materialConfig != null
                        ? materialConfig.GetReflectivity(decor)
                        : 0.1f; // Fallback: most decors are soft
                    return decorReflectivity;
                }
            }

            return baseReflectivity;
        }

        /// <summary>
        /// Convert a DDA hit normal to VS BlockFacing index.
        /// Returns -1 if normal is zero/invalid.
        /// BlockFacing indices: N=0, E=1, S=2, W=3, UP=4, DOWN=5
        /// </summary>
        private static int NormalToFaceIndex(Vec3d normal)
        {
            if (normal == null) return -1;

            // The DDA normal is always axis-aligned (one component is ±1, others 0)
            double absX = Math.Abs(normal.X);
            double absY = Math.Abs(normal.Y);
            double absZ = Math.Abs(normal.Z);

            if (absY >= absX && absY >= absZ)
            {
                return normal.Y > 0 ? BlockFacing.indexUP : BlockFacing.indexDOWN;
            }
            if (absX >= absZ)
            {
                return normal.X > 0 ? BlockFacing.indexEAST : BlockFacing.indexWEST;
            }
            return normal.Z > 0 ? BlockFacing.indexSOUTH : BlockFacing.indexNORTH;
        }

        /// <summary>
        /// Reflect a ray direction off a surface normal.
        /// </summary>
        private static Vec3d Reflect(Vec3d dir, Vec3d normal)
        {
            // reflection = dir - 2 * dot(dir, normal) * normal
            double dot = dir.Dot(normal) * 2.0;
            return new Vec3d(
                dir.X - dot * normal.X,
                dir.Y - dot * normal.Y,
                dir.Z - dot * normal.Z
            );
        }

        /// <summary>
        /// PHASE 4: Check if a bounce point has shared airspace (line-of-sight) to player.
        /// Uses surface normal to offset the test start position to avoid self-intersection.
        /// Now uses DDA grid traversal for accurate block-perfect LOS checks.
        /// </summary>
        private static bool HasSharedAirspace(Vec3d bouncePos, Vec3d surfaceNormal, Vec3d playerPos, IBlockAccessor blockAccessor)
        {
            // Offset slightly from surface along normal to avoid self-intersection
            // DDA is exact so we can use a tighter offset than the old stepped raycast (was 0.15)
            Vec3d testStart = new Vec3d(
                bouncePos.X + surfaceNormal.X * 0.05,
                bouncePos.Y + surfaceNormal.Y * 0.05,
                bouncePos.Z + surfaceNormal.Z * 0.05
            );

            return HasSharedAirspace(testStart, playerPos, blockAccessor);
        }

        /// <summary>
        /// PHASE 4: Check if a position has shared airspace (line-of-sight) to player.
        /// Uses DDA grid traversal — guaranteed to check every block on the path.
        /// </summary>
        private static bool HasSharedAirspace(Vec3d sourcePos, Vec3d playerPos, IBlockAccessor blockAccessor)
        {
            double dx = playerPos.X - sourcePos.X;
            double dy = playerPos.Y - sourcePos.Y;
            double dz = playerPos.Z - sourcePos.Z;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (distance < 0.1) return true; // Already at player

            return DDABlockTraversal.HasClearPath(sourcePos, playerPos, blockAccessor, _reverbBlockFilter);
        }

        /// <summary>
        /// Raycast to find first solid surface using DDA grid traversal.
        /// Guarantees visiting every block the ray passes through — no blocks are ever skipped.
        /// Returns exact entry position and face normal derived from DDA step axis.
        ///
        /// Replaces the old 0.5-step interval sampling which could miss thin blocks
        /// and produced imprecise normals from position guessing.
        /// </summary>
        private static (Block block, float distance, Vec3d position, Vec3d normal, BlockPos blockPos)?
            RaycastToSurface(Vec3d origin, Vec3d direction, float maxDistance, IBlockAccessor blockAccessor, BlockPos excludeBlock = null)
        {
            var hit = DDABlockTraversal.FindFirstBlock(origin, direction, maxDistance,
                blockAccessor, _reverbBlockFilter, excludeBlock);

            if (!hit.HasValue)
                return null;

            var h = hit.Value;
            return (h.Block, h.Distance, h.Position, h.Normal, h.BlockPos);
        }
    }
}
