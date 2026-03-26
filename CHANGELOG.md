# Changelog

All notable changes to Sound Physics Adapted will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.9.0] - 2026-03-22

### Added
- Per-tick time budget (8ms default) — prevents lagspikes from complex raycasts in dense geometry; remaining sounds deferred to next tick
- DDA step hard cap (MaxDDASteps=32) — long-distance rays through open air no longer walk 60+ blocks wastefully
- Block occlusion cache — caches GetOcclusion result per block ID, eliminates redundant lookups (cache size 16384)
- IsSolidForOcclusion composite cache — single lookup replaces repeated FirstCodePart + material + property checks
- IsMultiblockDoorSpacer prefix cache — avoids repeated string operations in DDA hot path

### Changed
- MaxOcclusion default lowered from 10.0 to 4.0 — 4.0 already produces functionally inaudible results (1.8% volume); the extra 6 units wasted DDA steps for zero perceptual benefit
- MaxSoundsPerTick lowered from 25 to 10 — time budget is now the primary spike guard; count cap is a secondary safety net
- MaxOverdueSoundsPerTick lowered from 6 to 3 — combined with time budget prevents burst processing spikes
- StringComparison.Ordinal used everywhere instead of default culture-sensitive comparisons
- DDA visitor hot path reordered for early exits on most common block types
- Existing user configs auto-migrated to new performance defaults (only if values match old defaults)

### Fixed
- Sound repositioning at >25m behind walls — bounce-based offset skipped when too far to prevent nonsensical placement
- Sound repositioning offset exceeding player-to-sound distance — rejected to prevent sounds jumping behind the listener

## [0.1.8] - 2026-03-19

### Added
- Volume-scaled occlusion for chiseled & partial blocks — carved blocks occlude sound proportional to their actual remaining volume instead of being treated as fully solid
- Thatch/sod roofing occlusion — thatch roofs, sod roofs, and hay bales now properly block rain and sound (previously near-transparent due to Plant material classification)
- Decorative block transparency — toolracks, torchholders, lanterns, candles, signs, firepits, anvils, paintings, clutter, ground storage, and similar non-structural blocks no longer block sound
- Berry bush & Wildcraftfruit mod support — proper foliage-level occlusion for all vanilla and Wildcraftfruit berry bush variants
- Wildgrass mod compatibility — wildgrass blocks no longer treated as solid walls
- Thunder pitch variety — distant thunder cracks pitch-shift lower based on distance; all thunder gets random pitch variation per strike
- Config auto-migration — saved material configs automatically pick up new block overrides on version upgrade

### Fixed
- Sound flickering near walls — coherence check prevents sound repositioning from flip-flopping direction at wall edges
- Permeated sound jumping — when no open paths exist, sound stays at original position instead of jumping wildly between offsets
- Distant sound muffling too slow — EMA smoothing now compensates for update interval differences so far sounds transition at the same perceived rate as close sounds
- Reverb cache oscillation at range boundary — dead-zone prevents cache key flip-flopping for sounds at ~45 blocks distance
- Volume wobble at budget cutoff — throttle fade now freezes when it detects rapid oscillation, unfreezes after stabilizing
- Chiseled blocks over-muffling — routed through collision-box path instead of solid fast path, so carved shapes reflect actual geometry
- Rain wobble under porches/overhangs — decorative blocks on walls no longer cause intermittent occlusion spikes as player moves
- Weather DDA start-position detection — proper step-back test instead of always skipping first block
- Fences/glass rain occlusion — thin geometry now applies correct material occlusion instead of near-zero AABB estimate
- Config initialization crash — duplicate key in defaults crashed material config loading
- Multiblock door spacers causing phantom occlusion — upper blocks of vanilla 2×3 gates (and similar multi-block doors) no longer register as solid wood in DDA raycasts

## [0.1.7] - 2026-03-05

### Added
- Medieval Expansion mod compatibility (doors, gates, and spacer blocks)
- Universal door/gate detection for modded blocks (portcullis, etc.)
- Wind sources now positioned at ceiling height for sky openings instead of floor level
- Ceiling height inference for wind placement (searches nearby roof geometry)
- Wind debug visualization (magenta blocks at inferred ceiling height)
- World-ready gate and warmup system — defers raycasting until world is fully loaded

### Fixed
- Multiplayer join freeze caused by raycasting against incomplete block accessor
- Opened gates/doors with spacer blocks no longer block sound (Medieval Expansion)
- Solid-face fast path now correctly skipped for open interactable blocks
- Reverb and occlusion deferred during world load instead of applied immediately

## [0.1.6.1] - 2026-03-02

### Fixed
- Resonator state not saving to chunk on server (ToTreeAttributes/FromTreeAttributes patches now applied server-side)
- Client-server desync when only client has mod installed
- Carry On mod detection missing on server side
- Tooltip now shows correct key binding based on Carry On presence

## [0.1.6] - 2026-03-02

### Added
- Thunder & lightning overhaul: dedicated enclosure system, 1000-block range with realistic falloff
- Rumble volume variety (0.2–1.0x RNG), indoor cracks muffled with aggressive LPF (500Hz floor)
- Thunder distance thresholds rescaled to match bolt distribution, raised source limits (L1:12, L2:20)
- March-along probe rays for cave exit detection, player-centric DDA heights for weather below player
- SoundSourceAdjuster for door Y-position correction and multiblock placeholder resolution
- Rain position averaging across nearest 9 columns instead of single nearest
- Config migration system for seamless upgrades between versions
- ConfigLib integration for optional in-game settings GUI

### Fixed
- Sounds at player position losing stereo (no longer forced to mono downmix)
- Spawn-time position fingerprinting prevents self-occlusion on player-emitted sounds
- Per-sound range used for reverb attenuation (removed MaxSoundDistance hard gate)
- Occlusion floor/ceiling inversion that made repositioned sounds too muffled
- Music pitch getting stuck after exiting water

### Changed
- Tuned adaptive EMA for more realistic acoustic transitions

## [0.1.5] - 2026-02-25

### Added
- Reverb cache redesign with composite key (soundCell + playerCell) — auto-invalidates on player movement
- Close sounds use 2-block player cells (responsive), far sounds use 8-block cells (stable)
- Acoustic boundary detection via SharedAirspaceRatio — sounds near corners/doorways get every-tick updates

### Changed
- Adaptive EMA smoothing scales alpha by change magnitude (large: 0.70/150ms, medium: 0.55/200ms, small: 0.25)
- Corner transitions reduced from ~1s to ~300ms with no discontinuities

### Fixed
- Filter discontinuity when sound crossed occ<1.0 threshold into skipRepositioning branch
- Capped max EMA alpha at 0.70 to prevent single-tick LPF pops

## [0.1.4] - 2026-02-22

### Added
- Runtime API for other mods to configure material overrides, occlusion, and reflectivity
- Door/multiblock sound source adjustment for correct occlusion
- Lava-specific sound filter configuration
- Wind sound exemption from reverb processing

### Fixed
- Resonator lifecycle handling (orphaned tracks and duplication)
- Throttle churn at budget boundary (distance hysteresis)
- Thunder sound placement underground/underwater (direction clamping)
- Weather source spawning through closed doors

## [0.1.3] - 2026-02-09

### Added
- Sound override system (custom sound assets replacing vanilla)
- Beehive sound override as first implementation
- CarryOn mod compatibility patches
- Sound repositioning jumps when walking around corners

### Changed
- Resonator patches refactored into consolidated file

## [0.1.2] - 2026-02-08

### Added
- Sound repositioning with smoothing and hysteresis
- DDA block traversal for reverb raycasting

### Fixed
- Sound repositioning jumps and audio artifacts
- Filter detach bugs during state transitions

## [0.1.1] - 2026-02-07

### Added
- Weather audio integration (rain, wind, hail positional sources)
- Thunder audio handler with direction-aware placement
- Enclosure-based weather muffling

### Changed
- Performance optimization for raycast operations

## [0.1.0] - 2026-02-06

### Added
- Initial release
- Raycast-based sound occlusion through walls
- Dynamic reverb from cave/room geometry
- Material-aware sound filtering (wood, stone, metal, etc.)
- Configurable sound physics settings
- Harmony patch system for audio interception
