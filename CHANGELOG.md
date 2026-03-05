# Changelog

All notable changes to Sound Physics Adapted will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.7] - 2026-03-05

### Added
- Config migration system for seamless upgrades between versions
- Acoustic boundary detection with adaptive EMA smoothing
- Cave exit detection via march-along probe rays
- Player-centric DDA heights for weather below player elevation
- Thunder enclosure system (independent from VS deepnessSub)
- Thunder range extended to 1000 blocks with natural falloff and speed-of-sound delay
- Lightning sound override and rumble RNG variety
- ConfigLib integration for optional in-game settings GUI
- Medieval Expansion mod compatibility (doors, gates, and spacer blocks)
- Universal door/gate detection for modded blocks

### Fixed
- Server-side persistence, CarryOn detection, and pause/resume desync
- Block self-occlusion at spawn boundaries
- Mono downmix for sounds at local player position (preserves L/R panning)
- Underwater music pitch getting stuck on water exit
- Reverb through walls now muffled instead of silent (SPR-style cutoff)
- Repositioned sounds no longer over-muffled (inverted occlusion floor/ceiling)
- Indoor thunder cracks use dedicated LPF instead of volume hack
- Gate/door occlusion guarded behind world-ready checks (fixes multiplayer join freeze)
- Opened gates/doors with spacer blocks no longer block sound

### Changed
- Reverb cache redesigned with composite key (soundCell + playerCell)
- Debug flags consolidated to top of config
- Smoother audio transitions via weighted trimmed mean and tuned EMA
- Rain uses averaged nearest 9 columns instead of single nearest
- Thunder asset thresholds rescaled to match bolt distribution

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
