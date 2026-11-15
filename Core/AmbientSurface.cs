using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// The open faces of an ambient volume's own blocks near the player: the surface of
    /// a pool or river, the open sides of a waterfall. One instance per ambient sound.
    ///
    /// Vintage Story keeps a volume as one box per 9x9x9 scan section around every block
    /// that plays the sound, so a box also covers the shore and the land between pools.
    /// A position on such a box can sit blocks away over dry land, a player on the bank
    /// stands inside it, and the terrain inside it cannot occlude. Near the player the
    /// real faces replace the boxes; farther out the box error is small next to the distance.
    /// </summary>
    internal sealed class AmbientSurface
    {
        /// <summary>Faces are collected up to this many blocks from the player, per axis.</summary>
        private const int RADIUS = 12;

        /// <summary>Rebuild once the player is this many blocks from the last build point.</summary>
        private const int REBUILD_MOVE = 2;

        /// <summary>Candidates handed to the resolver, one occlusion test each.</summary>
        private const int CANDIDATES = 6;

        /// <summary>
        /// Minimum distance between two candidates. Neighbouring faces behind the same wall
        /// give the same occlusion, so they would only repeat the test.
        /// </summary>
        private const double CANDIDATE_SPACING = 2.0;

        /// <summary>Sample points sit this far out in the open neighbour, so the ray starts in air.</summary>
        private const double FACE_OUTSET = 0.05;

        /// <summary>Keeps the clamped point off the face edges, where a ray clips block corners.</summary>
        private const double EDGE_INSET = 0.15;

        private const byte IN_VOLUME = 1;
        private const byte OWN = 2;
        private const byte OPEN = 4;

        // Scratch grid shared by every rebuild. The ambient update runs on the main thread only.
        private static byte[] grid = Array.Empty<byte>();
        private static readonly BlockSelection blockSel = new BlockSelection();

        // Every ambient asset some block type carries. A sound whose asset is not in here
        // was renamed after the scan: Watersheds gives the water of a whole section its
        // river sound, and the blocks keep the vanilla one.
        private static HashSet<AssetLocation> blockAmbientAssets;

        private struct Face
        {
            public int X, Y, Z;
            public BlockFacing Facing;
        }

        private readonly List<Face> faces = new();
        private int[] builtBoxes = Array.Empty<int>();
        private IList builtFrom;
        private int builtX, builtY, builtZ;
        private bool built;
        private AssetLocation asset;
        private bool waterFallback;

        public int FaceCount => faces.Count;

        public double LastBuildMs { get; private set; }

        /// <summary>
        /// Rebuilds the faces when the boxes changed or the player moved away from the last
        /// build point. Returns true when it rebuilt.
        /// </summary>
        public bool Update(IList boxes, AssetLocation soundAsset, Vec3d eye, int dim, IBlockAccessor ba)
        {
            int px = (int)Math.Floor(eye.X);
            int py = (int)Math.Floor(eye.Y);
            int pz = (int)Math.Floor(eye.Z);

            bool sameBoxes = ReferenceEquals(boxes, builtFrom);
            if (!sameBoxes && SameBoxes(boxes))
            {
                // Every rescan hands over a new list, mostly with the same boxes in it.
                builtFrom = boxes;
                sameBoxes = true;
            }

            if (built && sameBoxes && Equals(soundAsset, asset)
                && Math.Abs(px - builtX) < REBUILD_MOVE
                && Math.Abs(py - builtY) < REBUILD_MOVE
                && Math.Abs(pz - builtZ) < REBUILD_MOVE)
                return false;

            var watch = Stopwatch.StartNew();
            asset = soundAsset;
            waterFallback = !BlockAmbientAssets().Contains(soundAsset);
            Build(boxes, px, py, pz, dim, ba);
            LastBuildMs = watch.Elapsed.TotalMilliseconds;

            builtFrom = boxes;
            StoreBoxes(boxes);
            builtX = px;
            builtY = py;
            builtZ = pz;
            built = true;
            return true;
        }

        /// <summary>
        /// True when the head is in one of the sound's own blocks. That is the only place
        /// where the volume surrounds the listener; on the bank or above the water it does not.
        /// </summary>
        public bool HeadInside(IList boxes, Vec3d eye, int dim, IBlockAccessor ba)
        {
            if (!built) return false;

            int x = (int)Math.Floor(eye.X);
            int y = (int)Math.Floor(eye.Y);
            int z = (int)Math.Floor(eye.Z);

            bool inBox = false;
            foreach (var o in boxes)
            {
                // Half-open boxes: the block at X covers [X, X+1).
                if (o is Cuboidi b && x >= b.X1 && x < b.X2 && y >= b.Y1 && y < b.Y2 && z >= b.Z1 && z < b.Z2)
                {
                    inBox = true;
                    break;
                }
            }
            if (!inBox) return false;

            // Under the surface the water does not play itself, but the head is still in it.
            var pos = new BlockPos(x, y, z, dim);
            return IsOwnBlock(ba.GetBlock(pos), pos, ba, mustPlay: false);
        }

        /// <summary>
        /// Adds the nearest open faces, at least CANDIDATE_SPACING apart, to <paramref name="into"/>.
        /// The sample point is the eye position clamped onto the face, so it slides along a
        /// bank instead of jumping from block to block. The face center names the face for
        /// the resolver's hysteresis.
        /// </summary>
        public void AddCandidates(Vec3d eye, List<AmbientSoundPatches.FaceSample> into)
        {
            int start = into.Count;
            double spacingSq = CANDIDATE_SPACING * CANDIDATE_SPACING;

            for (int pick = 0; pick < CANDIDATES; pick++)
            {
                int best = -1;
                double bestDistSq = double.MaxValue;

                for (int i = 0; i < faces.Count; i++)
                {
                    var face = faces[i];
                    var n = face.Facing.Normali;
                    double cx = CenterCoord(face.X, n.X);
                    double cy = CenterCoord(face.Y, n.Y);
                    double cz = CenterCoord(face.Z, n.Z);

                    bool tooClose = false;
                    for (int j = start; j < into.Count; j++)
                    {
                        var c = into[j].FaceCenter;
                        double dx = c.X - cx, dy = c.Y - cy, dz = c.Z - cz;
                        if (dx * dx + dy * dy + dz * dz < spacingSq)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (tooClose) continue;

                    double ex = FaceCoord(face.X, n.X, eye.X) - eye.X;
                    double ey = FaceCoord(face.Y, n.Y, eye.Y) - eye.Y;
                    double ez = FaceCoord(face.Z, n.Z, eye.Z) - eye.Z;
                    double distSq = ex * ex + ey * ey + ez * ez;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = i;
                    }
                }

                if (best < 0) break;

                var chosen = faces[best];
                var cn = chosen.Facing.Normali;
                into.Add(new AmbientSoundPatches.FaceSample
                {
                    SamplePoint = new Vec3d(
                        FaceCoord(chosen.X, cn.X, eye.X),
                        FaceCoord(chosen.Y, cn.Y, eye.Y),
                        FaceCoord(chosen.Z, cn.Z, eye.Z)),
                    FaceCenter = new Vec3d(
                        CenterCoord(chosen.X, cn.X),
                        CenterCoord(chosen.Y, cn.Y),
                        CenterCoord(chosen.Z, cn.Z)),
                    Cell = new Cuboidi(chosen.X, chosen.Y, chosen.Z, chosen.X, chosen.Y, chosen.Z)
                });
            }
        }

        /// <summary>Forget the asset table, for a new world or a mod reload.</summary>
        public static void ClearShared()
        {
            blockAmbientAssets = null;
        }

        /// <summary>
        /// One coordinate of a point on a face. On the face normal axis the point sits just
        /// outside the block; on the other two axes <paramref name="along"/> is clamped into the face.
        /// </summary>
        private static double FaceCoord(int cell, int normal, double along)
        {
            if (normal != 0) return NormalCoord(cell, normal);
            return Math.Clamp(along, cell + EDGE_INSET, cell + 1 - EDGE_INSET);
        }

        /// <summary>One coordinate of a face center.</summary>
        private static double CenterCoord(int cell, int normal)
        {
            return normal != 0 ? NormalCoord(cell, normal) : cell + 0.5;
        }

        private static double NormalCoord(int cell, int normal)
        {
            return normal > 0 ? cell + 1 + FACE_OUTSET : cell - FACE_OUTSET;
        }

        private void Build(IList boxes, int px, int py, int pz, int dim, IBlockAccessor ba)
        {
            faces.Clear();

            // The union of the boxes inside the radius, in inclusive block coordinates.
            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
            foreach (var o in boxes)
            {
                if (o is not Cuboidi b) continue;
                minX = Math.Min(minX, b.X1);
                minY = Math.Min(minY, b.Y1);
                minZ = Math.Min(minZ, b.Z1);
                maxX = Math.Max(maxX, b.X2 - 1);
                maxY = Math.Max(maxY, b.Y2 - 1);
                maxZ = Math.Max(maxZ, b.Z2 - 1);
            }
            minX = Math.Max(minX, px - RADIUS);
            minY = Math.Max(minY, py - RADIUS);
            minZ = Math.Max(minZ, pz - RADIUS);
            maxX = Math.Min(maxX, px + RADIUS);
            maxY = Math.Min(maxY, py + RADIUS);
            maxZ = Math.Min(maxZ, pz + RADIUS);
            if (minX > maxX || minY > maxY || minZ > maxZ) return;

            // One extra cell on every side, so each face has its neighbour in the grid.
            int gx = minX - 1, gy = minY - 1, gz = minZ - 1;
            int sx = maxX - minX + 3, sy = maxY - minY + 3, sz = maxZ - minZ + 3;
            int size = sx * sy * sz;
            if (grid.Length < size) grid = new byte[size];
            Array.Clear(grid, 0, size);

            foreach (var o in boxes)
            {
                if (o is not Cuboidi b) continue;
                int x0 = Math.Max(b.X1, minX), x1 = Math.Min(b.X2 - 1, maxX);
                int y0 = Math.Max(b.Y1, minY), y1 = Math.Min(b.Y2 - 1, maxY);
                int z0 = Math.Max(b.Z1, minZ), z1 = Math.Min(b.Z2 - 1, maxZ);
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++)
                            grid[((y - gy) * sz + (z - gz)) * sx + (x - gx)] |= IN_VOLUME;
            }

            // One block read per cell. The default layer is what the vanilla scan reads:
            // the solid block, or the fluid when there is none.
            var pos = new BlockPos(dim);
            for (int y = 0; y < sy; y++)
            {
                for (int z = 0; z < sz; z++)
                {
                    for (int x = 0; x < sx; x++)
                    {
                        int i = (y * sz + z) * sx + x;
                        pos.Set(gx + x, gy + y, gz + z);
                        Block block = ba.GetBlock(pos);

                        if (block == null || block.Id == 0)
                        {
                            grid[i] |= OPEN;
                        }
                        else if ((grid[i] & IN_VOLUME) != 0 && IsOwnBlock(block, pos, ba, mustPlay: true))
                        {
                            grid[i] |= OWN;
                        }
                        else if (!BlockClassification.IsLiquidMaterial(block)
                            && !BlockClassification.IsSolidForOcclusion(block))
                        {
                            grid[i] |= OPEN;
                        }
                    }
                }
            }

            for (int y = 1; y < sy - 1; y++)
            {
                for (int z = 1; z < sz - 1; z++)
                {
                    for (int x = 1; x < sx - 1; x++)
                    {
                        if ((grid[(y * sz + z) * sx + x] & OWN) == 0) continue;

                        foreach (var facing in BlockFacing.ALLFACES)
                        {
                            var n = facing.Normali;
                            if ((grid[((y + n.Y) * sz + (z + n.Z)) * sx + (x + n.X)] & OPEN) == 0) continue;
                            faces.Add(new Face { X = gx + x, Y = gy + y, Z = gz + z, Facing = facing });
                        }
                    }
                }
            }
        }

        /// <summary>
        /// True for a block of this sound. With <paramref name="mustPlay"/> it must also play
        /// right now, the test the vanilla scan applies: still water only in the shallows,
        /// a Watersheds river only in its top cell, a sheltered metal block not at all.
        /// </summary>
        private bool IsOwnBlock(Block block, BlockPos pos, IBlockAccessor ba, bool mustPlay)
        {
            if (block == null || block.Id == 0) return false;

            blockSel.Position = pos;
            var ambient = block.GetSounds(ba, blockSel)?.Ambient;
            if (ambient == null) return false;
            if (!ambient.Equals(asset) && !(waterFallback && block.BlockMaterial == EnumBlockMaterial.Water))
                return false;

            if (!mustPlay) return true;
            var world = SoundPhysicsAdaptedModSystem.ClientApi?.World;
            return world == null || block.GetAmbientSoundStrength(world, pos) > 0f;
        }

        private static HashSet<AssetLocation> BlockAmbientAssets()
        {
            if (blockAmbientAssets != null) return blockAmbientAssets;

            // Built on first use, after the level finalized, so the ambient sounds this mod
            // injects into metal and torch blocks are in it.
            var set = new HashSet<AssetLocation>();
            var blocks = SoundPhysicsAdaptedModSystem.ClientApi?.World?.Blocks;
            if (blocks != null)
            {
                foreach (var block in blocks)
                {
                    var ambient = block?.Sounds?.Ambient;
                    if (ambient != null) set.Add(ambient);
                }
            }
            blockAmbientAssets = set;
            return set;
        }

        private bool SameBoxes(IList boxes)
        {
            if (boxes == null || boxes.Count * 6 != builtBoxes.Length) return false;
            int k = 0;
            foreach (var o in boxes)
            {
                if (o is not Cuboidi b) return false;
                if (builtBoxes[k] != b.X1 || builtBoxes[k + 1] != b.Y1 || builtBoxes[k + 2] != b.Z1
                    || builtBoxes[k + 3] != b.X2 || builtBoxes[k + 4] != b.Y2 || builtBoxes[k + 5] != b.Z2)
                    return false;
                k += 6;
            }
            return true;
        }

        private void StoreBoxes(IList boxes)
        {
            if (builtBoxes.Length != boxes.Count * 6) builtBoxes = new int[boxes.Count * 6];
            int k = 0;
            foreach (var o in boxes)
            {
                if (o is Cuboidi b)
                {
                    builtBoxes[k] = b.X1;
                    builtBoxes[k + 1] = b.Y1;
                    builtBoxes[k + 2] = b.Z1;
                    builtBoxes[k + 3] = b.X2;
                    builtBoxes[k + 4] = b.Y2;
                    builtBoxes[k + 5] = b.Z2;
                }
                k += 6;
            }
        }
    }
}
