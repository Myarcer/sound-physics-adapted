# Sound Physics Adapted - Implementation Plan

## Vision
Create a comprehensive sound physics mod for Vintage Story that matches or exceeds the feature set of Minecraft's Sound Physics Remastered, adapted for VS's unique mechanics and atmosphere.

---

## Phase Overview

```
Phase 1: Foundation & Basic Occlusion     [COMPLETE] v0.1.0
Phase 2: Material System                  [COMPLETE] v0.2.0
Phase 3: Enhanced Reverb (SPR-Style)      [COMPLETE] v0.3.0
Phase 4: Shared Airspace & Sound Paths    [COMPLETE] v0.4.0
Phase 5: Weather Audio System             [PLANNED]
  ├── 5A: Rain & Weather LPF Replacement  [Suppress VS + own sounds + LPF]
  ├── 5B: Positional Weather Sources        [RainHeightMap scan + opening clustering]
  └── 5C: Thunder Positioning              [Mode B sky search + bolt direction]
Phase 6: Polish & Performance             [PLANNED]
  ├── 6A: Air Absorption
  ├── 6B: Performance Optimization
  └── 6C: Edge Case Hardening
Phase 7: Debug Visualization              [PLANNED - can start anytime]
```

### Phase Renumbering Note (2026-02-10)
- Old Phase 5 (EnclosureManager approach) **SCRAPPED** — fails vertical shaft edge case, redundant with VS's BFS
- Old Phase 6 (Thunder) **absorbed into Phase 5C** — thunder is weather, managed by same WeatherAudioManager
- Old Phase 7 (Advanced Features) **split** → Phase 6 (polish/perf) + Phase 7 (debug viz)

---

## Phase 1: Foundation & Basic Occlusion - COMPLETE

### Delivered
- [x] Mod Framework (ModSystem, config, debug)
- [x] DDA Raycast occlusion from sound to player
- [x] Lowpass filtering based on accumulated occlusion
- [x] Per-sound filter management (no global filter conflicts)
- [x] Harmony patches for sound interception

---

## Phase 2: Material System - COMPLETE

### Delivered
- [x] Per-material occlusion values (Stone=1.0, Wood=0.5, Glass=0.1, etc.)
- [x] JSON configuration for customization
- [x] Block-specific overrides via wildcard matching
- [x] 3-tier lookup: block override → material default → fallback
- [x] Material reflectivity for reverb (Stone=1.5, Wood=0.4, etc.)

---

## Phase 3: Enhanced Reverb (SPR-Style) - COMPLETE

### Delivered
- [x] 4-slot EFX auxiliary reverb system
- [x] Fibonacci sphere ray distribution (32 rays)
- [x] Multi-bounce reflection calculation
- [x] Send filter gains control reverb intensity
- [x] SPR-matched effect parameters (per-slot gains)
- [x] Non-positional sound handling (player position fallback)
- [x] Material-based reflectivity affects reverb character

### Known Issue (Fixed in Phase 4)
Reverb is calculated at sound SOURCE position only. Distant occluded sounds in enclosed spaces produce strong reverb that can't actually reach the player.

---

## Phase 4: Shared Airspace & Sound Path Calculation - COMPLETE

### The Problem: Reverb + Occlusion Don't Integrate

**Current Bug - The Bee Example (FIXED):**
```
    YOU (player above ground, in open desert)
    ═══════════════════════════════════════════  Ground level
    ████████████████████████████████████████████  Stone
    ████████████████████████████████████████████
    ████████  ┌───────────┐  ███████████████████
    ████████  │  🐝 BEE   │  ███████████████████  3x3 stone chamber
    ████████  │  HIVE     │  ███████████████████  21 meters below
    ████████  └───────────┘  ███████████████████
    ████████████████████████████████████████████

Current behavior:
- Occlusion: Heavy muffling (many stone blocks) ✓ Correct
- Reverb: g0=0.64, 100% enclosed ✗ WRONG!
- Result: Quiet muffled buzz + MASSIVE cave reverb = Unnatural

The problem:
- 32/32 rays hit stone walls (100% enclosed)
- Stone reflectivity 1.5 → high reverb gains
- But those reflections CAN'T reach player through solid rock!

What should happen:
- Reverb reflections also need line-of-sight to player
- If blocked, reverb should be near-zero
- Result: Quiet muffled buzz + minimal reverb = Natural
```

### The Solution: SPR Shared Airspace

Only count reverb from bounce points that can actually reach the player:

```csharp
// Current (broken):
foreach (ray bounce in bounces) {
    reverbEnergy += bounce.reflectivity;  // All bounces count!
}

// SPR style (correct):
foreach (ray bounce in bounces) {
    // Can this bounce point SEE the player?
    if (HasClearPath(bouncePoint, playerPos)) {
        reverbEnergy += bounce.reflectivity;  // Only if unobstructed!
        sharedAirspace.Add(bouncePoint, distance);
    }
}
```

### Phase 4a: Shared Airspace Detection - COMPLETE

**Goal**: Fix reverb calculation so only audible reflections count.

**Implementation**:
```csharp
/// <summary>
/// Check if a bounce point has clear line-of-sight to player.
/// </summary>
private static bool HasSharedAirspace(Vec3d bouncePoint, Vec3d normal, Vec3d playerPos, IBlockAccessor blocks)
{
    // Offset slightly from surface to avoid self-intersection
    Vec3d testStart = bouncePoint + normal * 0.1;

    // Simple raycast to player
    return !RayHitsBlock(testStart, playerPos, blocks);
}

// In reverb calculation loop:
var hit = RaycastToSurface(soundPos, rayDir, maxDistance, blockAccessor);
if (hit.HasValue)
{
    // NEW: Only count this bounce if player can "hear" it
    if (HasSharedAirspace(hit.Position, hit.Normal, playerPos, blockAccessor))
    {
        float reflectivity = GetBlockReflectivity(hit.Block);
        sendGain0 += CalculateEnergy(reflectivity, distance);

        // Track for sound repositioning (Phase 4b)
        sharedAirspaces.Add((hit.Position, playerPos - hit.Position, distance));
    }
    // If no shared airspace, this bounce contributes NOTHING
}
```

**Bee Example After Fix**:
```
Bee in sealed chamber:
- 32 rays hit stone walls
- 0 bounce points can see player (blocked by stone)
- Shared airspace count: 0
- Reverb: g0 ≈ 0 ✓ Correct!

Result: Muffled bee sound with NO reverb = Natural!
```

### Phase 4b: Sound Path Resolution & Repositioning (SPP-Style Permeation)

**Goal**: Sounds heard through openings appear to come from the opening. Sounds through walls still contribute (quietly) to prevent unrealistic snapping to a single hole.
**Status**: PLANNED - Foundation (shared airspace detection) in place.

**Why SPP-Style, Not SPR-Style**:
- **SPR**: Binary shared airspace - bounce either sees player or doesn't. Only unblocked rays contribute.
  - Problem: A roof hole pulls ALL sound to the hole. No through-wall contribution.
- **SPP**: Permeation rays - EVERY ray contributes, weight reduced by occlusion along path.
  - Through-wall rays dilute the hole's dominance. Natural blending.
  - We already have material-weighted DDA occlusion → better than SPP's simple block count.

**Reference**: `references/SoundPhysicsPerfected/` - `RaycastingHelper.java` (Red rays: lines 765-823, weight calc: 825-839, averaging: 841-871)

**The Air Shaft Case**:
```
    YOU (player)
         \
          \  sound reaches you via shaft
    ═══════╔════════════════════════════  Ground
    ███████║████████████████████████████
    ███████║████████████████████████████  Air shaft
    ███████╔══════╗█████████████████████
    ███████║ 🐝   ║█████████████████████  Chamber
    ███████╚══════╝█████████████████████

SPP-style behavior:
1. Bounce rays through shaft → low occlusion to player → HIGH weight
2. Bounce rays through walls → high occlusion to player → LOW weight (but non-zero!)
3. Weighted average direction: strongly biased toward shaft, slightly pulled by walls
4. Weighted average muffle: low (shaft rays dominate)
5. Result: Sound mostly from shaft, not 100% snapped to it
```

**The Roof Hole Problem (solved)**:
```
    ████████  ░░  ████████████  ← roof with small hole
    ████████      ████████████
    ██  YOU          🐝  BEE ██  ← big room
    ████████████████████████████

SPR would: 100% reposition to hole (only unblocked rays count)
SPP-style: Hole rays HIGH weight + wall rays LOW weight → mostly from hole,
           slight pull from walls. More natural.
```

**Core Algorithm** (Mode A: Source-Centric):
```csharp
// In reverb calculation loop - EVERY bounce ray contributes
var hit = RaycastToSurface(soundPos, rayDir, maxDistance, blockAccessor);
if (hit.HasValue)
{
    float reflectivity = GetBlockReflectivity(hit.Block);
    Vec3d bouncePoint = hit.Position + hit.Normal * 0.1;

    // Permeation: raycast from bounce point to player, accumulate occlusion
    float pathOcclusion = CalculateOcclusionAlongPath(bouncePoint, playerPos, blockAccessor);
    float permeation = (float)Math.Pow(config.PermeationBase, pathOcclusion);
    // permeation: 1.0 = clear air, ~0.0 = heavy wall

    float totalDist = (float)hit.Distance + (float)bouncePoint.DistanceTo(playerPos);
    float weight = permeation / (totalDist * totalDist);

    // Reverb: scale by permeation (blocked bounces barely contribute)
    sendGain0 += CalculateEnergy(reflectivity, hit.Distance) * permeation;

    // Repositioning: ALL rays contribute, weighted by permeation
    // Direction from PLAYER toward BOUNCE (matches SPP's initialDirection)
    // NOT bouncePoint→player! That reverses the apparent position.
    Vec3d fromPlayerToBounce = (bouncePoint - playerPos).Normalize();
    pathResolver.AddPath(fromPlayerToBounce, totalDist, weight, pathOcclusion);
}

// After all rays:
var result = pathResolver.Evaluate(soundPos, playerPos);
if (result.HasValue)
{
    // Reposition sound to weighted average direction
    AL.Source3f(sourceId, ALSource3f.Position, result.Position);
    // Apply muffle (weighted average occlusion) as LPF
    float muffleCutoff = (float)Math.Exp(-result.AverageOcclusion * absorptionCoeff);
    ApplyDirectFilter(sourceId, muffleCutoff);
}
```

**Sound Path Resolver** (shared infrastructure for Phase 4b + Phase 5):
```csharp
public class SoundPathResolver
{
    private List<(Vec3d direction, double distance, double weight, double occlusion)> paths = new();

    public void AddPath(Vec3d direction, double totalDist, double weight, double occlusion)
    {
        paths.Add((direction.Normalize(), totalDist, weight, occlusion));
    }

    public SoundPathResult? Evaluate(Vec3d soundPos, Vec3d playerPos)
    {
        if (paths.Count == 0) return null;

        Vec3d weightedDir = Vec3d.Zero;
        double totalWeight = 0;
        double weightedOcclusion = 0;
        double weightedDistance = 0;

        foreach (var (dir, dist, weight, occlusion) in paths)
        {
            weightedDir = weightedDir.Add(dir.Scale(weight));
            weightedOcclusion += occlusion * weight;
            weightedDistance += dist * weight;
            totalWeight += weight;
        }

        if (totalWeight < 0.0001) return null;

        Vec3d apparentDir = weightedDir.Scale(1.0 / totalWeight).Normalize();
        double avgOcclusion = weightedOcclusion / totalWeight;
        double avgDist = weightedDistance / totalWeight;

        // Keep original distance for positioning, use apparent direction
        double originalDist = soundPos.DistanceTo(playerPos);
        Vec3d newPos = playerPos.Add(apparentDir.Scale(originalDist));

        return new SoundPathResult(newPos, avgOcclusion, avgDist, paths.Count);
    }
}
```

**Mode B: Player-Centric (Sky Search)** - Used by Phase 5 thunder:
```csharp
// Cast rays from PLAYER upward into upper hemisphere
// Instead of bounce-point → player, it's player → sky
// "Found" = ray reaches open sky (no hit within maxDist)
// Occlusion = accumulated material along the ray
// See Phase 5 Section C for thunder-specific usage
```

**Direct LOS Shortcut** (from SPP's `shortcutDirectionality`):
- If direct path player→sound has zero occlusion: skip repositioning, use original position
- Avoids unnecessary repositioning for sounds already in clear line of sight

### Phase 4 Deliverables

| Feature | Purpose | Fixes | Status |
|---------|---------|-------|--------|
| Permeation-weighted paths | All rays contribute, walls don't block entirely | Roof hole snap problem | DONE |
| Sound repositioning | Move source toward best path | Sound-through-doorway realism | DONE |
| Weighted muffle | LPF from average path occlusion | Through-wall sounds muffled naturally | DONE |
| SoundPathResolver | Shared infrastructure for Phase 4b + Phase 5 | Unified architecture | DONE |
| Direct LOS shortcut | Skip repositioning when unnecessary | Performance + correctness | DONE |

- [x] Replace binary shared airspace check with permeation-weighted paths
- [x] `SoundPathResolver` class (Mode A: source-centric)
- [x] `CalculatePathOcclusion()` reusing Phase 1 DDA raycast
- [x] Weighted average for apparent direction + muffle factor
- [x] Reposition sound source via OpenAL `alSource3f`
- [x] Apply muffle LPF from weighted average occlusion
- [x] Direct LOS shortcut (skip repositioning for unoccluded sounds)
- [x] Config: `PermeationBase`, enable/disable repositioning, enable/disable muffle
- [x] Debug logging: path count, average occlusion, reposition direction
- [ ] Mode B: player-centric sky search (deferred to Phase 5C — thunder positioning)

---

## Shared Infrastructure: RayDistribution Utility

### The Problem: Duplicated Ray Generation Code

Both `ReverbCalculator` (Phase 3) and the upcoming Weather systems need to cast rays in spherical/hemispherical patterns. Currently `ReverbCalculator.cs` has hardcoded Fibonacci sphere distribution.

### Solution: Extract to `RayDistribution` Static Utility

**New file**: `src/Core/RayDistribution.cs`

```csharp
public static class RayDistribution
{
    /// Full sphere (existing, for ReverbCalculator)
    public static Vec3d[] GenerateFibonacciSphere(int numRays) { ... }
    
    /// Upper hemisphere only (for Phase 5C thunder sky search)
    public static Vec3d[] GenerateUpperHemisphere(int numRays) { ... }
}
```

**Note**: The old `GenerateEnclosureProbes()` method is no longer needed — the EnclosureManager approach was scrapped in favor of using VS's built-in `roomVolumePitchLoss`. Phase 5B (`RainOpeningScanner`) does NOT use RayDistribution — it scans the RainHeightMap grid directly.

### Integration

1. **ReverbCalculator**: Refactor to use `RayDistribution.GenerateFibonacciSphere()`
2. **SkySearchUtil** (Phase 5C): Use `RayDistribution.GenerateUpperHemisphere()`
3. **RainOpeningScanner** (Phase 5B): Does NOT use rays — scans RainHeightMap grid + DDA verify

---

## Phase 5: Weather Audio System — PLANNED

**[Full Documentation: docs/phases/PHASE5_WEATHER.md](phases/PHASE5_WEATHER.md)**

### Architecture
```
WeatherAudioManager (coordinator)
├── RainAudioHandler (5A: LPF bed, 5B: multi-source positional via RainOpeningScanner)
└── ThunderAudioHandler (5C: Mode B sky search)
```

### Key Decisions (2026-02-10)
- **Suppress + Replace**: Harmony patches silence VS weather sounds, we play our own with LPF
- **Use `roomVolumePitchLoss`**: VS's BFS flood fill handles edge cases (shaft, etc.) correctly. No custom EnclosureManager.
- **RainHeightMap grid scan**: `RainOpeningScanner` scans ~450 columns, DDA verifies closest, clusters into opening groups (independent from Phase 4b)
- **Thunder via Mode B**: Player-centric upper hemisphere search, bolt direction bias

### Sub-Phases
- **5A: Rain LPF Replacement** — Suppress VS weather sounds → own mono sources + OpenAL EFX LPF proportional to `roomVolumePitchLoss`. Biggest single quality improvement.
- **5B: Positional Weather Sources** — `RainOpeningScanner` (RainHeightMap grid scan + DDA verify + clustering) detects openings → up to 4-6 positional sources at opening clusters. "Rain from the doorway" + "rain through the roof hole" simultaneously. Shared with hail + wind.
- **5C: Thunder Positioning** — Mode B sky search (16 upper hemisphere rays) + bolt direction bias → directional thunder from windows/openings.

---

## Phase 6: Polish & Performance — PLANNED

**[Full Documentation: docs/phases/PHASE6_POLISH.md](phases/PHASE6_POLISH.md)**

### Overview
- **6A: Air Absorption** — OpenAL EFX air absorption factor
- **6B: Performance** — LOD ray counts, result caching, spatial hashing, threading
- **6C: Edge Cases** — Minimum occlusion aggregation for corners

---

## Phase 7: Debug Visualization — PLANNED

**[Full Documentation: docs/phases/PHASE7_DEBUG_VISUALIZATION.md](phases/PHASE7_DEBUG_VISUALIZATION.md)**

### Overview
- IRenderer-based ray visualization with color coding
- Chat command toggles (hot-reloadable)
- Includes weather probe rays (sky fan, sky search)
- Can be implemented anytime (isolated from audio logic)

---

## Release Plan

| Version | Phase | Features |
|---------|-------|----------|
| 0.1.0 | 1 | Basic occlusion, framework |
| 0.2.0 | 2 | Material system, config |
| 0.3.0 | 3 | SPR-style reverb |
| 0.4.0 | 4 | Shared airspace, sound paths, repositioning |
| 0.5.0 | 5A | Weather LPF replacement (rain/wind/hail/tremble) |
| 0.5.1 | 5B | Rain positional source (sky probe + directional rain from openings) |
| 0.5.2 | 5C | Thunder positioning (Mode B sky search + bolt direction) |
| 0.6.0 | 6 | Air absorption, performance optimization, edge cases |
| 1.0.0 | 7 | Debug visualization, final polish |

---

## Success Criteria

### Current State (v0.3.0)
- [x] Sounds through walls are muffled
- [x] Caves have reverb, open sky doesn't
- [x] Materials affect both occlusion and reverb
- [ ] Distant enclosed sounds don't have excessive reverb (Phase 4)

### Feature Complete (v1.0)
- All MC Sound Physics features ported
- Weather audio with LPF + directional rain + positional thunder
- Smooth performance (<1ms per sound)
- Configurable everything
- Good defaults that "just work"

### User Experience Goals
- "I can't hear monsters through walls anymore"
- "The beehive underground sounds muffled, not echoey"
- "Sound comes from the doorway when the source is in another room"
- "Closing the door actually helps with the storm noise"
- "Rain sounds like it's outside and I'm inside — that bass rumble!"
- "I can hear the rain coming from the doorway, not from everywhere"
- "Thunder from the direction of the lightning bolt, through the window"
