# Carry On Mod Compatibility

## Overview

Sound Physics Adapted integrates with the **Carry On** mod to provide two features:

1. **Keybind Conflict Resolution** - Pause/resume changes to Ctrl+RMB
2. **Boombox Feature** - Music continues playing while carrying a resonator

---

## Feature 1: Keybind Conflict Resolution

### Issue

Carry On uses Shift+Right-Click to pick up blocks, which conflicts with our resonator pause/resume mechanic.

### Solution

We automatically change our keybind when Carry On is detected:

| Carry On Present | Pause/Resume Keybind |
|------------------|---------------------|
| No               | Shift + Right-Click |
| Yes              | Ctrl + Right-Click  |

---

## Feature 2: Boombox Mode

### Description

When carrying a playing resonator with Carry On, the music continues to play from the player's position - like carrying a boombox. The handoff is **completely seamless** in both directions:

- **Pickup**: Music continues without any gap
- **Carry**: Sound follows player position with full occlusion/reverb physics
- **Placement**: Music seamlessly transfers back to the placed block

### How It Works

1. **On Pickup** (`StopMusicPrefix`):
   - Intercepts the resonator's `StopMusic()` call
   - "Steals" the `ILoadedSound` reference before vanilla disposes it
   - Clears the track's Sound field so vanilla cleanup doesn't dispose it

2. **While Carrying** (`OnBoomboxTick`):
   - Updates sound position to player's head position every 50ms
   - Applies pitch glitch effects like normal resonator
   - AudioRenderer tracking keeps occlusion working

3. **On Placement** (`StartMusicPrefix`):
   - Intercepts `StartMusic()` on the new BlockEntity
   - Injects our existing sound into the new track
   - Disposes any vanilla-created duplicate sound
   - Clears boombox state

### Implementation Files

- `Patches/CarryOnCompatPatches.cs` - All boombox logic
- `SoundPhysicsAdaptedModSystem.cs` - Conditional loading when Carry On detected

---

## Technical Details

### Mod Detection

```csharp
// In StartClientSide:
carryOnModLoaded = api.ModLoader.Mods.Any(m => 
    m.Info.ModID.Equals("carryon", StringComparison.OrdinalIgnoreCase));

// Load compatibility patches only when needed
if (carryOnModLoaded)
{
    CarryOnCompatPatches.ApplyPatches(harmony, api);
}
```

### Carry On Data Structure

Carry On stores carried block data in player entity attributes:

```
entity.WatchedAttributes["carryon:Carried"]["Hands"]["Stack"] = ItemStack
entity.Attributes["carryon:Carried"]["Hands"]["Data"] = ITreeAttribute (BlockEntityData)
```

The BlockEntity is **destroyed** during carry - only data is preserved. Our boombox feature works around this by keeping the `ILoadedSound` alive separately.

### Edge Cases Handled

- Player dies while carrying → Sound disposed during cleanup
- Player drops block (not places) → Sound disposed when carry state ends
- Track is paused when picked up → Paused state preserved
- Different disc placed after carry → Normal StartMusic flow

---

## Configuration Options

All resonator/CarryOn features can be controlled via `soundphysicsadapted.json`:

```json
{
  "_ResonatorSystem": "--- Resonator enhancements: pause/resume, multi-client sync, Carry On boombox. ---",
  "EnableResonatorFix": true,       // Master toggle for resonator enhancements
  "EnableCarryOnCompat": true,      // Toggle for boombox feature
  "DebugResonator": false           // Debug logging for resonator + CarryOn
}
```

| Option | Default | Description |
|--------|---------|-------------|
| `EnableResonatorFix` | `true` | Master toggle for all resonator enhancements (pause/resume, sync) |
| `EnableCarryOnCompat` | `true` | Boombox feature when Carry On is detected. Requires `EnableResonatorFix=true` |
| `DebugResonator` | `false` | Detailed logging for debugging resonator and boombox issues |

---

## Mod Detection Pattern (Reusable)

```csharp
bool isModLoaded = api.ModLoader.Mods.Any(m => 
    m.Info.ModID.Equals("modid", StringComparison.OrdinalIgnoreCase));
```

**Available mod info:**
- `m.Info.ModID` - Unique identifier (from modinfo.json)
- `m.Info.Name` - Display name
- `m.Info.Version` - Version string
