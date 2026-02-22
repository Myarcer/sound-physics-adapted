# Phase 6: Polish & Performance — PLANNED

## Status: PLANNED (renumbered from old Phase 7)

**Last Updated**: 2026-02-10
**Depends On**: Phases 1-5 (core features complete)
**Note**: Old Phase 6 (Thunder & Lightning) was absorbed into Phase 5C. Old Phase 7 (Advanced Features) is now Phase 6.

---

## Overview

Phase 6 contains polish features and performance optimization. These are independent of each other and can be implemented in any order.

---

## 6A: Air Absorption

High frequencies attenuate faster over distance in air. This adds subtle realism to distant sounds.

```csharp
// OpenAL EFX air absorption — single line per source
AL.Source(sourceId, ALSourcef.EfxAirAbsorptionFactor, config.AirAbsorptionFactor);
```

- `EfxAirAbsorptionFactor`: 0.0 (disabled) to 10.0 (heavy absorption)
- Default: ~1.0 (realistic air absorption at sea level)
- Effect: Distant sounds lose treble naturally, close sounds unaffected
- Implementation: Add to `EfxHelper`, apply based on sound distance

### Config
```csharp
public bool EnableAirAbsorption { get; set; } = true;
public float AirAbsorptionFactor { get; set; } = 1.0f;
```

---

## 6B: Performance Optimization

### LOD System
Fewer rays for distant sounds:
```csharp
int rayCount = distance < 8 ? 32 : distance < 20 ? 16 : 8;
```

### Result Caching
Cache occlusion for static/slow sources:
- Block changes invalidate cache (VS provides block change events)
- Moving sources (entities) recalculate each cycle
- Static sources (beehives, water) cache until block change

### Spatial Hashing
Pre-compute block solidity in spatial hash for fast lookups during DDA traversal.

### Background Threading
Move heavy calculations off main thread:
- Reverb calculation already amortized
- Occlusion calculation could be threaded with proper synchronization
- Weather (Phase 5) already uses VS's background thread for BFS

### Performance Target
- **< 1ms per sound** for occlusion + reverb + repositioning
- **< 0.1ms per tick** for weather system overhead (Phase 5)

---

## 6C: Edge Case Hardening

### Minimum Occlusion Aggregation
For sounds near corners/edges, use minimum occlusion across offset rays:
```csharp
// Sound takes the easiest path — minimum of several probes
float[] offsets = { -0.3f, 0f, +0.3f };
float minOcclusion = float.MaxValue;
foreach (var offset in offsets)
{
    float occ = CalculateOcclusion(soundPos + Vec3d.Up * offset, playerPos);
    minOcclusion = Math.Min(minOcclusion, occ);
}
```

Prevents sounds from being over-muffled when the direct path clips a corner by 0.1 blocks.

---

## Deliverables

- [ ] Air absorption via OpenAL EFX
- [ ] LOD system for ray counts by distance
- [ ] Result caching for static sources
- [ ] Minimum occlusion aggregation for corners
- [ ] Performance profiling and optimization pass
- [ ] Performance target: <1ms per sound verified
