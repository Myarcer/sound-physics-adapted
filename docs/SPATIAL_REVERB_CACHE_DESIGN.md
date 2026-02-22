# Spatial Reverb Cache — Implementation Design

## Problem Statement

When 32+ entities (e.g., boars in a pen) occupy the same roofed area, the mod processes
each sound independently through the full raycast pipeline (~280 DDA traversals per sound).
Combined with the budget bypass for overdue/new sounds, this creates a spike of ~9,000
DDA traversals in a single 50ms tick, causing noticeable lag.

SPR Minecraft has zero batch processing — every sound runs independently. We're innovating
beyond SPR here.

---

## Architecture Overview

Two independent optimizations that complement each other:

### 1. Reverb Cell Cache (primary — ~10x reduction for clustered sounds)
### 2. Overdue Budget Cap (secondary — prevents burst spikes)
### 3. Allocation cleanup (tertiary — reduces GC pressure)

---

## 1. Reverb Cell Cache

### Core Insight

Reverb is a property of the **space**, not the sound source. Two boars 2 blocks apart in
the same roofed pen have essentially identical reverb environments. Per-sound occlusion
and path direction differ (different angles to player), but the fibonacci ray bounce data
is shared.

### Cell Grid

- **Cell size**: 4x4x4 blocks
- **Key**: `(int)(x/4), (int)(y/4), (int)(z/4)` — integer division gives cell coords
- **Storage**: `Dictionary<long, ReverbCellEntry>` where key = packed cell coords

```
Cell key packing (same pattern as existing PackBlockPos):
long key = ((long)(cellX & 0x1FFFFF) << 42) | ((long)(cellY & 0xFFFFF) << 21) | (long)(cellZ & 0x1FFFFF);
```

### What Gets Cached Per Cell

```csharp
class ReverbCellEntry
{
    // === REVERB RESULT (the expensive 32-ray x 4-bounce calculation) ===
    public ReverbResult Reverb;              // sendGain0-3, sendCutoff0-3

    // === BOUNCE HIT DATA (for per-sound path weighting) ===
    // Store the bounce points + normals from the fibonacci ray loop.
    // Per-sound path resolution reuses these instead of re-raycasting.
    public BouncePoint[] BouncePoints;       // Pre-allocated array
    public int BouncePointCount;             // How many are populated

    // === OPENING PROBE DATA (shared — openings are spatial) ===
    // ProbeForOpenings results: the opening positions + their properties
    public OpeningData[] Openings;           // Pre-allocated array
    public int OpeningCount;

    // === METADATA ===
    public float SharedAirspaceRatio;
    public bool IsOutdoors;                  // From sky probe at cache time
    public long CreatedTimeMs;               // For expiry
    public long LastUsedTimeMs;              // For LRU cleanup
    public Vec3d PlayerPosAtCreation;        // For movement invalidation
    public Vec3d CreatorSoundPos;            // Position of sound that created this entry
    public int UseCount;                     // Debug stats
}

struct BouncePoint
{
    public Vec3d Position;         // World position of bounce
    public Vec3d Normal;           // Surface normal
    public float Reflectivity;     // Material reflectivity
    public float PathOcclusion;    // Occlusion from bounce to player (at cache time)
    public float TotalDistance;    // Distance along ray path to this bounce
    public int BounceIndex;        // Which bounce (0-3)
    public float Permeation;       // Cached permeation value
}

struct OpeningData
{
    public Vec3d Position;         // Opening center
    public float OccToPlayer;      // Occlusion: opening -> player
    public float OccToCell;        // Occlusion: opening -> cell center
    public int AdjacentAir;        // Opening size metric
    public float OpeningBoost;     // Pre-computed size boost
    public float DiffractionPenalty; // Pre-computed diffraction
}
```

### Cache Lifetime — Distance-Based Expiry

```
Distance from player to cell center:
  0-16 blocks  (close):   1 second TTL
  16-48 blocks (medium):  5 seconds TTL
  48+ blocks   (far):    20 seconds TTL
```

Implementation:
```csharp
private long GetCellTTL(float distanceToPlayer)
{
    if (distanceToPlayer < 16f) return 1000;   // 1s
    if (distanceToPlayer < 48f) return 5000;   // 5s
    return 20000;                               // 20s
}
```

### Wall-Crossing Protection (Acoustic Zone Check)

**Problem**: A 4x4x4 cell can straddle a wall. Two sounds on opposite sides of a
wall map to the same cell key but have completely different reverb environments.
Indoor reverb (high gains) applied to an outdoor sound = audibly wrong.

```
Same cell, different acoustic zones:
|  Boar A   |WALL|  Boar B   |
|  (indoor)  | W |  (outdoor) |
|  reverb OK | A |  WRONG!    |
|            | L |            |
```

**Solution**: On cache hit, run **one DDA ray** from the new sound position to the
position that created the cache entry (`CreatorSoundPos`). If occluded (wall between
them), they're in different acoustic zones → treat as cache miss.

```csharp
public ReverbCellEntry TryGetCell(Vec3d soundPos, Vec3d playerPos,
    long currentTimeMs, IBlockAccessor blockAccessor)
{
    long key = PackCellKey(soundPos);
    if (!cells.TryGetValue(key, out var entry)) return null;

    // TTL check...

    // ACOUSTIC ZONE CHECK: verify no wall between this sound and cache creator.
    // Cost: 1 DDA traversal (~1 DDA) vs full compute (~280 DDA). Negligible.
    float losToCreator = OcclusionCalculator.CalculatePathOcclusion(
        soundPos, entry.CreatorSoundPos, blockAccessor);
    if (losToCreator >= 1.0f)
    {
        // Wall between them — different acoustic zone.
        // Return null (cache miss) but do NOT evict the existing entry.
        // The "wrong side" sound computes independently (correct behavior).
        // It does NOT store its result (would overwrite the valid entry).
        return null;
    }

    entry.LastUsedTimeMs = currentTimeMs;
    entry.UseCount++;
    return entry;
}
```

**Cost**: 1 extra DDA per cache hit. Saving ~280 DDA on a valid hit. Net: still ~279 DDA saved.

**Behavior for "wrong side" sounds**:
- They compute independently every time (no caching benefit)
- They do NOT overwrite the existing cell entry
- This is fine because the typical scenario (32 boars in a pen) has all boars on
  the SAME side — the edge case involves 2-3 boars near a wall boundary
- If performance matters for those too, a future extension could support multiple
  entries per cell key (zone list), but this is unnecessary for v1

**Why 1.0 threshold?**:
- `>= 1.0` means at least one solid block between them
- `< 1.0` means clear LOS or only foliage/plants (same acoustic zone)
- Matches the existing `skipRepositioning` threshold in AudioPhysicsSystem

### Cache Invalidation

Three invalidation triggers:

1. **Time expiry**: Check TTL on access (lazy expiry, no timer needed)
2. **Player movement**: If player moved > 4 blocks since `PlayerPosAtCreation`,
   invalidate cells within 32 blocks (reverb perspective changes with position)
3. **Block change**: `OnBlockChanged(BlockPos pos)` → compute cell key for pos →
   remove that cell entry. Also invalidate adjacent cells (block change at cell
   boundary affects neighbor's reverb)

For block changes, the existing `OnBlockChanged` in `SoundPhysicsAdaptedModSystem.cs`
already debounces at 200ms. Extend it to also do cell-targeted invalidation
instead of (or in addition to) the full `InvalidateCache()`:

```csharp
private void OnBlockChanged(BlockPos pos, Block oldBlock)
{
    // ... existing debounce logic ...

    // Targeted cell invalidation (cheap — just dictionary remove)
    reverbCellCache?.InvalidateCellAt(pos.X, pos.Y, pos.Z);

    // Still invalidate the per-sound static cache too
    // (occlusion cache is per-sound, not per-cell)
    acousticsManager.InvalidateCache();
}
```

### New File: `Core/ReverbCellCache.cs`

```csharp
public class ReverbCellCache
{
    private const int CELL_SIZE = 4;
    private const int MAX_BOUNCE_POINTS = 256;  // 32 rays * 4 bounces max + some probe
    private const int MAX_OPENINGS = 24;         // 12 probes * ~2 openings each
    private const int MAX_CELLS = 512;            // LRU cap

    private Dictionary<long, ReverbCellEntry> cells;

    // === PUBLIC API ===

    /// <summary>
    /// Try to get cached reverb for a sound position.
    /// Returns null if: no entry, expired TTL, or wall between sound and cache creator.
    /// Requires blockAccessor for the acoustic zone LOS check.
    /// Sets canStore=true when caller should cache its result (no existing entry),
    /// canStore=false when an entry exists but is acoustically separated (wall between).
    /// </summary>
    public ReverbCellEntry TryGetCell(Vec3d soundPos, Vec3d playerPos,
        long currentTimeMs, IBlockAccessor blockAccessor, out bool canStore);

    /// <summary>
    /// Store computed reverb data for a cell.
    /// Called after the first sound in a cell computes full raycast.
    /// IMPORTANT: Only stores if no existing entry for this cell key.
    /// "Wrong side" sounds (failed acoustic zone check) compute independently
    /// but do NOT overwrite valid entries from the dominant acoustic zone.
    /// </summary>
    public void StoreCellIfEmpty(Vec3d soundPos, Vec3d playerPos, long currentTimeMs,
        ReverbResult reverb, BouncePoint[] bounces, int bounceCount,
        OpeningData[] openings, int openingCount, float sharedAirspaceRatio);

    /// <summary>
    /// Invalidate cell containing this block position + adjacent cells.
    /// Called on block change events.
    /// </summary>
    public void InvalidateCellAt(int blockX, int blockY, int blockZ);

    /// <summary>
    /// Invalidate all cells within range of player (movement invalidation).
    /// </summary>
    public void InvalidateNearPlayer(Vec3d playerPos, float radius);

    /// <summary>
    /// Full clear (config change, etc.)
    /// </summary>
    public void Clear();

    /// <summary>
    /// LRU cleanup — remove oldest cells when over MAX_CELLS.
    /// Called periodically (every ~5 seconds).
    /// </summary>
    public void Cleanup(long currentTimeMs);
}
```

### Integration into AudioPhysicsSystem.ProcessSoundRaycast

The key change in `ProcessSoundRaycast()`:

```
BEFORE (current flow):
  1. OcclusionCalculator.Calculate(soundPos, playerPos)     — PER SOUND (keep)
  2. AcousticRaytracer.CalculateWithPaths(soundPos, ...)    — PER SOUND (expensive!)
  3. Apply reverb to source
  4. Apply occlusion filter

AFTER (with cell cache):
  1. OcclusionCalculator.Calculate(soundPos, playerPos)     — PER SOUND (keep)
  2. reverbCellCache.TryGetCell(soundPos, playerPos, time, blockAccessor)
     a. HIT  → Use cached ReverbResult + cached bounce data for path weighting
     b. MISS (no entry) → Full compute → StoreCellIfEmpty (first sound creates entry)
     c. MISS (wall) → Full compute → Do NOT store (wrong-side sound, entry exists)
  3. Per-sound path resolution using cached bounce data     — CHEAP (just math)
  4. Apply reverb to source
  5. Apply occlusion filter

  The TryGetCell miss reason matters:
  - "No entry" miss: compute and store (this sound becomes the cell creator)
  - "Wall between" miss: compute but don't store (preserves the valid entry
    for sounds on the dominant side of the wall)
```

### Per-Sound Path Resolution from Cached Bounce Data

When a cell cache hit occurs, we skip the full raycast but still need per-sound
path direction (each boar has a different angle to the player). This is done by
re-running ONLY the SoundPathResolver weighting math using cached bounce points:

```csharp
// Pseudo-code for per-sound path resolution from cache
SoundPathResult? ResolvePathFromCache(
    ReverbCellEntry cell, Vec3d soundPos, Vec3d playerPos,
    float directOcclusion, SoundPhysicsConfig config)
{
    pathResolver.Clear();

    // Add direct path (per-sound — unique angle)
    float directPermeation = Pow(config.PermeationBase, directOcclusion);
    float directDist = soundPos.DistanceTo(playerPos);
    float directWeight = directPermeation / (directDist * directDist + 0.01f);
    pathResolver.AddPath(normalize(soundPos - playerPos), directDist, directWeight,
        directOcclusion, config.PermeationOcclusionThreshold);

    // Add cached bounce paths (shared geometry, per-sound weighting)
    for (int i = 0; i < cell.BouncePointCount; i++)
    {
        var bp = cell.BouncePoints[i];
        // Direction from THIS sound's player perspective (unique per sound)
        Vec3d dirFromPlayer = normalize(bp.Position - playerPos);
        // Distance is sound→bounce + bounce→player
        float pathDist = soundPos.DistanceTo(bp.Position) + bp.Position.DistanceTo(playerPos);
        // Weight uses cached permeation (bounce→player occlusion doesn't change per sound)
        float weight = bp.Permeation / (pathDist * pathDist + 0.01f);
        pathResolver.AddPath(dirFromPlayer, pathDist, weight,
            bp.PathOcclusion, config.PermeationOcclusionThreshold);
    }

    // Add cached openings (shared, but weight includes per-sound distance)
    for (int i = 0; i < cell.OpeningCount; i++)
    {
        var op = cell.Openings[i];
        Vec3d dirToOpening = normalize(op.Position - playerPos);
        float distPlayerToOp = op.Position.DistanceTo(playerPos);
        float distOpToSound = op.Position.DistanceTo(soundPos);  // Per-sound!
        float totalDist = distPlayerToOp + distOpToSound;

        float soundSidePermeation = Pow(config.PermeationBase, op.OccToCell);
        float playerSidePermeation = Pow(config.PermeationBase, op.OccToPlayer);
        float weight = soundSidePermeation * playerSidePermeation * op.OpeningBoost
            / (totalDist * totalDist + 0.01f);

        float totalOcc = op.OccToPlayer + op.DiffractionPenalty + op.OccToCell;
        pathResolver.AddPath(dirToOpening, totalDist, weight,
            totalOcc, config.PermeationOcclusionThreshold);
    }

    return pathResolver.Evaluate(soundPos, playerPos, config, cell.SharedAirspaceRatio);
}
```

This is **just math** — no raycasts, no block lookups, no DDA traversals.
Cost: ~50 float ops per bounce point vs ~8 DDA traversals per bounce point.

### Quality Impact

- **Reverb**: Identical within a 4-block cell (imperceptible difference)
- **Occlusion**: Still fully per-sound (no quality loss)
- **Path repositioning**: Slightly approximate — bounce points were cast from cell
  center, not from this specific boar. For entities within 4 blocks of each other,
  the difference is negligible. The per-sound direct path and per-sound distance
  weighting handle the remaining directional nuance.
- **Opening probes**: Shared — openings don't move, so this is actually MORE correct
  than re-probing from each boar (less random variation in opening detection).

---

## 2. Overdue Budget Cap

### Problem

`AudioPhysicsSystem.cs:262` — overdue sounds bypass `MaxSoundsPerTick` entirely.
When 32 new sounds appear simultaneously (approaching a boar pen), ALL bypass the
budget and process in one tick.

### Fix

Add a separate overdue cap. Overdue sounds still get **priority** (sorted first),
but are limited to a configurable max per tick:

**File**: `AudioPhysicsSystem.cs`

**In UpdateAllSounds, PASS 2 (line ~257-274)**:

```csharp
int processed = 0;
int overdueProcessed = 0;
int maxOverdue = Math.Max(4, maxPerTick / 4);  // e.g., 6 for default MaxSoundsPerTick=25

for (int i = 0; i < _candidates.Count; i++)
{
    var candidate = _candidates[i];

    if (maxPerTick > 0 && processed >= maxPerTick)
    {
        // Over normal budget — only allow overdue, up to maxOverdue extra
        if (!candidate.IsOverdue || overdueProcessed >= maxOverdue)
        {
            deferredThisTick++;
            continue;
        }
        overdueProcessed++;
    }

    ProcessSoundRaycast(candidate.Sound, candidate.Cache, candidate.SoundPos,
        candidate.Distance, playerPos, blockAccessor, currentTimeMs);
    processed++;
}
```

This means in the 32-boar scenario:
- Tick 1: 25 normal + 6 overdue = 31 processed, 1 deferred
- Tick 2: remaining 1 processes immediately (still overdue)
- Total: spread over 2 ticks instead of 1 spike

With cell cache active, tick 1 computes reverb for ~2 cells, then the remaining 29
sounds in those cells get cache hits (just math, nearly free).

### Config Addition

**File**: `Config/SoundPhysicsConfig.cs`, in PERFORMANCE section:

```csharp
/// <summary>
/// Maximum additional overdue sounds that can bypass the normal budget per tick.
/// Overdue sounds (new or >2s stale) get priority but are still capped.
/// Prevents spikes when many sounds appear simultaneously (approaching a farm).
/// 0 = overdue sounds obey normal budget (strictest). Default 6.
/// </summary>
public int MaxOverdueSoundsPerTick { get; set; } = 6;
```

---

## 3. Allocation Cleanup (GC Pressure Reduction)

### Problem Areas

Inside `AcousticRaytracer.CalculateWithPaths()` hot loop:

1. **`bounceReflectivity = new float[bounces]`** — allocated per call
2. **`new Vec3d(bouncePoint)`, `new Vec3d(fromPlayerToBounce)`, `new Vec3d(dirNorm)`**
   — allocated per bounce per ray (32 × 4 × 3 = 384 per sound)
3. **`ProbeForOpenings`: `new Queue<>()`, `new HashSet<>()`, `new BlockPos()`**
   — allocated per probe ray (12 per sound)

### Fixes

All in `AcousticRaytracer.cs`:

```csharp
// Class-level pre-allocated (same pattern as existing _reusableSkyProbePos):

// bounceReflectivity — reuse across calls
private static float[] _reusableBounceReflectivity = new float[8]; // Max bounces

// ProbeForOpenings allocations
private static Queue<(int x, int y, int z, int depth)> _probeSearchQueue = new();
private static HashSet<long> _probeVisited = new();
private static BlockPos _probeBlockPos = new BlockPos(0, 0, 0, 0);

// Fibonacci loop reusable Vec3d instances
private static Vec3d _reusableBouncePoint = new Vec3d();
private static Vec3d _reusableFromPlayerToBounce = new Vec3d();
private static Vec3d _reusableDirNorm = new Vec3d();
private static Vec3d _reusableRayDir = new Vec3d();
```

**IMPORTANT**: These are safe because the mod runs single-threaded (all on client
game tick). Unlike SPR which uses `ClonedClientLevel` for thread safety, VS mod
ticks are synchronous.

---

## 4. File Change Summary

### New Files

| File | Purpose |
|------|---------|
| `Core/ReverbCellCache.cs` | Spatial reverb cache with distance-based TTL |

### Modified Files

| File | Changes |
|------|---------|
| `Core/AudioPhysicsSystem.cs` | Integrate cell cache in ProcessSoundRaycast; overdue cap in UpdateAllSounds |
| `Core/AcousticRaytracer.cs` | Extract bounce data for caching; pre-allocate hot-loop objects; new method `CalculateWithPathsCacheable()` that returns bounce data |
| `Config/SoundPhysicsConfig.cs` | Add `MaxOverdueSoundsPerTick`, `EnableReverbCellCache`, `ReverbCellSize` |
| `SoundPhysicsAdaptedModSystem.cs` | Instantiate ReverbCellCache; pass to AudioPhysicsSystem; cell-targeted block change invalidation |

### Config Additions (SoundPhysicsConfig.cs)

```csharp
// In PERFORMANCE section:

/// <summary>
/// Enable spatial reverb cell caching.
/// Sounds in the same 4x4x4 block area share reverb calculations.
/// Dramatically reduces CPU usage when many entities are clustered.
/// </summary>
public bool EnableReverbCellCache { get; set; } = true;

/// <summary>
/// Maximum additional overdue sounds that can bypass the normal budget per tick.
/// Overdue sounds (new or >2s stale) get priority but are still capped.
/// Prevents spikes when many sounds appear simultaneously.
/// 0 = overdue sounds obey normal budget. Default 6.
/// </summary>
public int MaxOverdueSoundsPerTick { get; set; } = 6;
```

---

## 5. Implementation Order

### Step 1: Overdue Budget Cap (smallest change, immediate impact)
- Modify `AudioPhysicsSystem.UpdateAllSounds()` PASS 2 loop
- Add `MaxOverdueSoundsPerTick` to config
- Test: spawn 32 boars, verify no single-tick spike

### Step 2: Allocation Cleanup (no behavior change, just GC reduction)
- Pre-allocate objects in `AcousticRaytracer.cs`
- Pre-allocate ProbeForOpenings collections
- Clear and reuse instead of `new`

### Step 3: ReverbCellCache (main feature)
- Create `Core/ReverbCellCache.cs` with full API
- Add `BouncePoint` and `OpeningData` structs
- Implement `TryGetCell`, `StoreCell`, `InvalidateCellAt`
- Distance-based TTL: 1s/5s/20s

### Step 4: AcousticRaytracer Integration
- New method `CalculateWithPathsCacheable()` that also outputs `BouncePoint[]`
  and `OpeningData[]` arrays for storage
- Modify `ProbeForOpenings` to output `OpeningData[]` instead of directly
  calling `pathResolver.AddPath()`

### Step 5: AudioPhysicsSystem Integration
- In `ProcessSoundRaycast`: check cell cache before calling raytracer
- On cache hit: use `ResolvePathFromCache()` for per-sound path direction
- On cache miss: call `CalculateWithPathsCacheable()`, store in cell cache
- Add cell cache cleanup to periodic cleanup tick

### Step 6: Block Change Hooks
- Extend `OnBlockChanged` to call `reverbCellCache.InvalidateCellAt()`
- The existing 200ms debounce on full `InvalidateCache()` stays
- Cell invalidation is instant (just a dictionary remove, no debounce needed)

---

## 6. Performance Expectations

### 32 Boars in Roofed Pen (worst case)

| Metric | Before | After (all optimizations) |
|--------|--------|---------------------------|
| DDA traversals/tick (initial burst) | ~9,000 | ~600 (1-2 cells computed, rest cached) |
| Max sounds processed per tick | 32 (all overdue) | 31 (25 + 6 overdue cap) |
| Reverb calculations | 32 independent | 1-2 (cell cached) |
| ProbeForOpenings calls | 32 | 1-2 (shared per cell) |
| Vec3d allocations in hot loop | ~12,000 | ~380 (pre-allocated reuse) |
| Queue/HashSet allocs (probes) | ~384 | 0 (pre-allocated reuse) |
| Effective improvement | baseline | **~10-15x fewer raycasts** |

### Single Distant Sound (no regression)

| Metric | Before | After |
|--------|--------|-------|
| DDA traversals | ~280 | ~280 (cache miss → full compute → cache for 20s) |
| Subsequent ticks | ~280 (if interval due) | 0 (cell cache hit for 20s) |

### Edge Cases

- **Player teleport**: All nearby cells expire on movement check (PlayerPosAtCreation)
- **Block break in farm**: Cell invalidated, next sound recomputes, re-cached
- **Spread-out entities**: Each in different cell → no sharing → same as before
- **Config `EnableReverbCellCache = false`**: Bypasses entirely, old behavior

---

## 7. Debug Support

### Stats Command Extension

Extend `/soundphysics acoustics` output:

```
CellCache: 12 cells, hits=847 misses=3 hitRate=99.6%
  Close(1s): 4 cells  Medium(5s): 5 cells  Far(20s): 3 cells
```

### Debug Logging

When `DebugReverb` is enabled:

```
[CELL-CACHE] HIT cell=(12,4,8) age=450ms uses=23 sounds_sharing=8
[CELL-CACHE] MISS cell=(15,4,10) → computing (32 rays, 4 bounces)
[CELL-CACHE] STORE cell=(15,4,10) bounces=89 openings=3 ttl=5000ms
[CELL-CACHE] INVALIDATE cell=(12,4,8) reason=block_change
```
