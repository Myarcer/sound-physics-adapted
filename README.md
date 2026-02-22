# Sound Physics Adapted

Realistic sound physics for [Vintage Story](https://vintagestory.at/). Raycast-based occlusion muffles sounds through walls, dynamic reverb reflects off cave and room geometry, and weather audio responds to your shelter level.

Inspired by Minecraft's [Sound Physics Remastered](https://github.com/henkelmax/sound-physics-remastered), built from scratch for Vintage Story's unique world and audio engine.

---

## Features

### 🔇 Sound Occlusion
- **Raycast-based obstruction** — sounds are muffled realistically when blocked by walls, floors, or terrain
- **Material-aware filtering** — wood, stone, metal, and other materials have distinct occlusion values
- **Smooth transitions** — filters ramp up/down naturally as you move around
- **Door & trapdoor awareness** — interactable blocks affect occlusion dynamically based on open/closed state

### 🏔️ Dynamic Reverb
- **4-slot EFX reverb** — short, medium, and long decay reverb calculated from your environment
- **Ray-traced geometry detection** — 32 rays sample surrounding surfaces to determine room size and shape
- **Material reflectivity** — stone caves echo, wooden rooms absorb, metal rooms ring
- **Reverb cell cache** — efficient spatial caching so reverb calculations don't repeat needlessly

### 🔊 Sound Repositioning
- **Sound paths through openings** — occluded sounds reposition to the nearest doorway or opening
- **Smoothed transitions** — no jarring jumps when moving around corners
- **Hysteresis** — prevents rapid flipping between competing sound paths

### 🌧️ Weather Audio
- **Positional rain, wind, and hail** — weather sounds spawn at openings around you
- **Shelter detection** — enclosure calculator determines how sheltered you are
- **Gradual attenuation** — weather fades smoothly, not binary on/off
- **Directional thunder** — thunder sources placed above the horizon with realistic positioning

### 🎵 Block Integration
- **Resonator/music block support** — custom audio handling for resonator blocks with proper lifecycle management
- **Boombox remote sync** — multiplayer synchronization for boombox blocks
- **Sound override system** — replace vanilla sounds with custom audio assets

### ⚙️ Mod API
- Runtime API for other mods to configure material overrides, occlusion values, and reflectivity
- `SoundPhysicsAPI` static class for easy integration

---

## Installation

1. Download the latest release ZIP from the [Releases](https://github.com/Myarcer/sound-physics-adapted/releases) page
2. Place the ZIP file in your `VintagestoryData/Mods/` folder
3. Launch the game — the mod will generate its config file on first run

---

## Configuration

Config file location: `%appdata%/VintagestoryData/ModConfig/soundphysicsadapted.json`

The mod is highly configurable. Key settings include:

| Setting | Default | Description |
|---------|---------|-------------|
| `EnableOcclusion` | `true` | Toggle raycast-based sound occlusion |
| `EnableCustomReverb` | `true` | Toggle dynamic reverb processing |
| `ReverbRayCount` | `32` | Number of rays for environment sampling |
| `ReverbBounces` | `4` | Max reflection bounces per ray |
| `EnableWeatherAudio` | `true` | Toggle weather audio enhancements |
| `EnableSoundRepositioning` | `true` | Toggle sound path redirection through openings |
| `DebugOcclusion` | `false` | Log occlusion calculations |
| `DebugReverb` | `false` | Log reverb calculations |

All settings can be adjusted in-game via the mod config menu.

---

## Building from Source

**Requirements**: .NET 8 SDK, Vintage Story game DLLs

1. Clone the repository
2. Copy the following DLLs from your Vintage Story installation into a `lib/` folder:
   - `VintagestoryAPI.dll`
   - `VintagestoryLib.dll`
   - `VSSurvivalMod.dll`
   - `protobuf-net.dll`
3. Build:
   ```
   dotnet build soundphysicsadapted.csproj -c Release
   ```
4. The built mod ZIP will be in `Releases/` and auto-deployed to your mods folder

---

## Compatibility

- **Vintage Story**: 1.21.0+
- **Side**: Client-side (not required on server)
- **Known compatible mods**: CarryOn (dedicated compatibility patches included)

---

## License

See [LICENSE](LICENSE) for details.

---

## Links

- [Vintage Story Mod DB Page](#) *(coming soon)*
- [Sound Physics Remastered (Minecraft)](https://github.com/henkelmax/sound-physics-remastered) — original inspiration
- [VS Modding Wiki](https://wiki.vintagestory.at/Modding:Getting_Started)
- [VS API Documentation](https://apidocs.vintagestory.at/)
