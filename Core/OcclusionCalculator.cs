using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using System;

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

            // Skip calculation if too far
            if (distance > config.MaxSoundDistance)
            {
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog($"Sound too far: {distance:F1} > {config.MaxSoundDistance}");
                return 0f;
            }

            // Multi-ray occlusion with voting system:
            // - CENTER ray represents direct line of sight (authoritative)
            // - OFFSET rays detect thin walls that center might miss
            // - Use voting: need majority of rays to agree on occlusion

            float centerOcclusion = RunOcclusion(soundPos, playerPos, blockAccessor, config);

            // EARLY EXIT: If center ray hits max occlusion, no need for offset rays
            if (centerOcclusion >= config.MaxOcclusion)
            {
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"Occlusion calc: dist={distance:F1} center=MAX (early exit)");
                return config.MaxOcclusion;
            }

            // If center ray finds significant occlusion, trust it (direct line blocked)
            if (centerOcclusion >= 0.5f)
            {
                // Center is blocked - this is the primary occlusion value
                float result = Math.Min(centerOcclusion, config.MaxOcclusion);
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"Occlusion calc: dist={distance:F1} center={centerOcclusion:F2} (blocked)");
                return result;
            }

            // OPTIMIZATION: If center ray is very clear (< 0.3), skip offset rays entirely.
            // True clear LOS confirmed - offset rays would just add cost without benefit.
            // Only check offset rays in the ambiguous 0.3-0.5 range where thin walls matter.
            if (centerOcclusion < 0.3f)
            {
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"Occlusion calc: dist={distance:F1} center={centerOcclusion:F2} (clear, skip offset)");
                return centerOcclusion;
            }

            // Center ray is ambiguous (0.3-0.5) - check offset rays for thin walls
            float variation = config.OcclusionVariation;
            if (variation <= 0f)
            {
                // No offset rays, center is clear
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
                            SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                                $"Occlusion calc: dist={distance:F1} early exit at {raysBlocked} votes, max={maxOffsetOcclusion:F2}");
                            return config.MaxOcclusion;
                        }
                    }
                }
            }

            // Voting threshold: need at least 6 of 8 offset rays blocked to override clear center
            // Was 4/8 which caused false positives: beehive with LOS but adjacent parallel walls
            // at ±0.35m offset poked into neighboring blocks, triggering "thin wall detected"
            // when the actual direct path was completely clear.
            // 6/8 = supermajority: only triggers for actual thin walls in the path,
            // not nearby geometry parallel to the sound direction.
            if (raysBlocked >= 6)
            {
                // Majority of offset rays blocked = thin wall detected
                // Use average occlusion of all rays
                float avgOcclusion = totalOcclusion / totalRays;
                float result = Math.Min(avgOcclusion, config.MaxOcclusion);
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"Occlusion calc: dist={distance:F1} center=clear but {raysBlocked}/8 offset blocked, avg={avgOcclusion:F2}");
                return result;
            }

            // Center clear and not enough offset rays blocked = clear path
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
        /// [LEGACY] Fast path occlusion — solid-face-only check, ignores partial blocks.
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
        /// [LEGACY] Fast path occlusion with entry point — solid-face-only check.
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
        /// Returns COMBINED occlusion (structural + interactable). Used by thunder/opening tracker
        /// where doors should still count as blocking.
        /// </summary>
        public static float CalculateWeatherPathOcclusion(Vec3d from, Vec3d to, IBlockAccessor blockAccessor)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.Enabled)
                return 0f;

            float structural = RunWeatherOcclusion(from, to, blockAccessor, config, out _, out float interactable);
            return structural + interactable;
        }

        /// <summary>
        /// Weather-aware path occlusion with entry point tracking.
        /// Returns STRUCTURAL occlusion only (walls, floors — not doors/trapdoors).
        /// Interactable occlusion (doors, trapdoors) returned separately via out param.
        /// This allows WeatherEnclosureCalculator to spawn sources through closed doors
        /// (using structural for threshold) while still applying door muffling.
        /// </summary>
        public static float CalculateWeatherPathOcclusionWithEntry(
            Vec3d from, Vec3d to, IBlockAccessor blockAccessor,
            out Vec3d entryPoint, out float interactableOcclusion)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.Enabled)
            {
                entryPoint = null;
                interactableOcclusion = 0f;
                return 0f;
            }

            return RunWeatherOcclusion(from, to, blockAccessor, config, out entryPoint, out interactableOcclusion);
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

            // Mutable state captured by the visitor lambda
            float occlusionAccumulation = 0f;
            int blockHits = 0;

            // Reusable BlockPos for collision box lookups (avoids allocation per block)
            BlockPos collisionCheckPos = new BlockPos(0, 0, 0, 0);

            bool stopped = DDABlockTraversal.Traverse(from, to, blockAccessor, (ref DDABlockTraversal.TraversalContext ctx) =>
            {
                Block block = ctx.Block;
                if (block == null || block.Id == 0 || block.BlockMaterial == EnumBlockMaterial.Air)
                    return false; // Continue

                float blockOcclusion = 0f;

                // FAST PATH: Blocks with any solid face always occlude without
                // collision box checks. Covers full cubes, slabs, stairs, etc.
                if (BlockClassification.IsSolidForOcclusion(block))
                {
                    blockOcclusion = BlockClassification.GetBlockOcclusion(block, config);
                }
                else if (BlockClassification.IsLiquidMaterial(block))
                {
                    // LIQUID/LAVA PATH: No collision boxes but still muffles sound.
                    blockOcclusion = BlockClassification.GetBlockOcclusion(block, config);
                    if (blockOcclusion > 0)
                    {
                        SoundPhysicsAdaptedModSystem.VerboseDebugLog(
                            $"  DDA liquid: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) occ={blockOcclusion:F2}");
                    }
                }
                else
                {
                    // PARTIAL BLOCK PATH: doors, fences, trapdoors, etc.
                    // Check if ray actually intersects the block's collision geometry.
                    collisionCheckPos.Set(ctx.X, ctx.Y, ctx.Z);
                    var collisionBoxes = block.GetCollisionBoxes(blockAccessor, collisionCheckPos);
                    if (collisionBoxes == null || collisionBoxes.Length == 0)
                    {
                        // No collision geometry (plants, grass, flowers, etc.)
                        blockOcclusion = BlockClassification.GetBlockOcclusion(block, config);
                        if (blockOcclusion > 0)
                        {
                            SoundPhysicsAdaptedModSystem.VerboseDebugLog(
                                $"  DDA foliage: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) occ={blockOcclusion:F2}");
                        }
                    }
                    else if (!RayHitsAnyCollisionBox(from, ndx, ndy, ndz, length, ctx.X, ctx.Y, ctx.Z, collisionBoxes))
                    {
                        // Ray misses collision geometry (e.g. open door panel on the side)
                        SoundPhysicsAdaptedModSystem.VerboseDebugLog(
                            $"  DDA pass-through: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) (ray misses geometry)");
                    }
                    else
                    {
                        blockOcclusion = BlockClassification.GetBlockOcclusion(block, config);
                    }
                }

                if (blockOcclusion > 0)
                {
                    occlusionAccumulation += blockOcclusion;
                    blockHits++;

                    SoundPhysicsAdaptedModSystem.VerboseDebugLog(
                        $"  DDA hit: {block.Code} at ({ctx.X},{ctx.Y},{ctx.Z}) occ={blockOcclusion:F2} total={occlusionAccumulation:F2}");

                    if (occlusionAccumulation >= config.MaxOcclusion)
                    {
                        SoundPhysicsAdaptedModSystem.VerboseDebugLog($"  Max occlusion reached after {blockHits} blocks");
                        return true; // Stop traversal
                    }
                }

                return false; // Continue
            }, skipFirst: true);

            return stopped ? config.MaxOcclusion : occlusionAccumulation;
        }

        /// <summary>
        /// [LEGACY] Fast DDA for weather: solid-face-only check, ignores partial blocks.
        /// Doors, trapdoors, fences, chiseled blocks are invisible to this method.
        /// Kept for reference — new code should use RunWeatherOcclusion instead.
        /// </summary>
        private static float RunOcclusionFullCubeOnly(Vec3d from, Vec3d to, IBlockAccessor blockAccessor, SoundPhysicsConfig config, out Vec3d entryPoint)
        {
            float occlusionAccumulation = 0f;
            bool previousBlockWasSolid = false;
            int entryX = 0, entryY = 0, entryZ = 0;
            bool hasEntryPoint = false;

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
            }, skipFirst: true);

            entryPoint = hasEntryPoint ? new Vec3d(entryX + 0.5, entryY + 0.5, entryZ + 0.5) : null;
            return stopped ? config.MaxOcclusion : occlusionAccumulation;
        }

        /// <summary>
        /// Weather-aware DDA: hybrid fast-path with AABB collision checks for partial blocks.
        /// 
        /// Uses DUAL ACCUMULATORS:
        /// - structuralOcclusion (return value): walls, floors, solid blocks, non-interactable partials.
        ///   Used for spawn threshold decisions — doors don't prevent source spawning.
        /// - interactableOcclusion (out param): doors, trapdoors (state-changing blocks).
        ///   Applied as muffling on spawned sources. Drops to 0 when door opens.
        /// 
        /// Processing order per block:
        /// 1. Solid-face blocks (full cubes, slabs, stairs): structural occlusion.
        /// 2. Liquids: structural occlusion.
        /// 3. Interactable partial blocks (doors, trapdoors): interactable occlusion.
        /// 4. Other partial blocks (chiseled, fences): structural occlusion via AABB check.
        /// 5. Air/plants with no collision: non-occluding.
        /// 
        /// No verbose debug logging — weather casts hundreds of rays per tick.
        /// Tracks last occluding-to-air transition for entry point detection.
        /// </summary>
        private static float RunWeatherOcclusion(Vec3d from, Vec3d to, IBlockAccessor blockAccessor, SoundPhysicsConfig config, out Vec3d entryPoint, out float interactableOcclusion)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double dz = to.Z - from.Z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < 0.001)
            {
                entryPoint = null;
                interactableOcclusion = 0f;
                return 0f;
            }

            // Normalize for collision box ray intersection tests
            double ndx = dx / length;
            double ndy = dy / length;
            double ndz = dz / length;

            float structuralAccum = 0f;
            float interactableAccum = 0f;
            bool previousBlockWasOccluding = false;
            int entryX = 0, entryY = 0, entryZ = 0;
            bool hasEntryPoint = false;

            // Reusable BlockPos for collision box lookups (avoids allocation per block)
            BlockPos collisionCheckPos = new BlockPos(0, 0, 0, 0);

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

                float blockOcclusion = 0f;
                bool isInteractable = false;

                // FAST PATH: Blocks with any solid face — no AABB check needed
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
                    // PARTIAL BLOCK PATH: doors, trapdoors, fences, chiseled blocks
                    collisionCheckPos.Set(ctx.X, ctx.Y, ctx.Z);
                    var collisionBoxes = block.GetCollisionBoxes(blockAccessor, collisionCheckPos);

                    if (collisionBoxes != null && collisionBoxes.Length > 0 &&
                        RayHitsAnyCollisionBox(from, ndx, ndy, ndz, length, ctx.X, ctx.Y, ctx.Z, collisionBoxes))
                    {
                        // Ray hits the partial block's geometry
                        blockOcclusion = BlockClassification.GetBlockOcclusion(block, config);
                        // Check if this is a door/trapdoor — separate accumulator
                        isInteractable = BlockClassification.IsWeatherInteractable(block);
                    }
                    // else: ray misses geometry (open door gap, fence gap, no collision boxes)
                }

                if (blockOcclusion > 0)
                {
                    if (isInteractable)
                    {
                        // Door/trapdoor: accumulate separately (doesn't affect spawn threshold)
                        // Don't set previousBlockWasOccluding — entry point tracks through doors
                        interactableAccum += blockOcclusion;
                    }
                    else
                    {
                        // Structural block: accumulates toward spawn threshold
                        structuralAccum += blockOcclusion;
                        previousBlockWasOccluding = true;

                        if (structuralAccum >= config.MaxOcclusion)
                            return true; // Stop
                    }
                }
                else
                {
                    // Non-occluding block (open door, grass, etc.) — track transition
                    if (previousBlockWasOccluding)
                    {
                        entryX = ctx.X; entryY = ctx.Y; entryZ = ctx.Z;
                        hasEntryPoint = true;
                    }
                    previousBlockWasOccluding = false;
                }

                return false; // Continue
            }, skipFirst: true);

            entryPoint = hasEntryPoint ? new Vec3d(entryX + 0.5, entryY + 0.5, entryZ + 0.5) : null;
            interactableOcclusion = interactableAccum;
            return stopped ? config.MaxOcclusion : structuralAccum;
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
                code.StartsWith("metalplate") || code.StartsWith("sheetmetal") ||
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
            // directCutoff = exp(-occlusionAccumulation * absorptionCoeff)
            float filterValue = (float)Math.Exp(-occlusion * config.BlockAbsorption);

            // Clamp to minimum to prevent completely silent sounds
            return Math.Max(filterValue, config.MinLowPassFilter);
        }
    }
}
