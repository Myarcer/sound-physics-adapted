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
    /// Cached reverb data for a spatial cell.
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
    /// Spatial reverb deduplication cache using composite keys.
    ///
    /// Purpose: batch dedup — when 50 entities are in the same pen, they share
    /// one reverb computation instead of 50. The cache should be IMPERCEPTIBLE:
    /// no audible staleness, no lag on player movement or rotation.
    ///
    /// Design: composite key = (soundCell, playerCell).
    /// - Player walks to new cell → automatic miss → fresh computation
    /// - 50 boars in same room → all share one computation per player-cell
    /// - No TTL needed for close/medium range (key change = instant invalidation)
    /// - Only far sounds (>48 blocks) get a modest TTL since perspective barely changes
    ///
    /// Wall-crossing protection: On cache hit, one DDA ray verifies the new sound
    /// is in the same acoustic zone as the cache creator. If a wall separates them,
    /// treat as cache miss (1 DDA vs ~280 DDA saved on valid hit).
    /// </summary>
    public class ReverbCellCache
    {
        // Sound cell: groups sounds in same 4x4x4 region
        private const int SOUND_CELL_SIZE = 4;
        // Player cell: groups player positions — smaller = more responsive to movement
        // 2 blocks = player takes ~1 step before cache invalidates
        private const int PLAYER_CELL_SIZE = 2;
        // Far player cell: for distant sounds, player perspective changes less
        private const int FAR_PLAYER_CELL_SIZE = 8;
        private const float FAR_DISTANCE = 48f;

        private const int MAX_CELLS = 512;

        // Only far sounds (>48 blocks) get TTL — close/medium invalidate via key change
        private const long FAR_TTL_MS = 5000;  // 5s for far sounds only

        private Dictionary<long, ReverbCellEntry> cells;

        // Stats
        private long totalHits = 0;
        private long totalMisses = 0;
        private long wallMisses = 0;

        public ReverbCellCache()
        {
            cells = new Dictionary<long, ReverbCellEntry>(64);
        }

        // === Composite key computation ===

        /// <summary>
        /// Pack composite (soundCell + playerCell) into a single long.
        /// Layout: [soundCellX:11][soundCellY:10][soundCellZ:11][playerCellX:11][playerCellY:10][playerCellZ:11]
        /// Total: 64 bits. Each axis uses enough bits for VS world range.
        /// </summary>
        private static long PackCompositeKey(Vec3d soundPos, Vec3d playerPos, float distance)
        {
            int sCellX = (int)Math.Floor(soundPos.X / SOUND_CELL_SIZE);
            int sCellY = (int)Math.Floor(soundPos.Y / SOUND_CELL_SIZE);
            int sCellZ = (int)Math.Floor(soundPos.Z / SOUND_CELL_SIZE);

            // Far sounds use larger player cells (less sensitive to small movements)
            int pCellSize = distance > FAR_DISTANCE ? FAR_PLAYER_CELL_SIZE : PLAYER_CELL_SIZE;
            int pCellX = (int)Math.Floor(playerPos.X / pCellSize);
            int pCellY = (int)Math.Floor(playerPos.Y / pCellSize);
            int pCellZ = (int)Math.Floor(playerPos.Z / pCellSize);

            // Pack: sound cells in upper 32 bits, player cells in lower 32 bits
            // Each cell coord gets 11/10/11 bits (same as original PackCellKey)
            long soundPart = ((long)(sCellX & 0x7FF) << 21) | ((long)(sCellY & 0x3FF) << 11) | (long)(sCellZ & 0x7FF);
            long playerPart = ((long)(pCellX & 0x7FF) << 21) | ((long)(pCellY & 0x3FF) << 11) | (long)(pCellZ & 0x7FF);

            return (soundPart << 32) | (playerPart & 0xFFFFFFFFL);
        }

        /// <summary>
        /// Pack sound-cell-only key for block invalidation (invalidate all player perspectives).
        /// Returns cell coords for iteration-based invalidation.
        /// </summary>
        private static (int cellX, int cellY, int cellZ) GetSoundCellCoords(double x, double y, double z)
        {
            return (
                (int)Math.Floor(x / SOUND_CELL_SIZE),
                (int)Math.Floor(y / SOUND_CELL_SIZE),
                (int)Math.Floor(z / SOUND_CELL_SIZE)
            );
        }

        /// <summary>
        /// Extract sound cell coords from a composite key (upper 32 bits).
        /// </summary>
        private static (int cellX, int cellY, int cellZ) ExtractSoundCell(long compositeKey)
        {
            long soundPart = (compositeKey >> 32) & 0xFFFFFFFFL;
            // Sign-extend 11-bit values
            int cellX = (int)((soundPart >> 21) & 0x7FF);
            if (cellX >= 0x400) cellX -= 0x800; // sign extend
            int cellY = (int)((soundPart >> 11) & 0x3FF);
            if (cellY >= 0x200) cellY -= 0x400;
            int cellZ = (int)(soundPart & 0x7FF);
            if (cellZ >= 0x400) cellZ -= 0x800;
            return (cellX, cellY, cellZ);
        }

        /// <summary>
        /// Try to get cached reverb for a sound position.
        /// Returns null if: no entry, expired (far sounds only), or wall between sound and cache creator.
        /// Sets canStore=true when caller should cache its result (no existing entry),
        /// canStore=false when an entry exists but is acoustically separated (wall between).
        /// </summary>
        public ReverbCellEntry TryGetCell(Vec3d soundPos, Vec3d playerPos,
            long currentTimeMs, IBlockAccessor blockAccessor, out bool canStore)
        {
            float distance = (float)soundPos.DistanceTo(playerPos);
            long key = PackCompositeKey(soundPos, playerPos, distance);

            if (!cells.TryGetValue(key, out var entry))
            {
                canStore = true;
                totalMisses++;
                return null;
            }

            // TTL check — only for far sounds. Close/medium invalidate via key change.
            if (distance > FAR_DISTANCE)
            {
                if (currentTimeMs - entry.CreatedTimeMs > FAR_TTL_MS)
                {
                    cells.Remove(key);
                    canStore = true;
                    totalMisses++;
                    return null;
                }
            }

            // ACOUSTIC ZONE CHECK: verify no wall between this sound and cache creator.
            // Cost: 1 DDA traversal (~1 DDA) vs full compute (~280 DDA). Negligible.
            Vec3d creatorPos = new Vec3d(entry.CreatorPosX, entry.CreatorPosY, entry.CreatorPosZ);
            float losToCreator = OcclusionCalculator.CalculatePathOcclusion(soundPos, creatorPos, blockAccessor);
            if (losToCreator >= 1.0f)
            {
                // Wall between them - different acoustic zone.
                // Return null but do NOT evict the existing entry.
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
        /// Only stores if no existing entry for this composite key.
        /// </summary>
        public void StoreCellIfEmpty(Vec3d soundPos, Vec3d playerPos, long currentTimeMs,
            ReverbResult reverb, BouncePoint[] bounces, int bounceCount,
            OpeningData[] openings, int openingCount, float sharedAirspaceRatio,
            float directOcclusion, bool hasDirectAirspace)
        {
            float distance = (float)soundPos.DistanceTo(playerPos);
            long key = PackCompositeKey(soundPos, playerPos, distance);

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

            int sCellX = (int)Math.Floor(soundPos.X / SOUND_CELL_SIZE);
            int sCellY = (int)Math.Floor(soundPos.Y / SOUND_CELL_SIZE);
            int sCellZ = (int)Math.Floor(soundPos.Z / SOUND_CELL_SIZE);
            string distLabel = distance > FAR_DISTANCE ? "far" : "near";
            SoundPhysicsAdaptedModSystem.DebugLog(
                $"[CELL-CACHE] STORE cell=({sCellX},{sCellY},{sCellZ}) bounces={bounceCount} openings={openingCount} dist={distance:F0} ({distLabel})");

            // LRU check
            if (cells.Count > MAX_CELLS)
            {
                Cleanup(currentTimeMs);
            }
        }

        /// <summary>
        /// Invalidate cells containing this block position + adjacent cells.
        /// Called on block change events.
        /// With composite keys, we must iterate and match on sound-cell portion.
        /// </summary>
        public void InvalidateCellAt(int blockX, int blockY, int blockZ)
        {
            var (targetCellX, targetCellY, targetCellZ) = GetSoundCellCoords(blockX, blockY, blockZ);

            // Collect adjacent cell coords to invalidate
            var cellsToInvalidate = new HashSet<(int, int, int)>();
            cellsToInvalidate.Add((targetCellX, targetCellY, targetCellZ));

            // Check if block is near cell boundary (within 1 block of edge)
            int localX = blockX - targetCellX * SOUND_CELL_SIZE;
            int localY = blockY - targetCellY * SOUND_CELL_SIZE;
            int localZ = blockZ - targetCellZ * SOUND_CELL_SIZE;

            if (localX == 0) cellsToInvalidate.Add((targetCellX - 1, targetCellY, targetCellZ));
            if (localX == SOUND_CELL_SIZE - 1) cellsToInvalidate.Add((targetCellX + 1, targetCellY, targetCellZ));
            if (localY == 0) cellsToInvalidate.Add((targetCellX, targetCellY - 1, targetCellZ));
            if (localY == SOUND_CELL_SIZE - 1) cellsToInvalidate.Add((targetCellX, targetCellY + 1, targetCellZ));
            if (localZ == 0) cellsToInvalidate.Add((targetCellX, targetCellY, targetCellZ - 1));
            if (localZ == SOUND_CELL_SIZE - 1) cellsToInvalidate.Add((targetCellX, targetCellY, targetCellZ + 1));

            // Iterate all entries and remove those whose sound cell matches any target
            List<long> toRemove = null;
            foreach (var kvp in cells)
            {
                var (cx, cy, cz) = ExtractSoundCell(kvp.Key);
                if (cellsToInvalidate.Contains((cx, cy, cz)))
                {
                    if (toRemove == null) toRemove = new List<long>();
                    toRemove.Add(kvp.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (var key in toRemove)
                    cells.Remove(key);
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[CELL-CACHE] INVALIDATE {toRemove.Count} entries near ({targetCellX},{targetCellY},{targetCellZ}) reason=block_change");
            }
        }

        /// <summary>
        /// Invalidate all cells within range of player.
        /// With composite keys, this is rarely needed — player movement
        /// naturally creates new keys. Kept for explicit invalidation (e.g. teleport).
        /// </summary>
        public void InvalidateNearPlayer(Vec3d playerPos, float radius)
        {
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
        /// LRU cleanup - remove oldest/expired cells when over MAX_CELLS.
        /// </summary>
        public void Cleanup(long currentTimeMs)
        {
            if (cells.Count <= MAX_CELLS / 2) return;

            // Remove expired far entries first
            List<long> expired = null;
            foreach (var kvp in cells)
            {
                var entry = kvp.Value;
                float dist = (float)Math.Sqrt(
                    (entry.CreatorPosX - entry.PlayerPosX) * (entry.CreatorPosX - entry.PlayerPosX) +
                    (entry.CreatorPosY - entry.PlayerPosY) * (entry.CreatorPosY - entry.PlayerPosY) +
                    (entry.CreatorPosZ - entry.PlayerPosZ) * (entry.CreatorPosZ - entry.PlayerPosZ));

                // Far entries expire by TTL; close/medium entries only expire by LRU
                if (dist > FAR_DISTANCE && currentTimeMs - entry.CreatedTimeMs > FAR_TTL_MS)
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

            // If still over limit, LRU evict down to half capacity
            if (cells.Count > MAX_CELLS)
            {
                int target = MAX_CELLS / 2;
                int toRemove = cells.Count - target;
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

            return $"CellCache: {cells.Count} cells, hits={totalHits} misses={totalMisses} wallMiss={wallMisses} hitRate={hitRate:F1}%";
        }

        public int CellCount => cells.Count;
    }
}
