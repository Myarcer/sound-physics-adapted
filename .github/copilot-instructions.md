# VS Sound Physics - GitHub Copilot Context

**🚨 AUTO-LOADED ON EVERY COPILOT SESSION**

---

## 🎯 Project Overview
**Goal**: Comprehensive sound physics mod for Vintage Story matching/exceeding Minecraft's Sound Physics Remastered  
**Current Phase**: Phase 4 Complete (Shared Airspace & Sound Paths)  
**Language**: C# (.NET 7.0)  
**Game API**: Vintage Story Modding API

---

## 📊 Current Implementation Status

### ✅ WORKING (Phases 1-4)
- **Occlusion**: Raycast-based sound blocking through blocks, lowpass filtering
- **Material System**: JSON-configurable per-material occlusion values
- **Reverb**: 4-slot EFX system (short/medium/long decay) with ray-traced environment detection
- **Shared Airspace**: Only audible reflections counted, sound repositioning through openings
- **Per-Sound Filters**: Independent AL filter per sound (no global conflicts)

### 🚧 ACTIVE CHALLENGES
1. **Weather/Enclosure Detection**: Binary rain muffling needs gradual transition
   - See `docs/HANDOFF_WEATHER_ENCLOSURE_RETHINK.md`
   
2. **Door State Handling**: Doors currently treated as solid blocks (need open/closed state detection)
   
3. **Performance**: Ray tracing optimization needed for dense environments
   - See `docs/OPTIMIZATION_STRATEGY.md`

---

## 🏗️ Architecture Key Points

### Per-Sound Filter System
- Each `TrackedSound` owns its AL filter object (avoid race conditions)
- Location: `src/Core/AudioRenderer.cs`

### 4-Slot Reverb System (SPR-Style)
- Slots: SHORT (0.3s), MEDIUM (0.6s), LONG (1.2s), UNUSED
- Send Filter Gain controls dry→reverb signal (SPR approach)
- Location: `src/Core/AudioRenderer.cs`, `src/Core/ReverbManager.cs`

### Shared Airspace Detection
- Only count rays hitting reflective materials that share listener's airspace
- Prevents distant occluded sounds from having excessive reverb
- Location: `src/Core/AudioPhysicsSystem.cs` - `TraceReverbRays`

---

## 📁 Key Files

**Core Logic:**
- `src/Core/AudioPhysicsSystem.cs` - Raytracing (occlusion + reverb)
- `src/Core/AudioRenderer.cs` - OpenAL state management
- `src/Core/ReverbManager.cs` - EFX effect slots
- `src/Core/TrackedSound.cs` - Per-sound state

**Config:**
- `src/Config/SoundPhysicsConfig.cs` - Mod settings
- `src/Config/MaterialSoundConfig.cs` - Material properties
- `resources/config/soundphysics-materials.json` - Material database

**Documentation:**
- `docs/TODO.md` - Current tasks
- `docs/TECHNICAL_FINDINGS.md` - Implementation notes
- `PROJECT_MEMORY.md` - Full project context (for detailed reference)

---

## 🛠️ Build & Test

```powershell
# Build
dotnet build

# Install to game
Copy-Item -Path "bin/Debug/net7.0/vssoundphysics.dll" -Destination "$env:APPDATA/VintagestoryData/Mods/"
```

**Debug**: Enable `EnableDebugLogging` in config, check `%APPDATA%/VintagestoryData/Logs/`

---

## 🎯 Next Steps (Phase 5)

1. Gradual weather attenuation (replace binary rain check)
2. Door state detection (open = non-occluding)
3. Cave opening detection (partial muffling)
4. Performance profiling & caching

---

## 💡 Context for AI Assistance

- **VS API**: Use decompiled references in `references/decompiled/` for API details
- **Sound Physics Remastered**: Reference implementation in `references/sound-physics-remastered-master/`
- **OpenAL-Soft EFX**: This mod uses OpenAL effects extension for reverb
- **Vintage Story Version**: 1.19+ (check mod compatibility)

---

**Last Updated**: 2026-02-07
