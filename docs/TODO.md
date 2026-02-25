# Sound Physics Adapted - TODO

## Current Status

**Phase 1 (Foundation)**: COMPLETE
- Multi-block occlusion: WORKING (DDA raycast, recalculates on player movement)
- Per-sound filters: WORKING (unique OpenAL filter per sound)
- Smoothing transitions: WORKING (0.3 factor, ~300ms)

**Phase 2 (Materials)**: COMPLETE
- Material-based occlusion: WORKING (stone=1.0, wood=0.5, etc.)
- Block code overrides: WORKING (metal blocks, wool, containers)

**Phase 3 (Enhanced Reverb)**: COMPLETE - SPR-Based Approach
- Complete override of VS reverb using SPR-style system
- 4 separate EAX reverb slots with different decay times
- Fibonacci sphere ray bouncing for realistic reflections
- Material-based reflectivity

**Phase 4 (Sound Paths)**: COMPLETE - SPP-Style Permeation + Repositioning
- Permeation-weighted paths: WORKING (all bounce rays contribute, occlusion-weighted)
- SoundPathResolver: WORKING (weighted average direction + muffle)
- Sound repositioning: WORKING (AL.Source3f via reflection)
- Path-based muffle: WORKING (LPF from average path occlusion)
- Direct LOS shortcut: WORKING (skip repositioning for clear paths)
- Stage 3 listener-centric propagation: 64 listener rays, 256+ cached bounce points
- See IMPLEMENTATION_PLAN.md and PHASE4B_PROPAGATION_HANDOFF.md

**Phase 5 (Weather Audio)**: Rewrite this part here its outdated

---


## Active Issues



### Issue 19: Probe Ray Shared Airspace Contribution Asymmetry
**Priority**: MEDIUM-HIGH
**Impact**: Opening detection and floor calculation may be disconnected

**Observation**:
The probe ray system and fibonacci bounce ray system have asymmetric contributions to the acoustic model:

**Fibonacci Bounce Rays** (32 rays × 4 bounces = 128 checks):
- Fire from sound source in all directions (fibonacci sphere distribution)
- At each bounce point: check `pathOcclusion < 0.5` to player
- If clear → increment `sharedAirspaceCount` (contributes to floor)
- Add bounce point to path resolver (contributes to repositioning)
- Contributes to reverb calculation

**Probe Rays** (12 rays, 15° cone):
- Fire from player toward sound (±15° jitter)
- On wall hit: check 6 face-adjacent neighbors for physical AIR blocks
- If air found: verify paths to player & sound, add to path resolver
- ❌ Does NOT increment `sharedAirspaceCount`
- ❌ Does NOT contribute to reverb

**Floor Calculation Dependency**:
```csharp
// AudioPhysicsSystem.cs:422-426
float sharedAirspaceFilterFloor = sqrt(sharedAirspaceRatio) * 0.2f;
float sharedAirspaceFloor = -log(sharedAirspaceFilterFloor) / blockAbsorption;
```
The SPR-style floor is ONLY based on `sharedAirspaceRatio` from fibonacci bounce rays.

**Observed Scenario** (from user testing logs):
```
directOcc=7.20 | blendedOcc=6.40 | sharedAirRatio=0% | clarity=0%
open=0 perm=42 | sharedAirFloor=4.61
```
- 42 paths exist (all permeated through walls)
- Fibonacci rays: 0% found shared airspace
- Probe rays: 0% found openings (15° cone + 1-block neighbor failed)
- Floor: 4.61 (filter = 0.01 = nearly silent)

**Hypothetical Scenario** (probe success but floor doesn't benefit):
If probes successfully find a corridor opening (works for thin walls):
- Probe verifies `occToPlayer < 2.0` (clear path)
- Adds path with high weight + opening boost to path resolver
- Path contributes to repositioning (sound shifts toward opening)
- But `sharedAirspaceRatio` stays 0% → floor = 4.61 (terrible)
- **Disconnect**: System found opening but floor acts like it didn't

**Current System Behavior**:
The floor recovery mechanism (Issues 1, 4, 5 fixes) relies entirely on fibonacci rays finding shared airspace. When probes are the ONLY system that finds openings (thin walls with acute angles), the floor doesn't benefit from those found paths.

**Design Question for Re-evaluation**:
Should probe-found openings contribute to `sharedAirspaceRatio` or an equivalent floor metric?

Arguments FOR contribution:
- Probes verify clear paths (`occToPlayer < 2.0`) before adding to resolver
- Opening-found paths are acoustically valid (similar to bounce point airspace checks)
- Floor should reflect "quality of found paths" not "which system found them"
- Current asymmetry means probes help repositioning but not muffling recovery

Arguments AGAINST contribution:
- Probes and fibonacci serve different purposes (deterministic search vs statistical sampling)
- Mixing contribution types could create inconsistent floor behavior
- SPR uses only bounce-based airspace (proven approach)
- Probes are already struggling with geometry (Issue 2) - masking that with floor boost could hide the real problem

**Related Issues**:
- Issue 2: Probe cone too narrow (15°) + neighbor search (1-block) fails for thick walls / right-angle corridors
- Current test results show BOTH systems failing (fibonacci 0% airspace, probes 0% openings)
- Fixing probe geometry (wider pattern, deeper neighbor search) might make this question moot

**Status**: NEEDS RE-EVALUATION
**Action**: Future agent should analyze whether probe-found openings should influence floor calculation, or if floor should remain purely fibonacci-based as in SPR

# Sound Physics Adapted - TODO

## Positional Weather Issues

### Centroid Jitter — Column Membership Hysteresis
**Priority**: MEDIUM  
**Status**: IMPLEMENTED (2026-02-21)

**Problem**:
Column membership in opening clusters was binary: each 100ms scan, a column passes if `occlusion < 0.3` and fails otherwise. Borderline columns flickered in/out, shifting cluster centroids and causing audible panning jitter.

**Resolution — Four Interconnected Fixes**:

1. **Temporal Opening Cache** (`WeatherEnclosureCalculator`):
   - `verifiedOpenings` no longer cleared every cycle. Instead, a `Dictionary<(int,int), CachedOpening>` persists columns between cycles.
   - Columns survive up to `MAX_CONSECUTIVE_FAILS = 3` (300ms) of DDA failure before removal.
   - `VerifiedRainPosition` struct gains `ColumnX`/`ColumnZ` for identity tracking.
   - Cache evicts on distance (scanRadius+4 blocks) and consecutive failure.

2. **Schmitt Trigger Hysteresis** (`WeatherEnclosureCalculator`):
   - Separate join/leave thresholds: `HYSTERESIS_JOIN_THRESHOLD = 0.25` (stricter to enter), `HYSTERESIS_LEAVE_THRESHOLD = 0.45` (lenient to stay).
   - Columns already in cache use the leave threshold; new columns must beat the join threshold.
   - Gap absorbs DDA noise from player micro-movement.

3. **Position Smoothing** (`OpeningTracker`):
   - `TrackedOpening.WorldPos` lerped at 30% per tick instead of instant snap.
   - Shifts < 0.1 block or > 3.0 blocks snap immediately (float precision / structural change).
   - Medium shifts get exponential smoothing (~700ms to 90% convergence).

4. **Nearby Tracked Opening Merge** (`OpeningTracker`, Step 3B):
   - After creating new tracked openings, merge any pair within `MERGE_RADIUS = 3.0` blocks.
   - Higher-weight opening absorbs lower-weight, transfers verification status.
   - Prevents duplicate sources from cluster split/merge oscillation.

**Affects**: `WeatherEnclosureCalculator.cs`, `OpeningTracker.cs`

### Semi-Outdoor Source Instability (Cave Maw / Ravine)
**Priority**: HIGH  
**Status**: IMPLEMENTED (2026-02-21)

**Problem**:
After increasing DDA budget from 15→50, semi-outdoor geometry (cave maws, ravines, tree canopies)
produced massive instability. Log analysis showed:
- Opening count swinging 30→82 within seconds
- Sky coverage oscillating 0.15↔0.80  
- 77 NEW openings in 15 seconds (almost all immediately merging)
- Position drift averaging 0.44-1.83 blocks/tick
- Cache evictions at 2-45/sec even while standing still
- Centroid jumps up to 5.9 blocks in one tick

**Root Causes**:
1. Greedy clustering re-seeded from scratch each cycle → different input sets produced different seeds → different centroids
2. 50 verified columns in semi-outdoor produced ~15 members per cluster → removing 3 boundary members shifted centroid ~1 block
3. Flat 0.3 lerp was too aggressive for large centroid shifts (1.5 block movement per tick)
4. Cache grace period of 300ms too short for boundary columns that flicker at 500ms+ cycles

**Resolution — Four Complementary Fixes**:

1. **Anchored Clustering** (`OpeningClusterer`, highest impact):
   - Phase 1: Previous cycle's tracked opening positions used as cluster seeds (anchors)
   - Verified openings assigned to nearest anchor within `CLUSTER_RADIUS`
   - Only `CurrentlyVerified` tracked openings used as anchors (not persisted behind-corner ones)
   - Same openings end up in same cluster each tick → stable centroids
   - Phase 2: Greedy fallback for unassigned openings and first cycle (no anchors yet)

2. **Adaptive DDA Budget** (`WeatherEnclosureCalculator`):
   - `toVerify = lerp(15, 50, SmoothedSkyCoverage)` instead of flat 50
   - Outdoors (sky~0): 15 candidates (rain is everywhere, no precision needed)
   - Enclosed (sky~1): 50 candidates (maximum precision for small openings)
   - Uses SmoothedSkyCoverage to prevent budget oscillation from raw sky swings

3. **Velocity-Aware Position Lerp** (`OpeningTracker`):
   - Lerp factor now depends on shift magnitude:
     - <1 block: 0.3 lerp (~700ms to 90% convergence)
     - 1-2 blocks: 0.12 lerp (~1.8s convergence)
     - 2-3 blocks: 0.05 lerp (~4.5s, near-frozen)
   - Large centroid jumps from member set changes are heavily dampened
   - Small jitter still converges quickly

4. **Extended Cache Grace Period** (`WeatherEnclosureCalculator`):
   - `MAX_CONSECUTIVE_FAILS`: 3→5 (500ms instead of 300ms)
   - Reduces eviction thrashing at cave maw boundaries
   - Structural integrity checks in OpeningTracker handle genuinely blocked columns

**Affects**: `OpeningClusterer.cs`, `WeatherAudioManager.cs`, `WeatherEnclosureCalculator.cs`, `OpeningTracker.cs`

---

### Cave Exit Detection
- When exiting a cave, rain positions at the cave opening above the player don't spawn positional sources early enough
- Root cause: heightmap sampling classifies columns where `playerY < rainHeight` as "covered" — but at cave exits, the rain impact surface IS above the player while sound CAN reach through the opening
- These columns never become DDA candidates, so no positional sources spawn until the player physically walks out
- Need: detect nearby "covered" columns that have clear DDA paths to the player (cave mouth scenario)
- Affects: `WeatherEnclosureCalculator.cs` Step 1 heightmap sampling

### DDA Range Increase
- Current 15 DDA candidates with SCAN_RADIUS=12 only spawns sources ~3 blocks away
- Works fine for walking through/near openings, but fails when opening is >3 blocks of airspace away
- Example: standing in a room with a window 5 blocks away — no positional source spawns
- Need: either increase DDA budget, extend scan radius, or add distance-based priority so farther exposed columns get checked
- Must not break performance (~0.9ms per 100ms tick budget)
- Affects: `WeatherEnclosureCalculator.cs` SCAN_RADIUS, MAX_DDA_CANDIDATES

### Other Potential Improvements (from revert analysis)
- ExpiryFadeRate: separate faster fade for source eviction (~1s vs 3s)
- Proximity fade scaling: gradual by cluster size instead of binary gate
- Fixed Y position: sound source stays at rain impact Y, not player Y
- Position stability: 1.5 block threshold to prevent micro-jitter
- Competitive eviction: score-based slot eviction for louder sources
- Angular sector clustering: prevent same-direction source waste

### Issue 18: RoomRegistry-Based Same-Room Optimization
**Priority**: MEDIUM
**Impact**: Performance optimization + acoustic correctness for enclosed rooms

**Concept**:
Use the basegame `RoomRegistry` (vsessentialsmod) to detect when player and sound sources share the same sealed room. When true, skip expensive per-sound raycasting and apply uniform acoustic treatment.

**Basegame API**:
```csharp
var roomReg = capi.ModLoader.GetModSystem<RoomRegistry>();
Room playerRoom = roomReg.GetRoomForPosition(playerBlockPos);
bool sameRoom = playerRoom.Contains(soundBlockPos);
```
- `Room.Contains(BlockPos)` uses BFS flood-fill bitfield — exact room shape, not just bounding box
- `Room.ExitCount == 0` = fully sealed room (no openings to outside)
- `Room.Location` = Cuboidi bounding box, `Room.PosInRoom` = per-block bitfield
- Works client-side (confirmed: `EntityParticleInsect` uses it with `capi`)

**Affected Systems** (when player + sound in same sealed room):
1. **Occlusion**: Set to 0 for all sounds in same room (no walls between player and source)
2. **Weather Positional**: Disable positional weather sources (sealed room = no rain/wind entry)
3. **Reverb**: Share single reverb calculation for all sounds in room (same acoustic space)
4. **Sound Repositioning**: Skip path resolution (direct LOS guaranteed within room)

**Limitations**:
- Max room size: 14 blocks per dimension (MAXROOMSIZE in RoomRegistry)
- Only works for sealed rooms (`ExitCount == 0`); open doors = exits = not sealed
- `GetRoomForPosition` BFS is cached per chunk but first call is expensive — rate-limit queries
- Larger buildings / open floor plans won't be detected as rooms
- Internal walls within a VS "room" (e.g. furniture, half-walls that don't retain heat) won't muffle

**Config**:
- `EnableRoomOptimization` (bool, default: true) — master toggle
- Only active when `ExitCount == 0` (fully sealed)
- When player is OUTSIDE the room, normal occlusion pipeline runs as usual

**Status**: PENDING DESIGN

---

### Issue 16: Asymmetric Enclosure Smoothing (Phase 5A)
**Priority**: LOW  
**Impact**: Indoor/outdoor transitions feel slightly unnatural

**Problem**:
The enclosure smoothing in `WeatherEnclosureCalculator` uses symmetric exponential smoothing (factor 0.2 at 100ms ticks, ~1s to 90%) for both indoor→outdoor and outdoor→indoor transitions. AAA games use asymmetric smoothing because the perceptual experience differs:

- **Indoor→outdoor** (stepping outside): Should be **fast** (~200-300ms). The psychological "freedom" effect is immediate — you expect the soundscape to open up quickly.
- **Outdoor→indoor** (entering building): Should be **slower** (~500-800ms). The acoustic impression of enclosure builds gradually as reflections wrap around the player.

`AudioRenderer` already does asymmetric smoothing for per-sound filters (250ms muffling, 400ms unmuffling). The weather enclosure metrics should follow the same pattern.

**Implementation**:
In `WeatherEnclosureCalculator.Update()`:
```csharp
// Asymmetric smoothing: fast when opening up, slow when enclosing
float skyFactor = (SkyCoverage < SmoothedSkyCoverage) ? 0.35f : 0.12f;
SmoothedSkyCoverage += (SkyCoverage - SmoothedSkyCoverage) * skyFactor;

float occlFactor = (OcclusionFactor > SmoothedOcclusionFactor) ? 0.12f : 0.35f;
SmoothedOcclusionFactor += (OcclusionFactor - SmoothedOcclusionFactor) * occlFactor;
```

**Complexity**: Low  
**Status**: PENDING

---

### Issue 15: Thunder Overhead Bolt Scoring Bias (Phase 5C)
**Priority**: LOW-MEDIUM  
**Impact**: Thunder sounds wrong when lightning strikes directly overhead with only wall openings

**Problem**:
The thunder opening selection formula in `ThunderAudioHandler.PlayThunderEvent()`:
```
score = dot(boltDir, openingDir) * 0.7 + sqrt(min(weight/8, 1)) * 0.3
```

When the bolt is directly overhead (common for lightning near the player), `boltDir` is nearly vertical. All horizontal wall openings (doors, windows) get low dot products and only sky openings (upward) score well. If the player is in a room with only a door and no roof hole, thunder scores poorly on the only available opening.

**Implementation**:
When `boltDir.Y > 0.7` (near-overhead bolt), interpolate scoring weights toward favoring cluster size:
```csharp
float overheadFactor = Math.Clamp((float)(boltDir.Y - 0.5) / 0.5, 0f, 1f);
float dirWeight = 0.7f * (1f - overheadFactor) + 0.3f * overheadFactor; // 0.7 → 0.3
float sizeWeight = 1f - dirWeight;                                       // 0.3 → 0.7
float score = dot * dirWeight + sizeWeight * sizeWeight;
```

**Complexity**: Low
**Status**: ✅ IMPLEMENTED in `ThunderAudioHandler.PlayLayer2AtBestOpening()` — overhead fix applied when `abs(boltDir.Y) > 0.7`

---

### Issue 14: Directional Clustering Guard (Phase 5B)
**Priority**: LOW-MEDIUM  
**Impact**: Openings in different directions may merge into a single cluster, losing directional audio cues

**Problem**:
`OpeningClusterer` uses a 3.5-block spatial radius to merge nearby openings. If a room has a door on the north wall and a window on the east wall, both within 3.5 blocks of each other, they merge into one cluster positioned between them. This loses the directional cue that is the whole point of positional audio — "rain from the door" vs "rain from the window."

**Implementation**:
Before absorbing a candidate into a cluster, check if the direction from the player to the candidate is within ~60 degrees of the direction from the player to the seed:
```csharp
// In OpeningClusterer.Cluster(), before absorbing candidate:
Vec3d seedDir = normalize(seed.WorldPos - playerEarPos);
Vec3d candDir = normalize(candidate.WorldPos - playerEarPos);
float angleDot = dot(seedDir, candDir);
if (angleDot < 0.5f) continue; // >60 degrees apart = separate clusters
```

Requires passing `playerEarPos` to `OpeningClusterer.Cluster()`.

**Edge cases**:
- Very close openings (< 1 block apart) should always merge regardless of angle
- Player very close to openings: small position differences create large angular differences — need minimum distance guard

**Complexity**: Low-Medium  
**Status**: PENDING DESIGN

---

### Issue 13: Block Change → Enclosure Invalidation (Phase 5B)
**Priority**: MEDIUM-HIGH  
**Impact**: Up to 100ms+ latency when blocks create or seal openings

**Problem**:
When a player places or breaks a block that creates or seals an opening, the weather enclosure system doesn't know until its next scheduled scan tick (up to 100ms + smoothing convergence). The existing `OnBlockChanged` handler in `SoundPhysicsAdaptedModSystem` only invalidates the per-sound `AudioPhysicsSystem` cache, NOT the `WeatherEnclosureCalculator`.

Meanwhile, the `WeatherEnclosureCalculator.Update()` is internally rate-limited to `UPDATE_INTERVAL_MS = 100`. A block placed 1ms after the last scan waits up to 99ms before taking effect, plus ~1s of smoothing convergence.

**Current data flow**:
```
BlockChanged event → SoundPhysicsAdaptedModSystem.OnBlockChanged()
    → acousticsManager.InvalidateCache()  // Per-sound occlusion only
    → (WeatherEnclosureCalculator NOT notified)
```

**Required data flow**:
```
BlockChanged event → SoundPhysicsAdaptedModSystem.OnBlockChanged()
    → acousticsManager.InvalidateCache()
    → weatherManager?.ForceEnclosureUpdate()  // NEW: bypass rate limiter
```

**Implementation**:
1. Add `ForceUpdate()` method to `WeatherEnclosureCalculator` (resets `lastUpdateMs = 0`)
2. Add `ForceEnclosureUpdate()` to `WeatherAudioManager` that calls calculator's `ForceUpdate()`
3. In `SoundPhysicsAdaptedModSystem.OnBlockChanged()`, check if changed block is within scan radius (~12 blocks) of player, if so call `weatherManager.ForceEnclosureUpdate()`
4. Distance check prevents remote block changes (chunk loads, other players) from triggering unnecessary re-scans

**Complexity**: Low  
**Status**: PENDING

---

### Issue 12: Opening Position vs Rain Impact Position (Phase 5B)
**Priority**: HIGH  
**Impact**: Wall openings (windows, horizontal gaps) have incorrect positional source placement

**Problem**:
- Current: Positional sources placed at rain impact position (where rain hits ground/surface)
- Reality: 
  - **Sky openings (roof holes)**: Rain impact ≈ opening position ✅ Works correctly
  - **Wall openings (windows, gaps)**: Rain impacts OUTSIDE the wall, opening is ~1 block inward toward player ❌ Wrong
  
**Example**:
```
[WALL] [OPENING] [INSIDE ROOM - PLAYER]
   ↓      ↓           ↓
 Rain  Actual     Current
 hits  opening    sound
 here  position   position
       (needs     (wrong -
        sound)     outside)
```

**Required Changes**:
1. **Algorithm to detect opening type**: Sky opening vs wall opening
2. **Direction detection**: Which direction is "inward" (toward player vs away)
3. **Position adjustment**: For wall openings, shift position ~1 block inward
4. **Verification**: Opening must still be structurally valid at adjusted position

**Complexity**: Medium-High  
- Need to distinguish opening types (could use opening normal vector from verified positions)
- Need player direction context (already have LastVerifiedPlayerPos)
- Risk: Adjusted position might not be in air (could be inside wall if opening is narrow)

**Related**: See `docs/PORTAL_DOOR_ANALYSIS.md` for door/trapdoor/window portal behavior analysis  
**Status**: IMPLEMENTED (2026-02-13)
**Resolution**: DDA entry point tracking added to `RunOcclusionFullCubeOnly`. Tracks last solid-to-air transition along each ray. Entry point flows through `VerifiedRainPosition.EntryPos` → `OpeningClusterer` centroid (uses `EntryPos ?? WorldPos`) → `TrackedOpening.WorldPos` → `PositionalSourcePool`. Sky openings (no solid blocks along path) get `EntryPos = null`, falling back to `WorldPos` (rain impact position = correct for sky openings).

---

### Issue 17: Height Sampling Gap for Wall Openings (Phase 5B)  
**Priority**: HIGH  
**Impact**: Wall openings at height 2-3 above ground (windows, archways) missed at distance

**Problem**:
Previous `COLUMN_SAMPLE_HEIGHTS = { 1.01, 4, 8, 12 }` had a 3-block gap between +1 and +4. DDA rays from rain columns to the player pass through walls at heights determined by ray angle. A 1-block hole at height 2-3 falls in the gap — no ray starts at the right height to pass through it at the correct angle.

At distance, ray angles flatten further: the +4 ray clears OVER the wall entirely instead of going through the hole. Result: wall openings only detected when standing close.

**Resolution**: IMPLEMENTED (2026-02-13)
Added +2 and +3 to fill the critical window/door-height gap:
```
COLUMN_SAMPLE_HEIGHTS = { 1.01, 2, 3, 4, 8, 12 }
```
Adds ~30 extra DDA rays worst case per scan (15 candidates × 2 extra heights). Still within performance budget (~0.9ms per 100ms tick).

---

### Issue 11: Cache Optimization - Adaptive Update Rates
**Status**: INVESTIGATION REQUIRED
**Priority**: MEDIUM
**Description**: 
The current caching system in `AudioPhysicsSystem.cs` uses fixed distance-based intervals and movement thresholds. There may be opportunities to further optimize by:
1. **Reducing update frequency when player is stationary or moving slowly**
2. **Reducing update frequency when player is not actively interacting with blocks** (opening doors, breaking/placing blocks, etc.)

**Current Implementation**:
- Distance-based intervals: 50ms (0-10m), 200ms (10-30m), 500ms (>30m)
- Static cache with 0.25 block movement threshold
- 2-second force refresh timer
- Block change invalidation with 200ms debounce

**Optimization Ideas**:
1. **Movement-Based Scaling**:
   - Track player velocity/acceleration
   - When player velocity < threshold (e.g., 0.1 blocks/tick), increase intervals by 2-4x
   - When player is completely stationary, use maximum intervals (e.g., 1000ms even for close sounds)
   - Gradually ramp intervals back down as player accelerates

2. **Block Interaction Detection**:
   - Track recent block interactions (OnBlockBroken, OnBlockPlaced, OnBlockInteract events)
   - When player hasn't interacted with blocks in last N seconds, increase intervals
   - On any block interaction, temporarily reset to aggressive intervals for M seconds
   - This catches door openings, block placements that create new occlusion paths

**Potential Gains**:
- Standing still in a cave listening to ambient sounds: could reduce raycasts by 50-75%
- Walking through open areas without touching blocks: could reduce raycasts by 25-50%
- Active building/mining: maintains current responsiveness

**Risks**:
- Delayed response to environmental changes if thresholds are too aggressive
- Complexity in tracking player state
- Edge cases where sounds change but player appears stationary (e.g., moving platforms, elevators)

**Agent Investigation Prompt**:
```
Investigate optimizing the AudioPhysicsSystem caching in Y:\ClaudeWINDOWS\projects\sound-physics-adapted\src\Core\AudioPhysicsSystem.cs.

Current system uses:
- Distance-based intervals (50ms/200ms/500ms)
- Static cache with 0.25 block movement threshold
- 2-second force refresh
- Block change invalidation (200ms debounce)

Research and propose:
1. **Player Movement Tracking**: How to detect when player is stationary/slow-moving vs. actively moving
   - Examine Vintage Story API for velocity/acceleration access
   - Determine appropriate thresholds for "stationary" vs. "slow" vs. "fast"
   - Design interval scaling algorithm (e.g., linear, exponential, stepped)

2. **Block Interaction Tracking**: How to detect when player is actively interacting with blocks
   - Find all relevant VS API events (OnBlockBroken, OnBlockPlaced, OnBlockInteract, door usage, etc.)
   - Design interaction window (how long after interaction to maintain aggressive updates)
   - Consider interaction proximity (only care about nearby interactions?)

3. **Implementation Strategy**:
   - Propose specific code changes to AudioPhysicsSystem.cs
   - Identify new fields/state to track
   - Define new config parameters (if any)
   - Estimate performance impact (best case, worst case, typical case)

4. **Edge Cases & Risks**:
   - What happens if player is on a moving platform?
   - What if blocks change far from player (chunk updates, other players building)?
   - How to handle sounds that move independently of player?
   - Potential for "stale" audio during edge cases

5. **Testing Strategy**:
   - How to measure raycast reduction
   - Test scenarios (standing still, walking, building, combat)
   - Metrics to track (raycasts/second, cache hit rate, perceived latency)

Deliverable: Write a detailed analysis document with:
- Proposed algorithm/pseudocode
- Config parameters
- Expected performance gains
- Risk assessment
- Recommendation (implement, defer, or reject with reasoning)
```

**Files to Examine**:
- `src/Core/AudioPhysicsSystem.cs` - Current caching implementation
- `src/SoundPhysicsAdaptedModSystem.cs` - Tick handlers, block change events
- Vintage Story API docs - Player entity, velocity, block events

---

### Issue 10: Bird/Forest Sounds Not Occluded (Log Missing)
**Status**: INVESTIGATION REQUIRED
**Priority**: HIGH
**Symptoms**:
- User reports hearing bird sounds unoccluded in caves.
- Logs show `waterwaves` and `beehive` are processed/occluded correctly.
- Logs contain NO entries for "bird" or "forest" sounds (grep confirmed).
- "Forest Symphony" mod is NOT currently loaded, so these are likely vanilla ambient sounds.
**Hypothesis**:
- Vanilla bird sounds might be played via a different system (Music? Ambient?) that bypasses `LoadSound` or `StartPlaying`.
- Or they are not named "bird"/"forest" in the assets (need to find asset names).
**Action**:
- Identify asset names for vanilla forest sounds.
- Determine how they are played (Ambient Sound System?).
- Ensure they go through the occlusion logic.
**Investigation Findings (2026-02-06)**:
- **Weather Sounds**: `WeatherSimulationSound.cs` initializes rain/wind with `(0,0,0)` position. This confirms they are global/ambient and correctly ignored by standard occlusion (Phase 5 will address this via Enclosure).
- **Mechanical Blocks**: `BEBehaviorLargeGear3m.cs` uses `PlaySoundAt`, which *should* be caught by `StartPlaying` patch if it has a position.
- **Bird Sounds (Vanilla)**:
  - Likely played as `EnumSoundType.Ambient` (Value 2) or `Weather`.
  - Most vanilla ambient sounds (wind, rain) use `Position = (0,0,0)` (Relative).
  - **Current Behavior**: `LoadSoundPatch` explicitly skips sounds with `(0,0,0)` position to avoid issues.
  - **Result**: These sounds are **unoccluded** by design in the current system.
  - **Fix**: Phase 5 (Enclosure) will handle global/ambient attenuation. We cannot "occlude" them without a virtual position.

---

### Issue 7: Add Debug Blocks for Audio Testing
**Status**: TODO
**Priority**: MEDIUM
**Description**: 
- Create 2 debug blocks to test sound behavior.
- Block 1: Triggers `PlaySound` at its location.
- Block 2: Triggers `PlaySoundAt` at its location (to test the `StartPlaying` patch).
- Goal: Verify that both methods are correctly occluded at the block's position.

### Issue 8: Positional Audio for Resonator (Music Box)
**Status**: FIXED (2026-02-09)
**Priority**: MEDIUM
**Resolution**: Implemented positional audio with full feature set.
- Resonator now plays from block position with distance attenuation
- Added pause/resume via Shift+RightClick
- Implemented playback position persistence across world save/load
- Client-server sync for multiplayer support

### Issue 6: Abrupt Filter Transitions (No Smoothing/Interpolation)
**Status**: FIXED (2026-02-04)
**Priority**: HIGH

**Symptoms**:
- Sound filter changes are instant/jarring
- Moving past a wall causes abrupt snap between muffled↔clear
- Unnatural, "clicky" transitions instead of smooth fades

**Resolution**: Added exponential smoothing to filter transitions.
- Each `FilterEntry` now has `CurrentValue` and `TargetValue`
- On each tick, `CurrentValue` moves 30% toward `TargetValue`
- With 100ms tick rate, full transition takes ~300-400ms
- Rapid changes are handled naturally (target updates, current follows)

**Implementation** in `SoundFilterManager.cs`:
```csharp
const float FILTER_SMOOTH_FACTOR = 0.3f;
entry.TargetValue = filterValue;
float smoothedValue = entry.CurrentValue + (entry.TargetValue - entry.CurrentValue) * FILTER_SMOOTH_FACTOR;
```

---

### Issue 5: Chiseled/Voxel Block Occlusion Behavior
**Status**: TO TEST
**Priority**: MEDIUM

**Context**: Hybrid DDA now uses ray-AABB intersection for partial blocks (non-full-cubes).
Doors confirmed working - open door panel on the side is missed by ray, closed door is hit.

**Remaining question**: How do chiseled/voxel blocks behave with this system?
- Chiseled blocks have arbitrary shapes - does `GetCollisionBoxes()` return accurate shapes?
- Does a chiseled block with a hole/archway correctly let rays pass through?
- Edge cases: very thin chiseled walls, L-shaped blocks, decorative shapes
- If collision boxes are coarse (single AABB wrapping the whole shape), rays can't pass through holes
- May need volume-based scaling as fallback for chiseled blocks

---

### Issue 4: Ray Passing Through Block Edges at Perpendicular Angles
**Status**: FIXED (2026-02-04) - Implemented DDA grid traversal
**Priority**: HIGH

**Symptoms**:
- Standing with a full block between player and sound source
- At certain angles (especially perpendicular), ray passes through block edge
- Results in no occlusion when there should be full occlusion
- Voting system helps but doesn't fully solve the fundamental raycast issue

**Root Cause**:
- Stepped raycast (even at 0.2 step size) samples positions along a line
- At perpendicular angles, ray can pass between block corner/edge samples
- Block position truncation (`AsBlockPos`) doesn't account for actual collision geometry

**Minecraft SPR Solution**:
- Uses `BlockGetter.traverseBlocks()` with VoxelShape collision
- Checks `blockHit.isFaceSturdy()` on the face that was hit
- Proper ray-box intersection, not position sampling

**VS API Available**:
```csharp
// Block face solidity check
block.SideSolid[BlockFacing.NORTH.Index]  // bool array for each face

// Collision box retrieval
block.GetCollisionBoxes(blockAccessor, pos)  // Cuboidf[]

// Ray-box intersection (on BlockRayTracer class)
RayIntersectsWithCuboid(Cuboidd box, ref BlockFacing hitFace, ref Vec3d hitPos)
```

**Potential Solutions** (ordered by complexity/performance):

1. **DDA Grid Traversal** (Medium complexity, good performance)
   - Use Digital Differential Analyzer algorithm (already in legacy/RayTraceDDA.cs)
   - Steps through EVERY block the ray passes through, not just sampled positions
   - Guaranteed to never skip a block
   - O(n) where n = blocks traversed

2. **Face Solidity Check** (Low complexity, minimal cost)
   - After detecting a block hit, check if the face we're entering is solid
   - `block.SideSolid[entryFace.Index]`
   - Reduces false negatives from non-solid faces (fences, doors)
   - Doesn't solve the edge-passing problem alone

3. **Proper Ray-Box Intersection** (High complexity, higher cost)
   - For each block, get `GetCollisionBoxes()` and test ray intersection
   - Most accurate but expensive for many sounds
   - Could be used as fallback for short-distance sounds only

4. **Hybrid Approach** (Recommended)
   - Use DDA for efficient block traversal (never skip blocks)
   - Add SideSolid check for the entry face
   - Only use GetCollisionBoxes for non-full blocks
   - Cache collision data where possible

**Performance Considerations**:
- Current: ~15 steps per ray × 9 rays = 135 block lookups per sound
- DDA: ~3-64 block lookups per ray (proportional to distance)
- GetCollisionBoxes: Allocates arrays, slower than simple block lookup
- Must maintain <1ms per sound calculation for smooth audio

**Resolution**: Implemented DDA grid traversal (Option 1). DDA guarantees visiting
every block the ray passes through by always stepping to the nearest grid boundary.
This eliminates the edge-passing problem without needing collision box tests.

**Additional Fix (2026-02-04)**: Changed offset rays from PARALLEL to CONVERGENT.
- Previous: Both endpoints offset by same amount (parallel rays)
- Now: Only sound source offset, all rays converge to player position
- Fixes false occlusion from adjacent blocks at short distances (beehive edge case)

---

## Completed

### Phase 5B Multi-Type Positional Weather (2026-02-10)
**Status**: COMPLETED
- Extracted PositionalSourcePool.cs (reusable Looping/OneShot)
- Created WeatherPositionalHandler.cs (3 pools: rain/wind/hail)
- Per-type config (MaxPositionalRainSources, etc.)
- Per-type ducking (rain/hail 50%, wind 20%)
- RainAudioHandler refactored to Layer 1 only

### Sealed Opening Persistence Fix (2026-02-10)
**Status**: COMPLETED
- Fixed DDA skipFirst bypass (explicit block check at member position)
- Reordered step 4 (structural checks before audibility)
- Gated audibility on sound repositioning (HasSmoothedOcc flag)
- Three debug states: VERIFIED / repositioned / persisted

### Block Code Pattern Matching
**Status**: COMPLETED (2026-02-04)
**Resolution**: Added `GetBlockCodeMultiplier()` for block name-based occlusion overrides.
- Metal blocks (metalblock, ironblock, steelblock, etc.): 1.0 (highest occlusion)
- Leather: 0.6 (moderate-high)
- Wool/carpet: 0.4 (absorptive)
- Concrete/plaster: 0.9 (high)
- Chests/containers: 0.5 (hollow)
- Beds: 0.3 (soft/hollow)
- Falls back to material-based occlusion if no pattern matches

### Issue 3: Thin Walls at 90-Degree Angles Not Occluding
**Status**: FIXED (2026-02-04)
**Resolution**: Implemented multi-ray occlusion with MAXIMUM aggregation.
- Shoots 9 rays: 1 center + 8 offset rays at corners (±variation in X,Y,Z)
- Takes MAXIMUM occlusion across all rays (catches walls that any ray hits)
- Changed from SPR's MINIMUM (finds gaps) to MAXIMUM (detects walls)
- Reduced step size from 0.5 to 0.2 for better accuracy at close range
- Config: `OcclusionVariation` (default 0.35)
- Fixes perpendicular thin walls that center ray missed at block edges

### Issue 1: Thick Walls Not Muffling Enough
**Status**: FIXED
**Resolution**: Increased `BlockAbsorption` to 0.5 (was 0.15) for much stronger muffling.
- 5 stone blocks now result in ~92% sound reduction (was ~10%).

### Issue 2: Occlusion Not Updating When Player Moves
**Status**: FIXED (2026-02-04)
**Resolution**: Added game tick that recalculates occlusion for all tracked sounds when player moves.
- Previously only calculated on sound load/position change
- Stationary sounds (waterfalls, beehives) never updated
- Now checks every 100ms if player moved >0.5 blocks, recalculates all

### Per-sound Filter Architecture
**Status**: COMPLETED (2026-02-03)
- Each sound gets unique OpenAL filter via EFX.GenFilter()
- No more global filter thrashing
- Proper cleanup on sound disposal

### Issue 9: Corner Occlusion Over-Muffling
**Status**: FIXED (2026-02-09) - Solved by Phase 4B
**Priority**: HIGH

**Symptoms**:
- Walking around a wall corner caused excessive muffling at the corner itself
- DDA raycast traversed 2-3 blocks diagonally through single wall thickness

**Resolution**: Phase 4B permeation-weighted path resolution (`SoundPathResolver.cs`).
- Multiple bounce rays find paths *around* corners with lower occlusion
- Permeation weighting: easier paths get higher weight via `PermeationBase^occlusion`
- Weighted average direction naturally routes sound toward least-occluded path
- Result: Sound "finds" the opening instead of counting diagonal blocks

### Forest Symphony Investigation
**Status**: INVESTIGATED
- Bird sounds spawn at leaf block positions, ARE positional
- Should be captured by LoadSound patch
- Check logs for `forestsymphony:sounds/` entries

---

## Architecture

```
LoadSound/SetPosition Postfix
    -> OcclusionCalculator.Calculate(soundPos, playerPos, blockAccessor)
    -> SoundFilterManager.SetOcclusion(sound, filterValue, position)
    -> EfxHelper.SetLowpassGainHF(filterId, filterValue)

OnOcclusionUpdateTick (every 100ms)
    -> Check if player moved >0.5 blocks
    -> SoundFilterManager.RecalculateAll(playerPos, blockAccessor)
```

### Key Files
- `src/Core/OcclusionCalculator.cs` - Ray trace logic
- `src/Core/SoundFilterManager.cs` - Per-sound filter management
- `src/Core/EfxHelper.cs` - OpenAL EFX wrapper
- `src/Patches/LoadSoundPatch.cs` - Harmony patches
- `src/SoundPhysicsAdaptedModSystem.cs` - Game tick handlers

---

## Last Updated
2026-02-05 - REWROTE Phase 3 architecture: switching from Parallel+Blend to Complete Override (SPR-based)
  - Previous implementation failed due to VS formula issues and double-counting
  - New approach: disable vanilla reverb, implement SPR-style multi-slot EAX reverb
  - See research/07_PHASE3_REVERB_ARCHITECTURE.md for full analysis
