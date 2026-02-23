# Phase 5: Weather Audio System — IN PROGRESS

## Status: Phase 5A ✅ COMPLETE | Phase 5B ✅ IMPLEMENTED (testing needed) | Phase 5C ✅ IMPLEMENTED (testing needed)

**Last Updated**: 2026-02-14
**Depends On**: Phase 1 (Harmony/DDA), Phase 4b (SoundPathResolver, complete)
**Supersedes**: Old PHASE5_WEATHER.md (EnclosureManager approach — SCRAPPED), old PHASE6_THUNDER.md (absorbed into 5C)

**Phase 5A Achievement**: Dual-metric weather enclosure system (SkyCoverage + OcclusionFactor) with multi-height column DDA fully functional. Two-phase ground-first ray optimization provides accurate indoor/outdoor detection with early exit performance benefits. System correctly handles all edge cases: open outdoors, roof-no-walls, tunnel ends, full buildings, and caves. Ready for Phase 5B positional source implementation.

**Phase 5B Achievement**: Positional rain sources at detected openings via `OpeningClusterer` + `OpeningTracker` + `RainAudioHandler` source pool. Sources are world-positioned mono loops routed through `AudioPhysicsSystem` for automatic occlusion, repositioning around corners (L-shaped room fix), and reverb. `OpeningTracker` persists openings beyond verification windows with hysteresis-based removal. `AudioLoaderPatch` extended with `ForceMonoNextLoad` flag for stereo→mono downmix on demand. Layer 1 ambient bed ducks when Layer 2 positional sources are audible.

---

## Architecture Decision Record

### Why the Old Phase 5 Was Scrapped

The original Phase 5 planned a custom `EnclosureManager` with 9 raycasts (1 zenith + 8 horizon). The handoff document (`HANDOFF_WEATHER_ENCLOSURE_RETHINK.md`) identified critical failures:

1. **Edge Case 3 (vertical shaft)**: Upward ray travels 60 blocks through air shaft → enclosure ≈ 0.0 (WRONG). VS's BFS correctly returns "fully enclosed" due to its 4-block vertical search limit.
2. **Redundancy**: VS already computes `roomVolumePitchLoss` via BFS flood fill on a background thread. Building a parallel system that's LESS accurate is waste.
3. **Scope creep**: The old plan had 6+ deliverable files, canopy blending, 4 sub-phases for roof holes — too much for what's needed.

### New Approach: Suppress + Replace + LPF

**Core insight from AAA game audio research** (Wwise, FMOD, TLOU2, RDR2):
- Every professional implementation uses TWO layers: non-positional ambient bed + positional spot emitters
- The single biggest quality gap in VS is **no lowpass filtering** — volume reduction alone sounds like "quieter outdoor rain," not "rain on the roof while I'm inside"
- The bass rumble characteristic of indoor rain comes from LPF, not volume reduction

**Decision**: Suppress VS weather sounds via Harmony → play our own replacements with OpenAL EFX lowpass filter → use VS's own `roomVolumePitchLoss` as the enclosure metric (it handles edge cases correctly).

### Thunder Separated from Rain

Thunder is fundamentally different:
| Aspect | Rain/Wind/Hail | Thunder |
|--------|---------------|---------|
| Nature | Continuous ambient loop | Discrete event |
| Direction | From sky/above (omnidirectional) | From bolt position (directional) |
| Processing | `roomVolumePitchLoss` → LPF | Mode B sky search → reposition + LPF |
| Update rate | Cached (weather changes slowly) | Per-event (each lightning strike) |

Both live under `WeatherAudioManager` but use completely separate handler classes.

---

## Architecture

```
WeatherAudioManager (coordinator)
├── Shared: Harmony patches on WeatherSimulationSound + WeatherSimulationLightning
├── Shared: Weather state cache (precipitation type, intensity, wind speed)
│
├── WeatherEnclosureCalculator (Phase 5A — replaces roomVolumePitchLoss)
│   ├── Outer zone: Heightmap sky coverage (~50pts, r=8) → SkyCoverage (volume)
│   ├── DDA verification on exposed samples → OcclusionFactor (LPF)
│   │   Obstruction (air path to rain = crisp) vs Occlusion (material = muffled)
│   └── VerifiedOpenings: open-air rain positions cached for 5B clustering
│
├── RainAudioHandler (Phase 5A + 5B)
│   ├── Suppress VS rain/hail/wind/tremble sounds → volume 0
│   ├── [5A] Play own replacement loops with EFX lowpass
│   │   ├── Volume driven by SkyCoverage (proportional overhead coverage)
│   │   └── LPF driven by OcclusionFactor (material vs air path to rain)
│   ├── [5B] Cluster calculator's VerifiedOpenings into positional source groups
│   └── [5B] Up to 4-6 positional sources at opening clusters
│
└── ThunderAudioHandler (Phase 5C)
    ├── Intercept VS thunder PlaySoundAt calls
    ├── Mode B sky search: 16 upper hemisphere rays from player
    ├── Bolt direction bias (known lightning position)
    └── Reposition thunder toward best sky opening
```

---

## Phase 5A: Rain & Weather LPF Replacement

### Goal
Suppress all VS weather sounds, replace with our own managed mono sources, apply lowpass filtering using our own **WeatherEnclosureCalculator** with two complementary metrics:

1. **SkyCoverage** (heightmap sampling, ~50 points, radius 8): "What fraction of sky above me is blocked?" → drives **VOLUME**. Proportional — one opening doesn't collapse the room.
2. **OcclusionFactor** (DDA on exposed samples): "Of nearby rain, how much is behind material?" → drives **LPF**. Cave entrance with visible rain = crisp. Behind a wall = muffled bass rumble.

This separates **obstruction** (air path → distance fading, no LPF) from **occlusion** (material path → heavy LPF) — the core principle from Wwise/FMOD professional audio.

**Replaces vanilla `roomVolumePitchLoss`** which uses BFS flood fill that treats ANY nearby opening as "fully outdoors" (loss → 0), making one open door unmute all ambient rain.

### How VS Weather Sounds Work (Reference)

**Ambient Weather Loops** (`WeatherSimulationSound.cs`):
| Sound | Asset | Type |
|-------|-------|------|
| Rain (leafy) | `sounds/weather/tracks/rain-leafy.ogg` | Loop, Weather |
| Rain (leafless) | `sounds/weather/tracks/rain-leafless.ogg` | Loop, Weather |
| Wind (leafy) | `sounds/weather/wind-leafy.ogg` | Loop, Weather |
| Wind (leafless) | `sounds/weather/wind-leafless.ogg` | Loop, Weather |
| Low Tremble | `sounds/weather/tracks/verylowtremble.ogg` | Loop, Weather |
| Hail | `sounds/weather/tracks/hail.ogg` | Loop, Weather |

- All loaded with `Position = (0,0,0)`, `RelativePosition = true` (listener-attached)
- VS applies `roomVolumePitchLoss` as volume AND pitch reduction (no LPF ever)
- `roomVolumePitchLoss` is `public static float` — we read it directly

**VS Has ZERO Material-Based Rain *Loop* Sounds**: The 6 global weather loops (rain-leafy, rain-leafless, wind, hail, tremble) have no material awareness. However, VS DOES have a **block-level ambient rain sound system**:

#### Glass Rain Ambient System (`BlockRainAmbient`)

Glass blocks (panes, slabs, full glass) override `GetAmbientSoundStrength()` to return rainfall intensity when rain hits them. VS's ambient sound scanner finds these blocks, clusters them, and plays `sounds/environment/rainwindow.ogg` as a **positional 3D sound** at the glass cluster position.

| Property | Value |
|----------|-------|
| Asset | `environment/rainwindow.ogg` |
| Type | `EnumSoundType.Weather` |
| Positioning | `RelativePosition = false` (world-space 3D) |
| Range | 40 blocks |
| Volume | `sqrt(nearbyGlassBlocks) / 4 * rainfallIntensity` |
| Condition | `Rainfall > 0.1`, `Temperature > 3°C`, block exposed to rain (RainHeightMap or BFS ≤ 2) |

**Only glass blocks** use this in vanilla VS. No other material has rain ambient sounds.

**🚨 CRITICAL for Phase 5A**: `rainwindow.ogg` uses `EnumSoundType.Weather`. Our Harmony suppression patches MUST only target the 6 global `WeatherSimulationSound` loops, NOT block-level ambient sounds. The glass rain sound already works correctly with our occlusion system (DDA from player to glass position) and should be left untouched.

**Not a replacement for RainOpeningScanner**: The glass system answers "where is rain hitting glass?" while our scanner answers "where can rain enter nearby?" (doors, roof holes, unglazed windows). Different spatial question. Generalizing this to all blocks would create hundreds of positional sources — performance disaster. Our scanner outputs 4-6 clustered sources max.

**Future consideration**: Material-aware LPF on the global weather loops (glass roof = higher cutoff, stone = lower) remains a Phase 6+ opportunity. See config section.

### How VS Uses `roomVolumePitchLoss` Per Weather Type (Reference — We Replace This)

Critical reference — each weather type applies the loss value differently. **We no longer use roomVolumePitchLoss** but our per-type curves are informed by VS's intent:

| Weather Type | VS Volume Formula | VS Pitch Formula | Notes |
|---|---|---|---|
| **Rain (leafy/leafless)** | `volumeTarget - roomVolumePitchLoss` | `pitchTarget - loss/4` | Subtractive. Volume can go to 0 at loss=1. |
| **Tremble** | `rainfall*1.6 - 0.8 - loss*0.25` | `1 - loss*0.65` | Gentle volume reduction (25%), but VERY heavy pitch reduction (65%). VS intentionally keeps tremble audible indoors but shifts it to low rumble. |
| **Hail** | `rainfall*2 - loss` then `target - loss` again | `pitchTarget - loss/4` | Loss applied TWICE — likely a VS bug. Hail goes silent much faster than rain indoors. |
| **Wind** | `(1 - loss) * windSpeed - 0.3` | No pitch adjustment | Multiplicative, not subtractive! Wind is fully blocked at loss=1. This is correct — you don't hear wind inside a sealed room. |
| **Thunder rumble** | Indirect via `deepnessSub` (loss * 0.5) | Indirect via `deepnessSub` | Uses Y-position + loss combined. Underground + enclosed = maximum suppression. |
| **Lightning bolt strikes** | Distance-based only | Distance-based only | Does NOT use `roomVolumePitchLoss` at all. Pure distance attenuation. |

**Implication for our LPF**: We should NOT use identical LPF curves for all weather types. Per-type behavior:
- **Rain**: Standard LPF curve (the reference case)
- **Tremble**: Keep volume higher, apply heavier LPF (match VS intent: bass rumble stays audible)
- **Hail**: May want faster LPF ramp than rain (hail is high-freq, attenuates quickly through walls)
- **Wind**: Multiplicative volume curve, moderate LPF (wind is broadband, not as frequency-dependent)

**`roomVolumePitchLoss` Values** (from BFS `GetDistanceToRainFall(plrPos, 12, 4)`):
| BFS Distance | Loss | Meaning |
|-------------|------|---------|
| 0-2 | 0.00 | In rain or 1-2 blocks from it |
| 5 | 0.09 | Near window/door |
| 7 | 0.25 | A room away |
| 9 | 0.49 | Deep in building |
| 12+ | 1.00 | Fully enclosed |

### Implementation

#### Step 1: Harmony Patches — Suppress VS Weather Sounds

```csharp
// In WeatherSoundPatches.cs
[HarmonyPostfix]
[HarmonyPatch(typeof(WeatherSimulationSound), "updateSounds")]
static void SuppressVSWeatherSounds(WeatherSimulationSound __instance)
{
    // Set all VS weather LOOP volumes to 0
    // VS still calculates intensity, leafy/leafless, roomVolumePitchLoss
    // We just silence its output and play our own
    // 🚨 IMPORTANT: This only targets WeatherSimulationSound's 6 global loops.
    // Block-level ambient sounds (glass rainwindow.ogg) are NOT affected —
    // they're managed by SystemPlayerSounds/AmbientSound, not this class.
    foreach (var sound in GetAllWeatherSounds(__instance))
    {
        sound?.SetVolume(0f);
    }
}
```

**Accessing VS sound references**: Use Harmony `Traverse` or reflection to read the private `ILoadedSound` fields from `WeatherSimulationSound`:
- `rainSoundLeafy`, `rainSoundLeafless`
- `windSoundLeafy`, `windSoundLeafless`
- `trembleSound`
- `hailSound`

#### Step 2: WeatherEnclosureCalculator — Replaces roomVolumePitchLoss

```csharp
public class WeatherEnclosureCalculator : IDisposable
{
    // Two outputs, computed every 500ms:
    
    /// 0=outdoors, 1=fully covered. Drives ambient volume.
    /// Heightmap sampling: ~50 points in radius 8, distance-weighted.
    /// GetRainMapHeightAt(x,z) is O(1) — precomputed per chunk.
    public float SkyCoverage { get; }
    
    /// 0=all rain in air (crisp), 1=all behind material (muffled). Drives LPF.
    /// DDA ray from each exposed rain-impact to player:
    ///   Clear air → "direct" (obstruction: fades with distance, minimal LPF)
    ///   Solid blocks → "occluded" (occlusion: heavy LPF, bass rumble)
    public float OcclusionFactor { get; }
    
    /// Verified open-air rain positions — used by Phase 5B for clustering.
    public IReadOnlyList<VerifiedRainPosition> VerifiedOpenings { get; }
    
    public void Update(Vec3d playerPos, long gameTimeMs)
    {
        // STEP 1: Heightmap coverage (O(1) per sample)
        for (sample in ~50 points, radius 8):
            rainHeight = GetRainMapHeightAt(x, z)
            playerY < rainHeight → covered (weight)
            else → exposed (candidate for DDA)
        SkyCoverage = coveredWeight / totalWeight
        
        // STEP 2: DDA on closest ~15 exposed (air-path check)
        for (candidate in closest 15 exposed):
            occlusion = OcclusionCalculator.CalculatePathOcclusion(rainPos, playerPos)
            if (occlusion < 0.3) → directRain (clear air path)
        OcclusionFactor = 1 - (directWeight / exposedWeight)
    }
}
```

**Key scenarios:**
| Scenario | SkyCoverage | OcclusionFactor | Volume | LPF |
|---|---|---|---|---|
| Outdoors | ~0.0 | N/A | Full | None |
| Cave entrance (see rain) | ~0.4 | ~0.2 (most rain visible) | Moderate | Light (crisp) |
| Cave deep (no LOS to rain) | ~0.6 | ~1.0 (all behind rock) | Moderate | Heavy (muffled) |
| Small room, rain outside wall | ~0.6 | ~1.0 (wall blocks all) | Moderate | Heavy (bass rumble) |
| Room with open door | ~0.85 | ~0.5 (some through door) | Quiet | Moderate |
| Fully enclosed | ~1.0 | 1.0 | Very quiet | Maximum |

**Performance**: ~50 heightmap O(1) lookups + ~15 DDA rays × ~10 blocks = **~0.1ms every 500ms**.

#### Step 3: WeatherAudioManager — Coordinator

```csharp
public class WeatherAudioManager : IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly RainAudioHandler rainHandler;
    private readonly WeatherEnclosureCalculator enclosureCalculator;
    
    // Two enclosure metrics (from calculator)
    public float SkyCoverage => enclosureCalculator.SmoothedSkyCoverage;
    public float OcclusionFactor => enclosureCalculator.SmoothedOcclusionFactor;
    
    // Vanilla fallback (still read for debug comparison)
    public float RoomVolumePitchLoss { get; private set; }
    
    public void OnGameTick(float dt)
    {
        UpdateWeatherState();
        
        // Update enclosure calculator (rate-limited to 500ms internally)
        enclosureCalculator.Update(playerPos, gameTimeMs);
        
        // Pass BOTH metrics to handler
        rainHandler.Update(dt, RainIntensity, SkyCoverage, OcclusionFactor,
                          IsLeafy, WindSpeed, HailIntensity);
    }
}
```

#### Step 4: RainAudioHandler — Replacement Sound Management

```csharp
public class RainAudioHandler : IDisposable
{
    // Our replacement sounds (stereo, listener-attached for 5A)
    private ILoadedSound rainLoop;
    private ILoadedSound windLoop;
    private ILoadedSound trembleLoop;
    private ILoadedSound hailLoop;
    
    public void Update(float dt, float rainIntensity, float skyCoverage, float occlusionFactor,
                       bool isLeafy, float windSpeed, float hailIntensity)
    {
        // ── Rain ──
        if (rainIntensity > 0.01f)
        {
            float lpf = CalculateRainLPF(occlusionFactor);     // LPF ← occlusion
            float vol = CalculateRainVolume(rainIntensity, skyCoverage); // Vol ← sky coverage
            EnsureRainPlaying(isLeafy);
            rainLoop.SetVolume(vol);
            EfxHelper.SetDirectLowpass(GetSourceId(rainLoop), lpf);
        }
        else { StopRain(); }
        
        // ── Hail ── (faster LPF ramp — high-freq content attenuates quickly)
        if (hailIntensity > 0.01f)
        {
            float lpf = CalculateHailLPF(enclosure);  // Steeper curve than rain
            float vol = CalculateHailVolume(hailIntensity, enclosure);
            EnsureHailPlaying();
            hailLoop.SetVolume(vol);
            EfxHelper.SetDirectLowpass(GetSourceId(hailLoop), lpf);
        }
        else { StopHail(); }
        
        // ── Wind ── (multiplicative volume like VS, moderate LPF)
        if (windSpeed > 0.3f) // VS uses -0.3 offset threshold
        {
            float lpf = CalculateWindLPF(enclosure);
            float vol = (1f - enclosure) * windSpeed * 0.8f; // Multiplicative, like VS
            EnsureWindPlaying(isLeafy);
            windLoop.SetVolume(vol);
            EfxHelper.SetDirectLowpass(GetSourceId(windLoop), lpf);
        }
        else { StopWind(); }
        
        // ── Tremble ── (keep audible, heavy LPF — bass rumble is the point)
        if (rainIntensity > 0.5f) // VS: rainfall*1.6 - 0.8 threshold
        {
            float lpf = CalculateTrembleLPF(enclosure);  // Heaviest filtering
            float vol = CalculateTrembleVolume(rainIntensity, enclosure); // Gentle reduction
            EnsureTremblePlaying();
            trembleLoop.SetVolume(vol);
            EfxHelper.SetDirectLowpass(GetSourceId(trembleLoop), lpf);
        }
        else { StopTremble(); }
    }
    
    // ── Per-Type LPF Curves ──
    // All map enclosure (0-1) to Hz cutoff, but with different shapes
    
    /// Rain: Standard quadratic. Full spectrum → bass rumble.
    private float CalculateRainLPF(float enc)
    {
        float t = enc * enc;
        return MathHelper.Lerp(22000f, 300f, t);
    }
    
    /// Hail: Steeper curve. Hail is mostly high-freq (ice on surfaces),
    /// attenuates very quickly through walls. Drops to muffled faster than rain.
    private float CalculateHailLPF(float enc)
    {
        float t = MathF.Pow(enc, 1.5f); // Steeper than quadratic
        return MathHelper.Lerp(22000f, 250f, t);
    }
    
    /// Wind: Moderate curve. Wind is broadband noise, less frequency-dependent
    /// than rain. Volume does most of the work (multiplicative curve above).
    private float CalculateWindLPF(float enc)
    {
        float t = enc * enc;
        return MathHelper.Lerp(22000f, 600f, t); // Higher floor — wind bass persists
    }
    
    /// Tremble: Heaviest filtering. Already mostly bass content.
    /// VS intentionally keeps tremble audible indoors (only 25% vol reduction)
    /// but shifts pitch down 65%. We replicate with aggressive LPF + gentle vol.
    private float CalculateTrembleLPF(float enc)
    {
        float t = enc; // Linear — tremble is already <200Hz content
        return MathHelper.Lerp(400f, 80f, t); // Very narrow band
    }
    
    // ── Per-Type Volume Curves ──
    
    private float CalculateRainVolume(float intensity, float enc)
    {
        float volumeLoss = enc * 0.6f; // Max 60% reduction (LPF does the rest)
        return intensity * (1f - volumeLoss);
    }
    
    private float CalculateHailVolume(float intensity, float enc)
    {
        float volumeLoss = enc * 0.8f; // More aggressive — hail clarity lost fast
        return intensity * (1f - volumeLoss);
    }
    
    private float CalculateTrembleVolume(float intensity, float enc)
    {
        // VS only reduces tremble 25% — keep it audible, LPF does the work
        float volumeLoss = enc * 0.25f;
        return (intensity * 1.6f - 0.8f) * (1f - volumeLoss);
    }
}
```

#### Step 4: Audio Asset Handling

**Problem**: VS weather .ogg files are STEREO. OpenAL positional audio requires MONO.

**Solution**: Either:
1. Extend existing `AudioLoaderPatch` stereo-to-mono conversion (already converts music) to also convert `sounds/weather/` assets
2. OR use `RelativePosition = true` for Layer 1 (non-positional ambient bed doesn't need mono) and only convert to mono for Layer 2 (Phase 5B positional source)

**Recommendation for 5A**: Use `RelativePosition = true` (same as VS) for the replacement sounds. We're not repositioning yet — just adding LPF. Stereo is fine for a non-positional ambient bed. Save mono conversion for Phase 5B when we need positional sources.

### LPF Curve Design

The LPF curve is the most important tunable in this system. Research-informed values for **rain** (the reference case — other types have different curves, see per-type code above):

| Enclosure | LPF Cutoff | Perceived Effect | Real-World Equivalent |
|-----------|-----------|------------------|----------------------|
| 0.00 | 22000 Hz | Full spectrum rain | Standing in the rain |
| 0.10 | ~18000 Hz | Slight warmth | Under an awning |
| 0.25 | ~12000 Hz | Noticeably muffled highs | Near open window |
| 0.50 | ~5500 Hz | Clearly indoor | Room with door open |
| 0.75 | ~1800 Hz | Heavy muffling | Closed room, thin walls |
| 1.00 | 300 Hz | Bass rumble only | Deep interior, thick walls |

**Key insight from research**: Always keep some bass — the characteristic "rain on the roof" thrum is what makes indoor rain feel real. Never go to 0 Hz or full silence.

### Config Options

```csharp
// Phase 5A config additions
public bool EnableWeatherEnhancement { get; set; } = true;
public float WeatherLPFMinCutoff { get; set; } = 300f;   // Hz, rain fully enclosed
public float WeatherLPFMaxCutoff { get; set; } = 22000f;  // Hz, outdoors
public float WeatherVolumeLossMax { get; set; } = 0.6f;   // Max volume reduction (0-1)
public float HailLPFMinCutoff { get; set; } = 250f;       // Hz, hail fully enclosed (steeper)
public float WindLPFMinCutoff { get; set; } = 600f;       // Hz, wind fully enclosed (higher floor)
public float TrembleLPFMinCutoff { get; set; } = 80f;     // Hz, tremble fully enclosed
public bool DebugWeather { get; set; } = false;
```

**Future consideration**: Material-aware rain contact sounds (Layer 3). Instead of changing LPF curves per material on the global loops, add `Sounds.Ambient` to key block categories (stone → `rain-stone.ogg`, wood → `rain-wood.ogg`, metal → `rain-metal.ogg`) at low ratios. VS's ambient system clusters and positions them automatically. Our existing occlusion processes them. This would create localized "rain hitting my specific roof" sounds layered on top of Layer 1 + Layer 2. See "Alternative Considered" section below for full analysis of why the ambient system can't REPLACE Phase 5 but can complement it.

### Verification — Phase 5A

1. **Basic indoor/outdoor**: Stand outside in rain → full spectrum. Walk into building → bass rumble with muffled highs. The difference should be IMMEDIATELY obvious and satisfying.
2. **Near door**: Stand near open door → moderate LPF (some air paths), louder. Walk deeper → heavier LPF (all occluded), quieter.
3. **Cave entrance**: Stand at mouth looking at rain → LOW LPF (air path, crisp rain), moderate volume. Walk deep → HIGH LPF (material blocks), muffled rumble.
4. **Small room, rain outside wall**: HEAVY LPF immediately (DDA blocked by wall), moderate volume. Correct muffled feeling.
5. **One opening doesn't break everything**: Sealed room + one open door → SkyCoverage still ~0.85, OcclusionFactor depends on air path through door. Not collapsed to 0 like vanilla.
6. **Wind/hail/tremble**: Each gets per-type LPF treatment — wind moderate (broadband), hail steep (high-freq), tremble heavy (already bass). See per-type curves.
7. **Performance**: ~0.1ms every 500ms for enclosure calculation. Zero frame impact.
8. **Debug**: `/soundphysics weather` shows SkyCoverage, OcclusionFactor, vanilla roomVolumePitchLoss for comparison.

### Deliverables — Phase 5A

- [x] `WeatherAudioManager.cs` — Coordinator, weather state cache, tick loop ✅
- [x] `RainAudioHandler.cs` — Rain/wind/hail/tremble replacement sound management + LPF ✅
- [x] `WeatherEnclosureCalculator.cs` — Sky coverage + DDA occlusion (replaces roomVolumePitchLoss) ✅
  - **Implementation Notes**: Multi-height column sampling (4 heights: +1.01, +4, +8, +12)
  - **Optimization**: Two-phase DDA (ground-first with early exit for outdoor case)
  - **Bug Fixes**: Fixed ray origin positioning, reverted to full collision-box DDA for accurate material detection
  - **Prerequisites for 5B**: VerifiedRainPosition list exposed and ready for clustering
- [x] Harmony patches in `WeatherSoundPatches.cs` — Suppress VS weather volumes ✅
- [x] Player ear-level positioning — Uses `player.LocalEyePos` for dynamic height ✅
- [ ] Config additions to `SoundPhysicsConfig.cs` (deferred to 5B)
- [ ] Debug logging when `DebugWeather = true` (deferred to 5B)

**Status**: ✅ COMPLETE — All core functionality implemented and tested. System ready for Phase 5B positional sources.

---

## Phase 5B: Positional Weather Sources (Using Calculator's Verified Openings)

### Goal
When indoors with openings (doors, roof holes, windows), place up to 4-6 positional weather sources at detected entry points. Creates the "rain from the doorway" and "rain through the roof hole" effects that **no Minecraft mod has achieved**.

**Key change from original plan**: Phase 5A's `WeatherEnclosureCalculator` already scans the heightmap and DDA-verifies openings every 500ms. Phase 5B simply **clusters the calculator's `VerifiedOpenings` output** into positional source groups — no separate scanner needed.

### Why Separate RainOpeningScanner Was Merged Into Calculator

The original Phase 5B planned a standalone `RainOpeningScanner` with heightmap scan + DDA + clustering. But the obstruction/occlusion split in Phase 5A requires the SAME data:
- Heightmap sampling → sky coverage (5A) + opening detection (5B)
- DDA verification → occlusion factor (5A) + opening positions (5B)

Running these twice would be wasteful. Instead, `WeatherEnclosureCalculator` computes everything once and exposes `VerifiedOpenings` for 5B's clustering.

### Two-Layer Audio Output

**Layer 1** (from 5A): Non-positional muffled ambient bed
- Always present when raining
- Volume + LPF driven by `roomVolumePitchLoss`
- Represents rain heard diffusely through walls/roof
- `RelativePosition = true` (listener-attached, stereo OK)

**Layer 2** (new): Up to 4-6 positional mono sources at detected openings
- Only active when partially enclosed (not fully outdoors, not fully sealed)
- Each source positioned at or toward a clustered opening
- Higher volume, less LPF than Layer 1 (the contrast creates the effect)
- `RelativePosition = false` (world-positioned, MUST be mono)
- Volume per-source proportional to cluster size + proximity + occlusion clarity

### Data Source: WeatherEnclosureCalculator.VerifiedOpenings

Phase 5A's calculator outputs `VerifiedOpenings` — a list of DDA-verified rain impact positions with clear air paths to the player. Each entry has:
- `WorldPos`: rain impact position (X, rainHeight, Z)
- `Occlusion`: path occlusion value (0 = clear)
- `Distance`: 3D distance to player

Phase 5B **clusters these into source groups** (max 4-6) and places positional audio sources at the cluster centroids.

#### Step-by-Step Walkthrough: Three Scenarios

**Scenario 1: Room with east door + roof hole**
```
    ████████  ░  █████████████  ← 1-block roof hole
    ██                      ██
    ██   YOU                ░░  ← east door, rain outside
    ██                      ░░
    ████████████████████████████
```
1. **Scan**: ~450 columns. Finds rain at ground level outside east door (many columns), rain at floor level under roof hole (1-3 columns), rain on roof above (many columns).
2. **Filter**: Roof rain is `rainY ≈ playerY + 3` → within Y-range (10) → candidate. Ground rain outside door → candidate. Rain on very high areas → filtered out.
3. **DDA Verify**: Roof-top rain = DDA blocked by roof → discarded. East door ground rain = DDA clear → verified. Roof hole floor rain = DDA clear → verified.
4. **Cluster**: East door points cluster together (cluster A, ~8 members). Roof hole points cluster together (cluster B, ~2 members).
5. **Result**: 2 positional sources. Source A toward east door (louder — 8 members). Source B at roof hole (quieter — 2 members).

**Scenario 2: On a pedestal, opening below**
```
    ██████████████████████████
    ██   YOU (on pedestal)  ██
    ██   ████               ██
    ██         ░░░░RAIN░░░░ ██ ← rain enters low wall opening, hits floor
    ██████████████████████████
```
1. **Scan**: Rain impacts at floor level (Y below player). `yDiff = playerY - rainY ≈ 3` → within range.
2. **DDA**: Player on pedestal → clear sightline downward to floor rain impact → verified.
3. **Result**: 1 cluster positioned at the floor-level opening. Player hears rain from below-and-ahead.

**Scenario 3: Ceiling hole 6 blocks lateral**
```
    ████████  ░  █████████████  ← hole at X+6
    ██                      ██
    ██   YOU      💧        ██  ← rain hits floor at X+6
    ████████████████████████████
```
1. **Scan**: Column at (playerX+6) has `rainY = floorY` (rain passes through hole, hits floor).
2. **DDA**: clear LOS inside room from player to floor impact → verified.
3. **Result**: 1 cluster at the hole position. Direction: lateral, not upward. Correct! Player hears rain from the side where the hole is.

### Shared Opening Data: Rain + Hail + Wind

The `RainOpeningScanner` output (opening clusters) is shared across weather types:

| Sound | Uses RainHeightMap openings? | Why / Why not |
|-------|----------------------------|---------------|
| **Rain** | YES — primary use case | Rain falls vertically, RainHeightMap records exactly where it lands |
| **Hail** | YES — same physics | Hail falls vertically, same blocks stop it, same openings |
| **Wind** | YES — as proxy | Openings that let rain in almost always let wind in. Misses: covered porch (rain blocked, wind flows through). Acceptable for v1. |
| **Tremble** | NO — not needed | Low-frequency omnidirectional rumble. Just uses Layer 1 LPF. No positioning benefit. |

**Architecture for sharing**:
```csharp
// In RainAudioHandler.Update():
openingScanner.Update(playerPos, blocks, gameTimeMs);
var openings = openingScanner.Clusters;

// Rain: positional sources at openings
UpdatePositionalRainSources(openings, rainIntensity);

// Hail: same positions, different sound asset
if (hailIntensity > 0)
    UpdatePositionalHailSources(openings, hailIntensity);

// Wind: same positions as proxy (wind enters through same openings)
if (windSpeed > 0.1f)
    UpdatePositionalWindSources(openings, windSpeed);
```

### Layer 2 Integration in RainAudioHandler

```csharp
private const int MAX_POSITIONAL_SOURCES = 4; // Pool of reusable sources
private ILoadedSound[] positionalRainSources = new ILoadedSound[MAX_POSITIONAL_SOURCES];

private void UpdatePositionalRainSources(
    IReadOnlyList<OpeningCluster> openings, float rainIntensity)
{
    if (openings.Count == 0 || enclosure < 0.15f) // Outdoors or near-outdoors
    {
        StopAllPositionalSources();
        return;
    }
    
    // Assign sources to clusters (up to MAX_POSITIONAL_SOURCES)
    int activeCount = Math.Min(openings.Count, MAX_POSITIONAL_SOURCES);
    
    for (int i = 0; i < MAX_POSITIONAL_SOURCES; i++)
    {
        if (i < activeCount)
        {
            var cluster = openings[i];
            EnsurePositionalSourcePlaying(i);
            
            // Volume: cluster size × clarity × intensity, scaled by distance
            float sizeWeight = Math.Min(cluster.MemberCount / 8f, 1f); // 8+ columns = max
            float clarityWeight = 1f - cluster.Occlusion;
            float distWeight = 1f / (1f + cluster.Distance * 0.15f);
            float volume = rainIntensity * sizeWeight * clarityWeight * distWeight * 0.7f;
            
            // LPF: less filtered than Layer 1 (these are openings, not walls)
            // But still some filtering based on path occlusion
            float layer2LPF = CalculateLPFCutoff(cluster.Occlusion * 0.5f);
            
            positionalRainSources[i].SetPosition(cluster.Position);
            positionalRainSources[i].SetVolume(volume);
            EfxHelper.SetDirectLowpass(
                GetSourceId(positionalRainSources[i]), layer2LPF);
        }
        else
        {
            // No cluster for this slot — fade out
            StopPositionalSource(i);
        }
    }
}
```

### Edge Cases

| Scenario | roomVolumePitchLoss | Opening Clusters | Layer 1 | Layer 2 |
|----------|-------------------|-----------------|---------|---------|
| **Fully outdoors** | ≈ 0.0 | Many (but enclosure too low) | Full volume, no LPF | Disabled (already in rain) |
| **Open door** | 0.04-0.09 | 1 cluster (door columns) | Light LPF | 1 source toward door |
| **Door + roof hole** | varies | 2 clusters | LPF from enclosure | 2 sources: door + hole |
| **Diagonal corner** | varies | 1 cluster (found by grid scan) | LPF from enclosure | 1 source toward diagonal |
| **Pedestal, low opening** | varies | 1 cluster (below player Y) | LPF from enclosure | 1 source below/ahead |
| **Roof hole 6 blocks away** | 0.00-0.01 | 1 cluster (floor impact) | Minimal LPF | 1 source toward hole |
| **Fully enclosed** | 1.0 | 0 clusters (all DDA blocked) | Bass rumble, heavy LPF | Disabled |

### Audio Asset Requirement

Layer 2 MUST use mono sources for proper 3D spatialization. Options:
1. **Extend AudioLoaderPatch**: Add `sounds/weather/` to stereo-to-mono conversion (already handles music)
2. **Ship mono versions**: Include pre-converted mono .ogg files in mod assets
3. **Runtime downmix**: Convert on load (already have the algorithm from music conversion)

**Recommendation**: Option 1 (extend existing patch) — least maintenance, already proven.

### Performance

- **RainHeightMap scan**: ~450 array lookups every 500ms = trivial (VS does 256/tick for particles)
- **DDA verification**: 12 raycasts × ~10 blocks each = ~0.02ms every 500ms
- **Clustering**: Greedy over ≤12 points = trivial
- **OpenAL voices**: 4-6 extra positional sources = negligible
- **Total Phase 5B overhead**: ~0.03ms every 500ms. Effectively zero.

### Verification — Phase 5B

1. **Door test**: Stand in room with one open door during rain → rain sounds directional from the door. Walk to opposite wall → rain clearly comes from door direction.
2. **Close door**: Close the door → Layer 2 fades, only muffled Layer 1 remains.
3. **Roof hole**: Building with hole in roof → rain heard from direction of the hole.
4. **Door + roof hole**: Two distinct directional rain sources simultaneously.
5. **Diagonal opening**: Rain from a corner opening at 45° → detected and positioned correctly.
6. **Below-player opening**: On raised platform, low wall opening → rain audible from below.
7. **Distant ceiling hole**: Hole 6 blocks lateral in ceiling → directional rain from the side.
8. **L-shaped room**: Rain at open hole on one side, walk around corner → source persists via `OpeningTracker`, `AudioPhysicsSystem` repositions sound to appear from around the corner with increasing LPF. Walk back → smooth transition to direct path.
9. **Hail through same openings**: Hail sound positioned at same opening clusters as rain. (Deferred to v2 — architecture ready)
10. **Wind through openings**: Wind sound directional from same detected openings. (Deferred to v2)
11. **Fully outdoors**: No Layer 2 artifacts — just normal rain.
12. **Performance**: FPS unchanged with scanner active.

### Deliverables — Phase 5B

- [x] Opening clustering logic (`OpeningClusterer.cs` — greedy clustering of `VerifiedOpenings` from calculator) ✅
- [x] Opening persistence tracker (`OpeningTracker.cs` — L-shaped room fix, hysteresis-based removal) ✅
- [x] Update `RainAudioHandler.cs` — Multi-source pool management (4 positional sources, configurable) ✅
  - **Key design**: Positional sources routed through `AudioPhysicsSystem` pipeline (not self-managed)
  - **L-shaped room solution**: Sources persist via `OpeningTracker`, `AudioPhysicsSystem` handles
    occlusion + repositioning around corners via existing bounce-ray + `SoundPathResolver` pipeline
  - **Volume ownership**: `RainAudioHandler` owns base volume + lifecycle, `AudioPhysicsSystem` owns LPF + position
  - **Layer ducking**: `positionalContribution` based on average audibility of active sources
- [x] Stereo-to-mono conversion for weather sounds (extended `AudioLoaderPatch` with `ForceMonoNextLoad` flag) ✅
- [x] Config: `EnablePositionalWeather`, `MaxPositionalSources`, `OpeningPersistenceSeconds`, `PositionalMinSkyCoverage`, `PositionalWeatherVolume`, `DebugPositionalWeather` ✅
- [x] `WeatherAudioManager` integration: clustering + tracker + handler wiring ✅
- [ ] Shared opening data for hail + wind positional sources (deferred — rain-only for v1, same architecture extensible)
- [ ] In-game testing and tuning

### Known Issues & Review Notes — Phase 5B

1. **FIXED: `OpeningClusterer.consumed` overflow** — Static `bool[32]` array would `IndexOutOfRangeException` if `VerifiedOpenings` exceeded 32 entries. The defensive branch cleared but didn't grow the array. Fixed: array now grows dynamically when `count > consumed.Length`. In practice, `VerifiedOpenings` is capped at 15 by `WeatherEnclosureCalculator.MAX_DDA_CANDIDATES`, so this was a theoretical-only bug.

2. **MONITOR: Stereo→mono downmix caching concern** — `ForceMonoNextLoad` relies on `OggDecoder.OggToWav` being called per-load (not cached per-asset). If VS caches the decoded `AudioMetaData` for `rain-leafy.ogg` after Layer 1 loads it as stereo, Layer 2's ForceMonoNextLoad flag won't fire and the positional source will be stereo (OpenAL won't spatialize it). Music downmixing uses the exact same mechanism and works correctly, suggesting VS re-decodes per load. **Verify in-game**: if positional sources don't appear directional, this is the likely cause. Fallback: ship pre-converted mono .ogg assets or create mono OpenAL buffers manually.

3. **Thread safety**: `ForceMonoNextLoad` is `[ThreadStatic]` and sound loading is main-thread (VS's `ClientMain.LoadSound`). Reset-after-use pattern has both inline reset (in AudioLoaderPatch Postfix) and defensive reset (in RainAudioHandler after `LoadSound` call). Safe for single-threaded sound loading.

4. **`Span<bool>` usage**: `OpeningTracker.Update()` uses `stackalloc bool[clusterCount]` which requires .NET 7+ (VS 1.19+ targets .NET 7). Safe for current target.

---

## Phase 5C: Thunder Positioning ✅ IMPLEMENTED

**Achievement**: Two-layer thunder system reusing Phase 5B opening detection. Harmony prefix on `WeatherSimulationLightning.ClientTick` fully replaces VS ambient thunder with positioned/LPF'd version. Postfix on `LightningFlash.ClientInit` adds Layer 2 at openings for bolt strikes. Three operation modes: OUTDOOR (direct 3D positioning toward bolt/random sky), INDOOR WITH OPENINGS (Layer 1 muffled rumble + Layer 2 crack at best opening), FULLY ENCLOSED (Layer 1 heavy LPF only).

**Design change from plan**: Dropped Mode B sky search (16-ray hemisphere). Instead reuses Phase 5B's `TrackedOpenings` from `OpeningTracker` — already validated, cached, handles L-shaped rooms. Thunder events score openings by cluster weight + proximity, with bolt direction bias for strike thunder (Issue 15 overhead fix included).

### VS Thunder Architecture (Reference)

**System 1: Ambient Thunder Rumble** (`WeatherSimulationLightning.ClientTick()`):
| Trigger | Asset | Cooldown |
|---------|-------|----------|
| `distantLightningRate` | `lightning-distant.ogg` | None |
| `nearLightningRate` (75%) | `lightning-near.ogg` | 5s |
| `nearLightningRate` (25%) | `lightning-verynear.ogg` | 10s |

- Played at `(0, 0, 0)` via `PlaySoundAt`, `EnumSoundType.Weather`
- VS applies `deepnessSub`: `player.Y / seaLevel` + `roomVolumePitchLoss * 0.5f`
- **Our prefix fully replaces this** — re-implements the random roll logic, routes through `ThunderAudioHandler`

**System 2: Lightning Bolt Strike** (`LightningFlash.cs`):
| Distance | Asset | Volume Curve |
|----------|-------|--------------|
| < 150 blocks | `lightning-verynear.ogg` | `1 - dist/180` |
| 150-200 blocks | `lightning-near.ogg` | `1 - dist/250` |
| 200-320 blocks | `lightning-distant.ogg` | `1 - dist/500` |
| < 200 blocks | `lightning-nodistance.ogg` | `pow(max(0, 1-dist/200), 1.5)` (Delayed crack layer) |

- Strike position known: `origin + points[last]`
- ALL sounds played at `(0, 0, 0)` — volume-scaled only, no 3D positioning
- **Our postfix ADDS Layer 2** at the best opening biased toward the bolt direction

### Implementation Architecture

```
ThunderAudioHandler
├── PlayAmbientThunder(asset, vol, pitch, openings, earPos, sky, occl)
│   ├── OUTDOOR (sky < 0.3): PlayOutdoorThunder -> 3D positioned random sky direction
│   ├── INDOOR + OPENINGS: PlayLayer1Rumble (LPF) + PlayLayer2AtBestOpening (one-shot)
│   └── FULLY ENCLOSED: PlayLayer1Rumble only (heavy LPF)
├── PlayBoltThunder(boltPos, distance, openings, earPos, sky)
│   ├── Matches vanilla thresholds for main asset: verynear (<150m), near (<200m), distant (<320m)
│   ├── OUTDOOR: PlayOutdoorBoltThunder -> 3D positioned toward bolt (rolloff=0, linear vol curve)
│   └── INDOOR + OPENINGS: PlayLayer2AtBestOpening (bolt-direction-biased scoring)
├── Layer 1: Shared EFX filter (GenFilter once), gainHF from CalculateThunderGainHF()
│   └── Quadratic curve: gainHF = (1 - occl)^2, clamped to [minCutoff, 1.0]
├── Layer 2: Uses PositionalSourcePool.PlayOneShot() -> routed through AudioPhysicsSystem
│   └── DDA occlusion + repositioning around corners + reverb applied automatically
├── Crack Layer (Delayed): nodistance.ogg spawned via OnGameTick 50ms after bolt (<200m)
│   └── Realistic HF atmospheric falloff curve: pow(1-dist/200, 1.5)
└── ManagedThunderSound tracking: list with 15s max lifetime, tick-based cleanup

Opening Scoring (Layer 2 placement):
├── Ambient: weight * (1 - dist/scanRadius) -- cluster size + proximity
├── Bolt: dotProduct * weight * (1 - dist/scanRadius) -- adds bolt direction bias
└── Issue 15 fix: when bolt is overhead (abs(dirY) > 0.7), reduce dot weight
    so horizontal openings still score well for overhead bolts
```

### Harmony Patches (Thunder)

**Prefix on `WeatherSimulationLightning.ClientTick`** (in `WeatherSoundPatches.cs`):
- Returns `bool` — `false` to skip VS's original (we handled it), `true` on reflection failures (fallback)
- Re-implements VS's random roll logic: distant + near thunder with proper cooldowns
- Sets `lightningTime` + `lightningIntensity` on instance for visual flash effects
- Routes through `ThunderAudioHandler.PlayAmbientThunder()`
- Preserves VS's `deepnessSub` calculation for volume/pitch

**Postfix on `LightningFlash.ClientInit`** (in `WeatherSoundPatches.cs`):
- Reads bolt position: `origin + points[last]`
- VS sounds still play (implicit Layer 1 for bolt strikes)
- Calls `ThunderAudioHandler.PlayBoltThunder()` to add Layer 2 at openings
- Range check: skips bolts > 320 blocks away

### Edge Cases

| Scenario | Opening State | Sound Output |
|----------|---------------|--------------|
| Outdoors, storm | No openings (outdoor gate) | Direct 3D positioned toward bolt/random sky |
| Near window facing bolt | Openings tracked | L1 muffled rumble + L2 crack from opening |
| Deep interior, no openings | Fully enclosed | L1 heavy LPF bass rumble only |
| Underground cave | No openings | Very quiet rumble (high occlusion + high deepnessSub) |
| Overhead bolt, horizontal opening | Openings tracked | Issue 15 fix: dot weight reduced, opening still scores |

### Performance

Per-thunder-event (NOT per-frame):
- No additional raycasting — reuses Phase 5B's already-cached `TrackedOpenings`
- Opening scoring: O(n) where n = tracked openings (typically 3-8)
- Thunder events are rare (cooldown 5-10s, usually 1-2 per storm)
- Total cost: negligible

### Verification — Phase 5C

1. **Outdoor thunder**: Lightning strikes → thunder comes from bolt direction (3D positioned)
2. **Indoor near opening**: Opening toward storm → thunder crack from opening + muffled rumble
3. **Deep interior**: Heavy storm → only bass rumble, no directional cue, heavy LPF
4. **Bolt vs ambient**: Bolt strikes have clear directionality. Ambient picks best opening or random sky
5. **Overhead bolt**: Issue 15 — horizontal opening still gets Layer 2 for overhead bolts

### Deliverables — Phase 5C

- [x] `ThunderAudioHandler.cs` — Full Layer 1 + Layer 2 + outdoor positioning (~600 lines)
- [x] Harmony patches in `WeatherSoundPatches.cs` — ClientTick prefix + ClientInit postfix
- [x] Config: `EnableThunderPositioning`, `DebugThunder`, `ThunderLPFMinCutoff`, `ThunderLayer1Volume`, `ThunderLayer2Volume`, `MaxThunderSources`, `ThunderCooldownMs`
- [x] Wiring in `WeatherAudioManager.cs` — `SetThunderHandler()` call + status display
- [x] ~~SkySearchUtil.cs~~ — Not needed, reuses Phase 5B openings
- [x] ~~RayDistribution extraction~~ — Not needed

---

## Implementation Order Summary

```
Phase 5A: Rain & Weather LPF Replacement + Enclosure Calculator
├── WeatherEnclosureCalculator.cs (sky coverage + DDA occlusion)
├── WeatherAudioManager.cs (coordinator, uses calculator)
├── RainAudioHandler.cs (suppress VS + play own + LPF driven by two metrics)
├── Harmony patches (suppress VS weather volumes)
└── Config additions
    ↓
Phase 5B: Positional Weather Sources (Cluster Calculator's Openings)
├── Opening clustering logic (from calculator's VerifiedOpenings)
├── Update RainAudioHandler (multi-source pool, shared opening data)
├── Positional sources for rain, hail, wind at detected openings
└── Stereo-to-mono conversion for weather sounds
    ↓
Phase 5C: Thunder Positioning ✅
├── ThunderAudioHandler.cs (Layer 1 rumble + Layer 2 at openings + outdoor positioning)
├── Harmony patches (ClientTick prefix + ClientInit postfix) in WeatherSoundPatches.cs
├── Config additions (7 new entries)
├── WeatherAudioManager wiring (SetThunderHandler + status)
└── Bolt direction bias + Issue 15 overhead fix
```

---

## Code References Quick Index

| What | File | Lines |
|------|------|-------|
| `GetDistanceToRainFall` (BFS) | `VintagestoryLib.decompiled.cs` | 73072-73109 |
| `roomVolumePitchLoss` calc | `WeatherSimulationSound.cs` | 196-200 |
| `roomVolumePitchLoss` applied to rain | `WeatherSimulationSound.cs` | 220-229 |
| `roomVolumePitchLoss` applied to tremble | `WeatherSimulationSound.cs` | 228-229 |
| `roomVolumePitchLoss` applied to hail | `WeatherSimulationSound.cs` | 252-256 |
| `roomVolumePitchLoss` applied to wind | `WeatherSimulationSound.cs` | 320 |
| `deepnessSub` (thunder, uses loss*0.5) | `WeatherSimulationLightning.cs` | 92 |
| Glass rain ambient (`BlockRainAmbient`) | `BlockGlassPane.cs` | 7-26 |
| Glass slab rain ambient | `BlockSlabSnowRemove.cs` | 89-97 |
| Ambient sound scan (block iteration) | `VintagestoryLib.decompiled.cs` | 153719-153750 |
| Ambient sound playback + positioning | `VintagestoryLib.decompiled.cs` | 150668-150720 |
| LightningFlash (bolt strikes) | `LightningFlash.cs` | 51-87 |
| Weather sound init (all assets) | `WeatherSimulationSound.cs` | 57-133 |
| `RainHeightMap` update | `VintagestoryLib.decompiled.cs` | 73022-73028 |
| `GetRainMapHeightAt` (array lookup) | `VintagestoryLib.decompiled.cs` | 72931 |
| `RainPermeable` block property | `VintagestoryLib.decompiled.cs` | 3655 |
| LightningFlash (bolt strikes) | `LightningFlash.cs` | 51-87 |
| Lightning rumble (ambient) | `WeatherSimulationLightning.cs` | 86-136 |
| Rain particles (DieOnRainHeightmap) | `WeatherSimulationParticles.cs` | 137, 352-353 |
| SPR skips (0,0,0) sounds | `SoundPhysics.java` | 211 |
| SPR rate-limits weather to 0 | `SoundRateConfig.java` | 111-113 |

---

## Alternative Considered: Block-Level Ambient Rain System

**Idea**: Patch all blocks to use `BlockRainAmbient` behavior (return rainfall intensity when rain-exposed). VS's existing ambient sound scanner would cluster nearby rain-exposed blocks into positional 3D sources. Our existing Phase 1-4 occlusion system would handle LPF. This would replace the entire Phase 5.

**Why it doesn't work as a Phase 5 replacement** (analyzed 2026-02-10):

| Issue | Detail |
|-------|--------|
| **Layer 1 vanishes** | Deep indoors (loss=1.0): zero rain-exposed blocks nearby → zero ambient sources → total silence. No bass rumble, no "rain on roof" feeling. Layer 1 exists for this case. |
| **Occlusion bypass** | Ambient sounds created via `LoadSound()+Start()`, never hit our `PlaySoundAt` hooks. Would need entirely new Harmony patches on `SystemPlayerSounds` pipeline — more hooks than Phase 5A, not fewer. |
| **Section-based clustering** | Fixed spatial chunks, not opening-aware. Roof + ground-outside can merge into one source. No "door opening" vs "roof hole" distinction. |
| **OpenAL source budget** | Hundreds of rain-exposed blocks → dozens of section clusters → dozens of OpenAL sources. Hardware soft limit ~32-64. |
| **Block patching fragility** | Must Harmony-patch base `Block.GetAmbientSoundStrength()` + dynamically set `Sounds.Ambient` on all blocks during load. Fragile across mod conflicts and VS updates. |

**What it IS good for**: Material-specific rain contact sounds as a **Layer 3** complement (Phase 6+). Add `Sounds.Ambient` to a few key block categories (stone, wood, metal, thatch) with material-specific rain assets. Low block-count ratios keep source count manageable. VS clusters and positions them automatically; our occlusion processes them. Creates localized "rain on my specific roof" character layered on top of the global Layer 1 + opening-based Layer 2.

---

## Research Context

### AAA Game Audio Reference (Feb 2026 Research)

Every professional implementation uses the **Two-Layer Pattern**:
1. **Layer 1**: Non-positional ambient bed (diffuse, material-agnostic rain feeling)
2. **Layer 2**: 3D spatialized spot emitters at openings/surfaces

**Key findings**:
- Wwise Rooms & Portals model maps directly to our Phase 5B approach (rain as outdoor Room Tone propagating through Portals/openings)
- LPF is the #1 differentiator between "quieter rain" and "indoor rain" — volume reduction alone is insufficient
- AAA games use 5-8 active surface emitters — we support up to 4-6 clustered opening sources
- No Minecraft mod solves rain-through-doorway — this is novel territory
- Rain changes slowly → 250-500ms cache intervals are standard practice
- Our RainHeightMap grid scan approach is more robust than raycast probes — it finds openings at any angle, below player level, and at any distance within scan radius
