# Reverb System Architecture

**Last Updated**: 2026-02-06
**Phase 3 Status**: COMPLETE
**Phase 4 Status**: COMPLETE (Shared Airspace Detection)

---

## 1. Current System Overview (Phase 3 - Complete)

### What We Built
SPR-style complete reverb replacement:
- **4 EAX reverb slots** with different decay times (0.15s, 0.55s, 1.68s, 4.14s)
- **32-ray fibonacci sphere** distribution for environment sampling
- **4-bounce ray tracing** for realistic reflection paths
- **Material-based reflectivity** (stone=1.5, wood=0.4, cloth=0.1, etc.)
- **Send filter gains** control reverb intensity per-source (SPR-style)
- **Vanilla reverb disabled** via Harmony patch

### Key Files
| File | Purpose |
|------|---------|
| `Core/ReverbCalculator.cs` | Ray tracing, gain calculation |
| `Core/ReverbEffects.cs` | EFX slot/effect management |
| `Core/EfxHelper.cs` | OpenAL EFX reflection wrapper |
| `Patches/ReverbPatch.cs` | Disables vanilla SetReverb |
| `Patches/LoadSoundPatch.cs` | Applies reverb to sounds |

### SPR Formula (What We Use)
```csharp
// Energy contribution - NO distance penalty (unlike vanilla VS!)
float energyTowardsPlayer = 0.25f * (reflectivity * 0.75f + 0.25f);

// Reflection delay based on total ray travel
float reflectionDelay = totalDistance * 0.12f * reflectivity;

// Distribute to 4 reverb slots by delay
float cross0 = 1f - Clamp(Abs(reflectionDelay - 0f), 0f, 1f);
float cross1 = 1f - Clamp(Abs(reflectionDelay - 1f), 0f, 1f);
// ... etc

sendGain0 += cross0 * energyTowardsPlayer * 6.4f * rcpTotalRays;
sendGain1 += cross1 * energyTowardsPlayer * 12.8f * rcpTotalRays;
```

### Effect Gains (SPR-Matched)
| Slot | Effect Gain | Decay Time | Purpose |
|------|-------------|------------|---------|
| 0 | 0.12 | 0.15s | Early reflections |
| 1 | 0.18 | 0.55s | Medium-short |
| 2 | 0.30 | 1.68s | Medium-long |
| 3 | 0.24 | 4.14s | Late reverb tail |

---

## 2. Known Issue: Reverb Ignores Occlusion

### The Problem
Reverb is calculated at SOURCE position only. Occluded sounds in enclosed spaces get strong reverb that can't actually reach the player.

### Example 1: Bee in Sealed Chamber
```
    YOU (player above ground)
    ═══════════════════════════════════════════  Ground
    ████████████████████████████████████████████  Stone
    ████████  ┌───────────┐  ███████████████████
    ████████  │  🐝 BEE   │  ███████████████████  21m below
    ████████  └───────────┘  ███████████████████
    ████████████████████████████████████████████

Log: g0=0.64, enclosed=100%, occlusion=heavy, filter=0.05
Result: Muffled bee + LOUD cave reverb = Unnatural
```

### Example 2: Underground Creek/Waterfall
```
Log evidence:
creek.ogg occlusion=9,00 filter=0,050
REVERB CALC: g0=0,91 g1=0,91 g2=0,91 enclosed=100%

Result: Very quiet muffled water + MASSIVE cave echo = Wrong
```

### Root Cause
| System | Calculation | Problem |
|--------|-------------|---------|
| Occlusion | Direct raycast source→player | Works correctly |
| Reverb | Rays from source, bounce off walls | Ignores if bounces can reach player |
| Combined | Muffled direct + full reverb | Reverb not attenuated |

---

## 3. Phase 4: Shared Airspace (IMPLEMENTED)

### Concept
Only count reverb from reflections that can actually reach the player.

### Implementation (ReverbCalculator.cs)

```csharp
// In ReverbCalculator.Calculate() - for each bounce:
bool hasAirspace = HasSharedAirspace(lastHitPos, lastHitNormal, playerPos, blockAccessor);

if (hasAirspace)
{
    sharedAirspaceCount++;
    // SPR energy formula - only applied when player can hear this reflection
    float energyTowardsPlayer = 0.25f * (reflectivity * 0.75f + 0.25f);
    sendGain0 += cross0 * energyTowardsPlayer * 6.4f * rcpTotalRays;
    // ... etc for other slots
}
// If no shared airspace, this bounce contributes NOTHING to reverb
```

### HasSharedAirspace Methods

```csharp
/// Check if bounce point has line-of-sight to player (with surface normal offset)
private static bool HasSharedAirspace(Vec3d bouncePos, Vec3d surfaceNormal,
                                       Vec3d playerPos, IBlockAccessor blockAccessor)
{
    // Offset 0.15 blocks from surface along normal to avoid self-intersection
    Vec3d testStart = bouncePos + surfaceNormal * 0.15;
    return HasSharedAirspace(testStart, playerPos, blockAccessor);
}

/// Simple raycast from source to player - returns true if clear path
private static bool HasSharedAirspace(Vec3d sourcePos, Vec3d playerPos,
                                       IBlockAccessor blockAccessor)
{
    Vec3d toPlayer = playerPos - sourcePos;
    double distance = toPlayer.Length();
    Vec3d direction = toPlayer.Normalize();

    var hit = RaycastToSurface(sourcePos, direction, (float)distance, blockAccessor);
    return !hit.HasValue; // No hit = clear path = shared airspace
}
```

### Expected Results After Fix

**Bee Underground (21m)**:
- 32 rays × 4 bounces = 128 bounce points inside chamber
- 0 bounce points can see player through stone
- Shared airspace: 0/128 (0%)
- Reverb: g0 ≈ 0 (CORRECT - no audible reverb)

**Creek Through Stone Walls**:
- Bounce points inside water cave can't see player
- Shared airspace: near 0%
- Reverb: near 0 (CORRECT)

**Cave With Player Inside**:
- Most bounce points can see player
- Shared airspace: 80-100%
- Reverb: high (CORRECT)

### Phase 4b: Sound Repositioning (FUTURE - Optional Enhancement)

**Goal**: Sounds through openings appear to come from the opening.
**Status**: Not yet implemented - reverb gating works well without this.

**Use Case - Air Shaft**:
```
    YOU (player)
         \
          \  sound via shaft
    ═══════╔════════════════════════  Ground
    ███████║████████████████████████
    ███████║████████████████████████  Air shaft
    ███████╔══════╗█████████████████
    ███████║ 🐝   ║█████████████████  Chamber with bee
    ███████╚══════╝█████████████████

Result: Sound appears to come from shaft opening
```

#### Reference Implementations

**SPR (Sound Physics Remastered)** - `references/sound-physics-remastered-master/`
- `ReflectedAudio.java`: Weighted average of shared airspace directions (1/d^2 weighting)
- `SoundPhysics.java:401-406`: Applies via `AL11.alSource3f(sourceID, AL11.AL_POSITION, ...)`
- Maintains original distance, only changes direction
- Config: `soundDirectionEvaluation` (default: true), `redirectNonOccludedSounds` (default: true)
- Skips repositioning for voicechat sounds and non-occluded sounds (when config set)

**SoundPhysicsPerfected (Red's fork)** - `references/SoundPhysicsPerfected/`
- More sophisticated approach with additional features:
- `RaycastingHelper.java:841-871`: `calculateWeightedAverages()` - weights by ray quality, not just distance
- `RaycastingHelper.java:210-278`: `playAveragedSoundWithAdjustments()` - applies repositioned coordinates
- `RedTickableInstance.java:103-133`: **Smooth position interpolation at speed of sound (17.15 blocks/tick)**
  - Wraps sound instance, overrides getX/Y/Z with animated position
  - Minecraft's audio engine reads updated coords naturally (no direct alSource3f)
  - Instant jump if very close (<0.001) or very far (> speed * tickRate)
- `Config.java:123`: `shortcutDirectionality` - if direct line of sight exists, skip weighted average
- Calculates BOTH weighted direction AND weighted distance (SPR only does direction)

#### Key Differences Between Approaches

| Feature | SPR | SoundPhysicsPerfected |
|---------|-----|----------------------|
| Weighting | 1/d^2 (distance only) | Ray quality * attenuation |
| Distance | Keeps original distance | Weighted average distance |
| Application | Direct alSource3f | Wrapper with smooth interpolation |
| Speed-of-sound | No | Yes (17.15 blocks/tick animation) |
| Direct LOS shortcut | No (config to skip non-occluded) | Yes (shortcutDirectionality) |

#### Our Implementation Plan

For Sound Physics Adapted, combine best of both:
1. **Data collection**: Already tracking shared airspace bounce points in ReverbCalculator
2. **Weighting**: Use SPR's 1/d^2 (simpler, proven effective)
3. **Application**: Direct OpenAL alSource3f (VS doesn't wrap sound instances like MC)
4. **Config**: Toggle on/off, skip for non-occluded sounds

```csharp
public class ReflectedAudio
{
    private List<(Vec3d direction, double distance)> sharedAirspaces = new();

    public void AddSharedAirspace(Vec3d bouncePoint, Vec3d toPlayer, double distance)
    {
        sharedAirspaces.Add((toPlayer.Normalize(), distance));
    }

    /// <summary>
    /// Calculate apparent sound position from weighted bounce directions.
    /// Closer bounces have more influence (inverse square weighting).
    /// </summary>
    public Vec3d? EvaluateSoundPosition(Vec3d originalPos, Vec3d playerPos)
    {
        if (sharedAirspaces.Count == 0)
            return null;  // No valid paths, use original

        Vec3d weightedDir = Vec3d.Zero;
        double totalWeight = 0;

        foreach (var (direction, distance) in sharedAirspaces)
        {
            double weight = 1.0 / (distance * distance);
            weightedDir = weightedDir.Add(direction.Scale(weight));
            totalWeight += weight;
        }

        if (totalWeight < 0.001)
            return null;

        Vec3d apparentDir = weightedDir.Scale(1.0 / totalWeight).Normalize();
        double originalDist = originalPos.DistanceTo(playerPos);

        // Place sound in apparent direction at original distance
        return playerPos.Add(apparentDir.Scale(originalDist));
    }
}

// Apply:
Vec3d? newPos = reflectedAudio.EvaluateSoundPosition(soundPos, playerPos);
if (newPos.HasValue)
{
    AL.Source3f(sourceId, ALSource3f.Position,
        (float)newPos.X, (float)newPos.Y, (float)newPos.Z);
}
```

#### Future Enhancement: Speed-of-Sound Animation
SoundPhysicsPerfected's smooth interpolation (17.15 blocks/tick) prevents jarring position jumps.
Could be added later if repositioning causes audible "snapping" when player/sound moves.

---

## 4. Fallback Fix (If Phase 4 Fails)

**Use only if shared airspace implementation has issues.**

Simple occlusion-based reverb attenuation:

```csharp
// In ReverbEffects.ApplyToSource():
// Reduce reverb when sound is heavily occluded

float occlusionFactor = 1.0f - Math.Min(occlusion / 10f, 0.9f);
// occlusion=0  → factor=1.0 → full reverb
// occlusion=5  → factor=0.5 → 50% reverb
// occlusion=9  → factor=0.1 → 10% reverb

sendGain0 *= occlusionFactor;
sendGain1 *= occlusionFactor;
sendGain2 *= occlusionFactor;
sendGain3 *= occlusionFactor;
```

**Pros**: Simple, immediately fixes loud reverb through walls
**Cons**: Not physically accurate, no sound repositioning, wrong for partial openings

---

## 5. Material Reflectivity

| Material | Reflectivity | Reverb Character |
|----------|--------------|------------------|
| Stone/Ore | 1.5 | Bright, long tail |
| Mantle | 1.5 | Deep resonance |
| Metal | 1.25 | Metallic ring |
| Brick | 1.3 | Warm |
| Ceramic | 1.1 | Clear |
| Ice | 0.9 | Crisp |
| Glass | 0.75 | Clean |
| Soil | 0.6 | Dull |
| Gravel | 0.5 | Scattered |
| Wood | 0.4 | Absorbed |
| Sand | 0.35 | Dead |
| Leaves | 0.2 | Diffuse |
| Snow | 0.15 | Muffled |
| Cloth | 0.1 | Near-silent |
| Plant | 0.1 | Absorbed |

---

## 6. Config Options

```json
{
  "EnableCustomReverb": true,
  "ReverbGain": 1.0,
  "ReverbRayCount": 32,
  "ReverbBounces": 4,
  "ReverbMaxDistance": 256,
  "DebugReverb": false
}
```

---

## 7. Debug Output

**Phase 4 Format** (current):
```
REVERB CALC: g0=X.XX g1=X.XX g2=X.XX g3=X.XX shared=X/X (XX%) enclosed=XX% dist=X.X direct=True/False
REVERB APPLIED src=XX: g0=X.XX g1=X.XX g2=X.XX g3=X.XX
```

**Key fields**:
- `shared=X/Y (Z%)` - Bounce points with line-of-sight to player / total bounce points
- `direct=True/False` - Whether sound source has direct line-of-sight to player
- `enclosed=XX%` - Percentage of rays that hit surfaces (vs escape to sky)

---

## 8. VS Vanilla Reverb (Disabled)

For reference, what we replaced:

### VS Formula (Broken for Small Spaces)
```csharp
contribution = (Log(distance + 1) / 18 - 0.07) * 3 * reflectivity
```

| Distance | Contribution |
|----------|-------------|
| 1 block | **-0.032** (negative!) |
| 2 blocks | **-0.009** (negative!) |
| 5 blocks | 0.029 (tiny) |
| 20 blocks | 0.099 (okay) |

Result: Tunnels and small rooms got near-zero reverb even in stone.

### VS Limitations
- Only 7 hardcoded materials (Stone, Metal, Ore, Mantle, Ice, Ceramic, Brick)
- No ray bouncing
- Single reverb value (0-1), not multi-slot
- Distance penalty breaks small spaces

---

## 9. References

- **SPR Source**: `references/sound-physics-remastered-master/`
  - `SoundPhysics.java` - Main reverb calculation
  - `ReflectedAudio.java` - Shared airspace & repositioning
  - `ReverbParams.java` - Effect gain values
- **OpenAL EFX Spec** - EAX reverb parameters
- **VS Analysis**: `research/06_VERIFIED_VS_AUDIO_BEHAVIOR.md`

---

## 10. Revision History

| Date | Change |
|------|--------|
| 2026-02-05 | Initial draft - Option B (Parallel + Blend) |
| 2026-02-05 | REWRITE - Option D (Complete Override, SPR approach) |
| 2026-02-05 | Phase 3 COMPLETE - SPR-style reverb working |
| 2026-02-06 | Added Phase 4 plan (shared airspace) |
| 2026-02-06 | Documented bee/creek bugs and fallback fix |
| 2026-02-06 | **Phase 4 COMPLETE** - Shared airspace detection implemented |
