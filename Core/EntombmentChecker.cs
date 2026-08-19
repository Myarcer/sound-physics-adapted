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
    /// Only a COMPLETED flood fill counts: if the search leaves the radius box through
    /// air or hits the node budget before exhausting the cavity, the cavity extends
    /// beyond what we explored and nothing is proven (Inconclusive → run the normal
    /// raytrace). The caller treats Clear and Inconclusive the same, so the search
    /// stops the moment either becomes the only possible outcome: the first passable
    /// block past the radius cap ends the search (A8 — an open cavity used to flood
    /// the whole box for a verdict that changed nothing).
    ///
    /// Passability uses BlockClassification.IsSolidForOcclusion (per-block-ID cached),
    /// so partial blocks and chisel work count as passable — biased against false
    /// entombment, which would silence an audible sound.
    ///
    /// Static scratch state is safe: all compute runs on the client game thread
    /// (off-thread Start() processing is deferred to the tick). The visited set and
    /// the queue are flat preallocated arrays over the search box — no hashing, no
    /// per-call allocation (A8).
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
            /// <summary>Open-ended cavity or budget hit — nothing proven.</summary>
            Inconclusive
        }

        // Local coordinates pack into 5-bit fields: lx | ly << 5 | lz << 10, each in
        // [0, 20] for the 21-block box. That index addresses _visited directly and is
        // what _queue stores — no hashing, no tuples, and a solid block is marked
        // visited too, so it is read at most once per check.
        private const int GRID = 2 * MAX_RADIUS + 1;                    // 21
        private static readonly byte[] _visited = new byte[32 * 32 * 32];
        private static readonly int[] _queue = new int[GRID * GRID * GRID];
        private static readonly BlockPos _probePos = new BlockPos(0, 0, 0, 0);

        private static readonly int[][] _neighbors = new int[][]
        {
            new[] { 1, 0, 0 }, new[] { -1, 0, 0 },
            new[] { 0, 1, 0 }, new[] { 0, -1, 0 },
            new[] { 0, 0, 1 }, new[] { 0, 0, -1 }
        };

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

            // Same block as the listener — no cavity can seal them apart.
            if (sx == px && sy == py && sz == pz) return Result.Clear;

            System.Array.Clear(_visited, 0, _visited.Length);
            int head = 0;
            int tail = 0;

            // Seed: the sound's own block, or its passable neighbors when the sound
            // sits inside a solid block (common for block-attached sounds).
            if (IsPassable(blockAccessor, sx, sy, sz))
            {
                int center = MAX_RADIUS | (MAX_RADIUS << 5) | (MAX_RADIUS << 10);
                _queue[tail++] = center;
                _visited[center] = 1;
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
                        int key = (nx - sx + MAX_RADIUS)
                                | ((ny - sy + MAX_RADIUS) << 5)
                                | ((nz - sz + MAX_RADIUS) << 10);
                        _queue[tail++] = key;
                        _visited[key] = 1;
                    }
                }
                // Fully embedded sound — can't establish a starting cavity.
                if (tail == 0) return Result.Inconclusive;
            }

            int nodesVisited = 0;

            while (head < tail)
            {
                int packed = _queue[head++];
                int cx = sx + (packed & 31) - MAX_RADIUS;
                int cy = sy + ((packed >> 5) & 31) - MAX_RADIUS;
                int cz = sz + ((packed >> 10) & 31) - MAX_RADIUS;

                if (cx == px && cy == py && cz == pz) return Result.Clear;

                if (++nodesVisited > MAX_NODES) return Result.Inconclusive;

                for (int n = 0; n < _neighbors.Length; n++)
                {
                    int nx = cx + _neighbors[n][0];
                    int ny = cy + _neighbors[n][1];
                    int nz = cz + _neighbors[n][2];

                    // Radius cap: AIR continuing past the search volume means the
                    // cavity is open-ended — Entombed is impossible, and the caller
                    // treats Clear and Inconclusive the same, so stop here. A solid
                    // block past the cap still seals the cavity.
                    if (System.Math.Abs(nx - sx) > MAX_RADIUS ||
                        System.Math.Abs(ny - sy) > MAX_RADIUS ||
                        System.Math.Abs(nz - sz) > MAX_RADIUS)
                    {
                        if (IsPassable(blockAccessor, nx, ny, nz))
                            return Result.Inconclusive;
                        continue;
                    }

                    // Listener check BEFORE passability: an ear position clipped into
                    // a solid block (snow layer, wall-embedded, bed clamp) must not read
                    // as a wall that seals the cavity — that would silence an audible sound.
                    if (nx == px && ny == py && nz == pz) return Result.Clear;

                    int key = (nx - sx + MAX_RADIUS)
                            | ((ny - sy + MAX_RADIUS) << 5)
                            | ((nz - sz + MAX_RADIUS) << 10);
                    if (_visited[key] != 0) continue;
                    _visited[key] = 1;

                    if (!IsPassable(blockAccessor, nx, ny, nz)) continue;

                    _queue[tail++] = key;
                }
            }

            // Frontier exhausted without leaving the box — the cavity is sealed and
            // the player is not inside it.
            return Result.Entombed;
        }
    }
}
