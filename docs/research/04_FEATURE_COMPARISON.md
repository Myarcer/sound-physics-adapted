# Feature Comparison: MC Sound Physics vs VS Built-in

## Quick Answer

**VS has approximately 30-40% of Sound Physics Remastered features**, but implemented differently:

| Feature | MC Sound Physics | VS Built-in | Gap |
|---------|------------------|-------------|-----|
| Sound Occlusion | Full raycast | NONE | **MISSING** |
| Material Absorption | Per-block config | NONE | **MISSING** |
| Room Reverb | Ray-bounce algorithm | Simplified raycast | PARTIAL |
| Material Reflectivity | Per-block config | Hardcoded materials only | PARTIAL |
| Underwater Filter | Yes | Yes | COMPLETE |
| Multiple Reverb Channels | 4 aux slots | 1 slot, 24 presets | PARTIAL |
| Sound Repositioning | Based on reflections | NONE | **MISSING** |
| Air Absorption | Configurable | NONE | **MISSING** |
| Doppler Effect | NONE | NONE | N/A |

---

## Detailed Feature Analysis

### 1. SOUND OCCLUSION - **NOT IN VS**

**MC Sound Physics:**
```java
// Raycast from sound to player
for (int i = 0; i < maxOcclusionRays; i++) {
    BlockHitResult rayHit = rayCast(soundPos, playerPos);
    float blockOcclusion = OCCLUSION_CONFIG.getBlockValue(blockHit);
    occlusionAccumulation += blockOcclusion;
}
// Apply lowpass filter based on accumulated occlusion
directCutoff = exp(-occlusionAccumulation * absorptionCoeff);
```

**VS:**
```csharp
// NOTHING - sounds pass through blocks at full volume
```

**Impact:** Monster growling behind 10 blocks of stone = same volume as right next to you

---

### 2. REVERB SYSTEM - PARTIAL IN VS

**MC Sound Physics:**
- 11+ rays bounced multiple times
- Calculates room SIZE and SHAPE
- 4 separate reverb channels for different delay times
- Per-block reflectivity values (stone=1.5, wool=0.1)

**VS:**
- 40 rays (8 horizontal × 5 vertical at 45° intervals)
- Only counts hits on reflective materials
- Single reverb value (0.5 to 7.0), 24 presets
- Hardcoded materials only: Stone, Metal, Ore, Mantle, Ice, Ceramic, Brick
- **NO room shape detection** - only creates bounding box (AABB)

**Key Difference:** VS reverb is simpler but functional for caves. MC mod has nuanced room acoustics. VS cannot distinguish tunnel from cavern - only overall size via bounding box.

---

### 3. MATERIAL SYSTEM - PARTIAL IN VS

**MC Sound Physics - Configurable per-block:**
```json
// OcclusionConfig
"wool": 1.5,
"glass": 0.1,
"leaves": 0.0,
"stone": 1.0

// ReflectivityConfig
"stone": 1.5,
"metal": 1.25,
"wool": 0.1,
"wood": 0.4
```

**VS - Hardcoded in code:**
```csharp
// Only these materials trigger reverb:
if (block.BlockMaterial == EnumBlockMaterial.Metal ||
    block.BlockMaterial == EnumBlockMaterial.Ore ||
    block.BlockMaterial == EnumBlockMaterial.Stone ||
    // etc...
```

**Impact:** No absorption values at all. Can't configure materials.

---

### 4. UNDERWATER AUDIO - COMPLETE IN VS

Both systems apply:
- Low-pass filter (muffled sound)
- Pitch reduction

VS implementation:
```csharp
loadedSound.SetLowPassfiltering(0.06f);  // Heavy muffling
loadedSound.SetPitchOffset(-0.15f);       // Lower pitch
```

---

### 5. SOUND REPOSITIONING - **NOT IN VS**

**MC Sound Physics:**
Sounds can appear to come from around corners based on reflection paths.
```java
Vec3 newSoundPos = audioDirection.evaluateSoundPosition(soundPos, playerPos);
if (newSoundPos != null) {
    setSoundPos(sourceID, newSoundPos);
}
```

**VS:**
Sound always comes from original source position.

---

## The Thunder Problem

### The Real Issue: NO OCCLUSION

Thunder's primary problem is **lack of occlusion/muffling**, not reverb.

VS's base game does apply SOME reverb to weather/ambient sounds. The issue is:

**Thunder plays at FULL VOLUME indoors** because VS has no block-based sound occlusion.

### What Should Happen

| Scenario | Current VS | Should Be |
|----------|------------|-----------|
| Outside during storm | Full volume | Full volume |
| Under tree canopy | Full volume | ~90% volume |
| Inside house, door open | Full volume | ~50% volume, muffled |
| Inside house, door closed | Full volume | ~30% volume, heavily muffled |
| Deep in cave | Full volume | ~5% volume, very muffled |

### Why It's Wrong

VS checks for "room" status but:
1. No raycast from sky to player
2. No block absorption calculation
3. Weather sounds bypass all occlusion

### Real Physics

Thunder heard from inside a cave should:
1. Be **heavily muffled** (blocked by many meters of rock) ← **MAIN ISSUE**
2. Have reverb (VS partially does this)
3. Sound distant (volume reduction)

---

## What Our Mod Needs to Add

### Priority 1: Sound Occlusion
- Raycast from sound source to player
- Accumulate block absorption
- Apply lowpass filter

### Priority 2: Fix External Sound Reverb
- Sounds OUTSIDE room should still get player's room reverb
- Thunder in sky → still reverbs in your cave
- This is actually MORE realistic

### Priority 3: Material Configuration
- JSON config for per-block values
- Allow players to customize

### Priority 4: Weather Sound Special Handling
- Detect weather/ambient sounds
- Apply enclosure-based muffling
- Not position-based (they're "everywhere")

---

## Summary Table

| What VS Has | What's Missing |
|-------------|----------------|
| Cave reverb (simplified) | Sound occlusion through blocks |
| Underwater muffling | Material-based absorption |
| Hardcoded reflective materials | Configurable material values |
| Single reverb channel | Multi-channel reverb |
| Position-based reverb check | External sound reverb |
| Weather volume slider | Weather occlusion |
