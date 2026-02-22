using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Bounce point data captured during acoustic raytracing.
    /// Stored per cell for reuse by sounds in the same spatial region.
    /// </summary>
    public struct BouncePoint
    {
        public double PosX, PosY, PosZ;       // World position of bounce
        public double NormalX, NormalY, NormalZ; // Surface normal
        public float Reflectivity;             // Material reflectivity
        public float PathOcclusion;            // Occlusion from bounce to player (at cache time)
        public float TotalDistance;            // Distance along ray path to this bounce
        public int BounceIndex;                // Which bounce (0-3)
        public float Permeation;               // Cached permeation value
    }

    /// <summary>
    /// Opening data captured during probe-for-openings.
    /// Stored per cell for reuse by sounds in the same spatial region.
    /// </summary>
    public struct OpeningData
    {
        public double PosX, PosY, PosZ;       // Opening center
        public float OccToPlayer;              // Occlusion: opening -> player
        public float OccToCell;                // Occlusion: opening -> cell creator
        public int AdjacentAir;                // Opening size metric
        public float OpeningBoost;             // Pre-computed size boost
        public float DiffractionPenalty;       // Pre-computed diffraction
    }

    /// <summary>
    /// Cached reverb data for a 4x4x4 block cell.
    /// Contains the expensive raycast results that can be shared by
    /// sounds in the same spatial region.
    /// </summary>
    public class ReverbCellEntry
    {
        // === REVERB RESULT (the expensive 32-ray x 4-bounce calculation) ===
        public ReverbResult Reverb;

        // === BOUNCE HIT DATA (for per-sound path weighting) ===
        public BouncePoint[] BouncePoints;
        public int BouncePointCount;

        // === OPENING PROBE DATA (shared - openings are spatial) ===
        public OpeningData[] Openings;
        public int OpeningCount;

        // === METADATA ===
        public float SharedAirspaceRatio;
        public bool IsOutdoors;
        public long CreatedTimeMs;
        public long LastUsedTimeMs;
        public double PlayerPosX, PlayerPosY, PlayerPosZ;  // Player pos at creation
        public double CreatorPosX, CreatorPosY, CreatorPosZ; // Sound pos that created this entry
        public int UseCount;

        // Direct occlusion info for path resolution
        public float DirectOcclusion;
        public bool HasDirectAirspace;

        public ReverbCellEntry()
        {
            BouncePoints = new BouncePoint[256]; // 32 rays * 4 bounces max + some probe
            Openings = new OpeningData[24];       // 12 probes * ~2 openings each
        }
    }

    /// <summary>
    /// Spatial reverb cache that groups sounds by 4x4x4 block cells.
    /// 
    /// Core insight: reverb is a property of the SPACE, not the sound source.
    /// Two boars 2 blocks apart in the same roofed pen have essentially identical 
    /// reverb environments. Per-sound occlusion and path direction differ, but the 
    /// fibonacci ray bounce data is shared.
    /// 
    /// Wall-crossing protection: On cache hit, one DDA ray verifies the new sound
    /// is in the same acoustic zone as the cache creator. If a wall separates them,
    /// treat as cache miss (1 DDA vs ~280 DDA saved on valid hit).
    /// </summary>
    public class ReverbCellCache
    {
        private const int CELL_SIZE = 4;
        private const int MAX_CELLS = 512;

        private Dictionary<long, ReverbCellEntry> cells;

        // Stats
        private long totalHits = 0;
        private long totalMisses = 0;
        private long wallMisses = 0;

        public ReverbCellCache()
        {
            cells = new Dictionary<long, ReverbCellEntry>(64);
        }

        // === Cell key computation ===

        /// <summary>
        /// Pack cell coordinates into a single long for dictionary key.
        /// Same packing pattern as existing PackBlockPos.
        /// </summary>
        private static long PackCellKey(int cellX, int cellY, int cellZ)
        {
            return ((long)(cellX & 0x1FFFFF) << 42) | ((long)(cellY & 0xFFFFF) << 21) | (long)(cellZ & 0x1FFFFF);
        }

        private static long PackCellKey(Vec3d pos)
        {
            // Integer division gives cell coords (floor division for negative coords)
            int cellX = (int)Math.Floor(pos.X / CELL_SIZE);
            int cellY = (int)Math.Floor(pos.Y / CELL_SIZE);
            int cellZ = (int)Math.Floor(pos.Z / CELL_SIZE);
            return PackCellKey(cellX, cellY, cellZ);
        }

        private static long PackCellKey(double x, double y, double z)
        {
            int cellX = (int)Math.Floor(x / CELL_SIZE);
            int cellY = (int)Math.Floor(y / CELL_SIZE);
            int cellZ = (int)Math.Floor(z / CELL_SIZE);
            return PackCellKey(cellX, cellY, cellZ);
        }

        /// <summary>
        /// Distance-based TTL for cache entries.
        /// Close cells expire fast (player moves, perspective changes).
        /// Far cells persist longer (reverb barely changes at distance).
        /// </summary>
        private static long GetCellTTL(float distanceToPlayer)
        {
            if (distanceToPlayer < 16f) return 1000;   // 1s
            if (distanceToPlayer < 48f) return 5000;   // 5s
            return 20000;                               // 20s
        }

        /// <summary>
        /// Try to get cached reverb for a sound position.
        /// Returns null if: no entry, expired TTL, or wall between sound and cache creator.
        /// Sets canStore=true when caller should cache its result (no existing entry),
        /// canStore=false when an entry exists but is acoustically separated (wall between).
        /// </summary>
        public ReverbCellEntry TryGetCell(Vec3d soundPos, Vec3d playerPos,
            long currentTimeMs, IBlockAccessor blockAccessor, out bool canStore)
        {
            long key = PackCellKey(soundPos);

            if (!cells.TryGetValue(key, out var entry))
            {
                canStore = true;
                totalMisses++;
                return null;
            }

            // TTL check
            float cellCenterX = (float)(Math.Floor(soundPos.X / CELL_SIZE) * CELL_SIZE + CELL_SIZE * 0.5);
            float cellCenterY = (float)(Math.Floor(soundPos.Y / CELL_SIZE) * CELL_SIZE + CELL_SIZE * 0.5);
            float cellCenterZ = (float)(Math.Floor(soundPos.Z / CELL_SIZE) * CELL_SIZE + CELL_SIZE * 0.5);
            float distToPlayer = (float)Math.Sqrt(
                (cellCenterX - playerPos.X) * (cellCenterX - playerPos.X) +
                (cellCenterY - playerPos.Y) * (cellCenterY - playerPos.Y) +
                (cellCenterZ - playerPos.Z) * (cellCenterZ - playerPos.Z));

            long ttl = GetCellTTL(distToPlayer);
            if (currentTimeMs - entry.CreatedTimeMs > ttl)
            {
                // Expired - remove and allow new storage
                cells.Remove(key);
                canStore = true;
                totalMisses++;
                return null;
            }

            // Player movement check - if player moved significantly, reverb perspective changed
            double playerMoved = Math.Sqrt(
                (playerPos.X - entry.PlayerPosX) * (playerPos.X - entry.PlayerPosX) +
                (playerPos.Y - entry.PlayerPosY) * (playerPos.Y - entry.PlayerPosY) +
                (playerPos.Z - entry.PlayerPosZ) * (playerPos.Z - entry.PlayerPosZ));
            if (playerMoved > 4.0)
            {
                cells.Remove(key);
                canStore = true;
                totalMisses++;
                return null;
            }

            // ACOUSTIC ZONE CHECK: verify no wall between this sound and cache creator.
            // Cost: 1 DDA traversal (~1 DDA) vs full compute (~280 DDA). Negligible.
            Vec3d creatorPos = new Vec3d(entry.CreatorPosX, entry.CreatorPosY, entry.CreatorPosZ);
            float losToCreator = OcclusionCalculator.CalculatePathOcclusion(soundPos, creatorPos, blockAccessor);
            if (losToCreator >= 1.0f)
            {
                // Wall between them - different acoustic zone.
                // Return null but do NOT evict the existing entry.
                // The "wrong side" sound computes independently (correct behavior).
                canStore = false;
                wallMisses++;
                totalMisses++;
                return null;
            }

            // Valid cache hit
            entry.LastUsedTimeMs = currentTimeMs;
            entry.UseCount++;
            canStore = false;
            totalHits++;
            return entry;
        }

        /// <summary>
        /// Store computed reverb data for a cell.
        /// Only stores if no existing entry for this cell key.
        /// "Wrong side" sounds (failed acoustic zone check) compute independently
        /// but do NOT overwrite valid entries from the dominant acoustic zone.
        /// </summary>
        public void StoreCellIfEmpty(Vec3d soundPos, Vec3d playerPos, long currentTimeMs,
            ReverbResult reverb, BouncePoint[] bounces, int bounceCount,
            OpeningData[] openings, int openingCount, float sharedAirspaceRatio,
            float directOcclusion, bool hasDirectAirspace)
        {
            long key = PackCellKey(soundPos);

            // Only store if no existing entry (don't overwrite valid entries)
            if (cells.ContainsKey(key)) return;

            var entry = new ReverbCellEntry();
            entry.Reverb = reverb;

            // Copy bounce data
            if (bounceCount > entry.BouncePoints.Length)
                entry.BouncePoints = new BouncePoint[bounceCount];
            Array.Copy(bounces, entry.BouncePoints, bounceCount);
            entry.BouncePointCount = bounceCount;

            // Copy opening data
            if (openingCount > entry.Openings.Length)
                entry.Openings = new OpeningData[openingCount];
            if (openingCount > 0)
                Array.Copy(openings, entry.Openings, openingCount);
            entry.OpeningCount = openingCount;

            entry.SharedAirspaceRatio = sharedAirspaceRatio;
            entry.CreatedTimeMs = currentTimeMs;
            entry.LastUsedTimeMs = currentTimeMs;
            entry.PlayerPosX = playerPos.X;
            entry.PlayerPosY = playerPos.Y;
            entry.PlayerPosZ = playerPos.Z;
            entry.CreatorPosX = soundPos.X;
            entry.CreatorPosY = soundPos.Y;
            entry.CreatorPosZ = soundPos.Z;
            entry.DirectOcclusion = directOcclusion;
            entry.HasDirectAirspace = hasDirectAirspace;
            entry.UseCount = 1;

            cells[key] = entry;

            SoundPhysicsAdaptedModSystem.DebugLog(
                $"[CELL-CACHE] STORE cell=({(int)Math.Floor(soundPos.X / CELL_SIZE)},{(int)Math.Floor(soundPos.Y / CELL_SIZE)},{(int)Math.Floor(soundPos.Z / CELL_SIZE)}) bounces={bounceCount} openings={openingCount} ttl={GetCellTTL((float)playerPos.DistanceTo(soundPos))}ms");

            // LRU check
            if (cells.Count > MAX_CELLS)
            {
                Cleanup(currentTimeMs);
            }
        }

        /// <summary>
        /// Invalidate cell containing this block position + adjacent cells.
        /// Called on block change events.
        /// </summary>
        public void InvalidateCellAt(int blockX, int blockY, int blockZ)
        {
            // Invalidate the cell containing this block
            long key = PackCellKey((double)blockX, (double)blockY, (double)blockZ);
            if (cells.Remove(key))
            {
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[CELL-CACHE] INVALIDATE cell=({blockX / CELL_SIZE},{blockY / CELL_SIZE},{blockZ / CELL_SIZE}) reason=block_change");
            }

            // Also invalidate adjacent cells (block change at cell boundary affects neighbor's reverb)
            int cellX = (int)Math.Floor((double)blockX / CELL_SIZE);
            int cellY = (int)Math.Floor((double)blockY / CELL_SIZE);
            int cellZ = (int)Math.Floor((double)blockZ / CELL_SIZE);

            // Check if block is near cell boundary (within 1 block of edge)
            int localX = blockX - cellX * CELL_SIZE;
            int localY = blockY - cellY * CELL_SIZE;
            int localZ = blockZ - cellZ * CELL_SIZE;

            if (localX == 0) cells.Remove(PackCellKey(cellX - 1, cellY, cellZ));
            if (localX == CELL_SIZE - 1) cells.Remove(PackCellKey(cellX + 1, cellY, cellZ));
            if (localY == 0) cells.Remove(PackCellKey(cellX, cellY - 1, cellZ));
            if (localY == CELL_SIZE - 1) cells.Remove(PackCellKey(cellX, cellY + 1, cellZ));
            if (localZ == 0) cells.Remove(PackCellKey(cellX, cellY, cellZ - 1));
            if (localZ == CELL_SIZE - 1) cells.Remove(PackCellKey(cellX, cellY, cellZ + 1));
        }

        /// <summary>
        /// Invalidate all cells within range of player (movement invalidation).
        /// </summary>
        public void InvalidateNearPlayer(Vec3d playerPos, float radius)
        {
            int cellRadius = (int)Math.Ceiling(radius / CELL_SIZE);
            int centerCellX = (int)Math.Floor(playerPos.X / CELL_SIZE);
            int centerCellY = (int)Math.Floor(playerPos.Y / CELL_SIZE);
            int centerCellZ = (int)Math.Floor(playerPos.Z / CELL_SIZE);

            // Remove cells within radius
            List<long> toRemove = null;
            foreach (var kvp in cells)
            {
                var entry = kvp.Value;
                double dx = entry.CreatorPosX - playerPos.X;
                double dy = entry.CreatorPosY - playerPos.Y;
                double dz = entry.CreatorPosZ - playerPos.Z;
                if (dx * dx + dy * dy + dz * dz < radius * radius)
                {
                    if (toRemove == null) toRemove = new List<long>();
                    toRemove.Add(kvp.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (var key in toRemove)
                    cells.Remove(key);
            }
        }

        /// <summary>
        /// Full clear (config change, etc.)
        /// </summary>
        public void Clear()
        {
            cells.Clear();
            totalHits = 0;
            totalMisses = 0;
            wallMisses = 0;
        }

        /// <summary>
        /// LRU cleanup - remove oldest cells when over MAX_CELLS.
        /// Called periodically (every ~5 seconds) or when exceeding max.
        /// </summary>
        public void Cleanup(long currentTimeMs)
        {
            if (cells.Count <= MAX_CELLS / 2) return;

            // Remove expired entries first
            List<long> expired = null;
            foreach (var kvp in cells)
            {
                var entry = kvp.Value;
                float dist = (float)Math.Sqrt(
                    (entry.CreatorPosX - entry.PlayerPosX) * (entry.CreatorPosX - entry.PlayerPosX) +
                    (entry.CreatorPosY - entry.PlayerPosY) * (entry.CreatorPosY - entry.PlayerPosY) +
                    (entry.CreatorPosZ - entry.PlayerPosZ) * (entry.CreatorPosZ - entry.PlayerPosZ));
                long ttl = GetCellTTL(dist);
                if (currentTimeMs - entry.CreatedTimeMs > ttl)
                {
                    if (expired == null) expired = new List<long>();
                    expired.Add(kvp.Key);
                }
            }
            if (expired != null)
            {
                foreach (var key in expired)
                    cells.Remove(key);
            }

            // If still over limit, collect all entries sorted by LRU and evict down to half capacity
            if (cells.Count > MAX_CELLS)
            {
                int target = MAX_CELLS / 2;
                int toRemove = cells.Count - target;
                // Single-pass: collect all keys with their LRU time, sort, remove oldest
                var lruList = new List<(long key, long lastUsed)>(cells.Count);
                foreach (var kvp in cells)
                    lruList.Add((kvp.Key, kvp.Value.LastUsedTimeMs));
                lruList.Sort((a, b) => a.lastUsed.CompareTo(b.lastUsed));
                for (int i = 0; i < toRemove && i < lruList.Count; i++)
                    cells.Remove(lruList[i].key);
            }
        }

        /// <summary>
        /// Get statistics string for debug display.
        /// </summary>
        public string GetStats()
        {
            long total = totalHits + totalMisses;
            float hitRate = total > 0 ? (float)totalHits / total * 100f : 0f;

            int closeCount = 0, medCount = 0, farCount = 0;
            foreach (var kvp in cells)
            {
                var e = kvp.Value;
                float dist = (float)Math.Sqrt(
                    (e.CreatorPosX - e.PlayerPosX) * (e.CreatorPosX - e.PlayerPosX) +
                    (e.CreatorPosY - e.PlayerPosY) * (e.CreatorPosY - e.PlayerPosY) +
                    (e.CreatorPosZ - e.PlayerPosZ) * (e.CreatorPosZ - e.PlayerPosZ));
                if (dist < 16f) closeCount++;
                else if (dist < 48f) medCount++;
                else farCount++;
            }

            return $"CellCache: {cells.Count} cells, hits={totalHits} misses={totalMisses} wallMiss={wallMisses} hitRate={hitRate:F1}%\n" +
                   $"  Close(1s): {closeCount} cells  Medium(5s): {medCount} cells  Far(20s): {farCount} cells";
        }

        public int CellCount => cells.Count;
    }
}
