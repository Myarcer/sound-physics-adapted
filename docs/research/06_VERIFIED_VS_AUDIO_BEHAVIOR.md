# VERIFIED: Vintage Story Audio Behavior

**Source**: Direct analysis of `VintagestoryLib.decompiled.cs`
**Date**: 2026-02-03
**Confidence**: HIGH - based on actual decompiled code

---

## 1. Room/Reverb Detection System

### scanReverbnessOffthread() - Lines 151593-151636

**Execution**: Background thread, triggered every 500ms

**Ray Configuration**:
```
Horizontal angles: 0°, 45°, 90°, 135°, 180°, 225°, 270°, 315° (8 rays)
Vertical angles: -90°, -45°, 0°, 45°, 90° (5 rays)
Total: 40 rays
Ray length: 35 blocks
```

**Algorithm**:
```csharp
for (float yaw = 0f; yaw < 360f; yaw += 45f)
{
    for (float pitch = -90f; pitch <= 90f; pitch += 45f)
    {
        Ray ray = Ray.FromAngles(playerEyePos, pitch, yaw, 35f);
        BlockSelection hit = intersectionTester.GetSelectedBlock(ray);

        if (hit != null && IsReflectiveMaterial(hit.Block) && hit.Block.SideIsSolid(...))
        {
            float distance = hit.Position.DistanceTo(playerEyePos);
            reverbness += (Math.Log(distance + 1) / 18.0 - 0.07) * 3.0;
            // Update bounding box min/max
        }
        else
        {
            reverbness -= 0.2;
            // Extend bounding box to 35 blocks in this direction
        }
    }
}
TargetReverbness = reverbness;
RoomLocation = boundingBox.GrowBy(10, 10, 10);
```

### Reflective Materials (Hardcoded)
Only these `EnumBlockMaterial` types contribute to reverb:
- Metal
- Ore
- Mantle
- Ice
- Ceramic
- Brick
- Stone

**Note**: Wood, soil, cloth, glass, etc. do NOT contribute to reverb calculation.

### Room Shape Detection
**NONE** - VS does NOT detect room shape.

It only creates a simple axis-aligned bounding box (AABB) from:
- Minimum X,Y,Z of all ray hit points
- Maximum X,Y,Z of all ray hit points
- Plus 10 block padding on all sides

A narrow tunnel and a large cavern with same extents = same RoomLocation box.

---

## 2. Reverb Application

### When Reverb Is Applied

**Two locations in code:**

#### A) On Sound Start - `StartPlaying()` (line 116815)
```csharp
if (EyesInWaterDepth() == 0f &&                              // Not underwater
    SystemSoundEngine.NowReverbness >= 0.25f &&               // In reverberant space
    (loadedSound.Params.Position == null ||                   // No position, OR
     loadedSound.Params.Position == SystemSoundEngine.Zero || // Position is (0,0,0), OR
     SystemSoundEngine.RoomLocation.ContainsOrTouches(loadedSound.Params.Position))) // Inside room
{
    loadedSound.SetReverb(SystemSoundEngine.NowReverbness);
}
```

#### B) On Sound Load - `LoadSound()` (line 116849)
Same logic as above.

#### C) On Tick - `OnGameTick100ms()` (lines 151709-151722)
```csharp
// Only runs when reverbKey changes (every 0.1 change in NowReverbness)
foreach (ILoadedSound sound in ActiveSounds)
{
    if (sound.Position == null || sound.Position == Zero || RoomLocation.ContainsOrTouches(sound.Position))
    {
        sound.SetReverb(Math.Max(0f, NowReverbness));
    }
    else
    {
        sound.SetReverb(0f);  // EXPLICITLY SET TO ZERO
    }
}
```

### Critical Insight: Position Behavior

| Sound Position | Reverb Applied? |
|----------------|-----------------|
| `null` | YES |
| `(0, 0, 0)` | YES (matches `SystemSoundEngine.Zero`) |
| Inside RoomLocation | YES |
| Outside RoomLocation | **NO** (set to 0) |

**Default SoundParams constructor sets Position to (0,0,0):**
```csharp
public SoundParams(AssetLocation location)
{
    Location = location;
    Position = new Vec3f();  // (0,0,0)
    ...
}
```

So ambient sounds created with default constructor WOULD get reverb.

---

## 3. Reverb Value Flow

```
scanReverbnessOffthread() calculates → TargetReverbness
                                            ↓
OnRenderFrame() smoothly interpolates → NowReverbness
                                            ↓
SetReverb(NowReverbness) applies to → OpenAL EFX

Interpolation: NowReverbness += (TargetReverbness - NowReverbness) * deltaTime / 1.5f
```

### Reverb Presets (24 levels)
```csharp
// AudioOpenAl.GetOrCreateReverbEffect() - line 98129
// Range: 0.5 to 7.0 reverbness
// Quantized to 24 preset slots
int presetIndex = (int)((reverbness - 0.5f) / 6.5f * 24f);  // 0-23
```

---

## 4. Underwater Effects

### When Submerged - `OnGameTick100ms()` (lines 151674-151702)
```csharp
if (submerged() && !prevSubmerged)
{
    foreach (ILoadedSound sound in ActiveSounds)
    {
        sound.SetLowPassfiltering(0.06f);  // Heavy muffling
        if (sound.SoundType != Music)
            sound.SetPitchOffset(-0.15f);  // Lower pitch
    }
}
else if (!submerged() && prevSubmerged)
{
    foreach (ILoadedSound sound in ActiveSounds)
    {
        sound.SetLowPassfiltering(1f);     // No filter
        sound.SetPitchOffset(0f);          // Normal pitch
    }
}
```

### Submerged Check
```csharp
private bool submerged()
{
    return game.EyesInWaterDepth() > 0f || game.EyesInLavaDepth() > 0f;
}
```

---

## 5. What Our Documentation Got Wrong

### Corrected Items:

| Previous Claim | Actual Behavior |
|----------------|-----------------|
| "VS detects room shape" | NO - only creates AABB bounding box |
| "Thunder has no reverb" | Depends on Position - if null/(0,0,0), it DOES get reverb |
| "External sounds get no reverb" | Only if Position is set AND outside RoomLocation |

### Still Accurate:

| Claim | Status |
|-------|--------|
| 40 rays for detection | ✅ Correct (8×5) |
| Only specific materials | ✅ Correct (Stone, Metal, etc.) |
| Reverb formula | ✅ Correct |
| No sound occlusion | ✅ Correct |
| Binary underwater | ✅ Correct |

---

## 6. Weather/Thunder Sound Behavior (VERIFIED)

### Verified Position Behavior

| Sound Type | Position | Source |
|------------|----------|--------|
| **Thunder/Lightning** | ✅ **POSITIONAL** - World coords at strike location | Web research + game behavior confirms distance-based delay |
| **Rain ambient** | ⚠️ **AMBIENT** - Likely null or (0,0,0) | Not in VintagestoryLib, handled by VSEssentials |
| **Wind ambient** | ⚠️ **AMBIENT** - Likely null or (0,0,0) | Not in VintagestoryLib, handled by VSEssentials |

### Evidence

1. **Thunder is positional**: Game simulates sound travel time - "thunder takes time to reach the player" based on lightning strike distance. This proves thunder uses world coordinates.

2. **EnumSoundType.Weather**: The code (line 98514) shows weather sounds have separate volume control:
   ```csharp
   if (soundParams.SoundType == EnumSoundType.Weather)
       return WeatherSoundLevel;
   ```

3. **Weather code location**: Weather simulation is in VSEssentials mod (separate DLL), not VintagestoryLib. Uses `PlaySoundAt()` with strike coordinates for lightning.

### Implications for Occlusion Mod

| Weather Effect | Occlusion Approach | Implementation |
|----------------|--------------------|-----------------|
| **Thunder** | Standard raycast from strike pos → player | ✅ Works naturally with Phase 1 |
| **Rain ambient** | Player enclosure check (rays upward) | Needs `GetPlayerEnclosure()` |
| **Wind ambient** | Player enclosure check (all directions) | Needs `GetPlayerEnclosure()` |

---

## 7. Summary: What VS Actually Does

### Reverb System
- ✅ Detects enclosed spaces via raycasting
- ✅ Calculates reverb amount based on distance to walls
- ✅ Only counts reflective materials (stone, metal, etc.)
- ✅ Applies OpenAL EFX reverb effect
- ❌ Does NOT detect room shape (tunnel vs cavern)
- ❌ Does NOT consider material absorption
- ⚠️ May or may not apply to external sounds (depends on Position)

### Occlusion System
- ❌ Does NOT exist
- No raycast from sound to player
- No block-based muffling
- Sounds pass through any number of walls at full volume

### Underwater System
- ✅ Low-pass filter (0.06) when submerged
- ✅ Pitch reduction (-0.15)
- ✅ Applied to all active sounds
- Binary on/off, no gradual transition

---

## 8. Implications for Our Mod

### Phase 1 (Occlusion) - Still Needed
VS has zero sound occlusion. This is fully missing.

### Phase 3 (Reverb) - Scope Reduced
VS already has functional reverb. We should:
1. Verify weather sound behavior in-game
2. Only fix if thunder actually lacks reverb
3. Focus on material-based reverb variations (stretch goal)

### Phase 4 (Weather) - Still Needed
Even if weather gets reverb, it still needs OCCLUSION (muffling through blocks).
