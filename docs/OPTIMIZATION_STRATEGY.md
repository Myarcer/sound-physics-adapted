# AudioPhysicsSystem - Optimization Strategy

## Design Philosophy

Sound Physics Remastered (Minecraft) ships with: distance culling + per-tick rate caps + a
chunk cache that goes stale for ~1 second. No priority queue, no occlusion weighting,
no velocity tracking. They process sounds synchronously with simple throttles and it works.

Our system follows the same philosophy: **simple distance intervals + static cache +
block change invalidation.** The block change hook puts us ahead of SPR, which has no
way to detect broken walls -- their users just wait up to 1 second.

No over-engineering. No occlusion-weighted buckets. No velocity-aware scaling.
Three distance buckets, a movement cache, and a block change hook. That's it.

---

## How It Works

### Every Tick (50ms)
All active sounds are iterated. For each sound:
1. **Resolve position** -- `AudioRenderer.GetStoredPosition()` then `Params.Position` fallback
2. **Interval gate** -- skip if not due yet (distance-based)
3. **Static cache** -- skip if nothing moved (movement threshold + force-refresh timer)
4. **Raycast** -- `OcclusionCalculator.Calculate()` + apply filter via `AudioRenderer`

### Distance Intervals
| Bucket | Distance | Interval | Raycasts/sec (per sound) |
|--------|----------|----------|--------------------------|
| Close  | 0-10m    | 50ms     | 20 (every tick)          |
| Near   | 10-30m   | 200ms    | 5                        |
| Far    | 30m+     | 500ms    | 2                        |

Close sounds update every single tick. Walking around a corner or opening a door
next to you = instant response. Far sounds update twice a second, still responsive
enough that you'd never notice the delay on a distant waterfall.

### Static Cache
If player moved < 0.25 blocks AND sound moved < 0.25 blocks since last raycast,
skip the raycast. The `LastRaycastTimeMs` timer is separate from the interval timer
and only resets on actual raycasts. Force-refresh after 2 seconds catches any
geometry changes we missed.

### Block Change Invalidation
`InvalidateCache()` resets `LastRaycastTimeMs = 0` for all cached sounds. This
doesn't force 20 raycasts in one tick -- each sound still waits for its interval.
Result: close sounds re-raycast within 50ms, far sounds within 500ms.

**Triggers**: player breaks/places a block, opens a door, interacts with a block.

### Sky Probe
5 upward rays every 500ms. All clear = outdoor, reverb rays drop from 32 to 8.

---

## What's Implemented vs TODO

### Implemented (AudioPhysicsSystem.cs)
- [x] Distance-based intervals (3 buckets)
- [x] Static cache with separate raycast timer
- [x] Force-refresh timer (2 seconds)
- [x] `InvalidateCache()` method
- [x] Position resolution from AudioRenderer stored positions
- [x] Sky probe
- [x] No round-robin starvation (all sounds checked every tick)
- [x] **Hook `capi.Event.BlockChanged`** to call `InvalidateCache()`
  - Reference: `capi.Event.BlockChanged += new BlockChangedDelegate(OnBlockChanged)`
  - Signature: `void OnBlockChanged(BlockPos pos, Block oldBlock)`
  - Unhook in `Dispose()`
  - Debouncing implemented: 200ms

### TODO for Next Agent
- [ ] **Test scenarios**:
  - Walk around corner near beehive (should be instant)
  - Stand still, break wall (should update within 50ms after invalidation)
  - Open door to room with fire (same)
  - Check `.soundphysics acoustics` stats in-game

---

## Performance Budget

**Worst case**: 20 active sounds, player walking constantly, all sounds close.
- 20 raycasts per tick = 400 raycasts/second
- Each raycast ~100us average = ~40ms/second = **4% of frame budget at 60fps**

**Typical case**: 10 sounds, mixed distances, standing still half the time.
- ~3-5 raycasts per tick (close sounds + interval-gated others)
- Static cache skips most = ~60-100 raycasts/second
- **< 1% of frame budget**

**After block change**: all caches invalidated, burst of raycasts over next 500ms
as each sound hits its interval. Self-distributing, no single-tick spike.

---

## Comparison with SPR (Minecraft)

| Feature | SPR | Us | Winner |
|---------|-----|----|--------|
| Update when sound starts | Yes | Yes (LoadSoundPatch) | Tie |
| Distance intervals | 512-block hard cutoff only | 3 buckets (10/30/30+m) | Us |
| Block change detection | None (1s stale cache) | `InvalidateCache()` on event | Us |
| Static cache | Level clone, refreshes every 20 ticks | Per-sound, movement threshold | Tie |
| Moving sound throttle | Hash-based, every 5 ticks | Interval-based | Tie |
| Thread safety | ClonedClientLevel for off-thread | Main thread only | SPR |
| Complexity | ~200 lines of optimization | ~250 lines | Similar |

---

## File Map
| File | Role |
|------|------|
| `AudioPhysicsSystem.cs` | Update loop, intervals, cache, sky probe |
| `AudioRenderer.cs` | Per-sound OpenAL filters, position storage |
| `OcclusionCalculator.cs` | DDA raycast + multi-ray voting |
| `SoundPhysicsAdaptedModSystem.cs` | Tick handler, BlockChanged hook (TODO) |
| `LoadSoundPatch.cs` | Harmony patches for sound lifecycle |
