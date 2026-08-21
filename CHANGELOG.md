# Changelog

This file lists the changes to Sound Physics Adapted.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.2.6-dev.7] - 2026-08-21

### Fixed
- Some sounds could lose their muffling forever after another sound ended on the same audio channel. The mod now checks that a finished sound still owns its channel before cleaning up.
- Stereo sounds (music, resonator tracks) can no longer lose stereo permanently after a fast sequence of loads. A failed load no longer leaves the flat mono version behind.
- Rain and thunder from the replacement system and the vanilla system can no longer play at the same time after you turn weather enhancement off and on between sessions.
- In multiplayer, the server now ignores resonator and boombox messages from players who are not near the device, arrive too fast, or carry a malformed track name.
- Two openings close together (one above the other) now keep their own identity instead of fighting over one voice.
- When there are more openings than voices, the nearest opening wins the free slot first — not the oldest one.
- The Layer 1 rain bed now fills in when an opening qualified for a voice but had no slot left, so wide cave mouths no longer go quiet.
- Leafless rain no longer adds extra volume on top of leafy rain in dense forests.
- Thunder cracks indoors no longer fight with the rumble over one filter.
- A disc taken out of a resonator no longer reloads at an old frozen rotation.
- Frozen resonator rotations no longer leak into a new world at the same coordinates.

### Changed
- Max Rain Sources default went from 4 to 8. This value also decides how many openings get tracked each tick; 4 starved cave mouths. Existing configs keep an edited value.
- Removed the dead "Opening Persistence" setting. It did nothing since persistence moved to the virtualization model.

### Performance
- Reverb raycasts, path records, and block-change invalidation allocate far less, so mining and fighting cause less stutter.
- Turning OFF sound repositioning no longer silently turns OFF positional reverb with it.
- The player reverb cache refreshes after a block change even when you stand still.

## [0.2.6-dev.6] - 2026-08-19

### Fixed
- Rain no longer plays from behind solid rock. In a cave, sources placed at openings found earlier from the surface kept playing through the stone above you and buried the one opening you could actually hear. A source that measures inaudible now gives its slot back.
- An opening you can see now wins its slot over one remembered from somewhere else.
- Rain sources sit on an opening, not in the middle between two of them.
- Rain and wind through windows or doors no longer flicker in volume.
- Window and door openings change size smoothly instead of jumping.
- A single block of open sky no longer sounds as loud as the open sky itself.
- Sounds fade in and out smoothly as you walk near them.
- The carried resonator (boombox) works again with Carry On 2.0. Older Carry On versions still work as before.
- Fixed music staying at an old position after a failed pickup.
- Rain and wind now fade in gently when first detected, instead of jumping straight to full volume.
- Quiet sounds change smoothly instead of jumping suddenly.
- Less stutter when you place or break a block.

### Changed
- Sounds muffle and fade at a more natural speed.
- Sound transitions now match how fast you are moving, such as sprinting or falling.

### Performance
- The mod runs noticeably faster, especially in rain.

## [0.2.6-dev.5] - 2026-08-16

### Fixed
- Rain on a wooden trapdoor no longer sounds like metal.
- Turning the mod off now fully restores normal game audio right away.
- The on/off toggle also reacts to changes made in the settings menu, not only the chat command.

### Changed
- A torch on a torch holder is louder.
- Sound transitions are smoother and more consistent.

### Performance
- The mod uses less CPU for sounds that are not changing.

## [0.2.6-dev.4] - 2026-08-14

### Changed
- Config files reset to new defaults. Save your custom settings before you update.
- The improved beehive sound now plays by default. The louder lightning sound stays optional.

## [0.2.6-dev.3] - 2026-08-14

This build joins two development branches into one. No features were lost.

### Fixed
- Standing inside a block, such as snow or a bed, no longer makes nearby sounds sound muffled.

## [0.2.6] - Unreleased

### Fixed
- A resonator (boombox) heard while joining a world no longer plays too loud and flat.
- A resonator track no longer plays without positioning for its first second.
- Standing inside a solid block no longer muffles all nearby sounds.
- A sound behind a doorway is no longer almost silent.
- Rain and wind react faster to nearby doors and block changes.
- A blocked view of the sky no longer wrongly counts as indoors.
- Downgrading the mod no longer breaks the config file.

### Performance
- Breaking or placing a block no longer clears all cached sound data. Mining and building cost much less performance.
- The mod finds sealed rooms with a cheaper method.
- General performance improvements to sound occlusion and reverb.

## [0.2.5] - 2026-07-02

### Fixed
- Rain and wind through a window or door no longer cut out as you walk deeper inside. They fade instead.
- Fixed audio glitches after leaving and rejoining a world.
- Two sounds in the same moment no longer swap positions.
- A volume fade no longer freezes under heavy load.

### Changed
- Building from source now needs the .NET 10 SDK.

### Performance
- Less stutter when many sounds start at once.

## [0.2.4] - 2026-04-27

### Added
- New sound distance settings, on by default. Sound carries farther and sounds more realistic at range. Walls and obstacles still block sound as before.

### Changed
- The mod is now required for players who join a server that uses it.

## [0.2.3] - 2026-04-22

### Changed
- Distant thunder sounds deeper and quieter.

## [0.2.2.5] - 2026-04-22

### Changed
- The mod now supports Vintage Story 1.22.

## [0.2.2.3] - 2026-04-03

### Added
- More stable, natural-sounding water, lava, and beehive audio.
- Smoother sound movement as you approach these sounds.

### Fixed
- Reverb no longer leaks through fully muffled walls.
- The beehive sound now matches the vanilla game sound.

### Changed
- Custom sound replacements are now optional and off by default.

## [0.2.2.1] - 2026-03-27

### Fixed
- Sounds like a quern, forge, or beehive heard while a world is loading are no longer too loud through walls.

### Performance
- Faster sound processing behind thick walls.

## [0.2.2] - 2026-03-27

### Added
- Sound now bends realistically around corners.

### Changed
- Thick walls muffle sound more realistically.

### Fixed
- A tall door no longer muffles its own sound.

### Performance
- General performance improvements.

## [0.2.1] - 2026-03-26

### Fixed
- Closed doors muffle sound. Open doors do not.
- Rain sounds correctly muffle behind closed doors.
- Standing on a slab, ladder, or other partial block no longer breaks sound muffling.
- Fixed the sky wrongly being audible through solid overhangs.

## [0.2.0] - 2026-03-25

### Added
- A new debug view for sound testing.
- Sound loses clarity when heard around a corner.
- Plants and foliage now block sound realistically.

### Fixed
- Reverb effects on Linux.
- Knapping, ore, and clay-forming sounds no longer block audio.
- Rain is no longer blocked by support beams.
- Sounds no longer play in stereo incorrectly after rejoining.
- Sound positioning no longer flickers between two paths.

### Performance
- General performance improvements.

## [0.1.9.0] - 2026-03-22

### Added
- A performance budget that prevents lag spikes from dense scenery.

### Fixed
- Distant sounds behind walls no longer move incorrectly.
- Sounds can no longer jump to the wrong position.

## [0.1.8] - 2026-03-19

### Added
- Partially broken blocks now block sound realistically.
- Thatch and sod roofs, and hay bales, now block sound and rain.
- Furniture and decorations, such as racks, torches, and signs, no longer block sound.
- Support for extra foliage from other mods.
- Distant thunder sounds deeper, with natural variation.

### Fixed
- Sounds no longer flicker at wall edges.
- Sounds with no clear path no longer jump between positions.
- Reverb no longer oscillates at certain distances.
- Several block-specific sound muffling bugs.

## [0.1.7] - 2026-03-05

### Added
- Support for Medieval Expansion doors and gates.
- Better door and gate detection for modded blocks.
- Wind sounds now come from the right height at sky openings.

### Fixed
- Fixed a freeze when joining a multiplayer server.
- Open gates no longer block sound.

## [0.1.6.1] - 2026-03-02

### Fixed
- Fixed a resonator save issue on servers.
- Fixed an audio mismatch between client and server.

## [0.1.6] - 2026-03-02

### Added
- New, more realistic thunder and lightning sounds with long range.
- Cave sounds now find their way to the nearest exit.
- Rain position is now more accurate.
- Support for the ConfigLib settings window.

### Fixed
- Several sound positioning and muffling bugs.
- Music no longer gets stuck at the wrong pitch after you leave water.

### Changed
- Better tuned, more realistic sound transitions.

## [0.1.5] - 2026-02-25

### Added
- Smarter caching for more accurate reverb.

### Changed
- Sound transitions near corners and doorways are now much faster.

### Fixed
- Fixed filter glitches when a sound crossed a wall.

## [0.1.4] - 2026-02-22

### Added
- Other mods can now customize sound behavior through this mod.
- Sound positioning fixes for doors and multiblock structures.

### Fixed
- Fixed issues with the resonator's sound lifecycle.
- Thunder now plays from the correct direction underground and underwater.
- Weather sounds no longer play through closed doors.

## [0.1.3] - 2026-02-09

### Added
- Custom sound replacements. The beehive sound is the first.
- Support for the Carry On mod.

## [0.1.2] - 2026-02-08

### Added
- Sounds now move smoothly as their source moves.

### Fixed
- Fixed sound glitches from repositioning.

## [0.1.1] - 2026-02-07

### Added
- Weather audio: rain, wind, and hail now have a position.
- Thunder now comes from the direction of the strike.

## [0.1.0] - 2026-02-06

### Added
- First release.
- Realistic sound muffling through walls.
- Reverb based on room and cave shape.
- Material-based sound filtering, such as wood, stone, and metal.
- Configurable settings.
