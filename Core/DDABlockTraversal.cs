using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Result from a DDA raycast that finds the first solid surface.
    /// Contains exact hit information derived from DDA grid traversal.
    /// </summary>
    public struct DDAHitResult
    {
        /// <summary>The block that was hit.</summary>
        public Block Block;

        /// <summary>Euclidean distance from ray origin to the block entry face.</summary>
        public float Distance;

        /// <summary>Exact world position where the ray enters the block (on the face boundary).</summary>
        public Vec3d Position;

        /// <summary>
        /// Exact surface normal derived from DDA step axis.
        /// Unlike guessed normals from stepped raycast, this is always correct
        /// because DDA knows exactly which axis boundary was crossed.
        /// </summary>
        public Vec3d Normal;

        /// <summary>Block position of the hit block (newly allocated, safe to store).</summary>
        public BlockPos BlockPos;
    }

    /// <summary>
    /// Shared DDA (Digital Differential Analyzer) grid traversal utility.
    /// Guarantees visiting every block a ray passes through — no blocks are ever skipped.
    ///
    /// This is the single source of truth for grid-walking raycasts in the mod.
    /// All systems (occlusion, reverb, airspace, weather) use this core.
    ///
    /// Two modes:
    ///   1. Point-to-point: Traverse(from, to) — for occlusion, airspace checks
    ///   2. Direction+range: TraverseDirection(origin, dir, maxDist) — for reverb bounce rays
    ///
    /// The visitor callback returns true to STOP traversal (found what it needs),
    /// or false to CONTINUE visiting blocks.
    ///
    /// DDA algorithm: At each step, advance to whichever axis boundary is nearest
    /// along the ray. This visits exactly the set of blocks the ray passes through,
    /// in order, with zero gaps. Cost = O(Manhattan distance).
    /// </summary>
    public static class DDABlockTraversal
    {
        // Pre-allocated normals to avoid allocation in hot path
        // Normal points TOWARD the ray origin (outward from the hit face)
        private static readonly Vec3d NormalPosX = new Vec3d(1, 0, 0);
        private static readonly Vec3d NormalNegX = new Vec3d(-1, 0, 0);
        private static readonly Vec3d NormalPosY = new Vec3d(0, 1, 0);
        private static readonly Vec3d NormalNegY = new Vec3d(0, -1, 0);
        private static readonly Vec3d NormalPosZ = new Vec3d(0, 0, 1);
        private static readonly Vec3d NormalNegZ = new Vec3d(0, 0, -1);

        /// <summary>
        /// Which axis the DDA stepped on to enter the current block.
        /// Used to derive the exact face normal of the entry face.
        /// </summary>
        public enum StepAxis
        {
            None = 0,  // First block (ray origin block)
            X = 1,
            Y = 2,
            Z = 3
        }

        /// <summary>
        /// Context passed to visitor callbacks during DDA traversal.
        /// Contains all information about the current block being visited.
        /// Passed by ref for performance (avoids copying the struct on each callback).
        /// </summary>
        public struct TraversalContext
        {
            /// <summary>Current block X coordinate.</summary>
            public int X;
            /// <summary>Current block Y coordinate.</summary>
            public int Y;
            /// <summary>Current block Z coordinate.</summary>
            public int Z;

            /// <summary>The block at this position.</summary>
            public Block Block;

            /// <summary>
            /// Which axis was stepped to enter this block.
            /// StepAxis.None for the starting block.
            /// </summary>
            public StepAxis EntryAxis;

            /// <summary>
            /// Step direction on the entry axis (-1 or +1).
            /// Combined with EntryAxis, gives the exact face normal.
            /// </summary>
            public int EntryStepDirection;

            /// <summary>
            /// Ray parameter t at which the ray entered this block (distance from origin in world units).
            /// For the first block, this is 0.
            /// </summary>
            public double TEntry;

            /// <summary>Normalized ray direction X component.</summary>
            public double DirX;
            /// <summary>Normalized ray direction Y component.</summary>
            public double DirY;
            /// <summary>Normalized ray direction Z component.</summary>
            public double DirZ;

            /// <summary>Ray origin.</summary>
            public Vec3d Origin;

            /// <summary>
            /// Get the exact world position where the ray enters this block.
            /// Computed from origin + direction * tEntry.
            /// </summary>
            public Vec3d GetEntryPosition()
            {
                return new Vec3d(
                    Origin.X + DirX * TEntry,
                    Origin.Y + DirY * TEntry,
                    Origin.Z + DirZ * TEntry
                );
            }

            /// <summary>
            /// Get the exact surface normal of the face the ray entered through.
            /// The normal points AWAY from the block interior (toward the ray origin side).
            /// Returns null for the starting block (no entry face).
            /// </summary>
            public Vec3d GetEntryNormal()
            {
                switch (EntryAxis)
                {
                    case StepAxis.X:
                        // Ray stepped +X to enter → entered through -X face → normal points -X (toward ray)
                        return EntryStepDirection > 0 ? NormalNegX : NormalPosX;
                    case StepAxis.Y:
                        return EntryStepDirection > 0 ? NormalNegY : NormalPosY;
                    case StepAxis.Z:
                        return EntryStepDirection > 0 ? NormalNegZ : NormalPosZ;
                    default:
                        return NormalPosY; // Fallback for starting block
                }
            }

            /// <summary>
            /// Euclidean distance from ray origin to the entry point of this block.
            /// </summary>
            public float GetDistance()
            {
                return (float)TEntry;
            }
        }

        /// <summary>
        /// Visitor delegate for DDA traversal.
        /// Called for each block the ray passes through.
        /// Return true to STOP traversal, false to CONTINUE.
        /// </summary>
        /// <param name="ctx">Context with current block info, position, entry face, etc.</param>
        /// <returns>True to stop, false to continue.</returns>
        public delegate bool BlockVisitor(ref TraversalContext ctx);

        /// <summary>
        /// Traverse all blocks along a ray from point A to point B.
        /// Visits every block the ray passes through in order.
        /// </summary>
        /// <param name="from">Ray start position (world coordinates).</param>
        /// <param name="to">Ray end position (world coordinates).</param>
        /// <param name="blockAccessor">Block accessor for world queries.</param>
        /// <param name="visitor">Callback for each block. Return true to stop.</param>
        /// <param name="skipFirst">If true, skip the starting block (common for sound source position).</param>
        /// <returns>True if visitor stopped traversal, false if ray completed without stopping.</returns>
        public static bool Traverse(Vec3d from, Vec3d to, IBlockAccessor blockAccessor, BlockVisitor visitor, bool skipFirst = true)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double dz = to.Z - from.Z;

            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < 0.001) return false;

            // Normalize direction
            dx /= length;
            dy /= length;
            dz /= length;

            return TraverseCore(from, dx, dy, dz, blockAccessor, visitor, skipFirst,
                (int)Math.Floor(to.X), (int)Math.Floor(to.Y), (int)Math.Floor(to.Z));
        }

        /// <summary>
        /// Traverse all blocks along a ray from origin in a direction up to maxDistance.
        /// Used by reverb bounce rays that think in direction + range.
        /// </summary>
        /// <param name="origin">Ray start position (world coordinates).</param>
        /// <param name="direction">Normalized ray direction.</param>
        /// <param name="maxDistance">Maximum distance to traverse.</param>
        /// <param name="blockAccessor">Block accessor for world queries.</param>
        /// <param name="visitor">Callback for each block. Return true to stop.</param>
        /// <param name="skipFirst">If true, skip the starting block.</param>
        /// <returns>True if visitor stopped traversal, false if ray completed without stopping.</returns>
        public static bool TraverseDirection(Vec3d origin, Vec3d direction, float maxDistance,
            IBlockAccessor blockAccessor, BlockVisitor visitor, bool skipFirst = true)
        {
            double dx = direction.X;
            double dy = direction.Y;
            double dz = direction.Z;

            // Ensure direction is normalized
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 0.001) return false;
            if (Math.Abs(len - 1.0) > 0.01)
            {
                dx /= len;
                dy /= len;
                dz /= len;
            }

            // Compute endpoint from direction + distance
            Vec3d to = new Vec3d(
                origin.X + dx * maxDistance,
                origin.Y + dy * maxDistance,
                origin.Z + dz * maxDistance
            );

            return TraverseCore(origin, dx, dy, dz, blockAccessor, visitor, skipFirst,
                (int)Math.Floor(to.X), (int)Math.Floor(to.Y), (int)Math.Floor(to.Z));
        }

        /// <summary>
        /// Core DDA implementation. All public methods delegate here.
        /// </summary>
        private static bool TraverseCore(Vec3d from, double dx, double dy, double dz,
            IBlockAccessor blockAccessor, BlockVisitor visitor, bool skipFirst,
            int endX, int endY, int endZ)
        {
            // Current block position (start)
            int x = (int)Math.Floor(from.X);
            int y = (int)Math.Floor(from.Y);
            int z = (int)Math.Floor(from.Z);

            // Step direction for each axis (-1, 0, or +1)
            int stepX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int stepY = dy > 0 ? 1 : (dy < 0 ? -1 : 0);
            int stepZ = dz > 0 ? 1 : (dz < 0 ? -1 : 0);

            // tMax: ray parameter t at which we cross the next block boundary on each axis
            double tMaxX = dx != 0 ? ((stepX > 0 ? (x + 1 - from.X) : (from.X - x)) / Math.Abs(dx)) : double.MaxValue;
            double tMaxY = dy != 0 ? ((stepY > 0 ? (y + 1 - from.Y) : (from.Y - y)) / Math.Abs(dy)) : double.MaxValue;
            double tMaxZ = dz != 0 ? ((stepZ > 0 ? (z + 1 - from.Z) : (from.Z - z)) / Math.Abs(dz)) : double.MaxValue;

            // tDelta: ray parameter t to cross one full block on each axis
            double tDeltaX = dx != 0 ? Math.Abs(1.0 / dx) : double.MaxValue;
            double tDeltaY = dy != 0 ? Math.Abs(1.0 / dy) : double.MaxValue;
            double tDeltaZ = dz != 0 ? Math.Abs(1.0 / dz) : double.MaxValue;

            // DDA steps one axis at a time → Manhattan distance bound
            int maxBlocks = Math.Abs(endX - x) + Math.Abs(endY - y) + Math.Abs(endZ - z) + 2;
            int blocksTraversed = 0;

            // Track which axis we stepped on (for exact normal computation)
            StepAxis lastStepAxis = StepAxis.None;
            int lastStepDir = 0;
            double tCurrent = 0.0; // Current ray parameter

            // Reusable BlockPos to avoid allocations in the hot loop
            BlockPos currentPos = new BlockPos(0, 0, 0, 0);

            // Context struct (stack-allocated)
            TraversalContext ctx = default;
            ctx.DirX = dx;
            ctx.DirY = dy;
            ctx.DirZ = dz;
            ctx.Origin = from;

            while (blocksTraversed < maxBlocks)
            {
                if (!skipFirst)
                {
                    currentPos.Set(x, y, z);
                    Block block = blockAccessor.GetBlock(currentPos);

                    ctx.X = x;
                    ctx.Y = y;
                    ctx.Z = z;
                    ctx.Block = block;
                    ctx.EntryAxis = lastStepAxis;
                    ctx.EntryStepDirection = lastStepDir;
                    ctx.TEntry = tCurrent;

                    if (visitor(ref ctx))
                        return true; // Visitor said stop
                }
                skipFirst = false;

                // Check if we've reached the destination block
                if (x == endX && y == endY && z == endZ)
                    break;

                // DDA core: step to the nearest block boundary
                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    tCurrent = tMaxX;
                    x += stepX;
                    tMaxX += tDeltaX;
                    lastStepAxis = StepAxis.X;
                    lastStepDir = stepX;
                }
                else if (tMaxY < tMaxZ)
                {
                    tCurrent = tMaxY;
                    y += stepY;
                    tMaxY += tDeltaY;
                    lastStepAxis = StepAxis.Y;
                    lastStepDir = stepY;
                }
                else
                {
                    tCurrent = tMaxZ;
                    z += stepZ;
                    tMaxZ += tDeltaZ;
                    lastStepAxis = StepAxis.Z;
                    lastStepDir = stepZ;
                }

                blocksTraversed++;
            }

            return false; // Traversal completed without visitor stopping
        }

        // =====================================================================
        // CONVENIENCE METHODS: Common patterns built on top of the core DDA
        // =====================================================================

        /// <summary>
        /// Find the first block along a ray that matches a filter predicate.
        /// Returns null if no matching block is found within range.
        ///
        /// This replaces the old stepped RaycastToSurface in AcousticRaytracer.
        /// Key improvements:
        ///   - Never skips blocks (DDA guarantee)
        ///   - Exact entry position and face normal (from DDA step axis)
        ///   - No guessed normals from position heuristics
        /// </summary>
        /// <param name="origin">Ray start position.</param>
        /// <param name="direction">Normalized ray direction.</param>
        /// <param name="maxDistance">Maximum distance to search.</param>
        /// <param name="blockAccessor">Block accessor.</param>
        /// <param name="blockFilter">Predicate: return true if block should count as a hit.</param>
        /// <param name="excludeBlock">Optional block position to skip (for bounce rays).</param>
        /// <returns>Hit result with exact position, normal, and distance, or null if no hit.</returns>
        public static DDAHitResult? FindFirstBlock(Vec3d origin, Vec3d direction, float maxDistance,
            IBlockAccessor blockAccessor, System.Func<Block, bool> blockFilter, BlockPos excludeBlock = null)
        {
            DDAHitResult? result = null;

            TraverseDirection(origin, direction, maxDistance, blockAccessor, (ref TraversalContext ctx) =>
            {
                Block block = ctx.Block;
                if (block == null || block.Id == 0)
                    return false; // Continue

                // Skip excluded block (the one we just bounced off)
                if (excludeBlock != null && ctx.X == excludeBlock.X && ctx.Y == excludeBlock.Y && ctx.Z == excludeBlock.Z)
                    return false; // Continue

                if (blockFilter(block))
                {
                    result = new DDAHitResult
                    {
                        Block = block,
                        Distance = ctx.GetDistance(),
                        Position = ctx.GetEntryPosition(),
                        Normal = ctx.GetEntryNormal(),
                        BlockPos = new BlockPos(ctx.X, ctx.Y, ctx.Z, 0)
                    };
                    return true; // Stop
                }

                return false; // Continue
            }, skipFirst: true);

            return result;
        }

        /// <summary>
        /// Check if there is a clear line-of-sight between two points.
        /// Returns true if NO solid block (matching the filter) is in the way.
        ///
        /// Replaces the old HasSharedAirspace which used stepped RaycastToSurface.
        /// </summary>
        /// <param name="from">Start position.</param>
        /// <param name="to">End position.</param>
        /// <param name="blockAccessor">Block accessor.</param>
        /// <param name="blockFilter">Predicate: return true if block counts as solid (blocking LOS).</param>
        /// <returns>True if path is clear, false if blocked.</returns>
        public static bool HasClearPath(Vec3d from, Vec3d to, IBlockAccessor blockAccessor, System.Func<Block, bool> blockFilter)
        {
            bool blocked = Traverse(from, to, blockAccessor, (ref TraversalContext ctx) =>
            {
                Block block = ctx.Block;
                if (block == null || block.Id == 0)
                    return false; // Continue

                if (blockFilter(block))
                    return true; // Stop — path is blocked

                return false; // Continue
            }, skipFirst: true);

            return !blocked; // Not blocked = clear path
        }
    }
}
