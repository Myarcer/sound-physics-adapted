# Door/Gate Occlusion Investigation

**Date**: 2026-03-25 (updated 2026-03-26)  
**Status**: Active investigation — diagnostic logging deployed  

## Problem

Closed doors, gates, and trapdoors produce near-zero occlusion. Sound passes through them as if they aren't there. User reports this is a regression from earlier versions.

## Root Cause Analysis

### Bug 1: Override Pattern Mismatch (FIXED)

**BlockOverrides** in `MaterialSoundConfig.cs` used patterns like:
```
"game:door-*-closed-*" → 0.8
"game:door-*-opened-*" → 0.05
```

But VS door block codes have **no state suffix**: `door-solid-aged`, `metaldoor-sleek-windowed-iron`, `door-2x2gate-larch`. The regex `^game:door-.*-closed-.*$` never matches. Without a match:
- `HasBlockOverride()` returns false
- Falls through to wood material = 0.6
- Volume scaling applied: 0.6 × sqrt(0.19) ≈ **0.21** occlusion

**Fix**: Broad prefix patterns (`game:door-*`, `game:metaldoor-*`, `game:trapdoor-*`, `game:*gate*`).

### Bug 2: Saved Config Override (FIXED)

The saved config file `soundphysicsadapted_materials.json` (Version 4) had the broken patterns baked in. Code changes to defaults had no effect because the saved file always wins.

**Fix**: Added v5 config migration that removes broken patterns and adds correct ones.

### Bug 3: Ray-AABB Missing Thin Door Panels (INVESTIGATED, REVERTED)

Door collision boxes are ~3/16 block thick. The slab intersection test (`RayIntersectsAABB`) missed at oblique angles, producing false "ray misses geometry" even for closed doors. When center ray missed → `centerOcclusion < 0.3` → "clear, skip offset" → offset rays never fire → zero occlusion.

**Attempted fix**: Skip `RayHitsAnyCollisionBox` for `IsWeatherInteractable` blocks and apply override occlusion directly.

**Result**: DDA correctly showed `door-hit: occ=0.80` for every ray. BUT the downstream **path resolution system** was finding 16/16 clear probe paths around/over the door, blending effective occlusion down to 0.57. The `bOcc` in `4B-LPF` was capped by path clarity probes finding alternate routes.

**Reverted because**: Even with correct DDA values (0.80), the path resolution system's 25th-percentile blending (`SoundPathResolver.cs` line ~242) reduced effective occlusion. The fix masked the real problem — the path probes shouldn't find clear routes in a fully enclosed room. Needs investigation into why 16/16 probes find open paths when only a closed door exists.

### Bug 4: Path Probe Clarity — RULED OUT

Initially suspected the path resolution system (`SoundPathResolver`) was blending door occlusion down via 25th-percentile clear-path probes. However:

**Testing with sound repositioning OFF** (disables path resolution entirely) produced **identical occlusion values**. The same low effective occlusion plays with or without the path system active.

This rules out `SoundPathResolver` as the culprit. The issue is upstream — somewhere between the DDA hitting the door and the final LPF value being applied, the occlusion is being reduced or ignored. Potential areas:
- The override pattern match is happening but the occlusion value isn't propagating to the final calculation
- Volume scaling is still being applied despite the override check
- The smoothing/EMA system in `AudioPhysicsSystem` is damping door transitions
- Some other code path is overriding the DDA result before LPF application
- The reverted `IsOpenInteractable` check in OcclusionCalculator is skipping door blocks entirely

## What's Currently Deployed

1. **MaterialSoundConfig.cs**: Broad override patterns (KEEP)
2. **SoundPhysicsAdaptedModSystem.cs**: v5 config migration (KEEP)
3. **OcclusionCalculator.cs**: Reverted to pre-investigation state — IsOpenInteractable checks restored, ray-AABB test restored for all non-solid blocks

## Log Evidence

```
# Before fix (override not matching):
DDA hit: game:door-solid-aged occ=0,21          # volume-scaled wood (0.6 × 0.35)
DDA pass-through: game:door-solid-aged (ray misses geometry)  # thin panel miss

# After override fix (correct value when ray hits):
DDA hit: game:door-solid-aged occ=0,80          # correct override value
DDA hit: game:metaldoor-sleek-windowed-iron occ=0,90

# After ray-AABB bypass (always hits):
DDA door-hit: game:door-solid-aged occ=0,80     # every ray hits
# But path system still finds clear routes:
4B-LPF: dOcc=0,83 bOcc=0,57 smooth=0,57 filt=0,562 clarity=100%
4B-Path: off=0,7m bOcc=0,83 paths=16/16 perm=0  # all paths "clear"
```

## Next Steps

1. ~~Investigate path probe clarity~~ — RULED OUT (same values with repositioning off)
2. Trace the full pipeline from DDA hit → final LPF application with logging to find where door occlusion is lost
3. Check if the reverted `IsOpenInteractable` check is causing DDA to skip door blocks
4. Verify override value actually reaches `OcclusionToFilter` and isn't overwritten
5. Check EMA smoothing behavior — is it keeping stale low values from before door was closed?

---

## VS 1.21+ Door Architecture (Deep Dive — 2026-03-26)

### New Door System (BlockBehaviorDoor + BEBehaviorDoor)

VS 1.21 replaced legacy `BlockDoor` with a **behavior-based multiblock system**. Understanding this is critical because it changes how collision boxes are exposed.

#### Block Type Definition (JSON)
```json
// assets/survival/blocktypes/wood/woodtyped/door.json
{
    code: "door",
    class: "BlockGeneric",       // NOT BlockDoor — plain BlockGeneric
    entityClass: "Generic",
    behaviors: [
        { name: "Lockable" },
        { name: "Door" },        // BlockBehaviorDoor (StrongBlockBehavior)
        { name: "BlockEntityInteract" }
    ],
    entityBehaviors: [{ name: "Door" }],  // BEBehaviorDoor
    blockmaterial: "Wood",       // NOT Air
    sidesolid: { all: false },
    collisionbox: { x1: 0, y1: 0, z1: 0.875, x2: 1, y2: 1, z2: 1 }
    // This is the DEFAULT thin panel — but BEBehaviorDoor overrides it
}
```

#### Class Hierarchy
```
Block (base)
  └─ BlockGeneric                    ← VS door blocks use this
       │  Overrides GetCollisionBoxes() to dispatch to StrongBlockBehaviors
       │
  └─ BlockMultiblock : Block         ← Upper door halves (NOT BlockGeneric!)
       │  Overrides GetCollisionBoxes() to delegate to IMultiBlockColSelBoxes
       │  Uses Handle<T,K>() pattern to find controller block
       │
  └─ BlockBaseDoor : Block           ← LEGACY (pre-1.21), still exists for old saves
       └─ BlockDoor
```

#### Behavior Chain
```
BlockBehaviorDoor : StrongBlockBehavior, IMultiBlockColSelBoxes, IMultiBlockBlockProperties
  │
  ├─ GetCollisionBoxes(ba, pos, ref handled)
  │    → handled = PreventSubsequent
  │    → returns BEBehaviorDoor.ColSelBoxes (from BlockEntity)
  │
  ├─ MBGetCollisionBoxes(ba, pos, offset)     // For multiblock upper half
  │    → getColSelBoxes() → BEBehaviorDoor.ColSelBoxes
  │
  BEBehaviorDoor (BlockEntity Behavior)
  │  boxesClosed = Block.CollisionBoxes rotated by RotateYRad
  │  boxesOpened = boxesClosed rotated ±90° around center
  │  ColSelBoxes => opened ? boxesOpened : boxesClosed
```

#### Key Insight: Collision Boxes Are Dynamic
The collision boxes returned by `block.GetCollisionBoxes(ba, pos)` for doors depend on:
1. **Open/closed state** — stored in `BEBehaviorDoor.opened`
2. **Rotation** — `BEBehaviorDoor.RotateYRad` determines facing
3. **BlockEntity existence** — requires `ba.GetBlockEntity(pos)` to work

**This means**: If the `IBlockAccessor` doesn't support `GetBlockEntity()`, or the BlockEntity isn't loaded, `GetCollisionBoxes()` returns null → foliage path → near-zero occlusion.

### Multiblock Structure (Tall Doors)

Standard 1x2 doors occupy 2 blocks vertically:
- **Bottom**: actual `game:door-*` block (BlockGeneric with Door behavior)
- **Top**: `game:multiblock-monolithic-0-p1-0` (BlockMultiblock, extends raw `Block` not `BlockGeneric`)

The multiblock JSON definition:
```json
{
    code: "multiblock",
    class: "BlockMultiblock",
    blockmaterial: "Wood",
    sidesolid: { all: false }
}
```

#### Multiblock Collision Delegation
```csharp
// BlockMultiblock.GetCollisionBoxes (extends Block, NOT BlockGeneric)
public override Cuboidf[] GetCollisionBoxes(IBlockAccessor ba, BlockPos pos)
{
    return Handle<Cuboidf[], IMultiBlockColSelBoxes>(
        ba,
        pos.X + OffsetInv.X, pos.InternalY + OffsetInv.Y, pos.Z + OffsetInv.Z,
        // Looks up the REAL door block at (pos + offset), checks for IMultiBlockColSelBoxes
        (inf) => inf.MBGetCollisionBoxes(ba, pos, OffsetInv),
        (block) => new Cuboidf[] { Cuboidf.Default() },  // fallback if multiblock chain
        (block) => block.GetCollisionBoxes(ba, pos.AddCopy(OffsetInv))
    );
}
```

This delegates to `BlockBehaviorDoor.MBGetCollisionBoxes()` on the main (bottom) door block, which then reads `BEBehaviorDoor.ColSelBoxes`.

### How DDA Sees Doors

Both `RunOcclusion()` and `RunWeatherOcclusion()` in OcclusionCalculator follow this path:
1. DDA visits block at (x,y,z)
2. `blockAccessor.GetBlock(pos)` returns the Block
3. Early exit: `block.BlockMaterial == EnumBlockMaterial.Air` → **doors are Wood, not Air** ✓
4. `IsSolidForOcclusion(block)` → false (sidesolid: all false) → enters non-solid path
5. `block.GetCollisionBoxes(blockAccessor, pos)` → **this is the critical call**
6. If boxes != null → `RayHitsAnyCollisionBox()` → apply occlusion if hit

### Suspected Failure Points (2026-03-26)

#### Hypothesis A: BlockEntity Not Available
If `GetBlockEntity(pos)` returns null (chunk not loaded, wrong accessor type), `BEBehaviorDoor.ColSelBoxes` is never reached. The fallback in `BlockBehaviorDoor.GetCollisionBoxes()`:
```csharp
return blockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorDoor>()?.ColSelBoxes ?? null;
```
Returns **null** → foliage path → near-zero scaled occlusion.

#### Hypothesis B: Multiblock Upper Half (BlockMultiblock extends Block, not BlockGeneric)
`BlockMultiblock` extends raw `Block`. The base `Block.GetCollisionBoxes()` just returns `CollisionBoxes` (static from JSON). It does NOT dispatch to behaviors.

However, `BlockMultiblock` **overrides** `GetCollisionBoxes()` with its own delegation to `IMultiBlockColSelBoxes`. So this should still work — but the delegation chain is long:
1. `BlockMultiblock.GetCollisionBoxes()` → `Handle()` → `GetBlock(pos+offset)` → check behaviors → `MBGetCollisionBoxes()` → `getColSelBoxes()` → `GetBlockEntity(pos+offset)` → `BEBehaviorDoor.ColSelBoxes`

If ANY link in this chain fails to resolve, it returns null or Cuboidf.Default().

#### Hypothesis C: DDA Never Visits Door Blocks
The DDA logs from 2026-03-26 show **zero door hits** — only slantedroofing blocks. If the ray path doesn't cross the door's grid cell, the door is never evaluated. This could happen if:
- The sound source and player are on the same side of the door
- The DDA path goes over/around/under the door cell

#### Current Diagnostic
`DOOR-DIAG` logging added to both `RunOcclusion()` and `RunWeatherOcclusion()` DDA visitors. Logs:
- Block code, ID, material
- `IsSolidForOcclusion()` result
- Collision box count and first box dimensions
- Position

This fires BEFORE the air/null early exit, so we'll see doors even if they're being skipped.

### Block Code Patterns (Verified from VS 1.22 Assets)
- Wood doors: `game:door-{style}-{wood}` (e.g., `door-solid-aged`, `door-sleek-windowed-oak`)
- Metal doors: `game:door-metal-{style}-{metal}` (via `door-metal.json`)
- Trapdoors: `game:trapdoor-{wood}` / `game:trapdoor-{metal}`
- Gates: `game:door-1x3gate-{wood}`, `game:door-2x2gate-{wood}`, etc.
- Multiblock upper halves: `game:multiblock-monolithic-0-p1-0` (all door types)

### Config Override Patterns (Current)
```csharp
{ "game:door-*", 0.8f },
{ "game:metaldoor-*", 0.9f },     // May need update — metal doors now "door-metal-*"?
{ "game:trapdoor-*", 0.7f },
{ "game:*gate*", 0.8f },
```
**Note**: Gates are now `door-2x2gate-*` etc., so `game:door-*` already catches them. The `*gate*` pattern is redundant but harmless.
