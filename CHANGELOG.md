# Changelog

This file lists the changes to Sound Physics Adapted.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.2.6-dev.6] - 2026-08-19

### Performance

Measured with two 90 second profiles of a rainy session, one before the changes
and one after the block-lookup pass. The mod went from 4465 ms to 3372 ms of
main-thread time, and the median occlusion tick went from 3.0 ms to 1.5 ms. Of
the 4465 ms, 3056 ms went into block lookup, not into acoustics. The changes
below remove that overhead. No sound changes: every ray reads the same block
ids and returns the same values.

- **The mod reads blocks through a chunk-caching accessor.** Each step of a ray
  reads one block, and the standard accessor takes two locks for each of those
  reads. The new accessor keeps the last two chunks and reads them directly, so a
  ray that stays inside one chunk takes no lock. This also gives time back to the
  render thread and the chunk thread, which share those locks.
- The weather enclosure scan uses the same accessor.
- The occlusion rays no longer build two temporary vectors for each offset ray.
- The mod asks once for each block type whether a sound position needs the door
  adjustment, instead of searching the block name for every sound on every tick.
- The mod does not measure the reverb at your position while you stand still. The
  result cannot change until you move or a block near you changes.
- Some debug messages were built even with debug off. They are not any more.
- **A flat wall settles the nine-ray measurement after five rays.** The mod uses
  the median of nine rays. When five rays agree, the median cannot leave their
  band, so the four rays that are left are not run. The result differs from the
  full measurement by at most 0.0001 occlusion units, which is below hearing.
- **The sealed-cavity search stops at the first opening it finds.** The search
  proves that a heavily occluded sound sits in a sealed cavity. In an open cave
  it explored the full search volume and proved nothing. Air past the search
  radius already ends the proof, so the search now stops there. The search also
  marks visited blocks in a flat array instead of a hash set, so each step got
  cheaper too. The second profile showed this search at 18 percent of the mod.
- **A far water, lava or rain volume now measures at the rate of its distance.**
  These volumes follow the player, so the mod held every one of them at the 50 ms
  rate, at any distance. Past 20 blocks the measured position barely moves per
  player step, so volumes beyond that now use the normal 200 ms and 500 ms rates.
  The face measurement of these volumes was 45 percent of the mod in the first
  profile, and rain pays this cost on every surface it wets.

### Fixed
- **Shorter stutter after you place or break a block.** A block change marks every
  sound for a new measurement, and those measurements had no time limit for the
  rest of the tick. They now stop at 20 ms. A sound that does not fit is measured
  on the next tick, before the others.

## [0.2.6-dev.5] - 2026-08-16

### Fixed
- **Rain no longer sounds like metal on a wooden trapdoor.** The rain impact list held the
  pattern `trapdoor`. The mod matches a pattern as a prefix, so the pattern also caught the
  wooden trapdoors and the legacy trapdoors. The list now holds the two metal styles only,
  `trapdoor-plate` and `trapdoor-bars`.
- **`.sp toggle` gives the game back to vanilla audio.** The toggle stopped new work only.
  A sound that already played kept the filter, the reverb, the pitch and the moved position
  of the mod. Rain was the worst case. It stayed at its last level and no longer got quieter
  when you walked inside. The toggle now restores every live sound. It stops the weather and
  thunder sounds of the mod, and it gives the reverb, the weather, the underwater filter and
  the distance attenuation back to the game.
- The toggle also reacts to a change in the ConfigLib window or in the config file. Before,
  it reacted to the chat command only.

### Changed
- **A torch on a torch holder is 20 percent louder.** The default volume of the crackle goes
  from 0.35 to 0.42.
- **One smoothing stage for each thing you hear.** The filter, the reverb and the position of
  a sound move toward their new value in one place, every 25 ms. Before, two steps smoothed
  the same value at rates that changed with the distance, so the real speed matched no
  written number.
- A far sound reacts as fast as a near sound. Its reverb needs the same time. Only the
  measurement is less frequent.
- The filter moves through the loudness in equal steps. A transition no longer runs fast at
  the loud end and slow at the quiet end.

### Performance
- The mod writes the reverb of a sound only while the reverb moves. A sound in a room that
  does not change costs no reverb work.
- The mod does not measure a throttled sound. Its fade continues in the audio step.

### Configuration
- **The mod upgrades an old config file. It does not replace it.** Both config files go to
  version 11. The mod keeps every value you set and changes only what this release changed.
  It replaces the `trapdoor` pattern, and it raises the torch volume to 0.42 if your file
  still holds the old default of 0.35. A value that you edited stays. A file older than
  version 10, or newer than the mod, still falls back to fresh defaults.

### Known limits of the toggle
- The game applies a replaced sound file and an added block ambient sound when it loads the
  assets. The mod silences the added block ambient sounds when you turn it off. A replaced
  sound file stays until you restart the game.
- A sound that the mod mixed down to mono stays mono until the game loads that asset again.
  A new sound loads in stereo, as vanilla does.

### Internal
- `AudioPhysicsSystem.ProcessSoundRaycast` is now `UpdateSoundAcoustics` with
  `ResolveAcousticPaths` and `ResolveRepositioning`. New files: `Core/SmoothingCurves.cs`,
  `Core/ThrottleFadeState.cs`, `Core/AmbientVolumeResolver.cs`, `Core/FilterPipeline.cs`.
- `SoundPhysicsAPI` and the weather system read the occlusion from the applied filter gain,
  so the value includes the airspace, opening and diffraction floors.

## [0.2.6-dev.4] - 2026-08-14

### Changed
- Both config files use one version number, and the number starts again at 10. The mod
  replaces both files with fresh defaults at the first start after the update. Write your own
  values down before you update.
- Sound file overrides are on by default. The better beehive sound plays without a change to
  the configuration. The louder lightning sound stays off. Set `OverrideLightningSound` to
  true if you want it.

## [0.2.6-dev.3] - 2026-08-14

This build joins the two development lines. Both lines keep all their work.

### Fixed
- **A sound is no longer sealed off when your ear sits inside a block.** The check stopped at
  your own block when that block counted as solid, for example a snow layer or a bed. A sound
  next to you then played dry and heavily muffled.

### Changed
- The guard against duplicate distance model work uses one design again.

## [0.2.6] - Unreleased

### Fixed
- **A resonator that starts while you join a world is no longer loud and flat.** A sound that
  starts in the join warmup kept its stereo buffer, and OpenAL does not position a stereo
  source. A resonator 100 blocks away played at full volume through walls and held the vanilla
  music back. The downmix to mono no longer waits for the end of the warmup. This also repairs
  block sounds that start while chunks load, such as the quern, the forge and the beehive.
- **A resonator track no longer plays wide open for the first second.** The mod gives the
  sound its position and its filter when the track loads, before playback starts.
- A downmix request that the mod cannot serve is no longer discarded without a trace. The
  request stays open and the debug log records the reason.
- **A solid block at your ear no longer muffles all sounds.** The occlusion ray skips your own
  block when it is fully solid. Snow layers, chiseled blocks and walls counted as a wall in
  front of every sound.
- **A mod that asks the API from another thread gets a safe answer.** `SoundPhysicsAPI` calls
  code that runs on the main thread only. A call from another thread now returns passthrough
  values and writes one warning.
- **A sound behind a doorway is no longer almost silent.** The mod moved the sound to the
  opening, but the muffle floor ignored that path.
- A door or a block change near you starts a weather rescan at once. Rain and wind at an
  opening react faster.
- One blocked ray to the sky, from a branch or a roof edge, no longer reads as indoors.
- Reverb cache entries age out from your current position, not from the position where the mod
  created them.
- A downgrade with a newer config file regenerates the config file.

### Performance
- **A block change no longer clears the whole reverb cache.** The mod invalidates the changed
  cells only. Mining and building cost much less raycast work.
- **The mod finds a fully sealed space with a cheap flood fill** and skips the full raytrace.
  The sound plays dry and heavily muffled, as before, at a fraction of the cost.
- The reverb estimate at sound start reads the cache without a change to the statistics.
- The occlusion ray and the reverb raytracer no longer allocate memory per step or per bounce.
- The freeze heartbeat log runs in DebugMode only.
- The mod no longer creates an unused reverb slot on a device with exactly three auxiliary
  sends.

## [0.2.5] - 2026-07-02

### Fixed
- **Indoor weather audio no longer cuts out.** Rain and wind through a window or a doorway
  went silent when you walked deeper into a building. An opening now stays tracked while it is
  quiet or occluded, and its sound fades. The sound swells again when you return. A louder
  opening still takes the limited source slots from a quieter one.
- The mod no longer recreates a silenced weather source every tick.
- When you walk indoors in rain, the ambient rain hands over to the window and door sources.
  There is no dropout and no double volume.
- Audio faults after you leave a world and join it again are gone. The mod resets its state.
- The distance model settings apply again after the game recycles a sound source.
- Two block sounds in the same tick no longer play at the position of the other sound.
- A sound that starts on the music thread no longer races the raycasts.
- A volume fade no longer freezes when the budget throttles the sound.

### Changed
- A build from source needs the .NET 10 SDK (Vintage Story 1.22 and later).

### Performance
- No raytrace at sound start, compiled OpenAL calls, and weather work without allocation. Less
  stutter when many sounds start together.

## [0.2.4] - 2026-04-27

### Added
- **Distance model overrides**, on by default. They tune the attenuation of each sound.
  - `SoundRangeMultiplier` (1.4): a sound carries farther. Real occlusion stops the bleed
    through walls.
  - `AirAbsorptionFactor` (1.0): a far sound loses treble. Thunder is deeper and far footsteps
    are duller. Use 0.0 for vanilla.
  - `DistanceRolloffFactor` (1.0): shapes the attenuation curve.
  - `DistanceModelExcludeMusic` (true): music keeps the vanilla model.
  - `EnableDistanceModelOverrides` (true): the master toggle.

### Changed
- The mod is `requiredOnClient`. A client downloads it from the ModDB when it joins a server
  that has it. The server side stays optional.

## [0.2.3] - 2026-04-22

### Changed
- The version has three segments. The game parses it without a warning.
- The distant thunder crack is deeper and quieter. `ThunderCrackPitchMin` goes from 0.35 to
  0.22. Beyond 400 m the crack is 40 to 50 percent quieter. Below 100 m the volume does not
  change.

## [0.2.2.5] - 2026-04-22

### Changed
- The mod supports Vintage Story 1.22. The minimum game version goes from 1.21.0 to 1.22.0.
- The game renamed the material `Liquid` to `Water`. The default config keys follow the game.

### Compatibility
- A config file with the old `liquid` key still works. The loader maps the key onto `water`
  when the file has no `water` key.

## [0.2.2.3] - 2026-04-03

### Added
- **An ambient volume sounds stable.** Water, lava and beehives use the least occluded face of
  their box as the sound origin. The averaged position produced points inside the geometry.
- The sound position blends toward you as you come close to an ambient volume. The pan stays
  smooth at the border.
- An occlusion ray skips the blocks inside the box of the volume. A volume no longer occludes
  itself.
- The mod takes the median of nine rays per face. The wall count is stable.
- A face keeps its choice unless another face is clearly better. Left and right no longer
  flip.
- Time smoothing on the sound position damps the rest of the jitter.

### Fixed
- Reverb no longer leaks through a fully muffled wall.
- The beehive sound override uses the `game:` domain, so it matches the vanilla sound.
- A point ambient sound, such as a resonator, is no longer handled as a box volume.
- The mod always clears the exclusion state, also after an exception.
- An early return in the box-excluding raycast respects `MaxOcclusion`.

### Performance
- The mod caches the reflectivity per block and allocates nothing in the opening probe.

### Config
- `SoundOverrides` is off by default. A custom sound replacement is opt-in.

## [0.2.2.1] - 2026-03-27

### Fixed
- **The mod now occludes a quern, a forge or a beehive that started during world load.**
  These sounds were invisible to the mod and played at full volume through any wall. The mod
  queues them and registers them when the warmup ends.

### Performance
- An occlusion ray stops at the threshold of inaudibility. A sound behind a thick wall costs
  about 80 percent fewer steps.

## [0.2.2] - 2026-03-27

### Added
- **A new diffraction floor.** A sound around a corner needs two open bounce paths and shared
  airspace before it gets relief. The relief follows a simple diffraction model, about 8 to
  10 dB for a 90 degree bend. A sealed sound gets no relief, so it cannot leak through walls.
- **A cache for a sound that does not move.** The sound skips its raycasts. Any block change
  bypasses the cache for one second. Toggle: `EnableStaticSoundCache`.
- The debug view has a `bocc` mode. It shows the quality of the line of sight.

### Changed
- The absorption multiplier per block goes from 3 to 2. A thick wall muffles more naturally.
- `MaxOcclusion` goes from 4.0 to 32.0. Four walls and eight walls no longer sound the same.

### Fixed
- A tall door no longer occludes its own sound.
- A sound that does not move writes its debug log correctly.

### Performance
- No allocation per tick in the occlusion ray. No string work when debug logging is off. The
  mod reuses the multi-ray result instead of a second raycast.

### Config
- `MaxDiffractionFilter` (0.35) caps the relief at a corner at about 9 dB.
- `MinDiffractionOcclusion` (0.3) keeps a corner from a fully open sound.
- `EnableStaticSoundCache` (true).
- The config goes to v4 and the material config to v8. An outdated file regenerates from
  fresh defaults.

## [0.2.1] - 2026-03-26

### Fixed
- A closed door muffles sound. An open door is transparent. A thin panel that a ray misses
  gets its occlusion from the override.
- A rain source spawns behind a closed door and still muffles correctly. The weather ray
  counts a door for the muffling and ignores it for the spawn test.
- A partial block at your position, such as a slab or a ladder, no longer skips the occlusion
  test.
- The sky test no longer reads sky through a solid overhang.
- A glass pane keeps its own occlusion through a config upgrade.

## [0.2.0] - 2026-03-25

### Added
- Direct path gain, for a more natural muffling through a wall.
- The debug view `.soundphysics viz` shows occlusion rays, bounce paths and reverb slots.
- A sound around a corner loses treble with the angle.
- Foliage occludes by its real block volume.
- The mod remembers a verified rain or wind column and rechecks it on a block change only.

### Fixed
- EFX on Linux. The mod reads the real number of auxiliary sends and remaps the reverb for a
  device with two sends.
- The reverb send no longer clobbers the direct gain.
- Knapping surfaces, loose stones, flint, ore and clay forming no longer block sound.
- A support beam no longer blocks rain.
- A positional sound no longer plays as stereo when you join again.
- Sound repositioning no longer wobbles between two paths.
- The mod removes an orphaned sound source.

### Performance
- All patches wait for the world-ready gate.
- The mod detects a small decorative block. The hardcoded list is gone.

## [0.1.9.0] - 2026-03-22

### Added
- A time budget per tick, 8 ms by default. Dense geometry no longer causes a lag spike. The
  mod defers the rest of the sounds to the next tick.
- A hard cap of 32 steps per ray, a block occlusion cache, and caches for the solid test and
  the door spacer test.

### Changed
- `MaxOcclusion` goes from 10.0 to 4.0. At 4.0 a sound is already inaudible.
- `MaxSoundsPerTick` goes from 25 to 10. `MaxOverdueSoundsPerTick` goes from 6 to 3. The time
  budget is the primary guard.
- The mod compares strings by ordinal.
- The mod migrates your config to the new performance defaults, but only where your values
  match the old defaults.

### Fixed
- The mod no longer moves a sound that is more than 25 m away behind a wall.
- The mod rejects an offset that is larger than the distance to the sound. A sound cannot jump
  behind you.

## [0.1.8] - 2026-03-19

### Added
- A chiseled or partial block occludes by its remaining volume.
- A thatch roof, a sod roof and a hay bale block rain and sound.
- Tool racks, torch holders, lanterns, candles, signs, firepits, anvils, paintings and clutter
  no longer block sound.
- Berry bushes, the Wildcraft Fruit mod and the Wildgrass mod get correct foliage occlusion.
- Distant thunder shifts to a lower pitch. Every strike gets a random pitch.
- A saved material config picks up new block overrides on an upgrade.

### Fixed
- A sound no longer flickers at a wall edge.
- A sound with no open path stays at its position instead of a jump between offsets.
- A far sound muffles at the same perceived rate as a near sound.
- The reverb cache no longer oscillates at about 45 blocks.
- The throttle fade freezes when it finds a fast oscillation and continues when the sound is
  stable.
- A chiseled block no longer muffles too much.
- A decorative block on a wall no longer causes rain wobble under a porch.
- A fence and a glass pane apply their material occlusion.
- The material config no longer crashes on a duplicate key.
- The upper block of a 2x3 gate no longer counts as solid wood.

## [0.1.7] - 2026-03-05

### Added
- Support for the Medieval Expansion mod: doors, gates and spacer blocks.
- Door and gate detection for modded blocks, such as a portcullis.
- A wind source sits at ceiling height for a sky opening, not at floor level. The mod infers
  the ceiling height from the roof geometry nearby.
- A debug view for wind, in magenta.
- A world-ready gate. The mod defers its raycasts until the world is ready.

### Fixed
- The freeze when you join a multiplayer server. The mod raycast against an incomplete world.
- An open gate with spacer blocks no longer blocks sound.
- The mod skips the fast path for a solid face on an open interactable block.

## [0.1.6.1] - 2026-03-02

### Fixed
- The resonator state saves to the chunk on the server.
- The desync when the client has the mod and the server does not.
- The mod detects Carry On on the server side. The tooltip shows the correct key.

## [0.1.6] - 2026-03-02

### Added
- New thunder and lightning. A dedicated enclosure system, a range of 1000 blocks and a
  realistic falloff.
- Random rumble volume. The mod muffles an indoor crack hard, with a floor at 500 Hz.
- Probe rays that march along a cave to find its exit.
- The mod corrects the sound position for a door and for a multiblock placeholder.
- The rain position is the average of the nearest nine columns.
- A config migration system and support for the ConfigLib settings window.

### Fixed
- A sound at your own position keeps its stereo.
- A sound that you make yourself no longer occludes itself.
- The reverb attenuation uses the range of the sound.
- Repositioned sounds are no longer too muffled.
- The music pitch no longer sticks after you leave the water.

### Changed
- Better tuned smoothing for a more realistic transition.

## [0.1.5] - 2026-02-25

### Added
- A new reverb cache key from the sound cell and your own cell. It invalidates itself when you
  move.
- A near sound uses a cell of 2 blocks, a far sound uses a cell of 8 blocks.
- A sound near a corner or a doorway updates every tick.

### Changed
- The smoothing scales with the size of the change. A corner transition needs about 300 ms
  instead of about 1 s.

### Fixed
- A jump in the filter when a sound crossed the repositioning threshold.
- A pop in the filter from a single tick, now capped.

## [0.1.4] - 2026-02-22

### Added
- A runtime API. Another mod can set material overrides, occlusion and reflectivity.
- The mod corrects the sound position for a door and a multiblock.
- A filter configuration for lava.
- Wind skips the reverb.

### Fixed
- The resonator lifecycle. Orphaned tracks and duplicates are gone.
- The churn at the budget border, with distance hysteresis.
- Thunder plays in the correct direction underground and underwater.
- A weather source no longer spawns through a closed door.

## [0.1.3] - 2026-02-09

### Added
- A sound override system. A custom sound file replaces a vanilla sound.
- The beehive override, as the first sound.
- Support for the Carry On mod.

### Changed
- The resonator patches live in one file.

## [0.1.2] - 2026-02-08

### Added
- Sound repositioning, with smoothing and hysteresis.
- A DDA block traversal for the reverb raycasts.

### Fixed
- Jumps and artifacts from the repositioning.
- A filter that detached during a state change.

## [0.1.1] - 2026-02-07

### Added
- Weather audio. Rain, wind and hail get a position.
- A thunder handler that knows the direction of the strike.
- Weather muffling from the enclosure of your position.

### Changed
- Faster raycasts.

## [0.1.0] - 2026-02-06

### Added
- The first release.
- Sound occlusion through walls, from raycasts.
- Reverb from the geometry of a cave or a room.
- A filter that knows the material: wood, stone, metal and more.
- Configurable settings.
- A Harmony patch system for the audio.
