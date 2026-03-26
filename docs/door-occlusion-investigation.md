# Door/Gate Occlusion Investigation

**Date**: 2026-03-25 (updated 2026-03-26)  
**Status**: IMPLEMENTED — skip AABB + query `BEBehaviorDoor.Opened` for state-aware occlusion. Bug 5 (weather volume threshold) also fixed.  

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

## Diagnostic Log Analysis (2026-03-26)

### Data Set
- **95,293** total DOOR-DIAG entries from `client-debug.log`
  - 59,911 from weather occlusion (DOOR-DIAG-WX)
  - 35,382 from sound occlusion (DOOR-DIAG)

### Block Types Observed (vanilla only, ignoring Medieval Expansion multiblocks)

| Block Code | DDA Hits | DDA Pass-Through | Hit Rate |
|---|---|---|---|
| `game:door-solid-aged` | 7,257 | — | — |
| `game:door-2x2gate-larch` | 1,406 | — | — |
| `game:door-sleek-windowed-walnut` | 389 | — | — |
| `game:metaldoor-sleek-windowed-iron` | 284 | — | — |
| **All vanilla doors combined** | **4,656** | **1,334** | **77.7%** |

When doors ARE hit: every hit logs `occ=0,80` (the correct override value).

### Multiblock Collision Box Results

| Multiblock Code | Boxes | Count |
|---|---|---|
| `game:multiblock-monolithic-0-p1-p1` | **boxes=-1 (null)** | 37,462 |
| `game:multiblock-monolithic-n1-p1-p1` | **boxes=-1 (null)** | 16,445 |
| `game:multiblock-monolithic-0-p1-0` | boxes=1 (valid) | 21,535 |
| `game:multiblock-monolithic-n1-p1-0` | boxes=1 (valid) | 8,283 |

**Pattern**: Multiblock codes with `p1` in the **last** position (e.g., `0-p1-p1`, `n1-p1-p1`) return **null collision boxes** — confirms Hypothesis A from investigation. These are 2x2 gate upper-rear blocks where `BlockMultiblock.Handle()` fails to resolve the door controller's `BEBehaviorDoor.ColSelBoxes`. Codes with `0` in the last position (e.g., `0-p1-0`) DO resolve successfully.

### Door Collision Box Dimensions (German locale, commas as decimals)

Doors correctly report thin panels rotated per facing:
- Z-aligned: `(0,00, 0,00, -0,00)-(1,00, 1,00, 0,12)` — 12% depth on Z axis
- X-aligned: `(0,00, 0,00, -0,00)-(0,12, 1,00, 1,00)` — 12% depth on X axis
- Z-far: `(0,00, 0,00, 0,88)-(1,00, 1,00, 1,00)` — 12% depth, far side
- Gates: `(-0,00, 0,00, 0,00)-(1,00, 1,00, 0,13)` — 13% depth

### Key Trace Pattern (every vanilla door in the log)
```
DDA hit: game:debarkedlog-aged-ud at (512074,3,511994) occ=0,60 total=0,60
DOOR-DIAG: game:door-solid-aged id=11090 ... boxes=1 box0=(0,00,0,00,-0,00)-(1,00,1,00,0,12)
DDA pass-through: game:door-solid-aged at (512073,3,511994) (ray misses geometry)
DOOR-DIAG: game:multiblock-monolithic-0-p1-0 id=79 ... boxes=1 box0=(0,00,0,00,-0,00)-(1,00,1,00,0,12)
DDA pass-through: game:multiblock-monolithic-0-p1-0 at (512073,4,511994) (ray misses geometry)
```

The wall frame (`debarkedlog-aged-ud`) contributes 0.60 occlusion, then both the bottom door and top multiblock half are **visited but pass through** — the thin 0.12-block panel fails `RayHitsAnyCollisionBox()`.

### 4B-LPF Downstream Values
Sounds through door walls show `dOcc=0,60` (wall only), not `dOcc=1,40` (wall + door 0.80). The door's occlusion contribution is effectively zero for the ~22% of rays that miss.

### Confirmed Root Cause

**`RayHitsAnyCollisionBox` slab test fails for thin door panels (~3/16 block thick).** The DDA steps into the door's grid cell, but the ray may enter/exit the 1×1×1 cell through a face that doesn't geometrically intersect the 0.12-thick panel sitting against one edge of the cell. This is correct ray-AABB math — the panel really is that thin, and from oblique angles the ray genuinely misses it.

**The fix**: For blocks with a `BlockOverride` in `MaterialSoundConfig`, skip the AABB intersection test and apply their configured occlusion directly when the DDA visits their cell. These blocks have intentional override values that should always apply when the ray enters their cell, regardless of whether the ray's exact path clips the thin geometry. This matches real-world acoustics — a closed door blocks sound even if you could peek through a tiny gap at an angle.

### Why Previous Revert Was Wrong

Bug 3's AABB bypass was reverted because `4B-LPF` showed `bOcc=0,57` instead of the expected 0.80, which was attributed to path probes finding clear routes. But Bug 4 testing proved path probes are NOT the cause (identical values with probes disabled). The real reason `bOcc` was 0.57 is that the path system's blend ratio weights direct occlusion with boundary opening calculations. With the AABB bypass giving correct `dOcc=0.80+`, the LPF will be proportionally correct.

## Next Steps

1. ~~Investigate path probe clarity~~ — RULED OUT (same values with repositioning off)
2. ~~Trace the full pipeline from DDA hit → final LPF application~~ — **DONE**: Ray-AABB miss is the confirmed cause
3. ~~Check if the reverted `IsOpenInteractable` check is causing DDA to skip door blocks~~ — **No**: DDA visits doors, the blocks are logged
4. ~~Verify override value actually reaches `OcclusionToFilter`~~ — **Yes**: When ray hits, occ=0.80 correctly applied
5. ~~Check EMA smoothing behavior~~ — Not the primary cause; direct occlusion `dOcc` is already wrong
6. **IMPLEMENT FIX**: Skip AABB + query `BEBehaviorDoor.Opened` — see "Open/Closed State-Aware Fix Design" section below
7. ~~Multiblock null boxes~~ — **EXPLAINED**: `p1-p1` suffix = non-adjacent offset, `BlockMultiblock.Handle()` chain failure. Bottom door block provides occlusion, upper half missing is acceptable.
8. **Add ME overrides**: `medievalexpansion:gate*`, `medievalexpansion:portcullis*`, `medievalexpansion:drawbridge*` — config only, no code changes needed for ME

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

---

## Open/Closed State-Aware Fix Design (2026-03-26)

### Problem Statement

Step 6 says "skip `RayHitsAnyCollisionBox` for blocks with `HasBlockOverride()`, apply override directly." But doors have open AND closed states. We must only occlude when **closed**. Open doors should be transparent to sound — they're air.

### Three Door Architectures

| Architecture | Examples | State Storage | Collision When Closed | Collision When Open |
|---|---|---|---|---|
| **Vanilla doors** (`BEBehaviorDoor`) | `game:door-solid-aged`, `game:door-2x2gate-larch` | `BEBehaviorDoor.Opened` (BlockEntity property) | Thin panel across doorway (non-null, 0.12 block thick) | Same panel rotated ±90° against wall (non-null) |
| **Vanilla trapdoors** (`BEBehaviorTrapDoor`) | `game:trapdoor-oak`, `game:trapdoor-iron` | `BEBehaviorTrapDoor.Opened` (BlockEntity property) | Thin panel across opening (non-null) | Panel rotated against wall (non-null) |
| **Medieval Expansion gates** (`Gate`, `Portcullis`, `Drawbridge`) | `medievalexpansion:gate3x3-model-aged-closed-north` | Block code variant (`-closed-`/`-opened-`) | Thin panel (non-null) | **Null** collision boxes |

### Key Insight: Open Vanilla Doors Still Return Collision Boxes

From `BEBehaviorDoor` (VS source):
```csharp
// Line 51: State-dependent collision
public Cuboidf[] ColSelBoxes => opened ? boxesOpened : boxesClosed;
public bool Opened => opened;  // Line 52: Public property

// Lines 194-208: UpdateHitBoxes()
// boxesOpened = boxesClosed rotated ±90° around center point (0.5, 0.5, 0.5)
```

When a door opens, the thin panel rotates into the adjacent wall cell. The boxes are **never null** for vanilla doors — they just move out of the doorway.

**This means blindly skipping AABB for all `HasBlockOverride` blocks would produce false occlusion for open doors** — the rotated panel is still a valid AABB that would get tested, and at flush-against-wall positions might even hit more reliably than the thin panel across the doorway.

### Fix Design: Skip AABB + Query Open State

For blocks in the non-solid path that have `HasBlockOverride(block)`:

```
collision boxes exist?
  ├─ NO  → foliage path (handles ME opened gates, spacer blocks)
  └─ YES → HasBlockOverride?
       ├─ NO  → existing AABB path unchanged (fences, chiseled blocks, etc.)
       └─ YES → IsDoorOpen(blockAccessor, x, y, z)?
            ├─ YES → pass-through (zero occlusion — door is open)
            └─ NO  → apply override directly, SKIP AABB test
```

#### `IsDoorOpen()` Helper

```csharp
private static bool IsDoorOpen(IBlockAccessor blockAccessor, int x, int y, int z)
{
    var be = blockAccessor.GetBlockEntity(new BlockPos(x, y, z, 0));
    if (be == null) return false; // No entity — assume closed (safe default)
    
    var doorBeh = be.GetBehavior<BEBehaviorDoor>();
    if (doorBeh != null) return doorBeh.Opened;
    
    var trapBeh = be.GetBehavior<BEBehaviorTrapDoor>();
    if (trapBeh != null) return trapBeh.Opened;
    
    return false; // Unknown block type with override — assume closed
}
```

**Performance**: Negligible. `GetBlockEntity()` is already called internally by `block.GetCollisionBoxes()` for doors. The `GetBehavior<T>()` call is an array scan on the cached entity behavior list. Only fires for override-matching blocks (~1-2% of DDA visits).

### Why This Works For Each Architecture

#### Vanilla Doors (closed)
1. DDA visits `game:door-solid-aged`
2. `IsSolidForOcclusion` → false → non-solid path
3. `GetCollisionBoxes()` → thin panel across doorway (non-null, from `BEBehaviorDoor.boxesClosed`)
4. `HasBlockOverride(block)` → true (`game:door-*` matches)
5. `IsDoorOpen()` → `BEBehaviorDoor.Opened` → **false**
6. Apply override 0.80 directly, skip AABB → **100% hit rate** ✓

#### Vanilla Doors (open)
1. DDA visits same block
2. `GetCollisionBoxes()` → thin panel rotated against wall (non-null, from `BEBehaviorDoor.boxesOpened`)
3. `HasBlockOverride(block)` → true
4. `IsDoorOpen()` → `BEBehaviorDoor.Opened` → **true**
5. Pass-through → **zero occlusion** ✓

#### Vanilla Trapdoors
Same as doors via `BEBehaviorTrapDoor.Opened`. Both behaviors expose identical `Opened` property.

#### Medieval Expansion Gates (closed)
1. DDA visits `medievalexpansion:gate3x3-model-aged-closed-north`
2. `GetCollisionBoxes()` → thin panel (non-null)
3. `HasBlockOverride(block)` → true (needs new override pattern)
4. `IsDoorOpen()` → `GetBlockEntity()` → `GateEntity`, no `BEBehaviorDoor` → returns **false**
5. Apply override directly → **correct** ✓

#### Medieval Expansion Gates (opened)
1. DDA visits `medievalexpansion:gate3x3-model-aged-opened-north`
2. `GetCollisionBoxes()` → **null** (ME sets `"*-opened-*": null` in JSON)
3. Falls to foliage path → `GetFoliageVolumeScale()` → zero-size shape → **0.0 occlusion** ✓

Never reaches the override check. Open state handled entirely by null collision.

#### ME Spacer Blocks
- Entity is null (`entityClassByType: "*-spacer*": null`)
- Zero-size invisible blocks → null or empty collision boxes
- Falls to foliage path → near-zero occlusion regardless of state ✓

### Multiblock Upper Halves (`game:multiblock-monolithic-*`)

**Override patterns do NOT match multiblocks** — `game:door-*` doesn't match `game:multiblock-monolithic-*`. This is **intentionally correct**:

- The bottom door block provides the door's full occlusion (0.80 via override)
- Counting the upper half would double the door's occlusion (1.60 — wrong)
- Both halves occupy the same DDA column; one hit is sufficient

For the multiblock delegation chain, `BlockMultiblock.OffsetInv` IS a public field:
```csharp
// BlockMultiblock.cs line 52
public Vec3i OffsetInv;  // Calculated in OnLoaded(): OffsetInv = -Offset
```

Navigation to controller: `Handle()` → `GetBlock(pos+offset)` → `GetBehavior(IMultiBlockColSelBoxes)` → `MBGetCollisionBoxes()` → `getColSelBoxes()` → `GetBlockEntity(pos+offset)` → `BEBehaviorDoor.ColSelBoxes`

The `p1-0` suffix multiblocks resolve successfully (boxes=1 in logs). The `p1-p1` suffix multiblocks fail to resolve (boxes=-1, null) — this is a VS engine limitation for non-adjacent offsets in 2x2 gates, not something we can fix.

Since multiblocks don't match override patterns, they go through the existing AABB path:
- `p1-0` (resolves): thin panel → AABB miss ~22% → acceptable loss (bottom block provides occlusion)
- `p1-p1` (null): foliage path → near-zero → acceptable (bottom block provides occlusion)

### Medieval Expansion Config Overrides

ME gates need override patterns added to `MaterialSoundConfig.cs` defaults. ME blocks use custom classes — NOT `BEBehaviorDoor` — so `IsDoorOpen()` returns false for them. But this is correct: opened ME gates have null collision → never reach the override check.

| Block Type | Code Pattern | Entity Class | Override Value |
|---|---|---|---|
| Gates | `medievalexpansion:gate*` | `GateEntity` | 0.8 |
| Portcullis | `medievalexpansion:portcullis*` | `PortcullisEntity` | 0.85 |
| Drawbridge | `medievalexpansion:drawbridge*` | `DrawbridgeEntity` | 0.8 |

**Variant format** (confirmed from JSONs):
- Gates: `medievalexpansion:gate{NxN}-{model|spacer}-{wood}-{closed|opened}-{facing}`
- Portcullis: `medievalexpansion:portcullis{NxN}-{model|spacer}-{closed|opened}-{facing}`
- Drawbridge: `medievalexpansion:drawbridge{NxM}-{model|hspacer|vspacer}-{wood}-{closed|opened}-{facing}`

The wildcard patterns match all variants including spacers. Spacers have null/zero collision → foliage path → volume scale ≈ 0. Override value never matters for spacers.

No code changes needed for ME — just config override additions. The open/closed state is handled naturally by ME's collision box architecture (null when open).

### Both DDA Paths Need The Fix

The same non-solid AABB logic exists in both:
- **`RunOcclusion()`** (sound occlusion) — lines 344-393
- **`RunWeatherOcclusion()`** (weather occlusion) — lines 566-601

Both need the `HasBlockOverride → IsDoorOpen → skip AABB` branch.

### Implementation Checklist

1. **`OcclusionCalculator.cs`**: Add `IsDoorOpen()` static helper ✅
2. **`OcclusionCalculator.cs` `RunOcclusion()`**: In non-solid collision path, before `RayHitsAnyCollisionBox`, check `HasBlockOverride` → `IsDoorOpen` → apply directly or pass-through ✅
3. **`OcclusionCalculator.cs` `RunWeatherOcclusion()`**: Same change in weather DDA visitor ✅
4. **`MaterialSoundConfig.cs`**: Add ME override patterns to defaults ✅
5. **Config migration**: Bump to v6, add ME patterns to existing saved configs ✅
6. **Remove DOOR-DIAG logging**: Diagnostic logging no longer needed after fix ✅
7. **Build + test**: Verify closed doors show occ=0.80, open doors show occ=0.00 ✅

---

## Bug 5: Weather Volume Threshold Bypassing Doors (FIXED — 2026-03-26)

### Problem
After implementing the AABB skip fix (bugs 1-4), sound occlusion through doors worked (5,971 `DDA door-closed` hits, 0 pass-throughs), but **weather/rain occlusion through doors was unchanged**. Opening and closing doors had no effect on rain volume.

### Root Cause
`RunWeatherOcclusion()` has a **collision volume filter** that `RunOcclusion()` does not:

```csharp
float totalVol = 0f;
for (int cb = 0; cb < collisionBoxes.Length; cb++) {
    var box = collisionBoxes[cb];
    totalVol += (box.X2 - box.X1) * (box.Y2 - box.Y1) * (box.Z2 - box.Z1);
}
if (totalVol < 0.15f) { return false; } // weather-transparent
```

Door panel volume: `1.0 × 1.0 × 0.12 = 0.12` → **0.12 < 0.15** → door rejected as "tiny collision" before the `HasBlockOverride` check ever fired.

### Fix
Moved `HasBlockOverride` check **before** the volume filter. Override blocks (doors, gates, trapdoors) bypass the tiny-volume threshold entirely. Non-override blocks still filtered normally.
