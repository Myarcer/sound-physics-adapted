# Changelog

All notable changes to Sound Physics Adapted will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
