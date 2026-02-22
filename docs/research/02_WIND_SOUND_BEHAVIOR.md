# Wind Sound Behavior in Vintage Story

## The Problem
User reports: "Wind still plays at full volume at cave openings but is muffled deep underground"

---

## How VS Determines Underground Status

### Primary Method: Sunlight Level
From research and code analysis:

1. **`sunv` value** - Sun exposure at player location (visible in Ctrl+F3 debug)
2. **Threshold-based** - Below certain sunlight = underground
3. **NOT block-based** - Does not count blocks between player and sky

### Why Cave Openings Still Have Wind

```
Surface (full sun)        Cave Opening           Deep Cave
      |                        |                      |
   sunv=15                  sunv=8                  sunv=0
      |                        |                      |
  Full wind              Still above            Below threshold
                         threshold!             = muffled wind
```

The sunlight system considers cave openings as "partially lit" areas, not enclosed spaces.

---

## Wind Sound Technical Details

### Sound Type
Wind uses `EnumSoundType.Weather` which has:
- Separate volume slider (`WeatherSoundLevel`)
- Applied multiplier: `ClientSettings.WeatherSoundLevel / 100f`

### Current Attenuation System
From Cave Symphony mod research:
- Uses "anchor blocks" that trigger wind sounds
- Wind sounds can fade when anchor's sunlight < `MinLightThreshold`
- But this is binary: above/below threshold, not gradual

---

## What Cave Symphony Mod Does

Reference: Salty's Cave Symphony (mods.vintagestory.at/cavesymphony)

1. **Atmospheric HUM** - Depth-based underground ambience
2. **Wind in tunnels** - Triggers at cave entrances/exits
3. **Auto-fadeout** - When "anchor blocks" detect enclosed room
4. **Sunlight checking** - Uses sunlight level, similar to vanilla

### Key Limitation
Cave Symphony ALSO uses sunlight-based detection, so it shares the same cave opening issue!

---

## The Solution We Need

### Current System (Sunlight-based)
```
Player position -> Check sunlight level -> Binary underground/surface
```

### Proposed System (Block-based)
```
Sound source -> Raycast to player -> Count/type blocks -> Calculate attenuation
```

### Specific Fix for Wind
1. Detect wind ambient sounds (by AssetLocation or SoundType)
2. Raycast from "sky" (or wind source position) to player
3. If blocked by N solid blocks, apply lowpass filter
4. Gradual attenuation, not binary

---

## Technical Implementation Notes

### Option A: Harmony Patch on Weather Sounds
Intercept weather sound playback, check player enclosure, modify volume/filter

### Option B: Custom Ambient Modifier
Register an AmbientModifier that adjusts weather sounds based on block enclosure

### Option C: Replace Weather Sound System
More invasive - create custom weather ambient system with occlusion

---

## Research Questions to Answer

1. Where exactly are weather sounds triggered? (`SystemWeather`? Ambient system?)
2. Can we get the current wind sound's ILoadedSound to modify it?
3. Does VS have a "player enclosure" calculation we can reuse?
4. How does the RoomLocation cuboid get calculated - can we use that?

---

## References
- Salty's Cave Symphony: https://mods.vintagestory.at/cavesymphony
- Salty's Forest Symphony: https://mods.vintagestory.at/forestsymphony
- VS Wiki - Sound Assets: https://wiki.vintagestory.at/Modding:Asset_Type_-_Sounds
