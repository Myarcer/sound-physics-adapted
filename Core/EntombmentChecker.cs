using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// ISSUE 20: BFS entombment pre-check.
    ///
    /// For heavily occluded sounds (sealed cave below the player, walled-off cellar),
    /// the full raytrace wastes its budget bouncing 32x4 rays around a sealed cavity
    /// only to find 0% shared airspace and no openings. A radius-capped flood fill
    /// from the sound can prove the negative — "no air path to the player exists" —
    /// far cheaper than rays can fail to find one.
    ///
    /// Only a COMPLETED flood fill counts: if the search hits the radius cap or the
    /// node budget before exhausting the cavity, the cavity extends beyond what we
    /// explored and nothing is proven (Inconclusive → run the normal raytrace).
    ///
    /// Passability uses BlockClassification.IsSolidForOcclusion (per-block-ID cached),
    /// so partial blocks and chisel work count as passable — biased against false
    /// entombment, which would silence an audible sound.
    ///
    /// Static scratch state is safe: all compute runs on the client game thread
    /// (off-thread Start() processing is deferred to the tick).
    /// </summary>
    public static class EntombmentChecker
    {
        /// <summary>Only sounds at least this occluded are worth a BFS.</summary>
        public const float MIN_OCCLUSION_TO_CHECK = 6f;

        /// <summary>Chebyshev radius around the sound the flood fill may explore.</summary>
        private const int MAX_RADIUS = 10;

        /// <summary>Hard cap on visited air blocks (~a 13x13x13 room fully explored).</summary>
        private const int MAX_NODES = 2500;

        public enum Result
        {
            /// <summary>An air path from sound to player exists within the radius.</summary>
            Clear,
            /// <summary>Cavity fully explored, player not inside — sound is sealed off.</summary>
            Entombed,
            /// <summary>Search hit the radius/node budget — nothing proven.</summary>
            Inconclusive
        }

        private static readonly Queue<(int x, int y, int z)> _queue = new Queue<(int, int, int)>();
        private static readonly HashSet<long> _visited = new HashSet<long>();
        private static readonly BlockPos _probePos = new BlockPos(0, 0, 0, 0);

        private static readonly int[][] _neighbors = new int[][]
        {
            new[] { 1, 0, 0 }, new[] { -1, 0, 0 },
            new[] { 0, 1, 0 }, new[] { 0, -1, 0 },
            new[] { 0, 0, 1 }, new[] { 0, 0, -1 }
        };

        private static long Pack(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0xFFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        private static bool IsPassable(IBlockAccessor blockAccessor, int x, int y, int z)
        {
            Block block = blockAccessor.GetBlock(_probePos.Set(x, y, z));
            return !BlockClassification.IsSolidForOcclusion(block);
        }

        public static Result Check(Vec3d soundPos, Vec3d playerPos, IBlockAccessor blockAccessor)
        {
            int sx = (int)System.Math.Floor(soundPos.X);
            int sy = (int)System.Math.Floor(soundPos.Y);
            int sz = (int)System.Math.Floor(soundPos.Z);
            int px = (int)System.Math.Floor(playerPos.X);
            int py = (int)System.Math.Floor(playerPos.Y);
            int pz = (int)System.Math.Floor(playerPos.Z);

            _queue.Clear();
            _visited.Clear();

            // Seed: the sound's own block, or its passable neighbors when the sound
            // sits inside a solid block (common for block-attached sounds).
            if (IsPassable(blockAccessor, sx, sy, sz))
            {
                _queue.Enqueue((sx, sy, sz));
                _visited.Add(Pack(sx, sy, sz));
            }
            else
            {
                for (int n = 0; n < _neighbors.Length; n++)
                {
                    int nx = sx + _neighbors[n][0];
                    int ny = sy + _neighbors[n][1];
                    int nz = sz + _neighbors[n][2];
                    if (IsPassable(blockAccessor, nx, ny, nz))
                    {
                        _queue.Enqueue((nx, ny, nz));
                        _visited.Add(Pack(nx, ny, nz));
                    }
                }
                // Fully embedded sound — can't establish a starting cavity.
                if (_queue.Count == 0) return Result.Inconclusive;
            }

            bool boundaryReached = false;
            int nodesVisited = 0;

            while (_queue.Count > 0)
            {
                var (cx, cy, cz) = _queue.Dequeue();

                if (cx == px && cy == py && cz == pz) return Result.Clear;

                if (++nodesVisited > MAX_NODES) return Result.Inconclusive;

                for (int n = 0; n < _neighbors.Length; n++)
                {
                    int nx = cx + _neighbors[n][0];
                    int ny = cy + _neighbors[n][1];
                    int nz = cz + _neighbors[n][2];

                    // Radius cap: AIR continuing past the search volume means the
                    // cavity is open-ended — we can no longer prove entombment.
                    // A solid block past the cap still seals the cavity, so only
                    // passable overflow flips the result to Inconclusive.
                    if (System.Math.Abs(nx - sx) > MAX_RADIUS ||
                        System.Math.Abs(ny - sy) > MAX_RADIUS ||
                        System.Math.Abs(nz - sz) > MAX_RADIUS)
                    {
                        if (!boundaryReached && IsPassable(blockAccessor, nx, ny, nz))
                            boundaryReached = true;
                        continue;
                    }

                    long key = Pack(nx, ny, nz);
                    if (_visited.Contains(key)) continue;
                    _visited.Add(key);

                    if (!IsPassable(blockAccessor, nx, ny, nz)) continue;

                    if (nx == px && ny == py && nz == pz) return Result.Clear;
                    _queue.Enqueue((nx, ny, nz));
                }
            }

            // Frontier exhausted. Only a fully-contained cavity proves entombment.
            return boundaryReached ? Result.Inconclusive : Result.Entombed;
        }
    }
}
