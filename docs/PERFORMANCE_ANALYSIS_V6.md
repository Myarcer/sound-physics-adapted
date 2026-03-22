# SPA Performance Analysis — v6 (2026-03-22)

## Summary

After optimization pass, SPA dropped from **22.2% → 10.9%** of main thread time.
Zero 50ms+ lagspikes (was 17.7% of ticks). Average tick **22.8ms → 9.7ms**.

## Comparison (90s captures, heavily modded server)

| Metric | v4 (before) | v6 (after) | Change |
|--------|------------|------------|--------|
| SPA total (OnOcclusionUpdateTick) | 20,013ms (22.2%) | 9,815ms (10.9%) | **-51%** |
| DDA TraverseCore calls | 8,642 | 4,006 | **-54%** |
| GetBlock (VS engine) | 13,016ms / 6,594 calls | 6,410ms / 3,255 calls | **-51%** |
| Regex.IsMatch | 4,257ms | 1,808ms | **-58%** |
| GetBlockEntity | 3,087ms | 599ms | **-81%** |
| GetPartialBlockVolumeScale | 3,116ms | 581ms | **-81%** |
| ProbeForOpenings | 6,144ms | 3,480ms | **-43%** |
| Avg tick duration | 22.8ms | 9.7ms | **-57%** |
| Worst spike | 129ms | 44ms | **-66%** |
| >50ms spikes | 155 (17.7%) | **0 (0.0%)** | **ELIMINATED** |
| >30ms spikes | 277 (31.6%) | 16 (1.6%) | **-95%** |
| <=16ms (within 60fps frame) | 545 (62.1%) | 811 (80.4%) | **+30%** |

## Optimizations Applied (this session)

1. **MaxOcclusion 10→4** — exp(-4)=1.8% volume, functionally inaudible. DDA stops 60% sooner.
2. **MaxDDASteps=32** — hard cap prevents 60+ step rays through open air.
3. **MaxTickBudgetMs=8** — time-based tick budget, primary spike guard.
4. **MaxSoundsPerTick 25→10, MaxOverdue 6→3** — conservative count caps.
5. **GetOcclusion per-block-ID cache** — eliminates regex after warmup.
6. **IsSolidForOcclusion composite cache** — single byte[] lookup per block ID.
7. **IsMultiblockDoorSpacer prefix cache** — avoids string ops in DDA hot path.
8. **StringComparison.Ordinal everywhere** — eliminated ICU interop overhead.
9. **Material enum Dictionary pre-cache** — avoids ToString().ToLowerInvariant() per call.
10. **BLOCK_CACHE_SIZE 8192→16384** — covers heavily modded servers.

## Remaining Hotspots (v6 profile)

### 1. ProbeForOpenings — 3,480ms (3.87%), 1,177 calls
- **What**: Scans perpendicular to blocked paths to find openings (doors, windows).
- **Why expensive**: Each probe fires multiple DDA rays in cardinal directions.
- **Potential**: See `docs/PROBE_FOR_OPENINGS_PERF_ANALYSIS.md` for 6 optimization ideas.
- **Estimated savings**: 30-50% of its cost (1-2s) with spatial caching or reduced ray count.
- **Risk**: Medium — affects sound repositioning quality through openings.

### 2. GetBlock (VS engine) — 6,410ms (7.12%), 3,255 calls
- **What**: Vintage Story's block accessor, called once per DDA step.
- **Why expensive**: Chunk lookup + block ID resolution. ~2ms average per call.
- **Potential**: Cannot optimize the call itself (engine code). Can only reduce call count.
  - **Idea A**: Block lookup cache per-tick (key: x,y,z → Block). Many DDA rays share blocks.
    ~30-40% of GetBlock calls may be redundant across rays for the same sound.
  - **Idea B**: Skip GetBlock for blocks already classified as air in recent traversals.
    Would need a spatial bitfield (expensive memory) or LRU cache.
  - **Idea C**: Use `GetBlockId(BlockPos, int layer)` instead of `GetBlock(BlockPos)` for
    the initial air check. BlockId=0 means air, avoiding full Block object resolution.
    This is lighter than GetBlock but needs testing for correctness with mod blocks.
- **Estimated savings**: 15-30% with per-tick block cache (idea A).
- **Risk**: Low for idea A (pure cache), medium for idea C (behavioral change).

### 3. Regex/GetOcclusion warmup — 1,808ms (2.01%), 1,020 calls
- **What**: First-time block type lookups run 60+ compiled regex patterns.
- **Why**: ~1,175 unique block IDs in modded world, each needs one regex pass.
- **Current mitigation**: Per-block-ID cache eliminates all repeat calls.
- **Potential**: Replace regex with prefix trie or sorted prefix array for O(log n) lookup.
  Regex.IsMatch on 60 patterns = O(60*n) per miss. Trie would be O(k) where k=pattern length.
- **Estimated savings**: 50-70% of warmup cost (0.9-1.3s).
- **Risk**: Low — pure algorithmic improvement, no behavioral change.

### 4. OcclusionCalculator.RunOcclusion visitor lambda — 3,178ms (3.53%)
- **What**: The per-block callback inside DDA traversal.
- **Breakdown**: IsSolidForOcclusion check → GetBlockOcclusion → partial block path.
- **Potential**: Already well-optimized with caches. Main cost is the sheer number of calls.
  Further gains come from reducing DDA step count (items 1-2 above).

### 5. AcousticRaytracer.RaycastToSurface — 1,567ms (1.74%), 823 calls
- **What**: Finds first solid block along a ray direction (for bounce/reflection).
- **Why**: Uses FindFirstBlock → TraverseDirection → TraverseCore. Each call is a DDA ray.
- **Potential**: Apply MaxDDASteps cap to TraverseDirection as well (currently only applied
  to Traverse). Bounce rays don't need to search >16 blocks.
- **Estimated savings**: 20-30% if capped at 16 steps.
- **Risk**: Low — bounces beyond 16 blocks are inaudible anyway.

### 6. Weather system — 520ms (0.58%)
- **What**: WeatherAudioManager + WeatherEnclosureCalculator.
- **Status**: Negligible. No action needed.

## Config Defaults Summary (new vs old)

| Setting | Old | New | Reason |
|---------|-----|-----|--------|
| MaxOcclusion | 10.0 | 4.0 | exp(-4)=1.8% — inaudible past 4 blocks |
| MaxDDASteps | (none) | 32 | Hard cap on ray length |
| MaxTickBudgetMs | (none) | 8.0 | Time-based spike guard |
| MaxSoundsPerTick | 25 | 10 | Count cap (time budget is primary) |
| MaxOverdueSoundsPerTick | 6 | 3 | Reduces overdue bypass spikes |
| BLOCK_CACHE_SIZE | 8192 | 16384 | Modded server support |

## Priority for Next Optimization Pass

1. **ProbeForOpenings** — highest ROI, analysis doc exists
2. **Block lookup cache** — moderate ROI, reduces GetBlock calls
3. **Regex → prefix trie** — reduces warmup cost
4. **RaycastToSurface step cap** — small but easy win
