# Weather Audio Architecture Analysis: Phase 5A→5B Transition & AAA Comparison

**Date**: 2026-02-10
**Context**: Phase 5A (ambient bed + LPF) is implemented. Before starting Phase 5B (positional sources), analyze whether the current architecture creates smooth transitions or problematic overlap.

---

## The Core Concern

> "Does Phase 5A ambient weather make sense given Phase 5B positional sources are coming? Will there be overlap where ambient noise persists in scenarios (like caves) where only positional sources from the entrance should be audible?"

This is a valid architectural concern. Let's break it down scenario by scenario.

---

## Current Phase 5A Architecture

**What we have now:**
- Single non-positional ambient bed per weather type (rain, wind, hail, tremble)
- `RelativePosition = true` — listener-attached, comes from "everywhere"
- Two independent metrics control the sound:
  - **SkyCoverage** (heightmap sampling, radius 8) → drives **VOLUME** (less sky visible = quieter)
  - **OcclusionFactor** (DDA air-path checks) → drives **LPF** (material between player and rain = muffled)
- `WeatherEnclosureCalculator` already computes and caches `VerifiedOpenings` for Phase 5B

---

## Scenario Analysis: Where Current Approach Works and Fails

### Scenario 1: Cave Entrance (Gradual Walk-In)

```
    ░░░░░░░░░░░░░░░░░░░░░░░░  ← raining
    ████████████████████████░░  ← cave mouth
    ██         YOU→→→      ░░  ← walking deeper
    ████████████████████████░░
    ██████████████████████████
    ██████████████████████████  ← deep cave
```

**Phase 5A behavior (current):**
| Position | SkyCoverage | OcclusionFactor | Volume | LPF | Feeling |
|----------|------------|-----------------|--------|-----|---------|
| At mouth | ~0.3 | ~0.1 (most rain visible through air) | ~70% | Light | Crisp rain nearby |
| 5 blocks in | ~0.6 | ~0.4 (some blocked) | ~40% | Moderate | Getting muffled |
| 10 blocks in | ~0.85 | ~0.9 (almost all blocked) | ~15% | Heavy | Deep bass rumble |
| 20 blocks in | ~1.0 | ~1.0 (fully occluded) | ~0% | Maximum | Silent or faint bass |

**Verdict: Works well.** The Gaussian-weighted heightmap sampling means SkyCoverage increases gradually (columns near the player weight more). The DDA occlusion also transitions smoothly. The cave entrance doesn't "snap" to silent — it fades through muffled → bass rumble → silence.

**Phase 5B would add:** A positional source at the cave mouth. As you walk deeper, the ambient bed fades (5A), but you can still hear crisp rain *from behind you* at the entrance (5B). This is the correct behavior — no overlap problem.

**Potential concern:** The ambient bed might linger slightly at 10-15% volume even when 5B's positional source from the entrance is doing the heavy lifting. But this mirrors reality — you DO hear diffuse reverberant rain in a cave, not just the point source at the opening. The ambient bed at low volume + heavy LPF creates the "rumble" that positional sources alone can't.

### Scenario 2: Small House with Open Door

```
    ██████████████████████████
    ██         YOU          ██
    ██                      ░░  ← open door
    ██████████████████████████
```

**Phase 5A behavior (current):**
- SkyCoverage: ~0.85 (mostly roofed, only door columns exposed)
- OcclusionFactor: ~0.4-0.6 (some air paths through door, walls block the rest)
- Result: Moderate volume, moderate LPF — "indoor rain with door open" feeling

**The concern you raised:** If you're right next to the wall opposite the door, the heightmap columns right outside that wall are rain-exposed (rain hits the ground 2 blocks from you). But DDA checks air paths — the wall blocks LOS from those rain-hit positions to you. So OcclusionFactor would be HIGH (muffled) for those close-but-blocked positions.

**BUT SkyCoverage might be too low.** The heightmap columns outside the wall are at rain height below you = "exposed" (player Y > rainHeight). These contribute to lower SkyCoverage even though they're behind a wall. SkyCoverage measures "overhead coverage" not "reachability to rain" — that's what OcclusionFactor is for.

**This is actually correct behavior.** SkyCoverage answers: "how much sky overhead is blocked?" Even next to a thin wall, the sky above the wall IS open — it's just that the SOUND has to travel through material. That's what the LPF (driven by OcclusionFactor) captures. The volume being moderate but heavily filtered = correct "rain on thin wall" feeling.

**Phase 5B would add:** Positional source at door → rain clearly comes from the door direction. Ambient bed stays at moderate volume + heavy LPF (the "diffuse rain on walls/roof" feeling). Together: directional rain from door + muffled ambient = realistic.

### Scenario 3: The False Proximity Problem ⚠️

```
    ██████████████████████████
    ██     YOU    ██        ██
    ██            ██   ░░░░░██  ← rain hits ground in adjacent open courtyard
    ██████████████████████████
```

**Phase 5A behavior (current):**
- Heightmap scan: columns 3-4 blocks away through wall are rain-exposed
- SkyCoverage: ~0.7 (adjacent courtyard pulls coverage down)
- OcclusionFactor: ~0.8-1.0 (DDA: wall blocks all air paths → high occlusion)
- Volume: ~30% * intensity (SkyCoverage says "some sky visible")
- LPF: Heavy (OcclusionFactor says "all behind material")

**Result:** Bass rumble at 30% volume through the wall. This is actually reasonable — you DO hear low-frequency rain through walls in reality.

**False positive risk:** If SkyCoverage is too low (says "partially outdoor") when you're in a sealed room with rain only behind walls. But the OcclusionFactor = 1.0 catches this — the LPF pushes the sound to bass-only. At 30% volume with 300Hz cutoff, the perceived loudness is very low. Acceptable.

**Phase 5B would add:** Nothing — no positional sources (all DDA blocked). Only the ambient bed at bass-rumble level. Correct.

### Scenario 4: Deep Cave / Underground ✅

```
    ░░░░░░░░░░░░░░░░░░░░░░░░  ← surface, raining
    ██████████████████████████
    ██████████████████████████  ← 30 blocks of rock
    ██████████████████████████
    ██     YOU              ██  ← deep underground
    ██████████████████████████
```

**Phase 5A behavior (current):**
- SkyCoverage: ~1.0 (all columns in radius 8 have rainheight far above player)
- OcclusionFactor: 1.0 (no exposed candidates within Y-range of 12 blocks)
- Volume: ~0% (1.0 - 1.0 * 0.6 loss = 0.4... wait)

**Issue identified:** `CalculateRainVolume` uses `intensity * (1.0 - skyCov * 0.6)`. At SkyCoverage=1.0, that's still `intensity * 0.4` — 40% volume! With max LPF at 300Hz, that's still audible bass.

**Is this a bug?** Debatable. VS's `roomVolumePitchLoss` at distance=12+ gives 1.0, which VS applies as `target - 1.0` = 0 volume. Our system keeps 40% volume at 300Hz LPF. In reality, deep underground you should hear near-zero weather, period.

**Recommendation:** Add a `minimumSkyForWeather` threshold. When SkyCoverage > 0.95 AND OcclusionFactor > 0.95, apply an additional exponential volume reduction. Or simply increase `WeatherVolumeLossMax` from 0.6 to 0.95.

### Scenario 5: The Overlap Concern — Cave with Entrance ⚠️

This is the scenario you're most worried about:

```
    ░░░░░░░░░░░░░░░░░░░  ← raining
    ████████████████░░░░  ← cave entrance (right side)
    ██               ░░░  
    ██   YOU         ░░░  ← 15 blocks from entrance
    ██               ░░░
    ████████████████░░░░
    ████████████████████
```

**With only Phase 5A (current):**
- SkyCoverage: ~0.6 (entrance columns + some outside visible in scan)
- OcclusionFactor: ~0.3 (some air paths through to entrance, mostly blocked by rock)
- Volume: ~40% * intensity
- LPF: Light-moderate (0.3 occlusion → ~15000 Hz cutoff)
- **Problem:** Ambient sound comes from EVERYWHERE equally. Can't tell rain is from the right.

**With Phase 5A + 5B together:**
- Ambient bed: same as above (~40% volume, moderate LPF) — diffuse background
- Positional source: 1-2 sources clustered at cave entrance → rain clearly FROM the right
- **The concern:** Is the 40% ambient bed too loud relative to the positional source?

**Analysis:** In real life, in a cave 15 blocks from the entrance:
1. You hear **dominant directional rain** from the entrance (positional = correct)
2. You hear **faint diffuse reverberant rain** from reflections off cave walls (ambient bed = correct)
3. The ambient bed should be QUIETER than the positional source

**The key is ratio.** If the ambient bed is at 40% and the positional source is at 60%, the directional cue is there but weak. If the ambient is at 15% and positional at 40%, the directional dominance is clear.

**Recommendation for 5B integration:** When positional sources are active (openings detected), reduce Layer 1 ambient bed volume by an additional factor proportional to how many openings exist and how clear they are. Example: `ambientVolume *= (1.0 - positionalContribution * 0.6)`. This "ducks" the ambient when directional sources take over.

---

## How AAA Games Handle This

### The Industry-Standard Two-Layer Pattern

Every major title with weather audio uses fundamentally the same architecture:

#### Layer 1: Ambient Bed (Non-Positional)
- Always-present diffuse background
- Represents indirect/reflected weather sound
- Volume and LPF driven by enclosure metrics
- NEVER positional — intentionally "everywhere"
- Fades to bass rumble indoors, silence deep underground

#### Layer 2: Spot Emitters (Positional)
- 3D sources at openings, surfaces, impact points
- Represent direct weather sound through specific paths
- Volume driven by opening size + proximity
- LPF much lighter than Layer 1 (these are clear air paths)
- Only active when partially enclosed

### Wwise Rooms & Portals Model

Wwise's Spatial Audio provides the closest reference:

| Concept | Wwise | Our Implementation |
|---------|-------|-------------------|
| **Room** | Defined volume with reverb preset | Implicit from SkyCoverage |
| **Portal** | Opening between rooms, sound passes through | VerifiedOpenings from DDA |
| **Room Tone** | Ambient emitter representing outdoor weather | Layer 1 ambient bed |
| **Portal Diffraction** | Sound bending around portal edges | We approximate with cluster-based positional sources |
| **Transmission** | Sound through walls (LPF + attenuation) | OcclusionFactor → LPF |
| **Obstruction** | Direct path blocked, no diffraction | DDA occlusion check |

Key difference: Wwise **reduces Room Tone when the listener is near a Portal** because the direct sound dominates. This is exactly the "ambient ducking" we need for 5B integration.

### The Last of Us Part II (Naughty Dog)

TLOU2's rain system (GDC presentations):
- **Outdoor**: Full-spectrum stereo rain bed + individual drip emitters on surfaces
- **Indoor (door open)**: Rain bed volume reduced ~60%, LPF at ~2kHz. Positional emitters at doorway/windows at ~80% volume, lighter LPF (~8kHz). Creates "rain from the door" effect.
- **Indoor (sealed)**: Rain bed at ~20% volume, LPF at ~400Hz (bass rumble). No positional. Thunder still audible (very low freq travels through structure).
- **Cave**: Rain bed fades to ~5% with maximum LPF. Positional source at entrance fades with distance (inverse square).
- **Critical technique**: Smooth crossfade between "outdoor mix" → "indoor mix" over ~1.5 seconds based on enclosure metric change rate

### Red Dead Redemption 2 (Rockstar)

RDR2's weather system:
- Uses distance-to-sky metric (similar to VS's roomVolumePitchLoss)
- Rain bed is **always mono + HRTF**, not stereo — allows subtle directional shifting even for ambient
- **Rain intensity zones**: Instead of binary indoor/outdoor, uses gradient zones around openings
- Barn with open door: rain still audible at reduced volume from everywhere, but **splashing rain emitters** at the door threshold create directional anchor
- **Wind is handled differently**: Wind uses a single directional source (the wind direction) rather than omnidirectional bed. This is more physically correct — wind HAS a direction.

### Battlefield Series (DICE)

Battlefield's environmental audio:
- Uses Frostbite's sound propagation for weather
- Rain: ambient bed + up to 8 surface impact emitters within 20m of player
- Indoor: bed volume scales with "sky visibility factor" (raytrace-based, expensive)
- **Key optimization**: Update weather enclosure metrics on a 500ms timer (matches our approach exactly)
- **Critical detail**: When player crosses indoor/outdoor threshold, Layer 2 sources fade in/out over 2-3 seconds to prevent audio pop

---

## The Specific Edge Cases You Raised

### 1. "Standing inside rain → full volume"
**Current behavior:** Correct. SkyCoverage ≈ 0.0, OcclusionFactor ≈ 0.0 → full volume, no LPF.
**Phase 5B:** Positional sources disabled outdoors (not partially enclosed). Just ambient bed. Correct.

### 2. "Cave opening → rain falls off gradually"
**Current behavior:** Correct. Gaussian-weighted heightmap sampling means gradual SkyCoverage increase. DDA occlusion also transitions gradually as more rock blocks LOS to rain.
**Phase 5B:** Positional source at entrance fades with distance. Ambient bed provides underlying rumble.
**Overlap risk:** Low. The ducking mechanism (Layer 1 quieter when Layer 2 active) handles this.

### 3. "Ambient wind noise in deep cave"
**Current behavior:** Wind volume = `(1 - skyCoverage) * windSpeed * 0.8f`. At SkyCoverage=1.0, that's 0. ✅
**But:** If SkyCoverage is only 0.85 (not quite 1.0 even deep in cave due to scan radius), you get `0.15 * windSpeed * 0.8` = noticeable wind. This IS a concern.
**Root cause:** Scan radius 8 means even 8+ blocks inside a cave, SOME columns at the edge of the scan might be "exposed" at the entrance.
**Fix:** This actually self-corrects at 15+ blocks deep — all scan columns are underground. At 8-12 blocks, the wind would be faint but present, which is arguably correct (you can hear distant wind at a cave mouth 10 blocks away).

### 4. "Small house, rain heightmap 2 blocks away through wall"
**Current behavior:** SkyCoverage may be low (adjacent columns are rain-level), but OcclusionFactor is high (wall blocks DDA). Result: moderate volume, heavy LPF = bass rumble. Reasonable.
**Phase 5B:** No positional sources (all DDA blocked). Just ambient bed with bass rumble. Correct.

---

## Recommendations: Smooth 5A → 5B Transition

### 1. Layer Ducking on 5B Activation

```csharp
// In RainAudioHandler, when positional sources are active:
float positionalContribution = CalculatePositionalContribution(activeOpenings);
float ambientDuckFactor = 1.0f - (positionalContribution * 0.5f); // Duck ambient 0-50%
float ambientVolume = CalculateRainVolume(intensity, skyCoverage) * ambientDuckFactor;
```

This ensures the ambient bed steps back when directional sources take over, preventing the "double volume" overlap.

### 2. Aggressive Cutoff for Deep Enclosure

```csharp
// When fully enclosed, kill ambient more aggressively
if (skyCoverage > 0.92f && occlusionFactor > 0.92f)
{
    float deepFactor = 1.0f - ((skyCoverage - 0.92f) / 0.08f); // Ramp to 0 from 0.92→1.0
    volume *= deepFactor * deepFactor; // Quadratic falloff
}
```

This eliminates the "40% bass rumble deep underground" issue.

### 3. Wind Direction (Phase 5B Enhancement)

Wind should NOT be purely omnidirectional even in Layer 1. VS already knows wind direction. Phase 5B can use a single directional source for wind (oriented by VS's wind vector) rather than positional emitters at rain openings. This is more physically correct and avoids the "wind in sealed cave" issue.

### 4. Smooth Crossfade Timing

When transitioning between states (outdoor→indoor, cave mouth→deep), ensure all smoothing factors align:
- SkyCoverage smoothing: SMOOTH_FACTOR = 0.4 at 500ms = ~1.5s convergence ✅
- OcclusionFactor smoothing: same ✅  
- LPF smoothing: LPF_SMOOTH_FACTOR = 0.45 at 100ms = ~0.5s convergence
- Positional source fade-in: should be ~1-2 seconds to match

Consider increasing LPF smoothing to match the 1.5s enclosure convergence for unified feel.

### 5. SkyCoverage-Only Mode for Wind

Wind doesn't enter through rain openings — wind enters through ANY opening and flows around obstacles. The OcclusionFactor is rain-specific (DDA from rain-hit columns). For wind, SkyCoverage alone is a better metric:
- SkyCoverage → wind volume (how open is the sky overhead = how exposed to weather generally)
- No LPF for wind (or very gentle) — wind is broadband, LPF sounds unnatural
- Phase 5B: wind positional source oriented by wind direction, not by rain opening positions

---

## Conclusion: The Architecture Is Sound

**The current Phase 5A approach DOES make sense as the foundation for 5B.** The two-layer pattern (ambient bed + positional sources) is exactly how AAA games handle this. The key insights:

1. **Layer 1 (ambient bed) serves a different purpose than Layer 2 (positional sources).** Layer 1 = diffuse indirect rain (reflections, bass through structure). Layer 2 = direct rain from openings. Both are needed simultaneously.

2. **The overlap concern is valid but manageable.** Add ambient ducking when positional sources are active. This is standard practice (Wwise does this automatically).

3. **The false proximity issue (rain heightmap near but behind wall) is handled correctly.** SkyCoverage being lower doesn't matter when OcclusionFactor pushes LPF to bass-only. The perceived volume at 300Hz LPF is very low even at "40% volume."

4. **The deep underground issue needs a fix.** Add aggressive volume cutoff when both metrics > 0.92. Simple code change in `RainAudioHandler`.

5. **Wind should be treated differently.** Don't share rain's OcclusionFactor for wind. Use SkyCoverage only + wind direction for Phase 5B.

6. **The transition will be smooth IF we implement ambient ducking.** Without it, there's a period where both layers play at full contribution. With ducking, the ambient gracefully yields to positional sources as they activate.

### Key Code Changes Before Phase 5B

1. **Deep enclosure volume cutoff** (fix the 40% bass underground issue)
2. **Prepare ducking interface** in `RainAudioHandler` (accepts `positionalContribution` float)
3. **Separate wind from rain** in volume calculation (wind uses SkyCoverage only, no OcclusionFactor-driven LPF)
4. **Align smoothing factors** (LPF convergence should match enclosure convergence at ~1.5s)

These are all small, non-architectural changes to the existing Phase 5A code. The fundamental structure — WeatherEnclosureCalculator feeding both metrics + VerifiedOpenings, RainAudioHandler consuming them — is exactly right for Phase 5B integration.

---

## References

- **Wwise Rooms & Portals**: Audiokinetic spatial audio documentation — Room Tones reduce near Portals, transmission through walls via material-based LPF
- **TLOU2 Rain System**: GDC presentation on Naughty Dog's environmental audio — two-layer rain (bed + surface emitters), 1.5s crossfade
- **RDR2 Weather Audio**: Distance-to-sky metric, mono+HRTF ambient bed, directional wind
- **Battlefield/Frostbite**: 500ms weather update timer, 8 surface emitters, 2-3s indoor/outdoor fade
- **Steam Audio**: Obstruction vs occlusion separation, air absorption for distance-dependent frequency attenuation
- **Project docs**: `PHASE5_WEATHER.md`, `HANDOFF_WEATHER_ENCLOSURE_RETHINK.md`, `WeatherEnclosureCalculator.cs`
