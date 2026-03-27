# VS Sound Physics Adapted — Copilot Instructions

## Project Overview
- **Goal**: Sound physics mod for Vintage Story (occlusion, reverb, positional weather audio)
- **Version**: 0.2.1 | **Target VS**: 1.21 CURRENTLY (also tested on 1.22)
- **Language**: C# (.NET 8.0) | **Framework**: Vintage Story Modding API + Harmony
- **Build**: `dotnet build soundphysicsadapted.csproj -c Release` (auto-deploys zip to VS mods folder)

---

## MANDATORY: VS API Lookup — Use LOCAL References First

**NEVER guess or hallucinate VS API details. We have full decompiled sources locally.**

### Decompiled VS Sources (SEARCH THESE FIRST)

| File | Contains | When to use |
|---|---|---|
| `references/1.21/decompiled/VintagestoryAPI.decompiled.cs` | Public API: `IBlockAccessor`, `Block`, `BlockPos`, `EnumBlockMaterial`, `Cuboidf`, `EnumSoundType`, `ILoadedSound`, `SoundParams` | Any public API class/interface lookup |
| `references/1.21/decompiled/VintagestoryLib.decompiled.cs` | Internal implementations: `BEBehaviorDoor`, `BlockBehaviorDoor`, `BlockMultiblock`, `ClientMain`, audio internals | Behavior implementations, internal game logic |
| `references/1.21/decompiled/Vintagestory.decompiled.cs` | Core engine: rendering, networking, client/server systems | Engine-level systems |

**Search strategy**: Use `grep_search` with `includePattern` targeting the specific decompiled file:
```
grep_search(query="BEBehaviorDoor", includePattern="references/**/decompiled/*.cs")
grep_search(query="GetCollisionBoxes", includePattern="references/**/VintagestoryLib.decompiled.cs")
```

### VS Game Mod Source Code (Decompiled C#)

| Folder | Contains | When to use |
|---|---|---|
| `references/1.21/vssurvivalmod/` | Survival mod: weather system (`WeatherSimulation*`), block behaviors, crafting, farming | Weather audio, block interactions, gameplay mechanics |
| `references/1.21/vsessentialsmod/` | Essentials mod: entity behaviors, AI, networking, rendering | Entity systems, client rendering |
| `references/1.22/vsapi/` | VS 1.22 API source (newer) | 1.22-specific API changes |

### VS DLL References (for type resolution only)

| File | Version |
|---|---|
| `references/1.22/VintagestoryAPI.dll` + `.xml` | 1.22 (primary build target) |
| `references/1.22/VintagestoryLib.dll` | 1.22 |
| `references/1.22/VSSurvivalMod.dll` + `.pdb` | 1.22 |

### Other Mod References (for compatibility/patterns)

| Folder | Purpose |
|---|---|
| `references/1.21/sound-physics-remastered-master/` | Minecraft SPR — original inspiration, algorithm reference |
| `references/1.21/SoundPhysicsPerfected/` | Another MC sound physics mod reference |
| `references/1.21/carryon/` | CarryOn mod — compatibility patches |
| `references/1.21/ForestSymphony/` | ForestSymphony mod — ambient sound reference |
| `references/1.21/configlib_1.10.14/` | ConfigLib dependency |
| `references/1.22/DoorVariants_1.1.0/` | DoorVariants mod — door compatibility |

### LOOKUP WORKFLOW (follow this order)
1. **grep the decompiled files** for the class/method name
2. **Read the matching section** (use line numbers from grep results)
3. **Cross-reference with vssurvivalmod/ or vsessentialsmod/** if it's game mod code
4. **Only then** consider web search for VS wiki/forum posts

---

## Source Code Structure

### Core Audio Pipeline (`Core/`)
| File | Purpose |
|---|---|
| `AudioPhysicsSystem.cs` | Main orchestrator — per-sound occlusion + reverb calculation, EMA smoothing |
| `OcclusionCalculator.cs` | DDA ray traversal, block occlusion values, AABB intersection, material overrides |
| `DDABlockTraversal.cs` | 3D DDA stepping algorithm for ray-block intersection |
| `AudioRenderer.cs` | OpenAL state: per-sound AL filters, source properties |
| `ReverbManager.cs` / `ReverbEffects.cs` | 4-slot EFX reverb (SHORT/MEDIUM/LONG decay) |
| `AcousticRaytracer.cs` | Reverb ray tracing — detects reflective surfaces in shared airspace |
| `SoundPathResolver.cs` | Multi-probe path resolution — finds openings and alternate sound paths |
| `BlockClassification.cs` | Block type classification helpers (`IsSolidForOcclusion`, material checks) |
| `SoundSourceAdjuster.cs` | Sound position adjustment — repositions sounds through openings |
| `WeatherEnclosureCalculator.cs` | Rain/weather enclosure detection via column DDA |
| `WeatherAudioManager.cs` | Weather audio orchestration |
| `WeatherPositionalHandler.cs` | Positional rain/wind/hail sources |
| `RainAudioHandler.cs` | Rain audio positioning |
| `ThunderAudioHandler.cs` | Thunder audio positioning |
| `OpeningClusterer.cs` | Clusters nearby openings into positional audio groups |
| `OpeningTracker.cs` | Persistent opening tracking with hysteresis |
| `PositionalSourcePool.cs` | Object pool for positional OpenAL sources |
| `MonoDownmixManager.cs` | Mono downmix for stereo→positional sounds |
| `SoundPlaybackThrottle.cs` | Rate limiting for sound processing |
| `SoundOverrideManager.cs` | Custom sound asset overrides |
| `DebugVisualization.cs` | Debug overlay for ray/occlusion visualization |

### Configuration (`Config/`)
| File | Purpose |
|---|---|
| `SoundPhysicsConfig.cs` | Mod settings (enable/disable features, thresholds) |
| `MaterialSoundConfig.cs` | Per-material occlusion values, block override patterns (doors, trapdoors, gates) |
| `ConfigLibBridge.cs` | ConfigLib mod integration for GUI settings |

### Harmony Patches (`Patches/`)
| File | Purpose |
|---|---|
| `LoadSoundPatch.cs` | Intercepts `ClientMain.LoadSound()` to register new sounds |
| `AudioLoaderPatch.cs` | Patches sound loading pipeline |
| `AmbientSoundPatches.cs` | Hooks `AmbientSound.updatePosition` for bbox face-sampling |
| `WeatherSoundPatches.cs` | Weather audio interception |
| `ReverbPatch.cs` | Reverb effect patching |
| `ResonatorPatches.cs` | Resonator block sound interception |
| `ResonatorRendererPatch.cs` | Resonator audio rendering |
| `CarryOnCompatPatches.cs` | CarryOn mod compatibility |
| `BoomboxRemoteHandler.cs` | CarryOn boombox sync |

### Entry Point
- `SoundPhysicsAdaptedModSystem.cs` — mod initialization, config loading, system registration

### Resources
- `resources/modinfo.json` — version (single source of truth), mod metadata
- `resources/config/soundphysics-materials.json` — material database (user-editable)

---

## Active Investigation Docs

| Document | Topic |
|---|---|
| `docs/door-occlusion-investigation.md` | Door/gate occlusion through thin AABB panels |
| `docs/AMBIENT_VOLUME_SOUND_OCCLUSION_BUG.md` | Beehive/ambient bbox face-sampling fix |
| `docs/TODO.md` | Current task list |
| `docs/OPTIMIZATION_STRATEGY.md` | Performance optimization roadmap |
| `docs/VS_1_22_UPDATE_INFO.md` | VS 1.22 compatibility notes |

### Phase Documentation (`docs/phases/`)
- `PHASE4B_PROPAGATION_HANDOFF.md` — Sound path propagation
- `PHASE5_WEATHER.md` — Weather positional audio (IMPLEMENTED)
- `PHASE6_POLISH.md` — Polish & optimization
- `PHASE7_DEBUG_VISUALIZATION.md` — Debug visualization

### Research (`docs/research/`)
- `00_DEEP_RESEARCH_FULL.md` — Comprehensive research compilation
- `06_VERIFIED_VS_AUDIO_BEHAVIOR.md` — Verified VS audio findings
- `07_PHASE3_REVERB_ARCHITECTURE.md` — Reverb system design
- `10_WEATHER_AUDIO_ARCHITECTURE_ANALYSIS.md` — Weather audio architecture

---

## Key VS API Patterns (Quick Reference)

### Block Collision Boxes (Door Investigation Context)
```csharp
// VS 1.21+ doors use behavior-based system, NOT legacy BlockDoor
// Block class: BlockGeneric (NOT BlockDoor)
// Behavior: BlockBehaviorDoor : StrongBlockBehavior, IMultiBlockColSelBoxes
// Entity behavior: BEBehaviorDoor — stores open/closed state, rotated collision boxes
// ColSelBoxes => opened ? boxesOpened : boxesClosed

// Multiblock upper halves: BlockMultiblock extends raw Block (NOT BlockGeneric)
// Delegates via Handle<T,K>() to IMultiBlockColSelBoxes on controller block

// Getting collision boxes:
block.GetCollisionBoxes(blockAccessor, pos)  // returns Cuboidf[] or null
// For doors: dispatches to BEBehaviorDoor.ColSelBoxes via BlockBehaviorDoor
```

### Sound Types
```csharp
EnumSoundType { Sound, Ambient, AmbientGlobal, Weather, Music, MusicGlitchunked, Entity }
// Ambient = block ambient (beehives, water) — uses bbox positioning
// Weather = rain/wind/hail — handled by WeatherAudioManager
```

### Block Material Check
```csharp
block.BlockMaterial == EnumBlockMaterial.Air  // skip air blocks
block.SideSolid[facing]  // per-face solidity
block.GetCollisionBoxes(ba, pos)  // null = no collision geometry
```

---

## Build & Debug

```powershell
# Build (auto-deploys to VS mods folder)
dotnet build soundphysicsadapted.csproj -c Release

# Logs
%APPDATA%/VintagestoryData/Logs/client-debug.log

# Config (user-saved, overrides defaults)
%APPDATA%/VintagestoryData/ModConfig/soundphysicsadapted_materials.json
```

Enable `EnableDebugLogging` in mod config for per-sound occlusion/reverb trace output.

---

**Last Updated**: 2026-03-26
