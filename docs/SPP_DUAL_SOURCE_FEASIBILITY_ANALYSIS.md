# SPP Dual-Source Approach Feasibility Analysis

**Date**: 2026-02-21
**Context**: Issue 19 - Probe Ray Shared Airspace Contribution Asymmetry
**Question**: Should we switch to SPP's dual-source (repositioned + permeated) approach instead of current single blended source?

---

## Executive Summary

**Verdict**: ❌ **NOT VIABLE with current architecture**
**Reason**: Insufficient data quality from current ray system
**Path Forward**: Complete Stage 3 listener-centric propagation FIRST, then re-evaluate

**Critical Blocker**: Current system uses **sparse probe data** (12 rays in 15° cone) + source-centric fibonacci rays. SPP's dual source requires **dense, stable path data** (640+ samples per sound) that we don't have.

---

## Current Architecture Analysis

### What We Have Now (Actual Code State)

**Despite PHASE4B_PROPAGATION_HANDOFF.md claiming "Stage 3 IMPLEMENTED"**, the actual code in `AcousticRaytracer.cs` reveals:

1. **Source-Centric Fibonacci Rays** (lines 332-474)
   - 64 rays from **sound position** (not listener)
   - Bounce up to 4 times
   - At each bounce: check `pathOcclusion < 0.5` to player (shared airspace)
   - Add weighted paths to `SoundPathResolver`
   - Feed reverb calculation (correct for source-centric reverb)

2. **Player Probe Rays** (lines 546-554, 588+)
   - 12 rays from **player** toward sound
   - 15° cone jitter (narrow coverage)
   - Wall hit → check 6 face-adjacent neighbors for air
   - If opening found → verify clear path to sound
   - Add to path resolver with opening boost

3. **Single Blended Output**
   - `BlendedOcclusion = weightedAvg(ALL paths)`
   - One OpenAL source per sound
   - One LPF filter driven by `BlendedOcclusion`
   - Position = weighted avg of open path directions

**Path Data Quality**:
- Fibonacci rays: ~128 bounce points (32 rays × 4 bounces)
- Probe rays: 12 attempts, often 0-3 successful openings
- **Total**: ~130-140 path samples per sound
- **Issue**: Probes flicker (position-based jitter), fibonacci rays miss small openings

### What Stage 3 Was SUPPOSED To Be (Not Implemented)

From PHASE4B_PROPAGATION_HANDOFF.md Section 3:
- 64 fibonacci rays from **PLAYER position** (listener-centric)
- Bounce up to 4 times = 256 bounce points
- At EACH bounce point: fire "red ray" toward EACH active sound
- Count blocks between bounce → sound
- Weight = `permeation^blockCount / distance²`
- **Result**: 256+ samples PER SOUND, shared cost across all sounds

**This is NOT in the codebase.** The current fibonacci rays are still source-centric.

---

## SPP Architecture (Reference)

### What SPP Does (From references/SoundPhysicsPerfected)

1. **Listener Propagation**
   - 128 rays from player position
   - Bounce up to 5 times = 640 potential bounce points
   - At EVERY bounce point: fire "red ray" toward EVERY active sound
   - Count blocks via `countBlocksBetween()`
   - Weight = `permeationAbsorption^blockCount / distance²`

2. **Dual OpenAL Sources**
   - **Repositioned source**: Plays at opening position
     - Direction = weighted avg of OPEN paths (low blockCount)
     - Filter = clean/moderate LPF
     - Volume = confidence-based (high when many open paths)
   - **Permeated source**: Plays at original sound position
     - Filter = heavy LPF based on through-wall permeation
     - Volume = `MAX_PERMEATED_GAIN=0.12` (low, just ambient presence)
     - Separate lifecycle (create/fade/destroy)

3. **Why It Works**
   - 640+ samples per sound → **always populated**
   - No probe flicker → targets stable frame-to-frame
   - Dual volumes/filters balance naturally from physics

---

## Feasibility Assessment

### Option A: Dual Source with Current Ray System ❌

**Blocker**: Tried and failed (commit f0f9468 reverted dual source)

**Problems Encountered** (from PHASE4B_PROPAGATION_HANDOFF.md):
| Problem | Root Cause |
|---------|------------|
| `filter=1.0` behind thick walls | `openPathOcc=0.00` when all paths permeated → open filter defaults to "clear" |
| Volume oscillation (louder behind wall) | 12-probe flicker → permeated weight swings → second source pumps |
| 5+ magic numbers needed | Band-aids for unstable data (`MAX_PERMEATED_GAIN=0.12`, energy caps) |
| Phase drift on looping sounds | Two sources sharing buffer need constant `SecOffset` sync |
| Cleanup complexity | Create/fade/destroy lifecycle per sound |

**Why SPP Doesn't Have These**:
- 640 samples → no flicker → no hold heuristic needed
- Open/permeated targets always populated → no default fallbacks
- Stable data → no magic caps needed

**Verdict**: Cannot reproduce SPP behavior with 12-ray probe data.

---

### Option B: Dual Source AFTER Implementing Stage 3 ✅ Viable

**Prerequisites**:
1. Implement listener-centric propagation (64 rays from player, 256 bounce points)
2. Fire "red rays" from each bounce → each sound (DDA block count)
3. Path resolver receives 256+ samples per sound, always populated

**Once Stage 3 Complete**:
- Data quality matches SPP (dense, stable, no flicker)
- Dual source becomes architecturally viable
- Open/permeated split is physics-based, not heuristic

**Estimated Complexity** (for dual source after Stage 3):
- **Source lifecycle**: ~200 lines (create/destroy/fade permeated source)
- **Energy balancing**: ~80 lines (confidence-based volume, max permeation caps)
- **Phase sync**: ~50 lines (SecOffset sync for looping sounds)
- **Filter management**: ~40 lines (dual LPF targets)
- **Total**: ~370 lines (matches removed code from f0f9468)

**Performance Cost**:
- 2× OpenAL sources per occluded sound (current max ~20 occluded = 40 sources)
- Acceptable (OpenAL supports 256 sources easily)

**Perceptual Benefit**:
- Spatial separation: through-wall sound at original pos + opening sound at reposition
- Directional cue: "sound is behind that wall" vs "sound is from that opening"
- Subtle but real improvement for thick-wall scenarios

---

### Option C: Improve Current Single Blended Source ✅ Best Short-Term

**Address Issue 19 WITHOUT dual source**:

**Problem**: Probe-found openings don't contribute to `sharedAirspaceRatio` → floor doesn't benefit.

**Solutions** (ranked by complexity):

1. **Path Clarity Floor (Already Implemented)** ✅
   - Lines 408-434 in `AudioPhysicsSystem.cs`
   - `pathClarity = openPaths / totalPaths`
   - `clarityFloor = -log(sqrt(pathClarity) * 0.35) / blockAbsorption`
   - Take `min(sharedAirspaceFloor, clarityFloor)`
   - **This already solves Issue 19** — probes DO influence floor via pathClarity

2. **Wider Probe Pattern** (Issue 2 from SOUND_REPOSITIONING_ANALYSIS.md)
   - Change 15° cone → hemisphere or wider cone (30-45°)
   - Or: Use fibonacci sphere from player (12 evenly distributed rays)
   - Cost: Same 12 rays, better coverage
   - Fixes: Right-angle corridors, thick walls with perpendicular openings

3. **Deeper Neighbor Search** (Issue 3)
   - Change 1-block neighbor check → 2-3 block BFS
   - When probe hits thick wall, search deeper for openings
   - Cost: ~18-54 extra block lookups per hit (low)
   - Fixes: Thick walls (5+ blocks)

**Verdict**: Options 1 is DONE. Options 2-3 are low-hanging fruit, implement before Stage 3.

---

## Recommendation Matrix

| Stage | Action | Complexity | Impact | Priority |
|-------|--------|-----------|--------|----------|
| **Immediate** | Verify clarity floor works (already in code) | 0 | High | ✅ DO NOW |
| **Short-term** | Widen probe pattern (hemisphere, not cone) | Low | Medium-High | ✅ DO NEXT |
| **Short-term** | Deeper neighbor search (2-3 block BFS) | Low | Medium | ✅ DO NEXT |
| **Mid-term** | Implement Stage 3 listener propagation | High | Very High | Plan & scope |
| **Long-term** | Re-evaluate dual source after Stage 3 | Medium | Medium | Defer decision |

---

## Stage 3 Implementation Scope (If Pursued)

**Files to Modify**:
1. `PropagationCalculator.cs` (NEW) — listener ray pass, ~400 lines
2. `AcousticRaytracer.cs` — remove `ProbeForOpenings()`, split reverb-only vs path-only, ~200 lines changed
3. `AudioPhysicsSystem.cs` — call propagation once per update, distribute results, ~50 lines changed
4. `SoundPathResolver.cs` — handle 256+ paths efficiently, ~40 lines changed

**Performance Budget**:
- 64 listener rays × 4 bounces = 256 bounce points
- Per bounce: N_occluded × 1 DDA traversal
- Example: 10 occluded sounds → 2560 DDA traversals
- Current: 10 sounds × 64 source rays × 4 bounces = 2560 DDA traversals (same!)
- **Net cost**: Zero increase for 10 sounds, SAVINGS for >10 sounds

**Risk**:
- High complexity (listener/source hybrid is unique to VSSSP)
- Must maintain source-centric reverb while adding listener-centric paths
- Testing burden (ensure reverb still correct, paths improve)

**Benefit**:
- Eliminates probe flicker entirely
- Opens path to dual source (if desired later)
- Matches SPP path quality

---

## Answer to User's Question

> "How viable/easy would it be to use SPP Minecraft approach for sound repositioning instead?"

**Answer**:

**Not viable with current architecture.** SPP's dual-source approach **requires dense, stable path data** (640+ samples per sound). Our current system provides only ~130-140 samples per sound, with 12-probe flicker causing the exact bugs that forced us to revert dual source in commit f0f9468.

**However**, your **current single-blended-source system already has Issue 19 partially solved** via the clarity floor (lines 408-434). Probe-found openings DO contribute to the floor via `pathClarity`, just indirectly.

**Better path forward**:
1. ✅ Verify clarity floor is working (check logs for `clarityFloor` vs `sharedAirspaceFloor`)
2. ✅ Fix probe geometry (widen cone to hemisphere, deeper neighbor search) — **LOW EFFORT, HIGH IMPACT**
3. 🔄 Implement Stage 3 listener-centric propagation — **HIGH EFFORT, enables SPP-style dual source**
4. ⏳ Re-evaluate dual source after Stage 3 data quality matches SPP

**SPP approach is viable AFTER Stage 3.** Not before.

---

## Issue 19 Specific Analysis

**Current Asymmetry**:
- Fibonacci rays finding shared airspace → contribute to `sharedAirspaceRatio` → SPR-style floor
- Probe rays finding openings → contribute to `pathClarity` → clarity floor
- `finalFloor = min(sharedAirspaceFloor, clarityFloor)` (line 437)

**Is this asymmetry a problem?**

**No.** The two floor mechanisms serve different purposes:
- **Shared airspace floor**: Measures how much of the acoustic environment around the sound has LOS to player (reverb-driven)
- **Clarity floor**: Measures how many of the found paths are clear vs permeated (path-driven)

Taking the minimum (most favorable) ensures sound recovery works via EITHER:
- Many fibonacci rays finding shared airspace (open rooms)
- OR many probe rays finding clear openings (corridors through walls)

**The asymmetry is intentional and correct.**

**Real Issue 19 Problem**: Probes failing to find openings in thick walls → both floors fail → terrible muffle.

**Root Cause**: Probe geometry (narrow cone, shallow neighbor search), NOT floor calculation.

---

## Conclusion

**Don't switch to SPP dual-source now.** Fix probe geometry first (easy wins), then implement Stage 3 listener propagation (prerequisite for dual source).

**Issue 19 is NOT about floor asymmetry** — it's about **probe coverage failing for thick walls / right-angle corridors**.

**Recommended fixes** (in order):
1. Widen probe cone 15° → 45° or hemisphere
2. Deeper neighbor search 1 block → 2-3 blocks BFS
3. (Later) Implement Stage 3 listener-centric propagation
4. (Optional) Re-add dual source after Stage 3

