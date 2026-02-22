# Vintage Story Current Audio Limitations

## Summary of What VS Audio System Actually Does

VS has a **room-based binary system**, not a **physics-based continuous system**.

---

## 1. Room Detection System (Binary)

### How It Works
- Game detects if player is in an "enclosed room"
- Room = surrounded by solid blocks on all sides
- **Binary state**: Either IN room or NOT in room

### The Door Problem
```
Door CLOSED:          Door OPEN:
┌─────────────┐      ┌─────────────┐
│ ▓▓▓▓▓▓▓▓▓▓▓ │      │ ▓▓▓▓▓   ▓▓▓ │
│ ▓         ▓ │      │ ▓         ▓ │
│ ▓  Player ▓ │      │ ▓  Player ▓ │
│ ▓         ▓ │      │ ▓         ▓ │
│ ▓▓▓▓▓▓▓▓▓▓▓ │      │ ▓▓▓▓▓▓▓▓▓▓▓ │
└─────────────┘      └─────────────┘
  Weather: MUTED       Weather: FULL VOLUME

Single open door = room detection FAILS = full weather sound
```

**Real physics**: Open door should slightly increase weather sound, not go from 0% to 100%

---

## 2. Weather Sound Handling

### Current Behavior
| Scenario | Weather Sound |
|----------|---------------|
| Outside | 100% |
| Inside, door open | 100% |
| Inside, door closed | Muted |
| Cave opening | 100% (sunlight-based) |
| Deep cave | Muted |

### What Should Happen (Physics-Based)
| Scenario | Weather Sound |
|----------|---------------|
| Outside | 100% |
| Inside, door open | ~70% + muffled |
| Inside, door closed | ~20% + heavily muffled |
| Cave opening | ~60% + slightly muffled |
| Deep cave | ~5% + very muffled |

---

## 3. Cave Reverb Limitations

### Current System
- Detects reflective materials (stone, metal, etc.)
- Calculates single "reverbness" value (0-7)
- Uses 24 pre-baked reverb presets
- Only sounds INSIDE detected room get reverb

### What's Missing
| Feature | Current | Should Be |
|---------|---------|-----------|
| Reverb intensity | Single value | Based on room size/shape |
| Reverb decay | Fixed presets | Dynamic based on materials |
| External sounds | No reverb | Should reverb in your space |
| Cave "character" | Generic | Different caves sound different |

### The Thunder Example
```
Thunder position: Sky (Y=200)
Player position: Cave (Y=50)
Blocks between: 150 blocks of stone

Current VS behavior:
→ No occlusion calculation
→ Thunder at FULL VOLUME in cave
→ Sounds like you're standing outside

What should happen:
→ Raycast from sky to player
→ 150 blocks × ~1.0 occlusion = heavily muffled
→ Thunder barely audible, very muffled
```

---

## 4. Sound Occlusion (Non-Existent)

### Current: Nothing
Sounds pass through ANY number of blocks at full volume and clarity.

### Examples of What's Wrong
| Scenario | Current | Reality |
|----------|---------|---------|
| Monster behind 1 stone wall | Full volume | Slightly muffled |
| Monster behind 10 stone walls | Full volume | Nearly inaudible |
| Player crafting in next room | Full volume | Muffled |
| Thunder during storm, inside | Full volume | Muffled by roof |

---

## 5. Material Properties (Hardcoded)

### Current Reflectivity (Reverb Only)
Only these materials count for reverb:
- Stone, Metal, Ore, Mantle, Ice, Ceramic, Brick

### Missing: Absorption Values
No materials have absorption/occlusion values:
- Wool should heavily absorb sound
- Glass should barely block sound
- Wood should moderately block sound
- Leaves should barely affect sound

---

## 6. Summary: VS Audio Gaps

### Not Implemented At All
- [ ] Sound occlusion through blocks
- [ ] Material-based absorption
- [ ] Gradual weather attenuation
- [ ] Per-block sound blocking
- [ ] **Weather/thunder occlusion** (full volume even underground)
- [ ] Sound path calculation

### Partially Implemented
- [~] Room reverb (simplified, single value)
- [~] Underground detection (sunlight-based, not block-based)
- [~] Weather muffling (binary room check, not gradual)

### Fully Implemented
- [x] Underwater audio effects
- [x] Basic 3D positional audio
- [x] Volume categories (weather, ambient, entity, etc.)
- [x] OpenAL EFX support (infrastructure exists)
