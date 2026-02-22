# Sound Physics Adapted

A comprehensive sound physics mod for Vintage Story that adds realistic sound occlusion, enhanced reverb, and physics-based audio propagation.

## Vision

Match or exceed the feature set of Minecraft's [Sound Physics Remastered](https://github.com/henkelmax/sound-physics-remastered), adapted for Vintage Story's unique atmosphere and mechanics.

---

## Current Status: Phase 3 Complete

### Phase 1: Basic Occlusion - COMPLETE
- [x] Sound occlusion through blocks (raycast-based)
- [x] Lowpass filtering for muffled sounds
- [x] Volume reduction based on obstruction
- [x] Per-sound filter management (no global filter conflicts)

### Phase 2: Material System - COMPLETE
- [x] Per-material occlusion values
- [x] JSON configuration for customization
- [x] Block-specific overrides via material config

### Phase 3: Enhanced Reverb (SPR-Style) - COMPLETE
- [x] 4-slot EFX reverb system (short/medium/long decay)
- [x] Ray-traced environment detection
- [x] Material-based reflectivity
- [x] Send filter gain control (SPR-style)
- [x] SPR-matched effect parameters
- [x] Non-positional sound handling (player position fallback)

### Phase 4: Shared Airspace & Sound Paths - COMPLETE
- [x] Shared airspace detection (only count audible reflections)
- [x] Fix: Distant occluded sounds no longer have excessive reverb
- [x] Sound repositioning (sound comes from doorway/opening)

### Phase 5: Weather & Ambient - PLANNED
- [ ] Gradual weather attenuation (not binary)
- [ ] Door state affects sound properly
- [ ] Cave openings have partial muffling

### Phase 6: Advanced - PLANNED
- [ ] Air absorption over distance
- [ ] Performance optimization
- [ ] Debug visualization

---

## Project Structure

```
sound-physics-adapted/
├── soundphysicsadapted.csproj         # Project file
├── SoundPhysicsAdaptedModSystem.cs    # Main mod entry
├── Config/                       # Configuration classes
│   ├── SoundPhysicsConfig.cs
│   └── MaterialSoundConfig.cs
├── Core/                         # Main processing logic
│   ├── OcclusionCalculator.cs
│   ├── AcousticRaytracer.cs      # SPR-style ray tracing
│   ├── SoundPathResolver.cs      # Sound repositioning
│   ├── ReverbEffects.cs          # EFX slot management
│   ├── EfxHelper.cs              # OpenAL EFX wrapper
│   ├── AudioPhysicsSystem.cs     # Main update loop
│   └── AudioRenderer.cs          # Per-sound OpenAL filters
├── Patches/                      # Harmony patches
│   ├── LoadSoundPatch.cs         # Sound creation hook
│   ├── AudioLoaderPatch.cs       # Audio loader hook
│   ├── ResonatorPatch.cs         # Resonator integration
│   └── ReverbPatch.cs            # Vanilla reverb disable
├── Network/                      # Multiplayer sync
│   └── ResonatorSyncPacket.cs
├── lib/                          # Reference DLLs
│   ├── VintagestoryAPI.dll
│   ├── VintagestoryLib.dll
│   ├── VSSurvivalMod.dll
│   └── protobuf-net.dll
├── resources/                    # Mod assets
│   ├── modinfo.json
│   └── assets/soundphysicsadapted/lang/
├── docs/                         # Documentation
│   ├── research/                 # Research documents
│   │   ├── 00_DEEP_RESEARCH_FULL.md
│   │   └── 01-08_*.md
│   ├── phases/                   # Phase documentation
│   ├── resolved-issues/          # Fixed issues archive
│   └── *.md                      # Technical docs
├── bin/Release/                  # Build output
├── Releases/                     # Packaged mod ZIPs
├── references/                   # Reference code (not for build)
│   ├── sound-physics-remastered-master/  # SPR source
│   ├── decompiled/               # VS decompiled source
│   └── ForestSymphony/           # Reference mod
└── README.md
```

---

## Technical Approach

### Occlusion System
- DDA raycasting from sound source to player
- Per-block occlusion accumulation
- Lowpass filter with smoothed transitions
- Material-based occlusion values

### Reverb System (SPR-Style)
- 4 auxiliary effect slots with different decay times
- Fibonacci sphere ray distribution (32 rays)
- Multi-bounce reflection calculation
- Send filter gains control reverb intensity
- Material reflectivity affects reverb character

### Key Files
| File | Purpose |
|------|---------|
| `AcousticRaytracer.cs` | Ray tracing, gain calculation |
| `SoundPathResolver.cs` | Sound repositioning through openings |
| `ReverbEffects.cs` | Aux slot/effect management |
| `EfxHelper.cs` | OpenAL EFX reflection wrapper |
| `LoadSoundPatch.cs` | Hooks sound playback |
| `ResonatorPatch.cs` | Resonator/music block integration |

---

## Build & Install

```bash
cd sound-physics-adapted
dotnet build soundphysicsadapted.csproj -c Release
# Output: Releases/soundphysicsadapted_v0.1.0.zip
# Auto-deploys to %appdata%/VintagestoryData/Mods/
```

---

## Configuration

Config file: `%appdata%/VintagestoryData/ModConfig/soundphysicsadapted.json`

```json
{
  "EnableOcclusion": true,
  "EnableCustomReverb": true,
  "ReverbGain": 1.0,
  "ReverbRayCount": 32,
  "ReverbBounces": 4,
  "ReverbMaxDistance": 256,
  "DebugOcclusion": false,
  "DebugReverb": false
}
```

---

## Debugging

### Log Locations
- **Debug log**: `%appdata%/VintagestoryData/Logs/client-debug.log`
- **Main log**: `%appdata%/VintagestoryData/Logs/client-main.log`

### Key Log Patterns
```
REVERB CALC: g0=X.XX g1=X.XX g2=X.XX g3=X.XX enclosed=XX% (hits/total) dist=X.X
REVERB APPLIED src=XX: g0=X.XX g1=X.XX g2=X.XX g3=X.XX
OCCLUSION: src=XX factor=X.XX
```

---


---

## Development Guidelines

### Reference Code
- **SPR Source**: `references/sound-physics-remastered-master/` - Full Minecraft Sound Physics Remastered
  - Key file: `common/src/main/java/com/sonicether/soundphysics/SoundPhysics.java`
  - Config: `common/src/main/java/com/sonicether/soundphysics/config/`
- **VS Decompiled**: `references/decompiled/` - Vintagestory source for reference

### VS Game References
Game entity logic (Resonator, EchoChamber, etc.) is in VSSurvivalMod DLL, not the API.

---

## References

### Local Reference Code
- `references/sound-physics-remastered-master/` - SPR source (primary implementation reference)
- `references/decompiled/` - VS game source for API reference

### External Links
- [Sound Physics Remastered (MC)](https://github.com/henkelmax/sound-physics-remastered) - Primary reference
- [VS Modding Wiki](https://wiki.vintagestory.at/Modding:Getting_Started)
- [VS API Documentation](https://apidocs.vintagestory.at/)
- [OpenAL EFX Guide](https://openal-soft.org/openal-extensions/EXT_EFX.txt)
