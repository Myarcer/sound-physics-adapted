# Technical Implementation Plan - Sound Physics Adapted

## Architecture Overview

```
┌────────────────────────────────────────────────────────────────┐
│                    Sound Physics Adapted Mod                        │
├────────────────────────────────────────────────────────────────┤
│  Harmony Patches                                               │
│  ├─ LoadedSoundNative.Play() - Apply occlusion                │
│  ├─ ClientMain.LoadSound() - Hook sound creation              │
│  └─ SystemSoundEngine - Enhance reverb calculation            │
├────────────────────────────────────────────────────────────────┤
│  Occlusion Calculator                                          │
│  ├─ Raycast from sound → player                               │
│  ├─ Accumulate block occlusion values                         │
│  └─ Apply lowpass filter + volume reduction                   │
├────────────────────────────────────────────────────────────────┤
│  Configuration                                                 │
│  ├─ Per-block material occlusion values                       │
│  ├─ Enable/disable features                                   │
│  └─ Performance settings (ray count, cache duration)          │
└────────────────────────────────────────────────────────────────┘
```

---

## Phase 1: Minimal Sound Occlusion

### Goal
Muffle sounds that pass through blocks between source and player.

### Key Hook Point
```csharp
// VintagestoryLib.decompiled.cs line ~116815
// In ClientMain.PlaySoundAt() or LoadSound()
if (EyesInWaterDepth() == 0f && SystemSoundEngine.NowReverbness >= 0.25f ...)
{
    loadedSound.SetReverb(SystemSoundEngine.NowReverbness);
}
// <-- ADD OCCLUSION CHECK HERE
```

### Implementation
```csharp
[HarmonyPatch(typeof(ClientMain))]
public class SoundOcclusionPatch
{
    [HarmonyPostfix]
    [HarmonyPatch("LoadSound")]
    static void ApplyOcclusion(ref ILoadedSound __result, SoundParams sound)
    {
        if (__result == null || sound.Position == null) return;

        Vec3d playerPos = GetPlayerEyePosition();
        Vec3d soundPos = sound.Position.ToVec3d();

        float occlusion = CalculateOcclusion(soundPos, playerPos);
        if (occlusion > 0)
        {
            float filterValue = 1f - (occlusion * 0.8f); // 0.2 to 1.0
            __result.SetLowPassfiltering(filterValue);
        }
    }
}
```

### Occlusion Calculation (Simplified)
```csharp
float CalculateOcclusion(Vec3d from, Vec3d to)
{
    float occlusion = 0f;
    BlockPos current = from.AsBlockPos;
    Vec3d direction = (to - from).Normalize();
    float distance = from.DistanceTo(to);

    for (float d = 0; d < distance; d += 0.5f)
    {
        Vec3d point = from + direction * d;
        BlockPos blockPos = point.AsBlockPos;
        Block block = world.BlockAccessor.GetBlock(blockPos);

        if (block.Id != 0) // Not air
        {
            occlusion += GetBlockOcclusion(block);
        }

        if (occlusion >= MAX_OCCLUSION) break;
    }

    return Math.Min(occlusion, MAX_OCCLUSION);
}
```

---

## Phase 2: Material-Based Values

### Block Material Occlusion Table
```json
{
  "Stone": 1.0,
  "Brick": 1.0,
  "Ceramic": 0.9,
  "Metal": 0.95,
  "Wood": 0.5,
  "Leaves": 0.1,
  "Cloth": 0.3,
  "Glass": 0.15,
  "Gravel": 0.4,
  "Sand": 0.3,
  "Snow": 0.2,
  "Ice": 0.7,
  "Plant": 0.05,
  "Air": 0.0
}
```

### Get Block Material
```csharp
float GetBlockOcclusion(Block block)
{
    // Check custom overrides first
    if (customOcclusionValues.TryGetValue(block.Code, out float value))
        return value;

    // Fall back to material type
    return materialOcclusion.GetValueOrDefault(block.BlockMaterial, 0.5f);
}
```

---

## Phase 3: Weather/Wind Occlusion

### Detect Weather Sounds
```csharp
bool IsWeatherSound(SoundParams sound)
{
    return sound.SoundType == EnumSoundType.Weather ||
           sound.SoundType == EnumSoundType.Ambient &&
           sound.Location.Path.Contains("weather");
}
```

### Special Handling
Weather sounds don't have a specific position - they're ambient.

**Strategy**: Check player enclosure instead
```csharp
float GetPlayerEnclosure()
{
    // Cast rays upward and in cardinal directions
    // Count how many hit solid blocks
    // Return enclosure factor 0-1
}
```

---

## Critical API References

### OpenAL EFX (Already Used by VS)
```csharp
// Low-pass filter (for muffling)
AL.Source(sourceId, ALSourcei.DirectFilter, filterId);

// Reverb (already used)
EFX.AuxiliaryEffectSlot(slotId, EffectSlotInteger.Effect, reverbId);
```

### VS Sound Interfaces
```csharp
interface ILoadedSound
{
    void SetLowPassfiltering(float value);  // 0-1, 1=no filter
    void SetPitchOffset(float value);
    void SetReverb(float reverbness);
    void SetVolume(float volume);
    void SetPosition(Vec3f pos);
    SoundParams Params { get; }
}

class SoundParams
{
    public Vec3f Position;
    public EnumSoundType SoundType;
    public AssetLocation Location;
    public float Volume;
    public bool RelativePosition;
}
```

---

## Performance Considerations

### From Minecraft Sound Physics
1. **Cache results** - Same sound position + player position = same result
2. **Limit rays per frame** - Don't process all sounds every tick
3. **Use thread pool** - VS already has `TyronThreadPool.QueueLongDurationTask()`
4. **Skip distant sounds** - Beyond certain range, don't bother calculating

### VS-Specific
```csharp
// VS already uses this pattern for reverb
if (!scanning)
{
    TyronThreadPool.QueueLongDurationTask(calculateOcclusionOffthread);
}
```

---

## Files to Create

```
sound-physics-adapted/
├── src/
│   ├── SoundPhysicsAdaptedMod.cs        # ModSystem entry point
│   ├── OcclusionCalculator.cs      # Raycast logic
│   ├── SoundPatches.cs             # Harmony patches
│   ├── MaterialConfig.cs           # Block occlusion values
│   └── Config.cs                   # User settings
├── assets/
│   └── soundphysicsadapted/
│       └── config/
│           └── occlusion.json      # Material values
└── modinfo.json
```

---

## Next Steps

1. [ ] Set up VS mod project with Harmony
2. [ ] Find exact method signatures to patch
3. [ ] Implement basic raycast occlusion
4. [ ] Test with simple stone walls
5. [ ] Add material-based values
6. [ ] Handle ambient weather sounds (rain/wind need `GetPlayerEnclosure()`)
   - Note: Thunder/lightning works naturally with raycast - uses strike position
7. [ ] Add config UI
