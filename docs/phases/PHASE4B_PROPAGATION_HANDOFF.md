# Phase 4B: Sound Propagation — Single Blended Source (Handoff Document)

## Status: Stage 3 IMPLEMENTED — Listener-centric propagation rays

**Last Updated**: 2026-02-09

---

## 📌 TL;DR

**Current State**: Sound repositioning works via single blended source. ONE OpenAL source per sound,
positioned at the best opening, muffled by `BlendedOcclusion` (weighted average of ALL acoustic paths).
12 narrow probes with occlusion-based hold workaround. ~1300 lines AudioRenderer, ~365 lines AudioPhysicsSystem.

**Architecture**: Single source with blended filter (replaces failed dual-source attempt).

**Current**: Stage 3 implemented. PropagationCalculator fires 64 listener rays, caches 256+ bounce points, fires red rays per-sound. ProbeForOpenings and path hold removed.

---

## Why Single Blended Source (Not SPP-Style Dual Source)

### The Root Cause: Data Quality

SPP fires 128 rays from player position, bouncing up to 5 times. At EVERY bounce point, it fires
"red rays" toward EVERY active sound. That's ~640 samples per sound per cycle. Results are always
populated, always stable.

**We have 12 probes in a 15° cone.** The data is sparse and flickers when the player moves slightly.

This difference is everything. SPP can run two independent OpenAL sources (repositioned + through-wall)
because both sources receive stable, reliable target values every cycle. When we tried dual sources:

| Problem | Cause |
|---------|-------|
| `filter=1.0` behind thick walls | `openPathOcc=0.00` when all paths are permeated → open filter defaults to "clear" |
| Volume oscillation (audibly louder behind wall) | 12-probe flicker → permeated weight swings wildly → second source pumps |
| 5+ magic numbers needed | `MAX_PERMEATED_GAIN=0.12`, `indirectCap` formula, energy caps — all band-aids for unstable data |
| Phase drift on looping sounds | Two independent sources sharing a buffer need constant SecOffset sync |
| Cleanup complexity | Create/fade/destroy lifecycle for the second source per-sound |

### What Single Blended Source Solves

`BlendedOcclusion = weightedAvg(ALL paths)` — one number, physics-based, no magic.

- Behind thick wall with small opening: many permeated paths (high occ, low weight) + few open paths (low occ, high weight) → blend lands at moderate muffle. Correct.
- Behind thin wall: permeated paths have lower occ → blend is less muffled. Correct.
- Direct LOS: no permeated paths → blend equals open average → clean. Correct.
- No open paths: returns null → falls back to direct occlusion. Correct.

One source, one filter, no energy management, no phase sync, no creation/destruction lifecycle.

### Decision Matrix (Why We Chose This)

| Criterion | Dual Source (SPP) | Single Blended (Ours) | Winner |
|-----------|-------------------|-----------------------|--------|
| Works with 12 sparse probes | No — flickery targets | Yes — averages dampen flicker | **Single** |
| Spatial separation (hear through vs over wall) | Yes — two positions | No — one blended position | Dual |
| Energy management complexity | High — must balance two sources | None — one source, one filter | **Single** |
| Code complexity (~lines) | ~350 extra lines | 0 extra lines | **Single** |
| Through-wall muffle quality | Dedicated filtered source | BlendedOcclusion approximation | Dual (marginal) |
| Stability / predictability | Poor with sparse data | High | **Single** |
| Future-proof (post Stage 3) | Becomes viable | Still works | Tie |

**Score: Single wins 4/7, Dual wins 1/7 (spatial separation), 1 marginal, 1 tie.**

The ONLY thing dual-source does better is spatial separation — hearing the through-wall component at
the original position while the over-wall component comes from the opening. With our current probe
quality, this spatial cue was completely masked by volume artifacts.

### Could Dual Source Work After Stage 3?

**Yes.** Once listener-centric propagation (Stage 3) gives us 256+ bounce points with red rays toward
each sound, our data quality matches SPP's. At that point:

- Path results are always populated (no flicker → no hold workaround needed)
- Permeated weight/volume targets would be stable frame-to-frame
- Dual source becomes architecturally viable

Whether it's *worth* re-adding dual source after Stage 3 is a judgment call. The spatial separation
benefit is real but subtle — most players won't notice the difference between "muffled from the opening"
and "muffled from the opening + faint through-wall from behind." The complexity cost is 350+ lines.

**Recommendation**: Implement Stage 3 first with single blended source. If spatial separation feels
lacking during testing, dual source can be cleanly re-added on top of stable data.

---

## What We Do Better / Worse Than SPP Right Now

### Better Than SPP

| Aspect | SPP | Us | Why Ours Is Better |
|--------|-----|----|--------------------|
| **Per-source reverb** | Listener-only (all sounds get player's room reverb) | Source-centric (64 rays from each sound) | A beehive behind a wall gets cave reverb, not player's outdoor reverb |
| **Filter physics** | Two separate LPFs (repositioned: clean, permeated: heavy muffle) | One blended LPF from all paths | Simpler, fewer edge cases, no energy balancing bugs |
| **OpenAL resource usage** | 2 sources per occluded sound | 1 source per sound | Half the source count, no sync overhead |
| **Config tuning** | Many knobs (permeation base, red ray weight, dual volume caps) | Fewer knobs (PermeationBase, BlockAbsorption, threshold) | Less for users to break |

### Worse Than SPP

| Aspect | SPP | Us | Why SPP Is Better |
|--------|-----|----|--------------------|
| **Path reliability** | 640+ samples per sound, always populated | 12 probes in 15° cone, flickers | Occlusion-based hold is a workaround, not a fix |
| **Spatial separation** | Through-wall sound at original pos + opening sound at reposition | One source at opening, blended muffle | SPP gives directional cue for "sound is behind that wall" |
| **Probe coverage** | Player-centric rays cover full sphere | Source-toward-player probes miss many openings | SPP finds openings we miss (side corridors, angled walls) |
| **Cost scaling** | O(rays + N × bounces) | O(N × (64 + 12)) | SPP's listener pass serves all sounds; ours is per-sound |

### Neutral / Trade-offs

| Aspect | SPP | Us | Notes |
|--------|-----|----|----|
| **Smoothing** | Target-based, tick-rate smoothing | Same pattern (SmoothAll @ 25ms) | Equivalent |
| **Permeation formula** | `pow(0.4, blocks)` | Same (`PermeationBase=0.4`) | Equivalent |
| **Through-wall muffle** | Dedicated second source | Blended into single filter | Different approach, similar perceptual result |

### The Gap That Matters Most

**Path reliability.** Everything else is manageable. Our 12-probe system is the bottleneck — it causes
the flicker that forced the hold workaround, prevented dual source from working, and limits opening
detection. Stage 3 (listener propagation) closes this gap entirely.

---

## Current System: Logic Flow

```
PER SOUND, EVERY RAYCAST CYCLE (~100-500ms):

  1. DIRECT OCCLUSION (existing)
     DDA ray: playerPos → soundPos → count blocks
     Result: directOcclusion → directFilter (fallback LPF)

  2. SOURCE REVERB (existing, per sound)
     64 fibonacci rays from soundPos → bounce → room characterization
     Result: ReverbResult (EFX slot send gains)

  3. PROBE FOR OPENINGS (existing, per sound)
     12 rays from playerPos → toward soundPos, 15° jitter cone
     At each hit block: check 6 neighbors for air → trace to sound
     Result: list of PathEntry (direction, distance, weight, occlusion)

  4. PATH EVALUATION (SoundPathResolver.Evaluate)
     Split paths into openPaths (occ < threshold) / permeatedPaths
     Position = weighted avg of OPEN path directions only
     BlendedOcclusion = weighted avg of ALL paths (open + permeated)
     Return SoundPathResult or null (no open paths)

  5. APPLY (AudioPhysicsSystem)
     if pathResult exists:
       Reposition source → ApparentPosition (from open paths)
       finalFilter = OcclusionToFilter(smoothed BlendedOcclusion)
     else if high occlusion + had path before:
       HOLD last pathFilter (probe missed, opening still there)
     else:
       finalFilter = directFilter (LOS or no paths)

  6. RENDER (AudioRenderer.ApplySoundPath)
     Set target filter + target position on FilterEntry
     SmoothAll() at 25ms: exponential smoothing → OpenAL calls
     ONE source, ONE lowpass filter per sound
```

---

## Actual Code State (Verified 2026-02-08)

### Core Files

| File | Lines | Purpose |
|------|-------|---------|
| `AudioRenderer.cs` | ~1298 | Per-sound OpenAL filter management, position smoothing |
| `AudioPhysicsSystem.cs` | ~365 | Main acoustics loop: occlusion, paths, reverb dispatch |
| `SoundPathResolver.cs` | ~293 | Weighted path evaluation, open/permeated split |
| `EfxHelper.cs` | ~1480 | OpenAL EFX reflection (includes AL source management — unused but kept) |
| `SoundPhysicsConfig.cs` | ~267 | Config properties |

### SoundPathResult Struct
```csharp
public readonly struct SoundPathResult {
    Vec3d ApparentPosition;      // Weighted avg of OPEN paths — drives repositioning
    double AverageOcclusion;     // Weighted avg of ALL paths — legacy, same as BlendedOcclusion
    double AverageDistance;       // For debugging
    int PathCount;               // Open path count
    double RepositionOffset;     // How far position moved
    int TotalPathCount;          // Open + permeated
    double PermeatedOcclusion;   // Through-wall avg occ — computed but unused by main loop
    double PermeatedWeight;      // Through-wall total weight — computed but unused
    int PermeatedPathCount;      // Through-wall count — computed but unused
    double OpenAverageOcclusion; // Open-only avg occ — computed but unused (BlendedOcclusion replaced it)
    double BlendedOcclusion;     // ← THE KEY FIELD: weighted avg of ALL paths, drives the LPF
}
```

### FilterEntry (AudioRenderer)
- `SourceId`, `FilterId` — one OpenAL source, one lowpass filter
- `TargetFilter`, `CurrentFilter` — smoothing targets
- `TargetRepositionedPos`, `CurrentRepositionedPos` — position smoothing
- `OriginalSoundPos` — for reset/debug
- No permeated fields (removed in refactor)

### What Was Removed (commit f0f9468)
- ~350 lines from AudioRenderer: `DeletePermeatedSource()`, `CreatePermeatedSource()`,
  `SyncPermeatedSourceState()`, permeated smoothing block, resync timer, AL state constants
- 8 FilterEntry fields (PermeatedSourceId, PermeatedFilterId, etc.)
- 6 constants (MAX_PERMEATED_SOURCES, MAX_PERMEATED_GAIN, etc.)
- `EnablePermeatedSound` config property

### What's Kept (Intentionally)
- `EfxHelper.cs` AL source management (~350 lines): Isolated reflection code for creating/destroying
  OpenAL sources. Useful if we revisit dual source after Stage 3.
- `SoundPathResult` permeated fields: Still computed by `Evaluate()`, useful for debugging and
  potentially for dual source after Stage 3.

---

## 🐛 Probe Flickering Bug (Fixed — Stage 1)

### Problem
`ProbeForOpenings()` fires 12 rays in a 15° cone with position-hash jitter. Tiny player movement
→ different hash → different jitter → different wall blocks → intermittent path failures.
Filter oscillated 0.37↔0.75 (~2× perceived volume swing) while walking behind a wall.

### Fix: Occlusion-Based Path Hold
```csharp
const float PATH_HOLD_MIN_OCCLUSION = 0.3f;

if (pathResult.HasValue)              → use path filter, cache it
else if (occlusion > 0.3 && active)   → HOLD last filter (opening still exists, probe missed)
else                                  → revert to direct (player has LOS)
```

### Why This Is A Workaround (Not A Fix)
SPP never has this problem because 640+ samples always populate results. Our hold heuristic works
but can't handle: opening closing (block placed), player moving to different opening, multiple
openings with different path characteristics. Stage 3 is the real fix.

---

## 🏗️ Architecture: Hybrid Source + Listener Rays

### Why Hybrid?

| Aspect | Source-centric rays | Listener-centric rays | Hybrid (ours) |
|--------|--------------------|-----------------------|---------------|
| Reverb accuracy | ✅ Correct per-source room | ❌ Listener room only | ✅ Source rays for reverb |
| Path reliability | ❌ 12 probes, flickery | ✅ 256+ sample points | ✅ Listener rays for paths |
| Cost (N sounds) | ❌ O(N × rays) | ✅ O(rays + N × bounces) | ✅ One listener pass for all |
| Industry standard | Sound Physics Remastered | Steam Audio, SPP, Project Acoustics | Best of both |

### How It Will Work (Post Stage 3)

```
EVERY RAYCAST CYCLE:
  
  1. SOURCE RAYS (existing, per sound):
     64 fibonacci rays from soundPos → bounce → reverb characterization
     Result: ReverbResult (send gains for EFX slots)
     
  2. LISTENER RAYS (new, once for ALL sounds):
     64 fibonacci rays from playerPos → bounce up to 4x
     At each bounce point, for each active occluded sound:
       - Fire "red ray" toward sound → count blocks between
       - Weight = permeation^blocks / distance²
       - Add to path resolver (direction, distance, weight, occlusion)
     Result: SoundPathResult per sound (reliable, always populated)
```

### Why Not Listener-Only?

A rock thrown inside a cave should have cave reverb — even when the listener is outside. Our hybrid
approach is unique among spatial audio systems. SPP/Steam Audio use listener-only and sacrifice
per-source reverb. We get both.

---

## 📋 Implementation Plan

### Stage 1: Probe Flickering Fix ✅ DONE
- Occlusion-based path hold
- Single `finalFilter` → single `SetOcclusion` call

### Stage 2: Dual Source ❌ ATTEMPTED → REVERTED
- Built full dual OpenAL source system (~350 lines)
- Failed due to sparse probe data: energy bugs, volume oscillation, magic numbers
- Replaced with single blended source using `BlendedOcclusion` (commit f0f9468)
- EfxHelper AL source management code retained for future use

### Stage 3: Listener-Centric Propagation Rays — **DONE**
**Goal**: Replace 12 sparse probes with SPP-style listener bounce rays. Eliminates probe flicker,
path hold workaround, and makes path-finding reliable for ALL sounds simultaneously.

**Files changed**: `PropagationCalculator.cs` (NEW), `AudioPhysicsSystem.cs`, `ReverbCalculator.cs`

**3a. New Listener Propagation Pass**:
- [x] Fire 64 fibonacci rays from **player position** per cycle
- [x] Bounce up to 4 times (reuses ReverbCalculator.RaycastToSurface + Reflect)
- [x] At each bounce point: for each active occluded sound, fire "red ray" toward sound
  - Count blocks between bounce point and sound (DDA traversal)
  - Weight = `pow(PermeationBase, blockCount) / (distance² + 0.01)`
  - Add to per-sound path resolver with direction from player→bounce
- [x] Returns: `SoundPathResult` per sound, from all bounce points
- [x] Run ONCE per update cycle (propagation.Update()), results distributed to all sounds

**3b. Refactor ReverbCalculator**:
- [x] Split into: `Calculate()` (reverb-only, source rays) + `PropagationCalculator.GetPathResult()` (listener rays)
- [x] Remove `CalculateWithPaths()` and `ProbeForOpenings()` entirely (~430 lines removed)
- [x] Remove occlusion-based hold workaround from AudioPhysicsSystem (PATH_HOLD_MIN_OCCLUSION, HasActivePath, LastPathFilter)

**3c. Performance Budget**:
- 64 listener rays × 4 bounces = 256 bounce points
- Per bounce: N_occluded × 1 DDA traversal
- 10 occluded sounds: 256 × 10 = 2560 DDA traversals
- Runs once, not per-sound. Net reduction with >3 occluded sounds.

### Stage 4: Revisit Dual Source (Optional, Post Stage 3)
With reliable 256+ sample data, dual source becomes viable again. Decision criteria:
- [ ] Does spatial separation (through-wall at original pos) add noticeable value?
- [ ] Is the ~350 lines of source lifecycle complexity worth it?
- [ ] Test: A/B compare single blended vs dual with reliable data

**If yes**: Re-add using retained EfxHelper source management + SoundPathResult permeated fields.
**If no**: Clean up unused permeated fields from SoundPathResult, remove EfxHelper source management.

---

## SPP Reference: Key Patterns

### Red Ray System
```java
// RaycastingHelper.java: at start pos + every bounce point
for (SoundData sound : entities) {
    double blockCount = countBlocksBetween(world, bouncePos, sound.position, player);
    double weight = permeation^blockCount / distance²;
    redRaysToTarget.get(sound).add(new RayHitData(..., weight, permeation, bounce));
}
```
640+ samples per sound. Always populated. This is why SPP's dual source works.

### SPP's Dual Output
- `rayHitsByEntity` → bounce paths through air → repositioned sound
- `redRaysToTarget` → permeation paths through walls → muffled through-wall sound
- Single blended source captures both in one `BlendedOcclusion` weighted average

### Target-Based Smoothing (We Match This)
```java
targetPosition = calculated;  targetVolume = calculated;  targetMuffle = calculated;
// tick(): ALWAYS smooth toward targets
```
Our `SmoothAll()` at 25ms with `SMOOTH_FACTOR_DOWN=0.259`, `SMOOTH_FACTOR_UP=0.172`. ✅

---

## Testing Scenarios

| # | Scenario | Expected | Stage |
|---|----------|----------|-------|
| 1 | Beehive behind 1-block wall, walk circle | Stable volume, no jumps | Stage 1 ✅ |
| 2 | Beehive behind wall, near edge | Repositioned toward opening, blended muffle | Current ✅ |
| 3 | Beehive behind 3-block wall | Heavy muffle (BlendedOcclusion dominated by permeated) | Current ✅ |
| 4 | Direct LOS to sound | No repositioning, no overhead | Current ✅ |
| 5 | Walk toward opening | Muffle decreases as open paths gain weight | Current ✅ |
| 6 | 10+ occluded sounds | Listener propagation completes in <5ms | Stage 3 |
| 7 | Rock thrown into cave from outside | Cave reverb (source rays), path from cave mouth (listener rays) | Stage 3 |
| 8 | Opening blocked (block placed) | Immediate path loss, fallback to direct occlusion | Stage 3 |

---

## Commit History (Phase 4B)

| Commit | Description |
|--------|-------------|
| `0cf45ae` | Stage 1: Occlusion-based path hold fix |
| `fd2113c` | Stage 2: Dual OpenAL source (permeated) — initial implementation |
| `260d9a3` | Fix: energy/filter bugs in dual source |
| `37666d0` | Fix: cap permeated filter by direct occlusion |
| `86ee4c0` | WIP: backup before single-source refactor |
| `f0f9468` | Refactor: replace dual-source with single blended source (~350 lines removed) |

---

## Key Reference Files

| File | What to Read | Why |
|------|-------------|-----|
| `src/Core/SoundPathResolver.cs` | `SoundPathResult`, `Evaluate()` | BlendedOcclusion computation, open/permeated split |
| `src/Core/AudioRenderer.cs:18-50` | `FilterEntry`, smoothing constants | Single-source filter management |
| `src/Core/AudioRenderer.cs:676+` | `ApplySoundPath()` | Where path result drives position + filter |
| `src/Core/AudioRenderer.cs` | `SmoothAll()` | 25ms tick smoothing |
| `src/Core/AudioPhysicsSystem.cs:230+` | Path application + hold logic | Current flow: BlendedOcclusion → filter |
| `src/Core/ReverbCalculator.cs:232` | `CalculateWithPaths()` | Will be split in Stage 3 |
| `src/Core/ReverbCalculator.cs:498` | `ProbeForOpenings()` | Will be REMOVED in Stage 3 |
| `src/Core/EfxHelper.cs:380-800` | AL source management | Retained for potential Stage 4 |
| **SPP Reference** | |
| `references/.../RaycastingHelper.java:440-530` | `castBouncingRay()` + `castRedRay()` | Model for Stage 3 |
| `references/.../wrappers/RedTickableInstance.java` | `updatePos()`, `updateVolume()` | Target smoothing (we match) |
