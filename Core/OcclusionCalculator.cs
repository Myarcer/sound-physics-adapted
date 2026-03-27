using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using System;
using System.Text;

namespace soundphysicsadapted
{
    /// <summary>
    /// Core occlusion calculation engine
    /// Algorithm adapted from Sound Physics Remastered (GPL)
    ///
    /// Main approach:
    /// 1. Raycast from sound position to player ears using CONVERGENT rays:
    ///    - Center ray: direct line from sound to player
    ///    - Offset rays: start from positions around the sound, ALL converge to player
    ///    - This prevents false occlusion from adjacent blocks at short distances
    /// 2. For each block intersection, accumulate occlusion value
    /// 3. Use voting system: center ray authoritative, offset rays detect thin walls
    /// </summary>
    public static class OcclusionCalculator
    {
        // === Shared State for RunOcclusion Visitor ===
        // Alloc-free DDA traversal in single-threaded context
        private static float _vOcclusionAccumulation;
        private static int _vBlockHits;
        private static bool _vVerboseLog;
        private static StringBuilder _vDdaTrace;
        private static SoundPhysicsConfig _vConfig;
        private static IBlockAccessor _vBlockAccessor;
        private static BlockPos _vCollisionCheckPos = new BlockPos(0, 0, 0, 0);
        private static Vec3d _vFrom;
        private static double _vNdx;
        private static double _vNdy;
        private static double _vNdz;
        private static double _vLength;

        private static readonly DDABlockTraversal.BlockVisitor _runOcclusionVisitor = RunOcclusionVisitor;

        /// <summary>
        /// Clear the block occlusion cache. Call when config reloads or materials change.
        /// Delegates to shared BlockClassification caches.
        /// </summary>
        public static void ClearCache()
        {
            BlockClassification.ClearCache();
        }
        /// <summary>
        /// Calculate occlusion between sound source and player using multi-ray soft occlusion.
        /// Shoots up to 9 rays with offset positions to handle thin walls at perpendicular angles.
        /// Returns the MINIMUM occlusion found (sound travels through the least-blocked path).
        /// </summary>
        /// <param name="soundPos">World position of sound source</param>
        /// <param name="playerPos">World position of player ears</param>
        /// <param name="blockAccessor">Block accessor for world queries</param>
        /// <returns>Occlusion value (0 = no occlusion, MaxOcclusion = fully occluded)</returns>
        public static float Calculate(Vec3d soundPos, Vec3d playerPos, IBlockAccessor blockAccessor)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.Enabled)
                return 0f;

            double distance = soundPos.DistanceTo(playerPos);

            // Multi-ray occlusion with voting system:
            // - CENTER ray represents direct line of sight (authoritative)
            // - OFFSET rays detect thin walls that center might miss
            // - Use voting: need majority of rays to agree on occlusion

            float centerOcclusion = RunOcclusion(soundPos, playerPos, blockAccessor, config);

            // EARLY EXIT: If center ray hits max occlusion, no need for offset rays
            if (centerOcclusion >= config.MaxOcclusion)
            {
                if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"Occlusion calc: dist={distance:F1} center=MAX (early exit)");
                return config.MaxOcclusion;
            }

            // If center ray finds significant occlusion, trust it (direct line blocked)
            if (centerOcclusion >= 0.5f)
            {
                // Center is blocked - this is the primary occlusion value
                float result = Math.Min(centerOcclusion, config.MaxOcclusion);
                if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"Occlusion calc: dist={distance:F1} center={centerOcclusion:F2} (blocked)");
                return result;
            }

            // OPTIMIZATION: If center ray is very clear (< 0.3), skip offset rays entirely.
            // True clear LOS confirmed - offset rays would just add cost without benefit.
            // Only check offset rays in the ambiguous 0.3-0.5 range where thin walls matter.
            if (centerOcclusion < 0.3f)
            {
                int startBX = (int)Math.Floor(soundPos.X);
                int startBY = (int)Math.Floor(soundPos.Y);
                int startBZ = (int)Math.Floor(soundPos.Z);
                int endBX = (int)Math.Floor(playerPos.X);
                int endBY = (int)Math.Floor(playerPos.Y);
                int endBZ = (int)Math.Floor(playerPos.Z);
                if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"Occlusion calc: dist={distance:F1} center={centerOcclusion:F2} (clear, skip offset) DDA=({startBX},{startBY},{startBZ})->({endBX},{endBY},{endBZ})");
                return centerOcclusion;
            }

            // Center ray is ambiguous (0.3-0.5) - check offset rays for thin walls
            float variation = config.OcclusionVariation;
            if (variation <= 0f)
            {
                // No offset rays, center is clear
                if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"Occlusion calc: dist={distance:F1} center=clear (no variation)");
                return centerOcclusion;
            }

            // Run offset rays and count how many find significant occlusion
            int raysBlocked = 0;
            float totalOcclusion = centerOcclusion;
            int totalRays = 1; // center already counted
            float maxOffsetOcclusion = 0f;

            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        // Convergent rays: offset only the SOURCE, all rays converge to same player position
                        // This prevents offset rays from clipping adjacent blocks at short distances
                        Vec3d offset = new Vec3d(x * variation, y * variation, z * variation);
                        float rayOcclusion = RunOcclusion(
                            soundPos.AddCopy(offset),
                            playerPos,  // No offset - all rays converge to player ears
                            blockAccessor,
                            config
                        );

                        totalOcclusion += rayOcclusion;
                        totalRays++;

                        if (rayOcclusion >= 0.5f)
                        {
                            raysBlocked++;
                            maxOffsetOcclusion = Math.Max(maxOffsetOcclusion, rayOcclusion);
                        }

                        // EARLY EXIT: If we have enough votes (6+) AND high occlusion (95%+),
                        // we're clearly fully occluded - no need to check remaining rays
                        if (raysBlocked >= 6 && maxOffsetOcclusion >= config.MaxOcclusion * 0.95f)
                        {
                            if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                                    $"Occlusion calc: dist={distance:F1} early exit at {raysBlocked} votes, max={maxOffsetOcclusion:F2}");
                            return config.MaxOcclusion;
                        }
                    }
                }
            }

            // Voting threshold: need at least 6 of 8 offset rays blocked to override clear center
            // Was 4/8 which caused false positives: beehive with LOS but adjacent parallel walls
            // at ┬▒0.35m offset poked into neighboring blocks, triggering "thin wall detected"
            // when the actual direct path was completely clear.
            // 6/8 = supermajority: only triggers for actual thin walls in the path,
            // not nearby geometry parallel to the sound direction.
            if (raysBlocked >= 6)
            {
                // Majority of offset rays blocked = thin wall detected
                // Use average occlusion of all rays
                float avgOcclusion = totalOcclusion / totalRays;
                float result = Math.Min(avgOcclusion, config.MaxOcclusion);
                if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"Occlusion calc: dist={distance:F1} center=clear but {raysBlocked}/8 offset blocked, avg={avgOcclusion:F2}");
                return result;
            }

            // Center clear and not enough offset rays blocked = clear path
            if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"Occlusion calc: dist={distance:F1} center=clear, only {raysBlocked}/8 offset blocked");
            return centerOcclusion;
        }

        /// <summary>
        /// Calculate occlusion along an arbitrary path (for Phase 4B permeation).
        /// This is a simplified single-ray version used by sound path resolution.
        /// Reuses the DDA raycast algorithm used for direct occlusion.
        /// </summary>
        /// <param name="from">Start position (typically bounce point)</param>
        /// <param name="to">End position (typically player position)</param>
        /// <param name="blockAccessor">Block accessor for world queries</param>
        /// <returns>Occlusion value along path (0 = clear, higher = more blocked)</returns>
        public static float CalculatePathOcclusion(Vec3d from, Vec3d to, IBlockAccessor blockAccessor)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.Enabled)
                return 0f;

            // Simple single-ray calculation for path occlusion
            // No offset rays needed - we just want the direct path occlusion
            return RunOcclusion(from, to, blockAccessor, config);
        }

        /// <summary>
        /// [LEGACY] Fast path occlusion ÔÇö solid-face-only check, ignores partial blocks.
        /// Kept for reference. New code should use CalculateWeatherPathOcclusion instead.
        /// </summary>
        public static float CalculatePathOcclusionFast(Vec3d from, Vec3d to, IBlockAccessor blockAccessor)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.Enabled)
                return 0f;

            return RunOcclusionFullCubeOnly(from, to, blockAccessor, config, out _);
        }

        /// <summary>
        /// [LEGACY] Fast path occlusion with entry point ÔÇö solid-face-only check.
        /// Kept for reference. New code should use CalculateWeatherPathOcclusionWithEntry instead.
        /// </summary>
        public static float CalculatePathOcclusionFastWithEntry(Vec3d from, Vec3d to, IBlockAccessor blockAccessor, out Vec3d entryPoint)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.Enabled)
            {
                entryPoint = null;
                return 0f;
            }

            return RunOcclusionFullCubeOnly(from, to, blockAccessor, config, out entryPoint);
        }

        /// <summary>
        /// Weather-aware path occlusion. Handles both solid-face blocks AND partial
        /// blocks (doors, trapdoors, chiseled blocks) via AABB collision box checks.
        /// Returns total occlusion along the path.
        /// </summary>
        public static float CalculateWeatherPathOcclusion(Vec3d from, Vec3d to, IBlockAccessor blockAccessor)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.Enabled)
                return 0f;

            return RunWeatherOcclusion(from, to, blockAccessor, config, doorTransparent: false, out _, out _);
        }

        /// <summary>
        /// Weather-aware path occlusion with entry point tracking.
        /// Returns total occlusion along the path (door-transparent for spawn detection).
        /// doorInclOcclusion returns the same path's occlusion WITH closed doors counted,
        /// for Layer 1 ambient muffling decisions.
        /// </summary>
        public static float CalculateWeatherPathOcclusionWithEntry(
            Vec3d from, Vec3d to, IBlockAccessor blockAccessor,
            out Vec3d entryPoint, out float doorInclOcclusion)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.Enabled)
            {
                entryPoint = null;
                doorInclOcclusion = 0f;
                return 0f;
            }

            return RunWeatherOcclusion(from, to, blockAccessor, config, doorTransparent: true, out entryPoint, out doorInclOcclusion);
        }

        /// <summary>
        /// Run a single occlusion ray from sound to player using shared DDA grid traversal.
        /// Accumulates occlusion values for each block the ray passes through.
        /// Includes collision box ray-AABB intersection for partial blocks (doors, trapdoors).
        /// </summary>
        private static float RunOcclusion(Vec3d from, Vec3d to, IBlockAccessor blockAccessor, SoundPhysicsConfig config)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double dz = to.Z - from.Z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < 0.001) return 0f;

            // Normalize for collision box ray intersection tests
            double ndx = dx / length;
            double ndy = dy / length;
            double ndz = dz / length;

            // SOURCE BLOCK vs WALL DETECTION (step-back test):
            // When VS places a sound on a block face, floor(from) can land on the source
            // block (-X/-Y/-Z faces: floor(N.00)=N=source) or adjacent air (+X/+Y/+Z faces:
            // floor(N+1.00)=N+1=air). The old approach (skip if air, count if solid) caused
            // self-occlusion on 3 of 6 faces for block-hit sounds.
            //
            // Step-back test: move 0.1 blocks AWAY from the listener (reverse ray direction).
            // If floor() gives the same block → sound is inside/on surface of source → skip it.
            // If floor() gives a different block → we crossed a wall boundary → count it.
            //
            // This correctly handles both cases:
            // - Block hit sounds: step back goes deeper INTO source block → same floor → skip
            // - Wall sounds (beehive behind wall): step back goes into air/source → different floor → count wall
            int startX = (int)Math.Floor(from.X);
            int startY = (int)Math.Floor(from.Y);
            int startZ = (int)Math.Floor(from.Z);
            int steppedX = (int)Math.Floor(from.X - ndx * 0.1);
            int steppedY = (int)Math.Floor(from.Y - ndy * 0.1);
            int steppedZ = (int)Math.Floor(from.Z - ndz * 0.1);
            bool skipFirstBlock = (startX == steppedX && startY == steppedY && startZ == steppedZ);

            // Batch verbose DDA logs into a single StringBuilder to reduce
            // synchronous I/O from ~10 Logger.Debug() calls per ray to 1.
            bool verboseLog = SoundPhysicsAdaptedModSystem.IsVerboseDebugEnabled;
            StringBuilder ddaTrace = verboseLog ? new StringBuilder(512) : null;

            _vOcclusionAccumulation = 0f;
            _vBlockHits = 0;
            _vVerboseLog = verboseLog;
            _vDdaTrace = ddaTrace;
            _vConfig = config;
            _vBlockAccessor = blockAccessor;
            _vFrom = from;
            _vNdx = ndx;
            _vNdy = ndy;
            _vNdz = ndz;
            _vLength = length;

            int maxDDASteps = config.MaxDDASteps;
            bool stopped = DDABlockTraversal.Traverse(from, to, blockAccessor, _runOcclusionVisitor, skipFirst: skipFirstBlock, maxSteps: maxDDASteps);
            
            float occlusionAccumulation = _vOcclusionAccumulation;

            // Flush entire DDA trace as single log entry
            SoundPhysicsAdaptedModSystem.VerboseDebugBatch(ddaTrace);

            return stopped ? config.MaxOcclusion : occlusionAccumulation;
        }

        private static bool RunOcclusionVisitor(ref DDABlockTraversal.TraversalContext ctx)
        {
            Block block = ctx.Block;

            if (block == null || block.Id == 0 || block.BlockMaterial == EnumBlockMaterial.Air)
                return false; // Continue

            float blockOcclusion = 0f;

            // OPEN DOOR/GATE CHECK: must run BEFORE solid-face fast path.
            // ME gate spacers have solid faces that would take the fast path and occlude.
            // Multiblock door spacers also need controller resolution here.
            if (IsOpenDoorOrGate(block, _vBlockAccessor, ctx.X, ctx.Y, ctx.Z))
            {
                if (_vVerboseLog) _vDdaTrace.Append($"  DDA door-open: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) (open, pass-through)\n");
                return false; // Continue — treat as air
            }

            // FAST PATH (95%+ of blocks): Solid-face check is purely cached array lookups.
            // Runs FIRST to skip all expensive checks below for stone, dirt, wood, etc.
            if (BlockClassification.IsSolidForOcclusion(block))
            {
                blockOcclusion = BlockClassification.GetBlockOcclusion(block, _vConfig);
            }
            else if (BlockClassification.IsLiquidMaterial(block))
            {
                // LIQUID/LAVA PATH: No collision boxes but still muffles sound.
                blockOcclusion = BlockClassification.GetBlockOcclusion(block, _vConfig);
                if (blockOcclusion > 0 && _vVerboseLog)
                {
                    _vDdaTrace.Append($"  DDA liquid: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) occ={blockOcclusion:F2}\n");
                }
            }
            else
            {
                // NON-SOLID BLOCK PATH: Only ~5% of DDA-visited blocks reach here.
                // Doors, fences, trapdoors, foliage, etc.
                // No special-casing — AABB collision geometry determines occlusion.

                // PARTIAL BLOCK PATH: fences, doors, trapdoors, chiseled blocks, etc.
                // Check if ray actually intersects the block's collision geometry.
                _vCollisionCheckPos.Set(ctx.X, ctx.Y, ctx.Z);
                var collisionBoxes = block.GetCollisionBoxes(_vBlockAccessor, _vCollisionCheckPos);
                if (collisionBoxes == null || collisionBoxes.Length == 0)
                {
                    // No collision geometry — foliage path.
                    // Blocks you walk through: leaves, flowers, grass, paintings, etc.
                    // Material/override occlusion scaled by selection box volume so
                    // small decorative items auto-reduce without needing overrides.
                    blockOcclusion = BlockClassification.GetBlockOcclusion(block, _vConfig);
                    if (blockOcclusion > 0)
                    {
                        blockOcclusion *= GetFoliageVolumeScale(block);
                        if (_vVerboseLog)
                        {
                            _vDdaTrace.Append($"  DDA foliage: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) occ={blockOcclusion:F3}\n");
                        }
                    }
                }
                else
                {
                    // Check if this is a door/gate with a configured override.
                    // Thin door panels (~0.12 block) cause RayHitsAnyCollisionBox to miss
                    // ~22% of rays at oblique angles. For override blocks, skip the AABB
                    // test entirely and apply the override directly — but only when closed.
                    var matConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;
                    bool hasOverride = matConfig != null && matConfig.HasBlockOverride(block);

                    if (hasOverride)
                    {
                        if (IsDoorOpen(block, _vBlockAccessor, ctx.X, ctx.Y, ctx.Z))
                        {
                            // Door/trapdoor is open — treat as air, zero occlusion
                            if (_vVerboseLog) _vDdaTrace.Append($"  DDA door-open: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) (open, pass-through)\n");
                        }
                        else
                        {
                            // Closed door/gate — apply override directly, skip AABB
                            blockOcclusion = BlockClassification.GetBlockOcclusion(block, _vConfig);
                            if (_vVerboseLog) _vDdaTrace.Append($"  DDA door-closed: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) occ={blockOcclusion:F2} (override, skip AABB)\n");
                        }
                    }
                    else if (!RayHitsAnyCollisionBox(_vFrom, _vNdx, _vNdy, _vNdz, _vLength, ctx.X, ctx.Y, ctx.Z, collisionBoxes))
                    {
                        // Ray misses collision geometry (e.g. fence post gap)
                        if (_vVerboseLog) _vDdaTrace.Append($"  DDA pass-through: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) (ray misses geometry)\n");
                    }
                    else
                    {
                        blockOcclusion = BlockClassification.GetBlockOcclusion(block, _vConfig);
                        blockOcclusion *= GetPartialBlockVolumeScale(block, _vBlockAccessor, ctx.X, ctx.Y, ctx.Z, collisionBoxes);
                    }
                }
            }

            if (blockOcclusion > 0)
            {
                _vOcclusionAccumulation += blockOcclusion;
                _vBlockHits++;

                if (_vVerboseLog)
                    _vDdaTrace.Append($"  DDA hit: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) occ={blockOcclusion:F2} total={_vOcclusionAccumulation:F2}\n");

                if (_vOcclusionAccumulation >= _vConfig.MaxOcclusion)
                {
                    if (_vVerboseLog) _vDdaTrace.Append($"  Max occlusion reached after {_vBlockHits} blocks\n");
                    return true; // Stop traversal
                }
            }

            return false; // Continue
        }

        /// <summary>
        /// [LEGACY] Fast DDA for weather: solid-face-only check, ignores partial blocks.
        /// Doors, trapdoors, fences, chiseled blocks are invisible to this method.
        /// Kept for reference ÔÇö new code should use RunWeatherOcclusion instead.
        /// </summary>
        private static float RunOcclusionFullCubeOnly(Vec3d from, Vec3d to, IBlockAccessor blockAccessor, SoundPhysicsConfig config, out Vec3d entryPoint)
        {
            float occlusionAccumulation = 0f;
            bool previousBlockWasSolid = false;
            int entryX = 0, entryY = 0, entryZ = 0;
            bool hasEntryPoint = false;

            int maxDDASteps = config.MaxDDASteps;
            bool stopped = DDABlockTraversal.Traverse(from, to, blockAccessor, (ref DDABlockTraversal.TraversalContext ctx) =>
            {
                Block block = ctx.Block;

                if (block != null && block.Id != 0 &&
                    block.BlockMaterial != EnumBlockMaterial.Air &&
                    BlockClassification.IsSolidForOcclusion(block))
                {
                    occlusionAccumulation += BlockClassification.GetBlockOcclusion(block, config);
                    previousBlockWasSolid = true;

                    if (occlusionAccumulation >= config.MaxOcclusion)
                        return true; // Stop
                }
                else
                {
                    if (previousBlockWasSolid)
                    {
                        // Solid-to-air transition: record where sound enters player's space
                        entryX = ctx.X; entryY = ctx.Y; entryZ = ctx.Z;
                        hasEntryPoint = true;
                    }
                    previousBlockWasSolid = false;
                }

                return false; // Continue
            }, skipFirst: true, maxSteps: maxDDASteps);

            entryPoint = hasEntryPoint ? new Vec3d(entryX + 0.5, entryY + 0.5, entryZ + 0.5) : null;
            return stopped ? config.MaxOcclusion : occlusionAccumulation;
        }

        /// <summary>
        /// Weather-aware DDA: hybrid fast-path with AABB collision checks for partial blocks.
        /// 
        /// Single accumulator — all blocks (walls, doors, fences, etc.) contribute equally.
        /// No special door handling — AABB collision geometry determines occlusion naturally.
        /// Open doors have no collision boxes → zero occlusion. Closed doors hit AABB → occlude.
        /// 
        /// Processing order per block:
        /// 1. Solid-face blocks (full cubes, slabs, stairs): direct occlusion.
        /// 2. Liquids: direct occlusion.
        /// 3. Partial blocks (doors, fences, chiseled): AABB ray intersection check.
        /// 4. Air/plants with no collision: non-occluding.
        /// 
        /// No verbose debug logging — weather casts hundreds of rays per tick.
        /// Tracks last occluding-to-air transition for entry point detection.
        /// </summary>
        private static float RunWeatherOcclusion(Vec3d from, Vec3d to, IBlockAccessor blockAccessor, SoundPhysicsConfig config, bool doorTransparent, out Vec3d entryPoint, out float doorInclOcclusion)
        {
            doorInclOcclusion = 0f;

            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double dz = to.Z - from.Z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < 0.001)
            {
                entryPoint = null;
                return 0f;
            }

            // Normalize for collision box ray intersection tests
            double ndx = dx / length;
            double ndy = dy / length;
            double ndz = dz / length;

            float occlusionAccum = 0f;
            // Dual accumulator: tracks occlusion including closed doors for Layer 1.
            // When doorTransparent=true, the main accumulator skips ALL doors (for spawn detection),
            // but doorInclAccum counts closed doors (for Layer 1 ambient muffling).
            float doorInclAccum = 0f;
            bool previousBlockWasOccluding = false;
            int entryX = 0, entryY = 0, entryZ = 0;
            bool hasEntryPoint = false;

            // Reusable BlockPos for collision box lookups (avoids allocation per block)
            BlockPos collisionCheckPos = new BlockPos(0, 0, 0, 0);

            // Step-back test (same as RunOcclusion): weather rays typically start in air
            // (rainH+1.01), so this skips the air block. But if the start position happens
            // to be AT a wall block, the step-back crosses a boundary → skipFirst=false →
            // wall gets counted instead of silently skipped.
            int startX = (int)Math.Floor(from.X);
            int startY = (int)Math.Floor(from.Y);
            int startZ = (int)Math.Floor(from.Z);
            int steppedX = (int)Math.Floor(from.X - ndx * 0.1);
            int steppedY = (int)Math.Floor(from.Y - ndy * 0.1);
            int steppedZ = (int)Math.Floor(from.Z - ndz * 0.1);
            bool skipFirstBlock = (startX == steppedX && startY == steppedY && startZ == steppedZ);

            // Destination block skip: all weather rays converge on playerEarPos.
            // Sub-blocks at the player's feet (ladders, half-slabs) have collision
            // volume >= 0.15 and would occlude EVERY ray, spiking OcclusionFactor.
            // The block the player is standing inside shouldn't occlude sound TO them.
            int destX = (int)Math.Floor(to.X);
            int destY = (int)Math.Floor(to.Y);
            int destZ = (int)Math.Floor(to.Z);

            int maxDDASteps = config.MaxDDASteps;
            bool stopped = DDABlockTraversal.Traverse(from, to, blockAccessor, (ref DDABlockTraversal.TraversalContext ctx) =>
            {
                Block block = ctx.Block;

                if (block == null || block.Id == 0 || block.BlockMaterial == EnumBlockMaterial.Air)
                {
                    // Air — track transition
                    if (previousBlockWasOccluding)
                    {
                        entryX = ctx.X; entryY = ctx.Y; entryZ = ctx.Z;
                        hasEntryPoint = true;
                    }
                    previousBlockWasOccluding = false;
                    return false;
                }

                // Skip destination block only for fully-solid blocks (player inside a wall).
                // Partial geometry (half-slabs, ladders) falls through to AABB intersection
                // below, which correctly determines if the ray actually passes through the
                // solid geometry. This fixes: player entering a wall half-slab block →
                // ray from outside correctly accumulates slab occlusion instead of bypassing it.
                if (ctx.X == destX && ctx.Y == destY && ctx.Z == destZ
                    && BlockClassification.IsSolidForOcclusion(block))
                    return false;

                float blockOcclusion = 0f;

                // DOOR/GATE CHECK (spawn path only): doors are weather-transparent
                // for 5B SPAWNING so rain sources appear behind closed doors.
                // Layer 1 stereo weather skips this — doors must occlude normally there.
                // Must run BEFORE solid-face fast path — ME gate spacers have solid faces.
                if (doorTransparent && IsDoorOrGateBlock(block, blockAccessor, ctx.X, ctx.Y, ctx.Z))
                {
                    // Dual accumulator: closed doors count for Layer 1 muffling
                    if (!IsDoorOpen(block, blockAccessor, ctx.X, ctx.Y, ctx.Z))
                    {
                        float doorOcc = BlockClassification.GetBlockOcclusion(block, config);
                        if (doorOcc <= 0)
                            doorOcc = 0.8f; // Fallback for doors without material config
                        doorInclAccum += doorOcc;
                    }

                    if (previousBlockWasOccluding)
                    {
                        entryX = ctx.X; entryY = ctx.Y; entryZ = ctx.Z;
                        hasEntryPoint = true;
                    }
                    previousBlockWasOccluding = false;
                    return false; // Continue — treat as air for weather spawning
                }

                // FAST PATH (95%+ of blocks): Solid-face check is purely cached array lookups.
                // Runs FIRST to skip all expensive checks for stone, dirt, wood, etc.
                if (BlockClassification.IsSolidForOcclusion(block))
                {
                    blockOcclusion = BlockClassification.GetBlockOcclusion(block, config);
                }
                else if (BlockClassification.IsLiquidMaterial(block))
                {
                    // Liquids muffle sound without collision geometry
                    blockOcclusion = BlockClassification.GetBlockOcclusion(block, config);
                }
                else
                {
                    // NON-SOLID BLOCK PATH: Only ~5% of DDA-visited blocks reach here.
                    // Doors, fences, trapdoors, chiseled blocks, foliage, etc.
                    // No special-casing — AABB collision geometry determines occlusion.

                    collisionCheckPos.Set(ctx.X, ctx.Y, ctx.Z);
                    var collisionBoxes = block.GetCollisionBoxes(blockAccessor, collisionCheckPos);

                    if (collisionBoxes != null && collisionBoxes.Length > 0)
                    {
                        // Door/gate override: check BEFORE volume filter — thin door panels
                        // (vol ~0.12) would be rejected by the 0.15 threshold below.
                        var matConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;
                        bool hasOverride = matConfig != null && matConfig.HasBlockOverride(block);

                        if (!hasOverride)
                        {
                            // Check max cross-sectional face area — skip tiny blocks (beams, rails, etc.)
                            // Uses face area instead of volume so thin-but-solid panels (glass panes,
                            // slabs) aren't rejected. A pane has face area 1.0 but volume 0.12.
                            float maxFace = 0f;
                            for (int cb = 0; cb < collisionBoxes.Length; cb++)
                            {
                                var box = collisionBoxes[cb];
                                float sx = box.X2 - box.X1;
                                float sy = box.Y2 - box.Y1;
                                float sz = box.Z2 - box.Z1;
                                float fXY = sx * sy, fXZ = sx * sz, fYZ = sy * sz;
                                float best = fXY > fXZ ? fXY : fXZ;
                                if (fYZ > best) best = fYZ;
                                if (best > maxFace) maxFace = best;
                            }

                            if (maxFace < 0.15f)
                            {
                                // Tiny cross-section — weather-transparent (beams, pillars, rails)
                                if (previousBlockWasOccluding)
                                {
                                    entryX = ctx.X; entryY = ctx.Y; entryZ = ctx.Z;
                                    hasEntryPoint = true;
                                }
                                previousBlockWasOccluding = false;
                                return false;
                            }
                        }

                        if (hasOverride && doorTransparent)
                        {
                            // 5B spawn path only: doors/gates/trapdoors are weather-transparent
                            // so rain sources spawn behind closed doors. AudioPhysicsSystem
                            // handles the actual sound occlusion independently.
                            // Dual accumulator: closed doors count for Layer 1 muffling
                            if (!IsDoorOpen(block, blockAccessor, ctx.X, ctx.Y, ctx.Z))
                            {
                                float doorOcc = BlockClassification.GetBlockOcclusion(block, config);
                                if (doorOcc <= 0)
                                    doorOcc = 0.8f;
                                doorInclAccum += doorOcc;
                            }

                            if (previousBlockWasOccluding)
                            {
                                entryX = ctx.X; entryY = ctx.Y; entryZ = ctx.Z;
                                hasEntryPoint = true;
                            }
                            previousBlockWasOccluding = false;
                            return false;
                        }
                        else if (hasOverride)
                        {
                            // Layer 1 path: override blocks apply their configured occlusion
                            // directly, skip AABB ray test (same as sound DDA door-closed path).
                            blockOcclusion = BlockClassification.GetBlockOcclusion(block, config);
                        }
                        else if (!RayHitsAnyCollisionBox(from, ndx, ndy, ndz, length, ctx.X, ctx.Y, ctx.Z, collisionBoxes))
                        {
                            // Ray misses geometry — pass through empty part of sub-block
                            if (previousBlockWasOccluding)
                            {
                                entryX = ctx.X; entryY = ctx.Y; entryZ = ctx.Z;
                                hasEntryPoint = true;
                            }
                            previousBlockWasOccluding = false;
                            return false;
                        }
                        else
                        {
                            blockOcclusion = BlockClassification.GetBlockOcclusion(block, config);
                        }
                    }
                    else
                    {
                        // No collision geometry — foliage, decorative items, open doors.
                        // Weather-transparent.
                        if (previousBlockWasOccluding)
                        {
                            entryX = ctx.X; entryY = ctx.Y; entryZ = ctx.Z;
                            hasEntryPoint = true;
                        }
                        previousBlockWasOccluding = false;
                        return false;
                    }
                }

                if (blockOcclusion > 0)
                {
                    occlusionAccum += blockOcclusion;
                    previousBlockWasOccluding = true;

                    if (occlusionAccum >= config.MaxOcclusion)
                        return true; // Stop
                }
                else
                {
                    if (previousBlockWasOccluding)
                    {
                        entryX = ctx.X; entryY = ctx.Y; entryZ = ctx.Z;
                        hasEntryPoint = true;
                    }
                    previousBlockWasOccluding = false;
                }

                return false; // Continue
            }, skipFirst: skipFirstBlock, maxSteps: maxDDASteps);

            entryPoint = hasEntryPoint ? new Vec3d(entryX + 0.5, entryY + 0.5, entryZ + 0.5) : null;
            float mainResult = stopped ? config.MaxOcclusion : occlusionAccum;
            doorInclOcclusion = Math.Min(mainResult + doorInclAccum, config.MaxOcclusion);
            return mainResult;
        }

        /// <summary>
        /// Get a volume-based occlusion scale for no-collision (foliage) blocks.
        /// Uses selection box volume as a proxy for physical presence.
        /// Returns fillRatio directly (not sqrt) — walkthrough blocks occlude
        /// proportional to their fill, not structural presence.
        /// </summary>
        private static float GetFoliageVolumeScale(Block block)
        {
            var selBoxes = block.SelectionBoxes;
            if (selBoxes == null || selBoxes.Length == 0)
                return 0f; // No geometry at all — fully transparent

            float totalVol = 0f;
            for (int i = 0; i < selBoxes.Length; i++)
            {
                var b = selBoxes[i];
                totalVol += (b.X2 - b.X1) * (b.Y2 - b.Y1) * (b.Z2 - b.Z1);
            }
            return Math.Min(totalVol, 1f);
        }

        /// <summary>
        /// Get a volume-based occlusion scale factor for partial/chiseled blocks.
        /// Chiseled blocks: uses VolumeRel from BlockEntityMicroBlock (pre-computed voxel ratio).
        /// Other AABB blocks: estimates fill from collision box volumes.
        /// Uses max cross-sectional face area for regular blocks (thin-but-solid panels
        /// like glass panes occlude fully, while lattice/fence geometry scales down).
        /// For chiseled blocks, uses sideAlmostSolid (any face ≥87.5% filled → full scale,
        /// otherwise falls back to VolumeRel for lattice shapes).
        /// Returns sqrt(fillRatio) clamped to [0.1, 1.0].
        /// </summary>
        private static float GetPartialBlockVolumeScale(Block block, IBlockAccessor blockAccessor,
            int x, int y, int z, Cuboidf[] collisionBoxes)
        {
            float fillRatio = 1f;

            if (BlockClassification.IsChiseledBlock(block))
            {
                var be = blockAccessor.GetBlockEntity(new BlockPos(x, y, z, 0)) as BlockEntityMicroBlock;
                if (be != null)
                {
                    // If any face is almost solid, this is a wall/slab shape — full occlusion scale
                    bool anySolid = false;
                    for (int i = 0; i < 6; i++)
                    {
                        if (be.sideAlmostSolid[i]) { anySolid = true; break; }
                    }
                    fillRatio = anySolid ? 1f : be.VolumeRel;
                }
            }
            else if (collisionBoxes != null && collisionBoxes.Length > 0)
            {
                // Use max cross-sectional face area across all collision boxes.
                // A thin panel spanning the full block face (1×1) gets scale ~1.0,
                // while a fence post (0.25×0.25) correctly scales down.
                float maxFaceArea = 0f;
                for (int i = 0; i < collisionBoxes.Length; i++)
                {
                    var box = collisionBoxes[i];
                    float sx = box.X2 - box.X1;
                    float sy = box.Y2 - box.Y1;
                    float sz = box.Z2 - box.Z1;
                    float faceXY = sx * sy;
                    float faceXZ = sx * sz;
                    float faceYZ = sy * sz;
                    float best = faceXY > faceXZ ? faceXY : faceXZ;
                    if (faceYZ > best) best = faceYZ;
                    if (best > maxFaceArea) maxFaceArea = best;
                }
                fillRatio = Math.Min(maxFaceArea, 1f);
            }

            float scale = (float)Math.Sqrt(fillRatio);
            return Math.Max(scale, 0.1f);
        }

        /// <summary>
        /// Check if a door/trapdoor/gate at this position is currently open.
        /// First checks BEBehaviorDoor / BEBehaviorTrapDoor (vanilla doors).
        /// Falls back to block code path check for "opened" / "-open-" which
        /// handles Medieval Expansion gates and other mods that encode state
        /// in block code variants rather than BlockEntity behaviors.
        /// </summary>
        private static bool IsDoorOpen(Block block, IBlockAccessor blockAccessor, int x, int y, int z)
        {
            var be = blockAccessor.GetBlockEntity(new BlockPos(x, y, z, 0));
            if (be != null)
            {
                var doorBeh = be.GetBehavior<BEBehaviorDoor>();
                if (doorBeh != null) return doorBeh.Opened;

                var trapBeh = be.GetBehavior<BEBehaviorTrapDoor>();
                if (trapBeh != null) return trapBeh.Opened;
            }

            // Fallback: check block code for open state (ME gates, modded doors)
            string path = block.Code?.Path;
            if (path != null && (path.Contains("opened") || path.Contains("-open-")))
                return true;

            return false;
        }

        // Pooled BlockPos for multiblock controller lookup in DDA (avoid alloc per call)
        private static readonly BlockPos _mbControllerPos = new BlockPos(0, 0, 0, 0);

        /// <summary>
        /// Check if a block should be treated as air because it's an open door/gate.
        /// Handles three cases:
        /// 1. Direct door/gate blocks with HasBlockOverride + IsDoorOpen (vanilla + ME)
        /// 2. ME gate spacer blocks with solid faces that bypass the non-solid path
        /// 3. Multiblock spacers whose controller is an open door/gate
        /// Must run BEFORE IsSolidForOcclusion — ME gate spacers have solid faces.
        /// </summary>
        private static bool IsOpenDoorOrGate(Block block, IBlockAccessor blockAccessor, int x, int y, int z)
        {
            var matConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;

            // Check if this block itself is an open door/gate with a config override
            if (matConfig != null && matConfig.HasBlockOverride(block))
            {
                if (IsDoorOpen(block, blockAccessor, x, y, z))
                    return true;
            }

            // Check if this is a multiblock spacer whose controller is an open door/gate
            string path = block.Code?.Path;
            if (path != null && path.StartsWith("multiblock-", StringComparison.Ordinal))
            {
                var variant = block.Variant;
                if (variant != null &&
                    variant.TryGetValue("dx", out string dxStr) &&
                    variant.TryGetValue("dy", out string dyStr) &&
                    variant.TryGetValue("dz", out string dzStr))
                {
                    int cdx = ParseVariantOffset(dxStr);
                    int cdy = ParseVariantOffset(dyStr);
                    int cdz = ParseVariantOffset(dzStr);

                    _mbControllerPos.Set(x - cdx, y - cdy, z - cdz);
                    Block controller = blockAccessor.GetBlock(_mbControllerPos);

                    if (controller != null && controller.Id != 0 &&
                        !(controller.Code?.Path?.StartsWith("multiblock-", StringComparison.Ordinal) == true))
                    {
                        if (matConfig != null && matConfig.HasBlockOverride(controller))
                        {
                            if (IsDoorOpen(controller, blockAccessor,
                                _mbControllerPos.X, _mbControllerPos.InternalY, _mbControllerPos.Z))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Check if a block is a door/gate/trapdoor (open OR closed).
        /// Used by weather DDA to make ALL doors weather-transparent for spawning,
        /// regardless of open/closed state. The 5B tracking system handles sound
        /// occlusion independently via AudioPhysicsSystem.
        /// </summary>
        private static bool IsDoorOrGateBlock(Block block, IBlockAccessor blockAccessor, int x, int y, int z)
        {
            var matConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;

            // Check if this block itself has a config override (doors, gates, trapdoors)
            if (matConfig != null && matConfig.HasBlockOverride(block))
                return true;

            // Check if this is a multiblock spacer whose controller has a config override
            string path = block.Code?.Path;
            if (path != null && path.StartsWith("multiblock-", StringComparison.Ordinal))
            {
                var variant = block.Variant;
                if (variant != null &&
                    variant.TryGetValue("dx", out string dxStr) &&
                    variant.TryGetValue("dy", out string dyStr) &&
                    variant.TryGetValue("dz", out string dzStr))
                {
                    int cdx = ParseVariantOffset(dxStr);
                    int cdy = ParseVariantOffset(dyStr);
                    int cdz = ParseVariantOffset(dzStr);

                    _mbControllerPos.Set(x - cdx, y - cdy, z - cdz);
                    Block controller = blockAccessor.GetBlock(_mbControllerPos);

                    if (controller != null && controller.Id != 0 &&
                        !(controller.Code?.Path?.StartsWith("multiblock-", StringComparison.Ordinal) == true))
                    {
                        if (matConfig != null && matConfig.HasBlockOverride(controller))
                            return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Parses a VS multiblock variant offset string.
        /// "0" -> 0, "p1" -> +1, "n1" -> -1, "p2" -> +2, etc.
        /// </summary>
        private static int ParseVariantOffset(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "0") return 0;
            if (s.StartsWith("n", StringComparison.Ordinal))
            {
                if (int.TryParse(s.AsSpan(1), out int val)) return -val;
            }
            else if (s.StartsWith("p", StringComparison.Ordinal))
            {
                if (int.TryParse(s.AsSpan(1), out int val)) return val;
            }
            return 0;
        }

        /// <summary>
        /// Test if a ray intersects any of a block's collision boxes.
        /// Uses the slab method for ray-AABB intersection.
        /// Collision boxes are in block-local coordinates, offset by block position.
        /// </summary>
        private static bool RayHitsAnyCollisionBox(
            Vec3d rayOrigin, double dirX, double dirY, double dirZ, double rayLength,
            int blockX, int blockY, int blockZ, Cuboidf[] boxes)
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                var box = boxes[i];
                if (RayIntersectsAABB(rayOrigin, dirX, dirY, dirZ, rayLength,
                    blockX + box.X1, blockY + box.Y1, blockZ + box.Z1,
                    blockX + box.X2, blockY + box.Y2, blockZ + box.Z2))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Ray-AABB intersection test using the slab method.
        /// Tests if a ray segment intersects an axis-aligned bounding box.
        /// </summary>
        private static bool RayIntersectsAABB(
            Vec3d rayOrigin, double dirX, double dirY, double dirZ, double rayLength,
            double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            double tMin = 0;
            double tMax = rayLength;

            // X axis
            if (Math.Abs(dirX) > 1e-8)
            {
                double t1 = (minX - rayOrigin.X) / dirX;
                double t2 = (maxX - rayOrigin.X) / dirX;
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Math.Max(tMin, t1);
                tMax = Math.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
            else if (rayOrigin.X < minX || rayOrigin.X > maxX) return false;

            // Y axis
            if (Math.Abs(dirY) > 1e-8)
            {
                double t1 = (minY - rayOrigin.Y) / dirY;
                double t2 = (maxY - rayOrigin.Y) / dirY;
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Math.Max(tMin, t1);
                tMax = Math.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
            else if (rayOrigin.Y < minY || rayOrigin.Y > maxY) return false;

            // Z axis
            if (Math.Abs(dirZ) > 1e-8)
            {
                double t1 = (minZ - rayOrigin.Z) / dirZ;
                double t2 = (maxZ - rayOrigin.Z) / dirZ;
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Math.Max(tMin, t1);
                tMax = Math.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
            else if (rayOrigin.Z < minZ || rayOrigin.Z > maxZ) return false;

            return true;
        }

        /// <summary>
        /// Get occlusion multiplier based on block code patterns
        /// Similar to Sound Physics Remastered's block name matching
        /// Returns null if no pattern matches (fall back to material-based)
        /// </summary>
        private static float? GetBlockCodeMultiplier(Block block)
        {
            string code = block.Code?.Path?.ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(code))
                return null;

            // === METAL BLOCKS - Very high occlusion (dense, reflective) ===
            // metalblock, ironblock, steelblock, copperblock, etc.
            if (code.Contains("metalblock") || code.Contains("ironblock") ||
                code.Contains("steelblock") || code.Contains("copperblock") ||
                code.Contains("tinblock") || code.Contains("goldblock") ||
                code.Contains("silverblock") || code.Contains("bronzeblock") ||
                code.Contains("brassblock") || code.Contains("leadblock") ||
                code.Contains("zincblock") || code.Contains("bismuthblock") ||
                code.Contains("titaniumblock") || code.Contains("chromiumblock") ||
                code.Contains("platinumblock") || code.Contains("electrumblock") ||
                code.StartsWith("metalplate", StringComparison.Ordinal) || code.StartsWith("sheetmetal", StringComparison.Ordinal) ||
                code.Contains("anvil") || code.Contains("metalladder"))
                return 1.0f;

            // === LEATHER - Moderate-high occlusion (dense, absorptive) ===
            if (code.Contains("leather"))
                return 0.6f;

            // === WOOL/CLOTH - Moderate occlusion (absorptive but not blocking) ===
            if (code.Contains("wool") || code.Contains("carpet") || code.Contains("rug"))
                return 0.4f;

            // === CONCRETE/PLASTER - High occlusion ===
            if (code.Contains("concrete") || code.Contains("plaster") || code.Contains("mortar"))
                return 0.9f;

            // === DOORS - Variable (closed = high, but material handles it) ===
            // Let material system handle doors for now

            // === CHESTS/CONTAINERS - Moderate (hollow inside) ===
            if (code.Contains("chest") || code.Contains("barrel") || code.Contains("crate"))
                return 0.5f;

            // === BEDS - Low occlusion (soft, hollow) ===
            if (code.Contains("bed"))
                return 0.3f;

            // No pattern match - use material-based fallback
            return null;
        }

        /// <summary>
        /// Convert occlusion value to lowpass filter value
        /// Based on SPR: filterValue = exp(-occlusion * absorption)
        /// </summary>
        /// <param name="occlusion">Accumulated occlusion value</param>
        /// <returns>Filter value (0 = silent, 1 = no filter)</returns>
        public static float OcclusionToFilter(float occlusion)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null)
                return 1f;

            // LINEAR ACCUMULATION (physically accurate)
            // Each material layer adds its transmission loss linearly (in dB).
            // The occlusion value represents the sum of all material losses.
            // Exponential converts from dB-like scale to linear intensity.
            // Formula from Sound Physics Remastered:
            // absorptionCoeff = blockAbsorption * 3D
            // directCutoff = exp(-occlusionAccumulation * absorptionCoeff)
            // The ×3 is SPR's internal scaling that maps the user-facing knob (0.1-4.0)
            // to physically meaningful absorption. Without it, muffling is 3× too weak.
            float filterValue = (float)Math.Exp(-occlusion * config.BlockAbsorption * 3.0f);

            // Clamp to minimum to prevent completely silent sounds
            return Math.Max(filterValue, config.MinLowPassFilter);
        }
    }
}
