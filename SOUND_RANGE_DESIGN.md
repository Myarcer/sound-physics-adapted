# Sound Range Extension — Architecture & Feasibility

## Current State (v0.2.4)

Already implemented via the **Distance Model Overrides** system in `LoadSoundPatch.cs → ApplyDistanceModel()`:

| Config key | Default | Effect |
|---|---|---|
| `EnableDistanceModelOverrides` | `true` | Master toggle |
| `SoundRangeMultiplier` | `1.4` | Scales `AL_MAX_DISTANCE` per source (+40% range) |
| `DistanceRolloffFactor` | `1.0` | Scales `AL_ROLLOFF_FACTOR` (curve steepness) |
| `AirAbsorptionFactor` | `1.0` | EFX `AL_AIR_ABSORPTION_FACTOR` (HF damping over distance) |
| `DistanceModelExcludeMusic` | `true` | Exempts Music sound types |

Applied once per sound start in `SoundStartPostfix`, idempotent via `_distanceModelApplied` dict keyed on source ID.

---

## What VS Vanilla Does

Vintage Story sets per-source OpenAL parameters from `SoundParams` when a sound starts:

```
AL_MAX_DISTANCE       = SoundParams.Range    (default 32 blocks, varies per sound)
AL_REFERENCE_DISTANCE = 3–8 blocks           (varies, often 3 for block sounds)
AL_ROLLOFF_FACTOR     = 1.0                  (inverse distance model)
```

OpenAL **inverse distance clamped** model:

```
gain = reference_distance / (reference_distance + rolloff * (clamp(dist, ref, max) - ref))
```

At distance = max_distance, gain ≈ 0 (hard cutoff). Sounds are inaudible beyond `Range` blocks regardless of actual volume.

---

## Approach 1: Global Multiplier on MaxDistance (current)

**How it works:** On every sound start, read current `AL_MAX_DISTANCE`, multiply by config value, write back.

**Pros:**
- Dead simple. Already works.
- Respects each sound's original relative range (a quiet bird stays quieter than a creaking tree at equal distance).
- Idempotent — won't compound on re-attach.

**Cons:**
- Single flat multiplier applies to ALL sounds identically. No per-material or per-category tuning.
- Doesn't affect `AL_REFERENCE_DISTANCE` — the inner "plateau" radius stays the same, so very close-range sounds aren't affected by the multiplier at all (they're already at full volume).
- MaxDistance > 4096 clamped to prevent voice starvation. At 1.4× the cap only matters for thunder (which bypasses this anyway) and a few hand-tuned 300-block sounds.

**Good enough for:** General "sounds feel more alive at a distance" improvement. Currently deployed.

---

## Approach 2: Per-SoundType Multiplier

**Extend config with a per-category override:**

```json
"SoundRangeMultiplierOverrides": {
  "Ambient": 2.0,
  "Sound": 1.4,
  "Weather": 0.0
}
```

In `ApplyDistanceModel`, look up `SoundType` from `sound.Params.SoundType` and apply the matching multiplier instead of the global one.

**Feasibility:** Easy. Already have the `EnumSoundType` check for Weather/Music exclusion. Just extend to a dict lookup.

**Use case:** Ambient block sounds (water, fire) could be tuned to a wider range separately from snappy transient sounds (break, hit, footstep).

---

## Approach 3: Per-AssetLocation Pattern Multiplier

**Extend config with path-pattern overrides:**

```json
"SoundRangePatternOverrides": {
  "game:sounds/block/water*": 2.5,
  "game:sounds/creature/sheep*": 1.8,
  "game:sounds/effect/thunder*": 0.0
}
```

Match `soundName` (from `sound.Params.Location.ToShortString()`) against patterns at play time.

**Feasibility:** Moderate. Pattern matching (glob or prefix) adds ~50ns per start — negligible. Storage is a `List<(string pattern, float multiplier)>` evaluated top-down. Already have `soundName` available in `ApplyDistanceModel`.

**Use case:** Fine-grained tuning without touching material config. Ships with sensible defaults for common categories.

---

## Approach 4: Reference Distance Scaling

Right now `AL_REFERENCE_DISTANCE` is left at vanilla (usually 3 blocks). This is the radius inside which gain = 1.0 exactly.

Increasing it makes sounds "feel louder near the source" — useful for footsteps, chisel taps, ambient volumes.

**Feasibility:** Trivial. Add `ReferenceDistanceMultiplier` config and apply in `ApplyDistanceModel` alongside the MaxDistance multiplier.

**Caution:** Reference distance > max distance is undefined behavior in OpenAL. Must clamp `new_ref < new_max`.

---

## Approach 5: Rolloff Factor Reduction (Gentler Falloff Curve)

Reducing `AL_ROLLOFF_FACTOR` from 1.0 toward ~0.5 gives a shallower attenuation curve. The sound doesn't drop to zero at `MaxDistance` — it just fades more gently throughout.

This is **mathematically independent** of MaxDistance scaling:
- MaxDistance scaling: extends the hard cutoff boundary.
- Rolloff reduction: changes the shape of the curve inside that boundary.

Both together produce sounds that start fading later AND fade more gently — most natural-feeling result.

`DistanceRolloffFactor = 0.6` with `SoundRangeMultiplier = 1.4` is a reasonable starting point to test.

**Already in config, default 1.0 (vanilla).** Lowering it is a user tuning choice.

---

## Interactions with Occlusion

Key reason range extension is now safe in this mod but wasn't before:

> **Occlusion provides natural range limiting through walls.** A footstep 40 blocks away through 3 walls will be filter=0.03 (nearly silent) even if its MaxDistance allows it to technically reach. Without occlusion, extending range would cause wall bleed-through everywhere.

This means we can increase MaxDistance more aggressively than in vanilla without sounds "bleeding" through structures. The DDA system handles the perceptual limiting.

**Caveat:** OpenAL **still mixes** occluded sounds into the voice budget. A sound that is audibly silent (occ = very high → filter ≈ 0) still occupies a voice slot if it is within MaxDistance. With a 2× range multiplier, this could double the number of active voices in dense areas.

**Mitigation already in place:** `InaudibleOcclusionThreshold` in the occlusion system — sounds above this threshold are unregistered from the active set entirely, freeing their voice slot.

---

## Approach 6: Block-Sound-Material Range Tuning (Future)

The material config (`soundphysicsadapted_materials.json`) already holds per-material occlusion and reflectivity values. A natural extension:

```json
"materials": {
  "wood": { "occlusion": 0.4, "reflectivity": 0.3, "rangeMul": 1.2 },
  "stone": { "occlusion": 0.8, "reflectivity": 0.7, "rangeMul": 0.9 }
}
```

The idea: apply a per-material range multiplier based on the *source block's material* rather than the sound type.

**Feasibility:** Medium. Requires knowing the block material at the sound source position. For placed block sounds (break/hit), the position is known. For entity sounds (footstep), would need to query the block under the entity. For ambient sounds, block material is already available since they're injected per-block.

The `LoadSoundPatch` currently knows the sound's `AssetLocation` and `Position` in `SoundStartPostfix`. Block lookup is a single `world.BlockAccessor.GetBlock(pos)` call — cheap. Material resolution already exists in `BlockClassification.GetMaterial()`.

**Risk:** Block lookup in the sound start path (which runs every sound play) adds a small cost. Safe if: cached block accessor is used (already available as `cachedBlockAccessor`).

---

## Recommendation / Priority Order

| Priority | Approach | Effort | Impact |
|---|---|---|---|
| **Done** | Global MaxDistance multiplier (`SoundRangeMultiplier`) | — | Medium |
| **Done** | Rolloff factor multiplier (`DistanceRolloffFactor`) | — | Medium |
| **Low** | Per-SoundType multiplier override | 2h | Medium |
| **Low** | Per-AssetLocation pattern override | 3h | High for power users |
| **Medium** | Lower `DistanceRolloffFactor` default (0.6–0.7) | 10min | Noticeable |
| **Future** | Block-material range multiplier in material config | 1 day | Natural, elegant |

**Immediate suggestion:** Test `DistanceRolloffFactor = 0.7` + `SoundRangeMultiplier = 1.6`. No code changes needed — pure config tuning. Gives a significantly more organic falloff feel without touching the voice budget much.
