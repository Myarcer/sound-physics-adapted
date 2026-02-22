# Sound Physics Adapted

Realistic sound physics for [Vintage Story](https://vintagestory.at/). Hear the difference walls, caves, and weather make.

[![VS Version](https://img.shields.io/badge/Vintage%20Story-1.21.0%2B-green)](https://vintagestory.at/)
[![Side](https://img.shields.io/badge/Side-Client-blue)]()

---

## Features?

**Occlusion** — Sounds behind walls get muffled. Different materials block different amounts: stone walls muffle heavily, wooden doors let more through. Open and close a door and hear the difference immediately.

**Reverb** — Caves echo. Small rooms sound tight. Open fields sound dry. The mod traces rays from your position to detect surrounding geometry and applies matching reverb in real time.

**Sound Repositioning** — When a sound source is behind a wall but there's a nearby doorway, the sound shifts to come from the opening instead of phasing through solid blocks.

**Weather Audio** — Rain, wind, and hail are positioned at openings around you. Step inside a shelter and weather sounds fade based on how enclosed you are — not a binary cutoff.

**Thunder** — Thunder cracks are placed directionally above the horizon with realistic distance falloff.

**Block Integration** — Resonators are fully supported with proper audio lifecycle management and optional CarryON Patch to make them behave like Boomboxes. (Mostly untested)

---

## Compatibility

| | |
|---|---|
| **Vintage Story** | 1.21.0+ NOT 1.22.x YET | 
| **Required on server** | No |
| **Required on client** | No (but only the client running it hears the effects) |
| **CarryOn** | Compatible (dedicated patches) |
| **RP Voice Chat** | Internal API Built, ready for integration (Waiting on RP Voice Chat Dev Team) |
| **Other Mods** | Should be non issues, report here if needed. Tested with CaveSymphony / ForestSymphony |

---

## Install

1. Download the latest `.zip` from [Releases](https://github.com/Myarcer/sound-physics-adapted/releases)
2. Drop it into your `VintagestoryData/Mods/` folder
3. Launch the game

Config generates automatically at `VintagestoryData/ModConfig/soundphysicsadapted.json`. Alot of tweaks for nerds. Will drop different performance config options in here later to drag n drop

---

## Building from Source

Requires .NET 8 SDK.

```
git clone https://github.com/Myarcer/sound-physics-adapted.git
cd sound-physics-adapted
```

**Important Legal Note**: Due to copyright restrictions, the proprietary Vintage Story game engine (`VintagestoryLib.dll`) and official survival content (`VSSurvivalMod.dll`) cannot be distributed with this repository.

You must copy these DLLs from your own game installation into the `lib/` folder:
- `VintagestoryAPI.dll`
- `VintagestoryLib.dll`
- `VSSurvivalMod.dll`
- `protobuf-net.dll`

```
dotnet build soundphysicsadapted.csproj -c Release
```

The mod ZIP lands in `Releases/` and auto-deploys to your mods folder.

---

## Repository Structure

```
├── SoundPhysicsAdaptedModSystem.cs   # Mod entry point
├── Config/                           # Configuration classes
├── Core/                             # Audio processing, raycasting, weather
├── Network/                          # Multiplayer sync packets
├── Patches/                          # Harmony patches for audio interception
├── resources/                        # Mod assets (modinfo, icon, sounds, lang)
├── soundphysicsadapted.csproj        # Build config
├── build.bat                         # Build helper
├── CHANGELOG.md                      # Release history
└── README.md
```

---


## Mod API

Other mods can interact at runtime via `SoundPhysicsAPI`:

```csharp
// Override occlusion for a specific block
SoundPhysicsAPI.SetOcclusionOverride("game:door-*", 0.4f);

// Set material reflectivity
SoundPhysicsAPI.SetMaterialReflectivity("metal", 0.95f);

// Get the full config instance
var config = SoundPhysicsAPI.GetMaterialConfig();
```

---

## Development Roadmap

<details>
<summary>Phase progress (click to expand)</summary>

### Phase 1: Basic Occlusion ✅
Raycast-based sound occlusion, lowpass filtering, volume reduction, per-sound filter management.

### Phase 2: Material System ✅
Per-material occlusion values, JSON configuration, block-specific overrides.

### Phase 3: Enhanced Reverb ✅
4-slot EFX reverb, ray-traced environment detection, material reflectivity, send filter gains.

### Phase 4: Shared Airspace & Sound Paths ✅
Shared airspace detection, sound repositioning through doorways/openings.

### Phase 5: Weather & Ambient - In Progress
Positional weather audio, shelter detection, gradual attenuation, directional thunder.
Currently Still in Development, i am not happy with the Positional Sources Detection and smoothing.

### Phase 6: Polish — In Progress
Air absorption, performance optimization, debug visualization.

</details>

---

## Links

- [Sound Physics Remastered (Minecraft)](https://github.com/henkelmax/sound-physics-remastered) — original inspiration
- [VS Modding Wiki](https://wiki.vintagestory.at/Modding:Getting_Started)
- [VS API Docs](https://apidocs.vintagestory.at/)
