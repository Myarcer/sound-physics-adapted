# Changelog

All notable changes to Sound Physics Adapted will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
