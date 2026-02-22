# Sound Repositioning System - Issue Analysis

> **Date:** 2026-02-21  
> **Symptom:** Sounds around thick cave corners feel too muffled or abruptly muffled relative to clear LOS  
> **Scope:** VSSSP sound repositioning (Phase 4B), based on SPR/SPP reference implementations  
> **Status:** Analysis complete, fixes pending

---

## Reference Implementations Studied

| Mod | Key File | Approach |
|-----|----------|----------|
| **SPR** (Sound Physics Remastered) | `ReflectedAudio.java`, `SoundPhysics.java` | Shared-airspace vectors weighted by `1/dist^2`, single blended position, `sqrt(avgAirspace) * 0.2` floor |
| **SPP** (Sound Physics Perfected) | `RaycastingHelper.java` | Permeation rays ("red rays") with `permeationAbsorption^blockCount` weighting, separate permeated sound instance |

---

## Issue 1: Occlusion Floor Proportional to Wall Thickness

**VALIDATED - Real issue, diverges from SPR**

### What VSSSP Does

```csharp
// AudioPhysicsSystem.cs line 416
float legacyFloor = occlusion * 0.4f;
```

The floor on blended occlusion scales linearly with `directOcclusion`. For thick walls this dominates:

| Wall thickness (blocks) | directOcclusion | legacyFloor | Floor filter (`e^(-floor * 1.0)`) |
|------------------------|-----------------|-------------|-----------------------------------|
| 1 | 1.0 | 0.40 | 0.67 |
| 3 | 3.0 | 1.20 | 0.30 |
| 5 | 5.0 | 2.00 | 0.14 |
| 8 | 8.0 | 3.20 | 0.04 |

Even with a perfectly clear L-shaped corridor, 8-block walls cap recovery at filter 0.04 (nearly silent).

### What SPR Does

```java
// SoundPhysics.java line 424
float averageSharedAirspace = (sharedAirspaceWeight0 + ...) * 0.25F;
directCutoff = Math.max((float) Math.pow(averageSharedAirspace, 0.5D) * 0.2F, directCutoff);
```

SPR's floor is `sqrt(averageSharedAirspace) * 0.2` where `sharedAirspace` is the normalized ratio of bounce rays that have clear LOS to the player. This is **independent of wall thickness** -- it only depends on how many rays found paths. A 1-block wall and 8-block wall produce the same floor if the ray coverage is equal.

### Mathematical Comparison

SPR floor formula: `floor = sqrt(sharedAirspaceRatio) * 0.2`

| Shared airspace ratio | SPR floor (filter) | VSSSP floor (5-block wall) |
|-----------------------|--------------------|---------------------------|
| 0.1 | 0.063 | 0.14 |
| 0.3 | 0.110 | 0.14 |
| 0.5 | 0.141 | 0.14 |
| 0.8 | 0.179 | 0.14 |
| 1.0 | 0.200 | 0.14 |

For thin walls, VSSSP is actually better (lower floor = less muffled). But for thick walls (the cave scenario), VSSSP's floor is catastrophically low regardless of path quality.

### Verdict

**VALID.** The floor should be based on path quality (like SPR), not wall thickness. Our `clarityFloor` attempts this but uses `openPaths/totalPaths` ratio which is diluted by bounce rays (see Issue 5).

**Severity: HIGH** -- Primary cause of thick-cave over-muffling.

---

## Issue 2: 15deg Probe Cone Too Narrow for Corridors

**PARTIALLY VALID - Real limitation, but geometry dependent**

### What VSSSP Does

```csharp
// AcousticRaytracer.cs line 672
double jitterAngle = 0.26; // ~15deg in radians
```

12 probe rays fire from player toward sound with max 15deg angular offset. At distance `d` from player to wall, the search area has diameter `2 * d * tan(15deg) = 0.54 * d`.

| Distance to wall | Search diameter | Can find opening if offset by: |
|-------------------|-----------------|-------------------------------|
| 5m | 2.7m | +/- 1.35m |
| 10m | 5.4m | +/- 2.7m |
| 20m | 10.7m | +/- 5.35m |

For right-angle cave corridors, the corridor entrance is typically perpendicular to player->sound direction:

```
Player . . . [WALL WALL WALL WALL WALL]
                                        .
                                        . Sound
```

The corridor entrance is at right angles (90deg). Probes aimed at the wall with 15deg jitter will hit the thick wall face. The **neighbor check** (Issue 3) then searches for adjacent air blocks, but in a thick wall those neighbors are also solid.

### What SPR Does

SPR does NOT use probe rays at all. Instead, it relies purely on fibonacci sphere bounce rays FROM the sound source. Each bounce point checks `getSharedAirspace()` (clear LOS to player). If a bounce ray happens to bounce around a corner and land in the player's airspace, it contributes.

SPR fires 32 rays with 4 bounces = 128 potential airspace checks. The probability of a bounce ray finding a 1-block opening is low, but multi-block corridors get found reliably through reflections.

### What SPP Does

SPP fires "red rays" from EACH bounce point directly toward the sound source, counting blocks in the way (`countBlocksBetween`). The weight is `permeationAbsorption^blockCount / dist^2`. This means SPP doesn't need to find openings -- it measures wall thickness from every bounce point.

### Mathematical Analysis

For a right-angle L-corridor with 5-block thick walls:

- **VSSSP probes:** 15deg cone hits wall face. Neighbors are all solid (thick wall). Opening is ~90deg off axis. **Fails to find corridor.**
- **SPR bounces:** Fibonacci bounce rays from sound side may bounce around corner. With 32 rays and 4 bounces, probability of at least one ray making it around an L-corner is moderate (~40-60% depending on corridor width).
- **SPP red rays:** Fires from each bounce point toward sound. Points in player's airspace (near corridor entrance on player side) count blocks to sound through the wall and get appropriate weights.

### Verdict

**PARTIALLY VALID.** The narrow cone is a real limitation for thick walls with perpendicular corridors. However, simply widening the cone won't fix the fundamental problem -- the neighbor-check approach can't find openings in thick walls regardless of probe angle. A 45deg cone would help for acute-angle corridors but not right-angle ones.

Better fix: Fire probes in a wider pattern (hemisphere, not cone) OR use the bounce-ray approach more like SPR (the fibonacci rays already do this, but their contributions are diluted by Issue 5).

**Severity: MEDIUM** -- Matters for thick walls with corridors, less relevant for thin walls.

---

## Issue 3: Single-Neighbor Opening Search in Thick Walls

**VALID but consequence of Issue 2**

### What VSSSP Does

```csharp
// AcousticRaytracer.cs line 713
BlockPos wallPos = hit.Value.blockPos;
for (int n = 0; n < neighborOffsets.Length; n++)
{
    int nx = wallPos.X + neighborOffsets[n][0];
    // Check if neighbor is air...
}
```

When a probe hits a wall block, it checks the 6 face-adjacent blocks for air. For a 1-2 block wall, this works -- the air on the other side IS adjacent. For a 5+ block wall, all 6 neighbors are solid.

### What SPR Does

SPR doesn't do neighbor searches. It relies on bounce rays finding shared airspace through reflection paths.

### What SPP Does

SPP fires rays from bounce points toward the sound and counts blocks between them (`countBlocksBetween`). It doesn't search for openings at all -- it measures wall permeation directly.

### Mathematical Analysis

For an N-block thick wall, the probability that a face-adjacent block of a wall-face block is air:

| Wall thickness | P(adjacent air) | Note |
|---------------|-----------------|------|
| 1 block | 100% | Air is right next door |
| 2 blocks | 50% | Only outer face has adjacent air |
| 3+ blocks | <<50% | Interior blocks have all-solid neighbors |

The neighbor search is designed for **thin walls** (1-2 blocks). For cave walls (5-10 blocks), it's ineffective.

### Verdict

**VALID but dependent on Issue 2.** If probes land on thick wall faces, neighbor search fails. The real fix should address Issue 2 first (wider probe strategy), making this less relevant.

**Severity: LOW** -- Consequence of Issue 2, not independent problem.

---

## Issue 4: Smoothing State Reset Causes Abrupt Transition

**VALIDATED - Logic error**

### What VSSSP Does

```csharp
// AudioPhysicsSystem.cs line 392-393
if (skipRepositioning)
{
    AudioRenderer.ResetSoundPosition(sound, soundPos);
    cache.HasSmoothedOcc = false;  // RESETS smoothing state entirely
```

When direct occlusion drops below 1.0 (player enters LOS), `HasSmoothedOcc` is cleared. The next frame that occlusion exceeds 1.0 again, the smoothing seeds RAW:

```csharp
// AudioPhysicsSystem.cs line 445-449
if (cache.HasSmoothedOcc)
    cache.SmoothedBlendedOcc += (blendedOcc - cache.SmoothedBlendedOcc) * 0.35f;
else
{
    cache.SmoothedBlendedOcc = blendedOcc;  // RAW value, no transition
    cache.HasSmoothedOcc = true;
}
```

### Transition Sequence

Frame by frame for a player rapidly crossing a corner edge:

| Frame | directOcc | skipRepos | HasSmoothedOcc | SmoothedBlendedOcc | Filter |
|-------|-----------|-----------|----------------|--------------------| ------|
| N | 0.8 | true | **reset** | -- | 0.45 (direct) |
| N+1 | 1.2 | false | false | **RAW = 4.5** | 0.011 |
| N+2 | 1.2 | false | true | 4.5 -> EMA | 0.011 -> smoothing... |

The jump from 0.45 to 0.011 in one frame is jarring -- even though filter smoothing in `SmoothAll()` softens it, the TARGET itself jumps by `~0.44` which takes `0.4s / SMOOTH_FACTOR_DOWN` to converge.

### What SPR Does

SPR evaluates per-sound on play start (oneshot) or on update tick (tickable). There's no frame-to-frame smoothing state to reset because it computes everything fresh each cycle. The `directCutoff` is always the result of `exp(-occ * absorption)` with the `sqrt(airspace) * 0.2` floor applied.

SPR doesn't have this problem because it doesn't maintain persistent smoothing state. But SPR also doesn't handle moving sounds as smoothly.

### Verdict

**VALID.** This is a logic error, not a design choice. The fix is simple: instead of resetting `HasSmoothedOcc`, seed it from the current direct filter value:

```csharp
if (skipRepositioning)
{
    AudioRenderer.ResetSoundPosition(sound, soundPos);
    // Seed smoothed occ from direct path so transition is smooth
    cache.SmoothedBlendedOcc = occlusion;
    cache.HasSmoothedOcc = true;  // DON'T reset -- keep smoothing active
}
```

**Severity: HIGH** -- Direct cause of the "abrupt muffling" perception.

---

## Issue 5: Bounce Rays Dilute Opening Paths in Weighted Average

**VALIDATED - Structural mathematical issue**

### What VSSSP Does

The `SoundPathResolver` accumulates ALL paths with weighted average:

```csharp
// SoundPathResolver.cs Evaluate()
foreach (var path in allPaths)
{
    weightedOcclusion += path.PathOcclusion * path.Weight;
    totalWeight += path.Weight;
}
double avgOcclusion = weightedOcclusion / totalWeight;
```

With 32 fibonacci rays x 4 bounces = up to 128 bounce paths, plus ~3 probe-found openings:

| Path type | Count | Typical weight | Typical occlusion | Contribution |
|-----------|-------|---------------|-------------------|-------------|
| Direct path | 1 | very low (thick wall) | 5.0+ | pulls avg UP |
| Bounce paths (through wall) | ~80 | very low | 3.0-5.0 | pulls avg UP |
| Bounce paths (in player's room) | ~40 | medium | 0.5-2.0 | moderate |
| Probe openings | 2-3 | moderate | 1.0-3.0 | pulls avg DOWN |

Even though low-weight paths contribute less individually, there are **80+ of them** versus 2-3 opening paths. The weighted average is dominated by volume.

### Numerical Example (5-block cave wall with corridor)

Assumptions: `permeationBase = 0.4`, `blockAbsorption = 1.0`

**Through-wall bounce paths (80 of them):**
- Avg occlusion to player: 4.0 blocks
- Permeation: `0.4^4.0 = 0.0256`
- Avg distance: 15m
- Weight: `0.0256 / (15^2) = 0.000114` per path
- Total weight: `80 * 0.000114 = 0.00909`
- Weighted occlusion: `80 * 0.000114 * 4.0 = 0.0364`

**Clear corridor opening (3 paths):**
- Avg occlusion: 0.5 (corridor + diffraction)
- Permeation: `0.4^0.5 = 0.632`
- Avg distance: 12m
- Weight: `0.632 / (12^2) = 0.00439` per path
- Total weight: `3 * 0.00439 = 0.01317`
- Weighted occlusion: `3 * 0.00439 * 0.5 = 0.00659`

**Result:**
- Total weight: `0.00909 + 0.01317 = 0.02226`
- Weighted avg occlusion: `(0.0364 + 0.00659) / 0.02226 = 1.93`
- Filter: `exp(-1.93) = 0.145`

But the **best actual path** has occlusion 0.5 -> filter `exp(-0.5) = 0.607`. The sound should be at ~0.6 filter but ends up at ~0.145 because through-wall paths drag the average up.

### What SPR Does

SPR computes `directCutoff` from the direct path only, then RAISES it via the floor:

```java
directCutoff = Math.max(sqrt(averageSharedAirspace) * 0.2, directCutoff);
```

The floor is based on shared airspace COUNT (how many rays found clear LOS), not weighted average occlusion. This is a fundamentally different approach -- SPR doesn't average occlusion across paths at all.

### What SPP Does

SPP uses separate sound instances for permeated vs. open paths. The permeated instance gets `permeationAbsorption^blockCount` attenuation. The open instance plays at the redirected position with confidence-based volume. They **don't average** -- they're separate audio sources.

### Verdict

**VALID.** The weighted average is mathematically sound but produces perceptually wrong results for the "clear corridor around thick wall" scenario. Both SPR and SPP avoid this by NOT averaging occlusion across path types.

Possible fixes:
1. **Min-of-best-N:** Use the occlusion of the best few paths instead of weighted average
2. **Weighted percentile:** Use 25th percentile by weight instead of mean
3. **SPR-style:** Drop the blended occlusion entirely and use `max(directCutoff, sqrt(clarity) * K)` floor
4. **SPP-style:** Separate open vs permeated into different processing (most work)

**Severity: MEDIUM-HIGH** -- Compounds with Issue 1 to make thick walls worse.

---

## Issue 6: Hard 1.0 Occlusion Threshold

**PARTIALLY VALID - Matches SPR's approach**

### What VSSSP Does

```csharp
// AudioPhysicsSystem.cs line 383
bool skipRepositioning = occlusion < 1.0f;
```

Binary switch: below 1.0 = pure direct path, above 1.0 = full path resolution system.

### What SPR Does

```java
// ReflectedAudio.java line 35
public boolean shouldEvaluateDirection() {
    return CONFIG.soundDirectionEvaluation.get() 
        && (occlusion > 0D || !CONFIG.redirectNonOccludedSounds.get())
        && !AudioChannel.isVoicechatSound(sound);
}
```

SPR's threshold is `occlusion > 0` (ANY occlusion triggers direction evaluation). With `redirectNonOccludedSounds = true` (default), sounds with occlusion == 0 are skipped. This is equivalent to a threshold of ~0.0, not 1.0.

However, in practice, SPR's occlusion uses stepping raycasts through blocks and returns integer-ish values (0, 1, 2...). The transition from 0 to 1 is discrete because MC blocks are full cubes. In VS, partial blocks create fractional values (0.1, 0.3, 0.7...), so the 1.0 threshold avoids triggering repositioning for plants/leaves/partial blocks.

### Mathematical Impact

The concern was filter discontinuity at the threshold. Let's check:

- At `directOcc = 0.95`: direct filter = `exp(-0.95) = 0.387`, no repositioning
- At `directOcc = 1.05`: repositioning kicks in, blended filter = floored value

The blended filter can be EITHER higher or lower than 0.387 depending on path results:
- If good corridor found: blended might be 0.25 -> MORE muffled than 0.387 (wrong direction!)
- If no good paths: blended ≈ directOcc * 0.4 floor = 0.42 -> filter 0.66 (LESS muffled, correct direction but discontinuous)

The floor (Issue 1) at `1.05 * 0.4 = 0.42` produces filter `0.66`, which is BETTER than the direct-path filter of `0.35`. This creates an **inverse discontinuity** -- sound gets LOUDER when you step behind a thin wall. The `max(legacyFloor, clarityFloor)` prevents the sound from going BELOW the floor, but the floor itself is already higher than the direct filter.

### Verdict

**PARTIALLY VALID.** The 1.0 threshold is a reasonable adaptation of SPR's binary approach to VS's fractional occlusion. The actual problem is the interaction with Issue 1 (floor formula) near the threshold, not the threshold itself.

A soft blend zone (0.8 to 1.5) would help but adds complexity. The real fix is making Issues 1 and 4 right first -- then the threshold transition becomes naturally smooth because both direct and repositioned filters converge to similar values at occlusion ~1.0.

**Severity: LOW** -- Symptom of Issues 1/4/5 rather than independent problem.

---

## Summary: Root Cause Ranking

| # | Issue | SPR Validated | Fix Complexity | Impact |
|---|-------|--------------|----------------|--------|
| **1** | Floor proportional to wall thickness | YES - SPR uses airspace ratio, not wall thickness | Low | **Highest** |
| **4** | Smoothing state reset on LOS transition | N/A (SPR is stateless) but clearly a logic bug | Low | **High** |
| **5** | Bounce rays dilute openings in weighted avg | YES - SPR doesn't average occlusion across paths | Medium | **Medium-High** |
| **2** | 15deg probe cone too narrow | Partial - SPR doesn't probe but relies on bounces | Medium | **Medium** |
| **3** | Single-neighbor can't find thick-wall openings | Partial - consequence of #2 | Low | **Low** |
| **6** | Hard 1.0 threshold | Partial - matches SPR's approach but VS-specific edge case | Medium | **Low** |

### Recommended Fix Order

1. **Issue 4** (1 line fix) -- Eliminate the abrupt spike immediately
2. **Issue 1** (change floor formula) -- Use airspace-based floor like SPR instead of wall-proportional
3. **Issue 5** (change averaging) -- Use best-path occlusion instead of weighted mean
4. **Issues 2+3** can wait -- They matter less once the floor/averaging is fixed

---

## Appendix: Key Formulas

### SPR Direct Cutoff Floor
```
absorptionCoeff = blockAbsorption * 3.0     // SPR multiplies by 3!
directCutoff = exp(-occlusionAccumulation * absorptionCoeff)
averageSharedAirspace = avg(sharedAirspaceWeight0..3)
directCutoff = max(sqrt(averageSharedAirspace) * 0.2, directCutoff)
```

Note: SPR uses `blockAbsorption * 3.0` as the coefficient, while VSSSP uses `blockAbsorption * 1.0`. This means SPR's un-floored filter is much more aggressive:
- SPR: 1 block -> `exp(-1.0 * 3.0) = 0.050`
- VSSSP: 1 block -> `exp(-1.0 * 1.0) = 0.368`

SPR relies HEAVILY on the floor to recover sound through openings. Without the floor, SPR would be near-silent through even 1 block.

### SPP Permeation
```
permeationAbsorption = 0.4  (default)
weight = permeation^blockCount / dist^2
where blockCount = actual blocks between bounce point and sound source
```

### VSSSP Current System
```
directFilter = exp(-directOcclusion * blockAbsorption)
blendedOcclusion = weightedAvg(allPaths.occlusion, allPaths.weight)
legacyFloor = directOcclusion * 0.4
clarityFloor = -ln(sqrt(pathClarity) * 0.35) / blockAbsorption
occlusionFloor = min(legacyFloor, clarityFloor)
finalOcclusion = max(blendedOcclusion, occlusionFloor)
pathFilter = exp(-smoothed(finalOcclusion) * blockAbsorption)
```
