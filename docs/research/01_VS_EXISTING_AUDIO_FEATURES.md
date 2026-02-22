# Vintage Story - Existing Audio Features Analysis

> **Note**: See `06_VERIFIED_VS_AUDIO_BEHAVIOR.md` for code-verified details.

## Summary
VS already has MORE audio features than initially thought, but they are **room-based and sunlight-based**, NOT **per-sound raycast occlusion**.

---

## 1. Built-in Reverb System (CONFIRMED)

### Location in Code
`VintagestoryLib.decompiled.cs` - `SystemSoundEngine` class (line ~151465)

### How It Works
```
1. scanReverbnessOffthread() runs on background thread
2. Casts rays at 45-degree intervals (8 horizontal x 5 vertical = ~40 rays)
3. Only counts REFLECTIVE materials: Metal, Ore, Mantle, Ice, Ceramic, Brick, Stone
4. Calculates "reverbness" value: (Math.Log(distance + 1) / 18 - 0.07) * 3
5. Builds RoomLocation bounding box of detected room
6. NowReverbness smoothly interpolates to TargetReverbness
7. Sounds inside RoomLocation get SetReverb() applied
```

### Key Variables
- `NowReverbness` / `TargetReverbness`: float 0-7 (0.5-7 range for active reverb)
- `RoomLocation`: Cuboidi bounding box of detected room
- `reverbEffectsByReverbness[24]`: Pre-cached OpenAL EFX reverb presets

### Materials That Trigger Reverb
- Metal, Ore, Mantle, Ice, Ceramic, Brick, Stone
- Must have solid face toward player

### What This Means
**VS already has cave reverb!** The "realistic sound" your modding contact mentioned is likely this system.

---

## 2. Underwater Audio Effects (CONFIRMED)

### Location
`SystemSoundEngine.OnGameTick100ms()` (line ~151674)

### Effects Applied
- Low-pass filter: `SetLowPassfiltering(0.06f)` - heavy muffling
- Pitch offset: `SetPitchOffset(-0.15f)` - sounds lower underwater
- Reversed when surfacing: filter=1.0, pitch=0

---

## 3. Sound Type Categories

VS has distinct sound categories with separate volume controls:
```
- EnumSoundType.Sound (general)
- EnumSoundType.Entity (creatures)
- EnumSoundType.Ambient (environmental)
- EnumSoundType.Weather (wind/rain)
- EnumSoundType.Music
- EnumSoundType.SoundGlitchunaffected (temporal stability)
- EnumSoundType.AmbientGlitchunaffected
- EnumSoundType.MusicGlitchunaffected
```

---

## 4. What VS Does NOT Have

### No Block-Based Sound Occlusion
- Sounds from monsters/thunder outside pass through walls unfiltered
- No raycast from sound source to player
- No per-block absorption values

### No Dynamic Wind Occlusion
- Wind sounds are controlled by sunlight level, not block penetration
- Cave openings still play full wind because sunlight reaches there

### No Material-Based Muffling
- Wool vs stone vs glass all transmit sound identically
- Only reverb considers materials (and only for reflection, not transmission)

---

## 5. OpenAL EFX Usage (CONFIRMED)

### Available Features
```csharp
// From AudioOpenAl class
HasEffectsExtension = true  // EFX supported
EFX.GenAuxiliaryEffectSlot()
EFX.GenEffect()
EFX.Effect(id, EffectInteger, AL_EFFECT_TYPE=REVERB)
EFX.AuxiliaryEffectSlot()
EXTEfx.AL_FILTER_LOWPASS  // Already used for underwater
```

### Implication for Mod
We can use the SAME OpenAL EFX APIs that VS already uses to add:
- More lowpass filters for occlusion
- Custom reverb configurations
- Per-sound attenuation

---

## 6. Ambient Sound System

### AmbientSound class (line ~150785)
- Position-based ambient sounds (water, lava, etc.)
- Volume scales with `QuantityNearbyBlocks`
- Uses bounding boxes for spatial management
- Automatic volume fading with `FadeToNewVolume()`

---

## Key Insight

**The modder who said VS has "realistic sound" is correct** - but it's:
1. Room-based reverb (not per-sound occlusion)
2. Sunlight-based underground detection (not block raycast)
3. Underwater muffling (global, not directional)

**What's MISSING** and what the mod should add:
1. Per-sound occlusion (raycast through blocks)
2. Material-based attenuation (wool absorbs, stone reflects)
3. Wind/weather muffling based on enclosure (not just sunlight)
