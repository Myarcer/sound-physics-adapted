# Future Phase: Outdoor Valley/Canyon Echo

**Date**: 2026-02-05
**Status**: CONCEPT - Separated from Phase 3 for later implementation
**Dependencies**: Phase 3 (reverb system established)

---

## Overview

This document describes a **future feature** for outdoor echo in valleys/canyons.
This is NOT part of Phase 3 and should be implemented in a later phase.

**Key Distinction:**
- **Reverb** = Many blended reflections (indoor rooms, caves)
- **Echo** = Distinct delayed repetitions (outdoor, distant surfaces)

---

## What SPR Does

SPR does NOT have specific valley/canyon detection. Their reverb uses 4 slots with increasing decay times (0.15s -> 4.14s), and distant reflections naturally contribute to longer decay. No dedicated outdoor echo effect.

---

## Proposed Feature (VS-Specific)

OpenAL EFX has a dedicated **Echo effect** (`AL_EFFECT_ECHO`) separate from reverb.

### Valley/Canyon Detection Algorithm

```csharp
public enum OutdoorEnvironment
{
    Open,           // No nearby surfaces (plains, ocean)
    Valley,         // Walls on 2 sides (canyon, ravine)
    Enclosed,       // Walls on 3-4 sides (deep canyon)
    Mountain        // High cliffs nearby
}

public static class OutdoorEchoDetector
{
    private const float MIN_ECHO_DISTANCE = 30f;  // Minimum distance for echo
    private const float MAX_ECHO_DISTANCE = 150f; // Maximum ray distance
    private const float SKY_CHECK_HEIGHT = 20f;   // Check for sky visibility

    /// <summary>
    /// Detect outdoor environment type for echo application.
    /// Only runs when player is outdoors (sky visible).
    /// </summary>
    public static (OutdoorEnvironment env, float avgDistance) Analyze(
        Vec3d playerPos,
        IBlockAccessor blockAccessor)
    {
        // Step 1: Check if outdoors (sky visible)
        if (!IsSkyVisible(playerPos, blockAccessor))
            return (OutdoorEnvironment.Open, 0f); // Indoors, no echo

        // Step 2: Cast horizontal rays to find distant surfaces
        int wallsDetected = 0;
        float totalDistance = 0f;
        int hitCount = 0;

        // Cast 8 horizontal rays
        for (float angle = 0f; angle < 360f; angle += 45f)
        {
            Vec3d direction = DirectionFromYaw(angle);
            var hit = RaycastHorizontal(playerPos, direction, MAX_ECHO_DISTANCE, blockAccessor);

            if (hit.HasValue && hit.Value.distance >= MIN_ECHO_DISTANCE)
            {
                wallsDetected++;
                totalDistance += hit.Value.distance;
                hitCount++;
            }
        }

        float avgDistance = hitCount > 0 ? totalDistance / hitCount : 0f;

        // Step 3: Classify environment
        OutdoorEnvironment env = wallsDetected switch
        {
            >= 6 => OutdoorEnvironment.Enclosed,   // Deep canyon/crater
            >= 3 => OutdoorEnvironment.Valley,     // Valley/ravine
            >= 1 => OutdoorEnvironment.Mountain,   // Near cliff/mountain
            _ => OutdoorEnvironment.Open           // Open plains
        };

        return (env, avgDistance);
    }

    private static bool IsSkyVisible(Vec3d pos, IBlockAccessor blockAccessor)
    {
        // Check blocks above player
        for (int y = 1; y <= SKY_CHECK_HEIGHT; y++)
        {
            BlockPos above = new BlockPos((int)pos.X, (int)pos.Y + y, (int)pos.Z);
            Block block = blockAccessor.GetBlock(above);
            if (block != null && block.Id != 0 && block.BlockMaterial != EnumBlockMaterial.Leaves)
                return false;
        }
        return true;
    }
}
```

### OpenAL Echo Effect Parameters

```csharp
// Echo delay based on distance (sound travel time)
// Speed of sound ~ 343 m/s, VS blocks ~ 1m
// Round trip: delay = (distance * 2) / 343
float delay = Math.Clamp((avgDistance * 2f) / 343f, 0.01f, 0.207f);

// EFX Echo parameters
EFX.Effect(echoEffect, EffectFloat.EchoDelay, delay);
EFX.Effect(echoEffect, EffectFloat.EchoLRDelay, delay * 0.5f);
EFX.Effect(echoEffect, EffectFloat.EchoDamping, damping);
EFX.Effect(echoEffect, EffectFloat.EchoFeedback, feedback);
EFX.Effect(echoEffect, EffectFloat.EchoSpread, 0.7f);
```

### Environment -> Echo Characteristics

| Environment | Detection | Delay | Feedback | Effect |
|-------------|-----------|-------|----------|--------|
| **Open** | 0-1 distant walls | - | 0 | No echo |
| **Mountain** | 1-2 walls | ~0.1-0.2s | 0.2 | Single faint echo |
| **Valley** | 3-5 walls | ~0.15-0.3s | 0.4 | Multiple echoes |
| **Enclosed** | 6+ walls | ~0.1-0.2s | 0.5 | Strong echoes (canyon) |

### Config Options (Future)

```csharp
public bool EnableOutdoorEcho { get; set; } = true;
public float MinEchoDistance { get; set; } = 30f;
public float MaxEchoDistance { get; set; } = 150f;
public float EchoGain { get; set; } = 0.5f;
```

---

## This Would Be Unique to VS

SPR doesn't use `AL_EFFECT_ECHO` - they rely only on reverb with varying decay times. True echo (distinct delayed repetitions) would be a VS-exclusive feature.

---

## Implementation Phase: TBD

This feature should be implemented after Phase 3 reverb is stable and tested.
