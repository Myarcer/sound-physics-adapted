# Weather & Enclosure System - Handoff Notes for Rethink

## Status
~~Phase 5 enclosure system needs rethinking before implementation.~~ **RESOLVED 2026-02-10**: EnclosureManager approach scrapped. Using VS's `roomVolumePitchLoss` + LPF instead. See "Decisions Made" section at bottom. Full implementation plan in [PHASE5_WEATHER.md](phases/PHASE5_WEATHER.md).

This document is retained as research reference. All open questions have been answered.

---

## Key Research Findings

### 1. VS's `GetDistanceToRainFall` is a BFS Flood Fill (Not a Raycast)
**File**: `VintagestoryLib.decompiled.cs:73072-73109`

```csharp
public int GetDistanceToRainFall(BlockPos pos, int horziontalSearchWidth = 4, int verticalSearchWidth = 1)
{
    // Quick check: player already exposed to rain?
    if (GetRainMapHeightAt(pos) <= pos.Y) return 0;

    // BFS flood fill from player, through non-solid/non-sound-retaining blocks
    // Each neighbor checked: !SideSolid AND GetRetention(Sound) == 0
    // Returns Manhattan distance to nearest rain-exposed block, or 99
}
```

- **Search bounds**: Called with `(plrPos, 12, 4)` = 12 blocks horizontal, 4 blocks vertical
- **Smart**: Uses `EnumRetentionType.Sound` per-block and per-face - handles doors, windows, non-full blocks
- **RainHeightMap**: Precomputed per-column (X,Z), the Y of the highest non-`RainPermeable` block. Rain particles die at this height.
- **Background thread**: Runs on `TyronThreadPool.QueueTask`, only recalculates when previous search complete

### 2. `roomVolumePitchLoss` Curve & Application
**File**: `WeatherSimulationSound.cs:196-200`

```csharp
var dist = capi.World.BlockAccessor.GetDistanceToRainFall(plrPos, 12, 4);
float val = (float)Math.Pow(Math.Max(0, (dist - 2) / 10f), 2);
roomVolumePitchLoss = GameMath.Clamp(val, 0, 1);
```

| Distance | Loss | Meaning |
|----------|------|---------|
| 0-2 | 0.00 | In rain or 1-2 blocks from it |
| 5 | 0.09 | Near window/door |
| 7 | 0.25 | A room away |
| 9 | 0.49 | Deep in building |
| 12+ | 1.00 | Fully enclosed |

**How VS applies it** (all subtractive, NO lowpass ever):

| Sound | Volume | Pitch |
|-------|--------|-------|
| Rain leafy/leafless | `target - loss` | `target - loss/4` |
| Tremble | `rainfall*1.6 - 0.8 - loss*0.25` | `1 - loss*0.65` |
| Hail | `target - loss` (twice!) | `target - loss/4` |
| Wind | `(1-loss) * windSpeed - 0.3` | None |
| Thunder `deepnessSub` | Y-depth + `loss * 0.5` | Same |

**Critical: `roomVolumePitchLoss` is `public static float`** - we can read it directly from our mod.

### 3. MC Reference Mods: Neither Handles Weather

**SPR** (`SoundPhysics.java:211`):
```java
if (posX == 0D && posY == 0D && posZ == 0D) {
    setDefaultEnvironment(sourceID, auxOnly);  // SKIP entirely
    return null;
}
```
SPR explicitly skips ALL sounds at (0,0,0). Weather sounds in MC are at (0,0,0). Additionally, SPR rate-limits weather to 0 (`SoundRateConfig.java:111-113`):
```java
map.put(SoundEvents.WEATHER_RAIN.location(), 0);       // Skip
map.put(SoundEvents.WEATHER_RAIN_ABOVE.location(), 0);  // Skip
map.put(SoundEvents.LIGHTNING_BOLT_THUNDER.location(), 0); // Skip
```

**SPP**: No weather-specific handling found at all. The `@Nullable` grep hit was a false positive import.

**Conclusion**: Neither MC sound physics mod touches weather sounds. This is uncharted territory - no reference implementation exists for weather occlusion/repositioning. Whatever we build is novel.

### 4. Minecraft Weather Sound Architecture (For Reference)
MC has `WEATHER_RAIN`, `WEATHER_RAIN_ABOVE`, `LIGHTNING_BOLT_THUNDER` as sound events. These appear to be non-positional (played at 0,0,0) based on SPR's explicit skip. MC rain is simpler than VS - it doesn't have the leafy/leafless distinction or the `roomVolumePitchLoss` BFS that VS has.

---

## Edge Cases That Break Simple Approaches

### Edge Case 1: One-Block Hole in 3-Block-Thick Roof
```
    ████████  ░  █████████████  ← 3-block thick roof, 1-block hole
    ████████  ░  █████████████
    ████████  ░  █████████████
    ██                      ██  ← big room, player inside
    ████████████████████████████
```
**VS BFS result**: Distance to rain = ~1-3 (hole is nearby) → loss ≈ 0.00-0.01 → **rain at near-full volume**
**Reality**: There's one column of rain dripping through the hole. The room should NOT sound like standing outside.
**Our raycast approach**: Upward ray through hole = zero occlusion → enclosure ≈ 0.0 → same problem.

Both approaches fail here. The issue: a tiny hole makes the system think you're "outdoors."

### Edge Case 2: Open Door
```
    ██████████████████████████
    ██                      ██
    ██  YOU                 ░░  ← open door, rain outside
    ██                      ░░
    ██████████████████████████
```
**VS BFS result**: Distance to rain through door = ~3-4 → loss ≈ 0.04 → rain nearly full volume
**But this actually makes sense!** With the door open, you WOULD hear rain loudly. The issue is it's omnidirectional - rain comes from "everywhere" instead of from the door direction.

**This is exactly the Phase 4b repositioning problem.** If rain were positional, our permeation system would:
- High weight path through door → reposition rain sound toward door
- Low weight through walls → slight ambient pull
- Result: Rain from the doorway, muffled through walls

### Edge Case 3: Deep Cave with Vertical Shaft
```
    ░░░░░░░░░░░  ← surface, raining
    ██████░░█████  ← shaft opening
    ██████░░█████
    ██████░░█████  ← 60 blocks of air shaft
    ██████░░█████
    ██████╔═╗████
    ██████║ ║████  ← player in chamber connected to shaft
    ██████╚═╝████
```
**VS BFS result**: verticalSearchWidth = 4 → BFS never reaches surface → dist = 99 → fully muffled. **Correct!**
**Our raycast approach**: Upward ray through shaft = zero occlusion for 60 blocks of air → enclosure ≈ 0.0 → **WRONG!**

The BFS handles this correctly because of its 4-block vertical limit. Our raycasts would need distance weighting to match.

---

## The Conceptual Problem

Rain/wind are currently treated as "omnidirectional ambient" - they come from everywhere equally. But in reality:
- **Rain comes from above** (through the roof/sky)
- **Wind comes from openings** (doors, windows, gaps)
- **Hail hits surfaces** (the roof, the ground outside)

If these were **positional** (or at least directional), our Phase 4b permeation system could handle them naturally:
- Place a virtual "rain source" above the player's roof
- Phase 4b calculates permeation through the roof → muffled rain from above
- Open door → shared airspace → rain repositioned toward door
- Hole in roof → high-weight path → rain from the hole, with permeation through rest of roof adding quiet ambient

This would unify the weather system with our existing sound physics rather than building a separate enclosure system.

---

## My Subjective Assessment

### What Works About VS's Existing System
- The BFS is clever - distance-bounded, handles the shaft case, uses sound retention
- `roomVolumePitchLoss` as a public static is convenient for us
- The curve shape (quadratic, with 2-block grace) feels reasonable

### What's Missing From VS's System
- **No lowpass**: Volume and pitch reduction only. Indoors during rain should have heavy LPF (bass rumble on roof)
- **No directionality**: Rain sounds identical from all directions when indoors
- **Binary indoor/outdoor**: The BFS result is used uniformly for all weather sounds, no per-direction nuance

### The "Make Everything Positional" Idea
This is the most elegant solution architecturally but has concerns:

**Pros:**
- Unifies weather with Phase 4b - no separate enclosure system needed
- Naturally handles all edge cases (door open, roof hole, shaft)
- Rain from the door, thunder from the window - realistic

**Cons:**
- Performance: Weather loops tick every frame. Running Phase 4b permeation raycasts per-frame on ambient sounds is expensive. Would need aggressive caching (calculate once, cache for 250ms+)
- Virtual source placement: Where exactly do you put a "rain" source? Above the player? Multiple sources around the player? A dome of virtual sources?
- VS weather sounds are `RelativePosition = true` with `LoadSound()` - these are listener-attached loops. Repositioning requires either creating new positional sources or patching how VS manages these sounds. Not trivial.

### The Simplest Approach That Might Work
1. **Read `roomVolumePitchLoss`** from VS (it's already calculated, handles shaft case)
2. **Add LPF** proportional to it (what VS is missing)
3. **For thunder only**: Use Phase 4b Mode B sky search (already planned)
4. **Defer positional rain to Phase 6** as an advanced feature

This gets 80% of the value with 20% of the work. The main missing piece would be directional rain from doorways, which is a nice-to-have.

---

## Open Questions for Next Session

~~1. **Positional weather: performance feasible?** Need to profile Phase 4b raycasts to see if cached results at 250ms intervals would work for weather loops~~
→ **RESOLVED**: Yes — `RainOpeningScanner` scans RainHeightMap grid (~450 O(1) lookups) + DDA verifies top 12 candidates every 500ms. Negligible cost (~0.03ms). Replaced earlier `RainSkyProbe` 12-ray fan which had fundamental blind spots (diagonal openings, below-player, distant holes).

~~2. **Virtual source placement**: If making rain positional, investigate dome/hemisphere approach vs single source above~~
→ **RESOLVED**: Up to 4-6 positional sources at detected opening clusters (RainOpeningScanner groups nearby rain-impact columns). Multiple simultaneous sources (door + roof hole). AAA games use 5-8 emitters; we cap at 4-6.

~~3. **VS `ILoadedSound` API**: Can we modify position of a `RelativePosition=true` sound after creation? Or do we need to spawn our own positional sources and suppress VS's originals?~~
→ **RESOLVED**: Suppress + Replace. Harmony patches silence VS weather sounds, we play our own `ILoadedSound` instances. Layer 1 (ambient bed) uses `RelativePosition=true` (stereo OK). Layer 2 (positional) uses `RelativePosition=false` (mono required).

~~4. **MC reference gap**: Neither SPR nor SPP handles weather. Consider checking other MC sound mods (e.g., AmbientSounds, Presence Footsteps) for any weather-related approaches~~
→ **RESOLVED**: Deep research confirmed no MC mod solves rain-through-doorway. AAA games (Wwise Rooms & Portals) validate our approach. We reference TLOU2, RDR2, Battlefield patterns.

~~5. **Hybrid approach**: Could we use `roomVolumePitchLoss` for the VOLUME/PITCH part (let VS handle it) and only add our LPF + optional repositioning on top?~~
→ **RESOLVED**: No — we suppress VS sounds entirely and play our own. Gives full control over volume curves (LPF does heavy lifting, gentler volume reduction than VS).

---

## Decisions Made (2026-02-10)

### Architecture
- **WeatherAudioManager** coordinator with **RainAudioHandler** + **ThunderAudioHandler** subcomponents
- Shared Harmony patches, shared weather state cache, separate audio strategies
- NOT fully separate classes (avoids duplicate Harmony hooks and weather state reading)

### Rain Strategy
- **Suppress + Replace** (not piggyback): Harmony patches zero out VS weather volumes, we play our own sounds with EFX LPF
- **Two layers**: Layer 1 = non-positional muffled ambient bed (always), Layer 2 = up to 4-6 positional sources at opening clusters (when partially enclosed)
- **`roomVolumePitchLoss` as enclosure metric**: VS's BFS handles edge cases correctly. No custom EnclosureManager.
- **Old EnclosureManager: SCRAPPED** — fails vertical shaft (Edge Case 3), redundant with VS's BFS

### Rain Opening Detection
- **`RainOpeningScanner`**: Scans RainHeightMap grid in 12-block radius (~450 O(1) array lookups), filters by Y-range, DDA verifies top 12 closest, clusters into opening groups (max 4-6)
- **Replaced `RainSkyProbe`** (12-ray fan): Had blind spots — diagonal openings, below-player openings, distant ceiling holes all missed by fixed rays. Grid scan catches ALL cases.
- **Independent from Phase 4b**: Different question ("where does rain land nearby?") vs Phase 4b ("path to sound source?")
- **Shared opening data**: Rain, hail, and wind all use the same detected opening clusters

### Thunder Strategy
- **Separate from rain** in handler logic, but under same `WeatherAudioManager`
- **Mode B sky search**: 16 upper hemisphere rays from player, bolt direction bias
- **Per-event** calculation (not cached — thunder is rare, discrete events)

### Implementation Order
1. **Phase 5A**: Rain LPF bed (biggest bang for buck, immediate quality win)
2. **Phase 5B**: Rain positional source + sky probe (directional rain from openings)
3. **Phase 5C**: Thunder Mode B sky search + bolt positioning

### Phase Renumbering
- Old Phase 5 (EnclosureManager) → **SCRAPPED**, replaced by new Phase 5 (Weather Audio System)
- Old Phase 6 (Thunder) → **absorbed into Phase 5C**
- Old Phase 7 (Advanced) → **split into Phase 6 (Polish/Perf) + Phase 7 (Debug Viz)**

### Research Backing
- AAA game audio research (Wwise, FMOD, TLOU2, RDR2, Battlefield) validates two-layer approach
- LPF is #1 missing feature in VS weather audio (volume alone ≠ indoor rain)
- No Minecraft mod has solved rain-through-doorway — this is novel
- 500ms cache interval is standard practice for weather audio calculations

---

---

## Phase 5C Rework: Bolt Thunder — Full Custom Layer 1 (2026-02-14)

### Problem Statement

The original Phase 5C design kept VS's native bolt sounds as "implicit Layer 1" and only added Layer 2 at openings. This has critical flaws:

1. **VS bolt sounds bypass our muffling entirely**: `LightningFlash.ClientInit` and `Render` play at `(0,0,0)` listener-relative. Our `SoundFilterManager` catches them but starts at `gainHF=1.0` and smooth-interpolates — the initial transient (the crack) is heard at full spectrum through walls before filters catch up.
2. **No LPF on bolt Layer 1**: VS applies only `deepnessSub` (underground depth). A bolt at 50 blocks while inside a stone building sounds the same as outdoors.
3. **No directional positioning**: All bolt sounds play at `(0,0,0)` — omnidirectional. No sense of "the bolt struck OVER THERE."
4. **Ambient L1 muffling formula ignores sky coverage**: `CalculateThunderGainHF` only uses `occlusionFactor`. At `sky=0.15, occl=0.02` → `gainHF=0.999` (zero filtering).

### Decision: Fully Replace All Bolt Sounds

Replace BOTH VS bolt sound events with our own managed, positioned, LPF-filtered versions. VS visuals (mesh, point lights, particles) remain untouched.

### VS `LightningFlash` Sound Events (Reference — Suppressed)

VS plays **TWO separate sounds per bolt** in `LightningFlash`:

**Event 1: `ClientInit()` — Instant thunder on bolt spawn**
```
Impact position: origin + points[last]
  origin.Y = GetRainMapHeightAt(x,z) + 1  (sky entry = rain map height)
  points[last] = random walk to cloud height

Distance thresholds (MUST MATCH in our replacement):
  < 150 blocks: lightning-verynear.ogg   vol = 1 - dist/180
  < 200 blocks: lightning-near.ogg       vol = 1 - dist/250
  < 320 blocks: lightning-distant.ogg    vol = 1 - dist/500
  >= 320: no sound

All played at (0, 0, 0), EnumSoundType.Weather, pitch=1, range=32
```

**Event 2: `Render()` — Delayed impact crack (bolt animation reaches ground)**
```
Fires when: secondsAlive * 10 >= 0.9 (once only, soundPlayed flag)
  secondsAlive increments at dt * 3 rate
  Effective delay: ~0.03-0.05 seconds after bolt spawn

  < 150 blocks: lightning-nodistance.ogg  vol = max(0.1, 1 - dist/70)
  >= 150: no sound

Same (0,0,0), Weather, pitch=1, range=32

Also triggers at < 100 blocks:
  - lightningTime + lightningIntensity flash effect (VISUAL - keep)
  - Particle explosion at impact (VISUAL - keep)
```

### Suppression Strategy: Transpiler

**Why not prefix**: Can't skip `ClientInit()` — it creates visuals (genPoints, genMesh, point lights). Can't skip `Render()` — it draws the bolt mesh and handles particles.

**Why not volume-zeroing-after**: Thunder is a transient — the crack is already heard by the time our filter catches up. Too late.

**Transpiler approach**: NOP only the `PlaySoundAt` calls inside both methods:
- `ClientInit()`: 3x `PlaySoundAt` calls (verynear, near, distant) → NOP
- `Render()`: 1x `PlaySoundAt` call (nodistance) → NOP
- All visual code (genPoints, genMesh, AddPointLight, particles, lightningTime) untouched.

**Implementation**: Find all `callvirt IWorldAccessor::PlaySoundAt` instructions, replace with stack cleanup (`pop` arguments) + `nop`. Standard Harmony transpiler pattern.

### Our Custom Bolt Layer 1 Architecture

**Two modes based on sky coverage**, matching vanilla distance/volume values exactly:

#### OUTDOOR (SkyCoverage < 0.5) — "Sky Upcast" Mode

When the player has partial or full sky visibility, thunder should come from a direction. The "sky upcast" finds where the bolt enters from the sky above its impact column.

```
Bolt hits at P = (X, Y_ground, Z)
Sky entry S = (X, GetRainMapHeightAt(X,Z) + 1, Z)   [= origin.Y, already computed by VS]

Step 1: Check occlusion from P → player ear
Step 2: If CLEAR → play positionally from impact direction
         If OCCLUDED (hill/terrain) → use sky entry S, arc over via FindClearSkyDirection

Asset + volume by distance (MATCH VANILLA EXACTLY):
  dist < 150:  verynear.ogg    vol = 1 - dist / 180
  dist < 200:  near.ogg        vol = 1 - dist / 250
  dist < 320:  distant.ogg     vol = 1 - dist / 500

Virtual placement distance (OpenAL source distance from player):
  CLOSE  (< 50 blocks):  place at actual distance (min 3 for spatialization)
  MEDIUM (50-150):        place at 30-60 blocks in bolt direction
  FAR    (150-320):       place at 50 blocks in general bolt direction

Range set to placeDist + 32 to prevent double-attenuation (our volume already encodes distance).

nodistance.ogg delayed crack (same timing as vanilla):
  dist < 150:  nodistance.ogg  vol = max(0.1, 1 - dist / 70)
  Played at same position as main thunder, slight delay matching VS's Render timing
```

#### INDOOR (SkyCoverage >= 0.5) — Occlusion + Sky Muffling

No sky upcast needed. Sound is coming through walls/ceiling regardless of bolt direction.

```
Combined enclosure = max(occlusionFactor, (skyCoverage - 0.15) / 0.85 * 0.7)
  ↑ Fixes the sky=0.15/occl=0.02 → gainHF=0.999 bug

Layer 1: Muffled rumble
  Same asset selection by distance (verynear/near/distant)
  Volume: vanilla_vol * ThunderLayer1Volume * (1 - combinedEnclosure * 0.5)
  LPF gainHF: CalculateThunderGainHF(combinedEnclosure) — quadratic curve
  Played as listener-relative (omnidirectional indoor rumble)

Layer 2: Crack at best opening (existing logic, unchanged)
  Only if trackedOpenings.Count > 0
  verynear.ogg one-shot at opening position, bolt-direction-biased scoring
  Routes through AudioPhysicsSystem for DDA occlusion + reverb

nodistance.ogg delayed crack:
  Indoor: same LPF as Layer 1, same timing
  Adds to the "punch" of nearby indoor bolts (muffled thud through roof)
```

### nodistance.ogg Timing

VS fires the delayed crack in `Render()` when `secondsAlive * 10 >= 0.9`. At `dt * 3` rate, this is roughly 0.03-0.05s after bolt spawn. For now: **same timing** — store bolt spawn time in our postfix, fire the delayed crack in `ThunderAudioHandler.OnGameTick()` when elapsed matches.

### Combined L1 Muffling Formula Fix

OLD (broken):
```csharp
float gainHF = 1 - (occlusionFactor^2) * (1 - minGainHF)
// sky=0.15, occl=0.02 → gainHF = 0.999 → zero filtering
```

NEW:
```csharp
float skyContribution = Math.Max(0, (skyCoverage - minSky) / (1f - minSky));
float combinedEnclosure = Math.Max(occlusionFactor, skyContribution * 0.7f);
float gainHF = 1 - (combinedEnclosure^2) * (1 - minGainHF)
// sky=0.15, occl=0.02, minSky=0.15 → skyContrib=0 → combined=0.02 → minimal filter (correct, barely indoor)
// sky=0.80, occl=0.02, minSky=0.15 → skyContrib=0.76 → combined=0.53 → moderate filter (correct, enclosed)
```

Applies to BOTH ambient thunder L1 AND bolt thunder L1.

### Data Flow

```
LightningFlash.ClientInit() [transpiler: sounds NOPped, visuals intact]
  ↓ postfix
BoltClientInitPostfix()
  ├── Read bolt position: origin + points[last]
  ├── Read distance, skyCoverage, occlusionFactor
  ├── Store pending bolt: { position, distance, spawnTimeMs }
  └── Play immediate thunder (Layer 1 + Layer 2) via ThunderAudioHandler

ThunderAudioHandler.PlayBoltThunder() [REWORKED]
  ├── if sky < 0.5:  OUTDOOR mode — sky upcast + positional placement
  │   ├── Occlusion check impact → player
  │   ├── If occluded: FindClearSkyDirection(earPos, boltDir) — arc over terrain
  │   ├── Virtual placement distance by bolt distance tier
  │   └── PlaySoundAt(asset, x, y, z, ...) — 3D positioned in bolt direction
  ├── if sky >= 0.5:  INDOOR mode — muffled + LPF
  │   ├── Layer 1: PlayLayer1Rumble() with combined enclosure (sky + occl)
  │   └── Layer 2: PlayLayer2AtBestOpening() (existing, unchanged)
  └── Queue delayed nodistance.ogg crack (fires in OnGameTick after timing matches)

ThunderAudioHandler.OnGameTick()
  ├── Check pending delayed cracks → fire when elapsed >= ~0.05s
  ├── TickOneShotSources (existing)
  └── Clean up expired Layer 1 sounds (existing)
```

### Suppression Details: What Gets NOPped vs Kept

**`ClientInit()` — line-by-line:**
| Line | Code | Action |
|------|------|--------|
| 53 | `genPoints(weatherSys)` | KEEP (visual) |
| 54 | `genMesh(points)` | KEEP (visual) |
| 56-61 | Point light setup + AddPointLight | KEEP (visual) |
| 63-65 | `var lp = points[last]; pos = origin + lp; dist = ...` | KEEP (we read these in postfix) |
| 67-87 | Three `PlaySoundAt` calls (verynear/near/distant) | **SUPPRESS** |

**`Render()` — line-by-line:**
| Line | Code | Action |
|------|------|--------|
| 201-215 | GameTick, shader uniforms, mesh render | KEEP (visual) |
| 217-218 | `if (cntRel >= 0.9 && !soundPlayed)` + `soundPlayed = true` | KEEP (flag) |
| 219-221 | `pos = ...; dist = ...` | KEEP |
| 223-225 | `PlaySoundAt(nodistance.ogg)` | **SUPPRESS** |
| 227-242 | lightningTime, lightningIntensity, particles | KEEP (visual) |

---

## Code References Quick Index

| What | File | Lines |
|------|------|-------|
| `GetDistanceToRainFall` (BFS) | `VintagestoryLib.decompiled.cs` | 73072-73109 |
| `roomVolumePitchLoss` calc | `WeatherSimulationSound.cs` | 196-200 |
| `roomVolumePitchLoss` applied to rain | `WeatherSimulationSound.cs` | 220-229 |
| `roomVolumePitchLoss` applied to hail | `WeatherSimulationSound.cs` | 252-256 |
| `roomVolumePitchLoss` applied to wind | `WeatherSimulationSound.cs` | 320 |
| `deepnessSub` (thunder) | `WeatherSimulationLightning.cs` | 92 |
| Weather sound init (all assets) | `WeatherSimulationSound.cs` | 57-133 |
| `RainHeightMap` update | `VintagestoryLib.decompiled.cs` | 73022-73028 |
| `RainPermeable` block property | `VintagestoryLib.decompiled.cs` | 3655 |
| SPR skips (0,0,0) sounds | `SoundPhysics.java` | 211 |
| SPR rate-limits weather to 0 | `SoundRateConfig.java` | 111-113 |
| SPR ambient pattern skip | `SoundPhysics.java` | 42, 242 |
| LightningFlash ClientInit sounds | `LightningFlash.cs` | 67-87 (three PlaySoundAt) |
| LightningFlash Render delayed crack | `LightningFlash.cs` | 217-225 (nodistance.ogg) |
| LightningFlash Render particles | `LightningFlash.cs` | 227-242 (particles + flash) |
| Lightning rumble (ambient) | `WeatherSimulationLightning.cs` | 86-136 (ClientTick) |
| Rain particles (DieOnRainHeightmap) | `WeatherSimulationParticles.cs` | 137, 352-353 |
