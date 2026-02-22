# Wall Opening Invalidation — Block-Event Design

## Problem Statement

When a player seals a wall opening (places a block in a door/window/gap), the weather
positional source tracking the opening survives longer than it should. The current
structural integrity check (`CheckStructuralIntegrity`, 4c) works by DDA from each
member position to a stored player position — but member positions are **rain impact
columns** (outdoors), not wall face positions. The actual sealing block lands at the
wall face in between, which the DDA may or may not cross depending on geometry.

Ceiling holes already work correctly via the heightmap check (4b): placing a block
above an opening changes `GetRainMapHeightAt()`, which is checked every tick.
Wall holes have no equivalent ground-truth check.

The corner-walking false-positive is also unsolved: using current player pos in the
DDA triggers spurious removals when the player rounds a corner inside a house.
The stored player pos workaround avoids false kills but also misses some real seals.

---

## Geometry

```
OUTSIDE              WALL              INSIDE
  ↓                   ↓                  ↓
[Rain Impact]  →  [OPENING]  →      [Player]
 WorldPos          EntryPos       LastVerifiedPlayerPos
 (stored as        (wall face —     (stored)
  MemberPos)        NOT in tracker)
```

`MemberPositions` in `TrackedOpening` stores `candidate.WorldPos` — the outdoor rain
column, not the wall face. The wall face entry point (`EntryPos`) is used for the
cluster centroid only and is currently not persisted on the tracker.

---

## Solution: Entry-Point Block-Event Invalidation

### Core Idea

Rather than polling every tick with DDA, **listen to the existing `BlockChanged` event**
and check if the changed block is within 1 block of a tracked opening's **entry points**
(wall faces). This is event-driven, cheap, and immune to corner-walking false positives
because movement never fires `BlockChanged` — only actual block placement/removal does.

### Why Entry Points, Not Member Positions

Placing a block outdoors near a rain column (building a fence, extending a wall) would
false-trigger if we checked proximity to `MemberPositions`. Entry points are wall faces
— inside or on the wall — so an outdoor block is naturally distant from them.

---

## Data Flow

```
BlockChanged event
  → SoundPhysicsAdaptedModSystem.OnBlockChanged(pos, oldBlock)     [EXISTING]
      → acousticsManager.InvalidateCache()                    [EXISTING]
      → weatherManager.NotifyBlockChanged(pos)                [NEW]
          → OpeningTracker.OnBlockChanged(pos)                [NEW]
              → per tracked opening: check entry point proximity
              → hit → tracked.ForceReverify = true
                    → next weather tick re-scans this opening
```

---

## Changes Required

### 1. `OpeningCluster` — add `MemberEntryPositions`

```csharp
// OpeningCluster.cs / OpeningClusterer.cs
public List<Vec3d> MemberEntryPositions;  // Parallel to MemberPositions
```

Populated alongside `MemberPositions` in both clustering phases:
```csharp
memberPositions.Add(candidate.WorldPos);
memberEntryPositions.Add(candidate.EntryPos ?? candidate.WorldPos);
```

`EntryPos ?? WorldPos` fallback: sky openings have no wall face, fall back to rain
column. Sky openings are handled by 4b heightmap anyway, so this path is inert.

---

### 2. `TrackedOpening` — store entry positions + reverify flag

```csharp
public List<Vec3d> MemberEntryPositions;
public bool ForceReverify;
```

Populated when a cluster is first tracked and when it gets updated:
```csharp
tracked.MemberEntryPositions = new List<Vec3d>(cluster.MemberEntryPositions);
```

---

### 3. `OpeningTracker` — new `OnBlockChanged(BlockPos)`

```csharp
public void OnBlockChanged(BlockPos changedPos)
{
    for (int i = 0; i < trackedOpenings.Count; i++)
    {
        var tracked = trackedOpenings[i];
        if (tracked.MemberEntryPositions == null) continue;

        for (int m = 0; m < tracked.MemberEntryPositions.Count; m++)
        {
            Vec3d entry = tracked.MemberEntryPositions[m];
            int dx = Math.Abs(changedPos.X - (int)Math.Floor(entry.X));
            int dy = Math.Abs(changedPos.Y - (int)Math.Floor(entry.Y));
            int dz = Math.Abs(changedPos.Z - (int)Math.Floor(entry.Z));

            if (dx <= 1 && dy <= 1 && dz <= 1)
            {
                tracked.ForceReverify = true;
                break;  // One hit is enough per opening
            }
        }
    }
}
```

Radius of 1 block: tight enough to exclude unrelated outdoor building, wide enough to
catch blocks placed at the edge of a multi-block opening.

---

### 4. `RemoveStaleOpenings` — act on `ForceReverify`

On the ForceReverify path, we don't remove immediately. We reset `CurrentlyVerified`
and `LastVerifiedTimeMs` so the next weather scan re-evaluates the opening from scratch:

```csharp
// In RemoveStaleOpenings, before 4b/4c:
if (tracked.ForceReverify)
{
    tracked.ForceReverify = false;
    tracked.CurrentlyVerified = false;
    tracked.LastVerifiedTimeMs = 0;  // Forces immediate persistence timeout if not re-found
}
```

If the opening is genuinely sealed, the enclosure calculator won't find it in the next
scan → `CurrentlyVerified` stays false → persistence timeout → 4d removes it.
If the block didn't actually seal it (e.g., partial closure, player placed adjacent),
the enclosure calculator re-finds it → `CurrentlyVerified = true` again → no removal.

---

### 5. `WeatherAudioManager` — forward the event

```csharp
public void NotifyBlockChanged(BlockPos pos)
{
    openingTracker?.OnBlockChanged(pos);
}
```

---

### 6. `SoundPhysicsAdaptedModSystem.OnBlockChanged` — extend existing handler

```csharp
private void OnBlockChanged(BlockPos pos, Block oldBlock)
{
    if (acousticsManager == null || clientApi == null) return;

    long currentTime = clientApi.World.ElapsedMilliseconds;
    if (currentTime - lastBlockChangeTimeMs < BLOCK_CHANGE_DEBOUNCE_MS)
        return;

    lastBlockChangeTimeMs = currentTime;
    acousticsManager.InvalidateCache();
    weatherManager?.NotifyBlockChanged(pos);   // NEW
}
```

Note: the existing debounce applies here too, which is fine. Block placements within
the debounce window (~50ms?) still get caught by 4c DDA on the next tick.

---

## Files Changed

| File | Change | Size |
|------|--------|------|
| `OpeningClusterer.cs` | Add `MemberEntryPositions` to `OpeningCluster`, populate in both clustering phases | ~10 lines |
| `OpeningTracker.cs` | Add `MemberEntryPositions` + `ForceReverify` to `TrackedOpening`, add `OnBlockChanged()`, act on flag in `RemoveStaleOpenings` | ~40 lines |
| `WeatherAudioManager.cs` | Add `NotifyBlockChanged()` forwarding to tracker | ~5 lines |
| `SoundPhysicsAdaptedModSystem.cs` | One line added to existing `OnBlockChanged` | ~1 line |

---

## What Stays The Same

- **4b heightmap check** — still the fast broad-phase for ceiling holes
- **4c `CheckStructuralIntegrity()`** — keep as fallback for openings without entry
  positions (older tracked state, edge cases at chunk boundaries)
- **4d audibility persistence** — unchanged, still handles gradual fade-out
- All clustering, DDA scanning, enclosure calculation — untouched

---

## Edge Cases

| Case | Behavior |
|------|----------|
| Player builds fence outside near a rain column | Entry points are wall faces (indoors), outdoor block too far → no trigger |
| Block placed in 3-wide doorway (partial seal) | Hits 1+ entry points → `ForceReverify` → re-scan finds remaining gap → cluster survives with fewer members |
| Block placed fully sealing doorway | All entry points now blocked → re-scan finds nothing → persistence timeout → removal |
| Block removed (opening created) | Same event path → `ForceReverify` → re-scan picks up new opening faster than normal tick cycle |
| Sky opening (EntryPos null) | Falls back to WorldPos; already handled by 4b heightmap; block-event check is inert |
| Chunk load / large block batch | Existing debounce in `OnBlockChanged` limits re-verify thrash |

---

## Relation to Existing TODO

This replaces the approach described in `TODO.md` ("Replace Structural DDA Check (4c)
with Block Event Hooks") with a more targeted variant: rather than fully replacing 4c,
we add block-event invalidation using entry points as the precise check target, and
keep 4c as a backup. The `LastVerifiedPlayerPos` and the DDA-to-stored-pos path remain
as a secondary safety net.
