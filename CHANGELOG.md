# Changelog

All notable changes to Sound Physics Adapted will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.2.5] - 2026-07-02

### Fixed
- **Indoor weather audio no longer cuts out.** Rain/wind heard through windows and doorways used to go silent the moment you walked deeper into a building (a structural check falsely marked the opening as sealed, then a timeout deleted it). Openings now stay tracked while quiet or occluded and their sources fade naturally — and swell back when you return. Louder openings still take over the limited source slots from quieter ones.
- Fixed a create/destroy loop that recreated silenced weather sources every tick (audio churn, wasted CPU).
- Walking indoors during rain: the ambient rain bed now hands over smoothly to positional window/door sources — no dropout while the positional sources spin up, no double volume.
- Fixed audio glitches after leaving and rejoining a world (stale state reset).
- Distance-model settings could fail to re-apply when the game recycled a sound source.

### Performance
- No more raytrace on sound start, compiled OpenAL calls, zero-allocation weather processing — less stutter when many sounds start at once.

## [0.2.4] - 2026-04-27

### Added
- **Distance Model overrides** — per-source OpenAL attenuation tuning (default ON):
  - `SoundRangeMultiplier` (default `1.4`): scales `AL_MAX_DISTANCE`. Sounds carry farther — safe to extend now that real occlusion prevents wall bleed-through.
  - `AirAbsorptionFactor` (default `1.0`): EFX `AL_AIR_ABSORPTION_FACTOR` per source. Distant sounds lose treble naturally (deeper thunder, muffled distant footsteps). `0.0` = vanilla.
  - `DistanceRolloffFactor` (default `1.0`): scales `AL_ROLLOFF_FACTOR` for curve shaping.
  - `DistanceModelExcludeMusic` (default `true`): music sound types skip the overrides.
  - Master toggle: `EnableDistanceModelOverrides` (default `true`).
- Applied universally on every sound start via `SoundStartPostfix`. Idempotent per source — safe with re-attachments.

### Changed
- Mod is now `requiredOnClient: true` so clients auto-download from ModDB when joining a server that has it. Server-side remains optional (`requiredOnServer: false`) — server can run without the mod and clients with it still get all clientside patches.

## [0.2.3] - 2026-04-22

### Changed
- Versioning switched to 3-segment SemVer (was 4-segment) to match Vintage Story's parser. No more `Failed parsing version string` warning at mod load.
- Distant thunder crack (`nodistance.ogg`) is now noticeably deeper and quieter at long range:
  - `ThunderCrackPitchMin` default lowered `0.35` → `0.22` (deeper bass tail at >500m)
  - Far-distance crack volume curve reshaped: ~40-50% quieter beyond 400m vs old curve. Close-range (≤100m) volume unchanged.
  - 100-400m: now `0.75 → 0.21` (was `0.75 → 0.35`)
  - 400-1000m: now `0.21 → 0.05` (was `0.35 → 0.10`)

## [0.2.2.5] - 2026-04-22

### Changed
- Updated for Vintage Story 1.22.0 — minimum game version bumped from 1.21.0 to 1.22.0
- `EnumBlockMaterial.Liquid` references migrated to `EnumBlockMaterial.Water` (renamed upstream in 1.22)
- Default material config keys renamed `liquid` → `water` (occlusion + reflectivity sections)

### Compatibility
- Existing user `soundphysicsadapted_materials.json` files using the old `liquid` key continue to work — the loader transparently maps `liquid` onto the new `Water` material when no `water` key is present

## [0.2.2.3] - 2026-04-03

### Added
- Face-sampled ambient volume occlusion — ambient sounds (water, lava, beehives) now use multi-face sampling to determine the least-occluded bbox face center as the acoustic origin, replacing the old averaged-position method that produced buggy interior points and unstable occlusion
- Proximity center blend — as the player approaches an ambient volume, the acoustic position blends toward the player for an immersive enveloping effect with smooth panning transition at boundaries
- Bbox-excluding DDA methods (`CalculatePathOcclusionExcludingBboxes`, `CalculateExcludingBboxes`) — occlusion rays now skip blocks inside the ambient volume's own bounding boxes, preventing self-occlusion artifacts
- Median-of-9-rays occlusion for ambient face centers — robust to both DDA corner-clipping (occ=2 outliers) and edge-slipping (occ=0 outliers), producing stable wall counts (1 wall→1.0, 2 walls→2.0)
- Face hysteresis with distance and raw occlusion tiebreakers — prevents L/R flip-flop when faces have similar clarity; perpendicular paths (occ=1) preferred over diagonal paths (occ=2+)
- EMA temporal smoothing on acoustic position (α=0.15, ~300ms convergence) — damps face-switching jitter

### Fixed
- Reverb leak through occluded sources — 3 SPR-style fixes prevent reverb from bleeding through fully muffled walls
- Beehive sound override domain — moved from `survival:` to `game:` domain so the override actually matches the vanilla sound key
- Point-source ambients (resonators) no longer misclassified as bbox volumes — `SoundType.Ambient` sounds without bbox data now fall through to normal repositioning with probe rays instead of being stuck with no repositioning
- Exclusion state leak safety — `try/finally` around `RunOcclusion` in bbox-excluding DDA ensures static exclusion state is always cleared even if an exception occurs
- Unclamped early return in `CalculateExcludingBboxes` — now consistently capped to `MaxOcclusion`

### Performance
- `GetReflectivity` cached per block ID — eliminates redundant material lookups in `ProbeForOpenings`
- `Vec3d`/`Random` allocations eliminated from `ProbeForOpenings` hot path

### Config
- `SoundOverrides` defaults to `false` (was `true`) — custom sound replacements opt-in only

## [0.2.2.1] - 2026-03-27

### Fixed
- Pre-existing block entity sounds (querns, forges, beehives) that started during world loading were permanently invisible to the occlusion system — they played at full volume through any number of walls. Now queued during startup and retroactively registered with correct occlusion once warmup completes

### Performance
- DDA rays now early-abort at the inaudibility threshold instead of continuing to `MaxOcclusion=32` — saves ~80% of DDA steps for entombed sounds behind thick walls. Threshold is derived from `MinLowPassFilter` and `BlockAbsorption` (default: ~3.8 blocks of stone = inaudible, material-aware)

## [0.2.2] - 2026-03-27

### Added
- Diffraction floor (rebuilt from scratch) — the old bOcc system that let entombed sounds bleed through many walls is replaced with a physics-grounded floor using bounce ray data. When 2+ open bounce paths and meaningful shared airspace are detected (indicating a real L-corridor or corner path), the LPF floor is raised based on simplified UTD/Maekawa diffraction (~8-10dB per 90° bend). Entombed sounds cannot benefit — both the open path count and >5% shared airspace requirements block wall-leaking
- Static sound cache — sounds that haven't moved skip raycasts entirely. Automatically bypassed for 1s after any block change (door open/close, break/place) to prevent step-down artifacts. Toggle via `EnableStaticSoundCache` config option
- `bocc` debug visualization mode — shows bOcc LOS path quality (green=clear, yellow=partial, orange=heavy, red=blocked)

### Changed
- Occlusion absorption multiplier reduced from ×3 to ×2 — less aggressive per block, more natural muffling curve through thick walls
- `MaxOcclusion` default raised from `4.0` to `32.0` — the previous cap of 4 blocks caused sounds behind 4+ walls to clamp at the same filter level regardless of additional walls; 32 gives full headroom across the realistic range

### Fixed
- Tall door (2-3 block) self-occlusion — multiblock upper halves no longer push the sound source above the door into ceiling/wall blocks causing false occlusion
- Stationary sound debug gap — `FORCE_REFRESH_MS` now bypasses `RateLimitedLog` so sounds that haven't moved still log correctly

### Performance
- Extracted `RunOcclusion` lambda to a static method, eliminating closure allocations on every tick
- Zero-cost debug log guards added to all hot-path files — no string formatting overhead when debug logging is off
- Reuse `multiRayOcclusion` result instead of redundant DDA recalculation

### Config
- `MaxDiffractionFilter` (default `0.35`) — caps diffraction relief at ~9dB, realistic for a single 90° corner bend
- `MinDiffractionOcclusion` (default `0.3`) — minimum occlusion on diffracted paths (~8dB), prevents unrealistically transparent corners
- `EnableStaticSoundCache` (default `true`)
- Config bumped to v4, material config to v8 — outdated configs auto-regenerate from fresh defaults; no legacy migration chains

## [0.2.1] - 2026-03-26

### Fixed
- Door occlusion — closed doors now properly muffle sound; open doors are fully transparent; thin panels that miss AABB ray tests get override-based occlusion directly
- Weather DDA door separation — dual-accumulator separates Layer 1 ambient muffling (doors count) from 5B spawn detection (doors transparent), so rain sources spawn behind closed doors but still muffle correctly
- DDA partial-block destination — player inside a partial block (slab, ladder, etc.) no longer gets blanket skip; AABB check determines actual occlusion contribution
- DDA sky coverage — step-back test replaces legacy heuristic, preventing false sky readings through solid overhangs
- Glass pane TreatAsFullCube migration — glass panes no longer promoted to full-cube occlusion during config version upgrade

## [0.2.0] - 2026-03-25

### Added
- Direct path gain (AL_LOWPASS_GAIN) — SPR-style gain formula for more natural wall muffling alongside frequency filtering
- Debug visualization — `.soundphysics viz` / `.sp viz` shows occlusion rays, bounce paths, and reverb slots as colored wireframes
- Diffraction HF darkening — sounds around corners lose treble proportional to diffraction angle
- Auto-scaled foliage occlusion — bushes and foliage scale by actual block volume instead of flat overrides
- Weather column memory — verified rain/wind columns persist and only recheck on block changes

### Fixed
- Linux EFX compatibility — probes real auxiliary send count, remaps reverb for 2-send devices
- Reverb filter gain — SetLowpassGainHF was clobbering direct gain on reverb sends
- Knapping/crafting occlusion — knapping surfaces, loose stones, flints, ores, clayforming no longer block sound
- Beams in weather — treated as weather-transparent instead of blocking rain sources
- Positional sources playing as stereo on rejoin — warmup unified with IsWorldReady so spatial audio is applied correctly
- Sound repositioning wobble — EMA smoothing on target damps dual-path oscillation
- Stale sound cleanup — orphaned sources detected and removed
- Bounce offset accuracy — normal offset 0.15 to 0.01

### Performance
- All Harmony patch hooks gated behind IsWorldReady startup check
- Small decorative objects auto-detected instead of hardcoded overrides
- DDA debug logs batched per ray

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
