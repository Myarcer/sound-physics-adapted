# Performance Options: Improvement Roadmap

Analysis of current performance config design gaps and proposed improvements.
Simple/uninvasive fixes were applied in v0.1.2. This document tracks the remaining work.

---

## Applied in v0.1.2 (Simple Fixes)

- **`ThrottleProtectedRadius` default: 32 → 12** — The old default of 32 effectively disabled
  the throttle in most practical scenarios (farm animals, village mobs). At 32 blocks the throttle
  only competed for sounds at the absolute edge of hearing range. 12 blocks targets the outer shell
  where distance-based eviction actually makes sense.

- **`MaxSoundsPerTick` comment** — Added tick-rate context ("VS runs at 20 ticks/second, default
  25 = 500 sounds/sec max throughput") so players can reason about the number.

- **`MaxOverdueSoundsPerTick` comment** — Clarified that real per-tick max =
  `MaxSoundsPerTick + MaxOverdueSoundsPerTick` (31 by default), which was previously invisible.

- **`WeatherTickIntervalMs` moved to Performance section** — Was buried in the Weather section
  despite being a CPU load tuning knob. Now grouped with other performance options.

---

## Remaining Improvements

### 1. `SoundUpdateIntervalTicks` — Re-raytrace frequency per sound

**Priority: High**

Currently each sound re-runs its full raycasting every tick it's within budget. There's no option
to say "only re-raytrace each sound every N ticks."

**Problem**: The biggest CPU cost is the raycasting itself. For ambient sounds (birds, wind, distant
water) that are not moving and whose occlusion doesn't change, re-tracing every tick is wasteful.

**Proposed addition:**
```csharp
/// <summary>
/// How many ticks between full raytrace updates per sound.
/// 1 = every tick (maximum accuracy, default).
/// 2 = every other tick (halves raytrace CPU with minimal perceptible lag).
/// 3-4 = for slow-paced singleplayer with many ambient sounds.
/// </summary>
public int SoundUpdateIntervalTicks { get; set; } = 1;
```

**Implementation**: In `AudioPhysicsSystem` update loop, skip sounds whose
`lastUpdateTick + SoundUpdateIntervalTicks > currentTick` (unless overdue).

**Impact**: Halving this from 1→2 roughly halves raytrace CPU in steady state.

---

### 2. `ReverbCellSize` — Configurable reverb cache grid size

**Priority: Medium**

`EnableReverbCellCache` groups nearby sounds into a 4x4x4 block grid to share reverb
calculations. The cell size is hardcoded. Users have no way to trade quality for performance here.

**Proposed addition:**
```csharp
/// <summary>
/// Block size of each reverb cache cell.
/// Sounds within the same cell share reverb calculations.
/// 2 = high quality (less sharing), 4 = default, 8 = high performance (more sharing).
/// Only used when EnableReverbCellCache=true.
/// </summary>
public int ReverbCellSize { get; set; } = 4;
```

**Implementation**: Replace hardcoded `>> 2` shift in `ReverbCellCache` with a configurable
divisor derived from `ReverbCellSize`.

---

### 3. Physics LOD — Distance-based raycast simplification

**Priority: Medium**

All sounds get the same raycast quality regardless of distance. A sound at 60 blocks costs the
same as one at 5 blocks, even though the player can barely hear the distant sound.

**Proposed additions:**
```csharp
/// <summary>
/// Distance (blocks) beyond which sounds switch to simplified single-ray occlusion.
/// Within this radius: full 9-ray soft occlusion (OcclusionVariation applies).
/// Beyond this radius: single center ray only — faster but harder shadow edges.
/// 0 = disable LOD (all sounds always use full quality).
/// Default 24 — full quality for nearby sounds, simplified for distant ones.
/// </summary>
public float SimplifiedPhysicsDistance { get; set; } = 24f;
```

**Implementation**: In `OcclusionCalculator`, check distance before building the 9-ray offset
grid. If beyond threshold, skip offset variation and cast single ray only.

**Impact**: Significant for large open areas with many distant sounds (forests, farms, coastlines).

---

### 4. Collapse/expose the two-budget interaction

**Priority: Low**

`MaxSoundsPerTick` and `MaxOverdueSoundsPerTick` are two interacting numbers that both need
tuning together. The hardcoded 2-second "overdue" threshold is invisible to users but critically
affects how the two numbers interact at low budget settings.

**Option A**: Expose the overdue threshold:
```csharp
/// <summary>
/// Seconds before an un-updated sound is considered overdue and gets priority processing.
/// Default 2.0. Increase if CPU is low and you want older updates to wait longer.
/// Decrease for more responsive occlusion updates at the cost of higher CPU spikes.
/// </summary>
public float OverdueThresholdSeconds { get; set; } = 2.0f;
```

**Option B**: Collapse to a single quality tier with internal split:
Replace both `MaxSoundsPerTick` and `MaxOverdueSoundsPerTick` with a single
`SoundProcessingBudget` and internally always reserve 20% for overdue sounds.

Option A is less invasive; Option B is cleaner UX.

---

### 5. Performance Presets

**Priority: Low**

Raw numbers are opaque to end users. A preset system would let players pick quality tiers without
needing to understand the underlying scheduler architecture.

**Proposed config:**
```csharp
/// <summary>
/// Performance preset. Overrides manual settings when not "Custom".
/// "Low"    — MaxSoundsPerTick=8,  MaxConcurrentSounds=20, no LOD, ReverbCellSize=8
/// "Medium" — MaxSoundsPerTick=25, MaxConcurrentSounds=40, LOD at 24, ReverbCellSize=4
/// "High"   — MaxSoundsPerTick=50, MaxConcurrentSounds=64, LOD at 48, ReverbCellSize=2
/// "Custom" — Uses all individual settings below.
/// </summary>
public string PerformancePreset { get; set; } = "Custom";
```

**Implementation**: On config load, if preset != "Custom", override the relevant fields before
the rest of the system reads them.

---

## Summary Table

| Improvement | Effort | Impact | Priority |
|---|---|---|---|
| `SoundUpdateIntervalTicks` | Small (skip logic in update loop) | High (direct CPU halving) | High |
| `ReverbCellSize` | Small (replace hardcoded shift) | Medium | Medium |
| Physics LOD (`SimplifiedPhysicsDistance`) | Medium (branch in OcclusionCalculator) | Medium-High | Medium |
| Expose `OverdueThresholdSeconds` | Trivial | Low-Medium | Low |
| Performance Presets | Medium (preset loading on startup) | UX only | Low |
