# Sound Physics Adapted - Phase 3 Reverb Handoff

## Project Location
```
Y:\ClaudeWINDOWS\projects\sound-physics-adapted
Branch: phase3-spr-rewrite
```

## Current Status: MOSTLY WORKING - ONE ISSUE REMAINS

### What Works
- EFX reverb initialization (4 aux slots, 4 EAX reverb effects)
- Send filter gains control reverb intensity (SPR-style)
- Positional sounds get environment-based reverb
- Non-positional sounds (block breaking, tools) use player position
- Material reflectivity affects reverb character
- Enclosed spaces (caves, houses) get proper reverb

### The Remaining Issue: Open Sky Still Has Audible Reverb

**Log Evidence (open sky, lantern placement):**
```
REVERB CALC: g0=0,12 g1=0,08 g2=0,00 g3=0,00 enclosed=59% (19/32) dist=0,0
```

**Analysis:**
- 59% enclosure is EXPECTED under open sky (ground hits account for ~50% of rays)
- g0=0.12 seems low, but user still hears noticeable reverb
- Compare to enclosed wooden house: `g0=0.52, enclosed=100%`

**The Problem:**
Even low gains (0.12) produce audible reverb. Under open sky, reverb should be nearly inaudible, not just "less than cave."

---

## Potential Solutions to Investigate

### 1. Minimum Threshold
Add a threshold below which reverb is disabled entirely:
```csharp
if (sendGain0 < 0.15f) sendGain0 = 0f;
```

### 2. Vertical Ray Weighting
Open sky = rays going UP escape, rays going DOWN hit ground.
Could weight reverb by how many rays hit WALLS (horizontal) vs GROUND (downward):
- Ground-only hits = outdoor (minimal reverb)
- Wall + ceiling hits = indoor (full reverb)

### 3. Sky Detection
Check if upward rays escape - if most do, dramatically reduce reverb:
```csharp
int upwardRaysEscaped = 0;
// Count rays with positive Y direction that didn't hit
if (rayDir.Y > 0.3 && !hit.HasValue) upwardRaysEscaped++;
// If >80% upward rays escape, it's open sky
```

### 4. Reduce Effect Gain Further
Current effect settings might be too loud. Try reducing:
```csharp
EfxHelper.SetReverbGain(effect, 0.20f);  // Currently 0.32
EfxHelper.SetReverbLateReverbGain(effect, 0.8f);  // Currently 1.26
```

### 5. Check SPR's Approach
SPR config has `reverbAttenuationDistance` (default 0) - bandaid for distance attenuation.
Also check their default reflectivity values and effect parameters.

---

## Key Files

| File | Purpose |
|------|---------|
| `src/Core/ReverbCalculator.cs` | Ray tracing, gain calculation |
| `src/Core/ReverbEffects.cs` | Aux slot/effect management, ApplyToSource |
| `src/Core/EfxHelper.cs` | OpenAL EFX reflection wrapper |
| `src/Patches/LoadSoundPatch.cs` | Hooks sound playback, applies reverb |
| `src/Patches/ReverbPatch.cs` | Disables vanilla reverb |
| `docs/REVERB_ISSUES.md` | Documentation |

---

## Recent Fixes (This Session)

1. **Send filter gains** - Was passing filter=0 (full signal), now uses calculated gains
2. **Ray coordinate swap** - Y/Z were swapped, vertical rays went horizontal
3. **Removed openness penalty** - Was double-counting (escaped rays already = 0 energy)
4. **EAX parameter names** - Fixed `ReverbDecayTime` → `EaxReverbDecayTime`
5. **Aux slot gain** - Added `SetAuxSlotGain(slot, 1.0f)` (was never set)

---

## Build & Test

```bash
cd Y:/ClaudeWINDOWS/projects/sound-physics-adapted/src
dotnet build -c Release
cp Releases/soundphysicsadapted_v0.1.0.zip /c/Users/marck/AppData/Roaming/VintagestoryData/Mods/
```

**Logs:**
- `C:\Users\marck\AppData\Roaming\VintagestoryData\Logs\client-main.log`
- `C:\Users\marck\AppData\Roaming\VintagestoryData\Logs\client-debug.log`

**What to look for:**
```
REVERB CALC: g0=X.XX g1=X.XX g2=X.XX g3=X.XX enclosed=XX% (hits/total) dist=X.X
```

---

## Test Scenarios

1. **Open sky** - Place lanterns on flat ground, no structures nearby
   - Expected: Near-zero reverb, <20% gains
   - Current: 59% enclosed, g0=0.12 (too audible)

2. **Enclosed wooden house** - Small room, wood walls, no windows
   - Expected: Subtle reverb (wood absorbs)
   - Current: 100% enclosed, g0=0.52 (working)

3. **Stone cave** - Underground tunnel
   - Expected: Strong reverb, long tail
   - Current: Working well

---

## SPR Reference Code
```
Y:\ClaudeWINDOWS\projects\sound-physics-adapted\references\sound-physics-remastered-master\
```

Key file: `common/src/main/java/com/sonicether/soundphysics/SoundPhysics.java`

---

## Config Options (modconfig/soundphysicsadapted.json)
```json
{
  "EnableCustomReverb": true,
  "ReverbGain": 1.0,
  "ReverbRayCount": 32,
  "ReverbBounces": 4,
  "ReverbMaxDistance": 256,
  "DebugReverb": true
}
```
