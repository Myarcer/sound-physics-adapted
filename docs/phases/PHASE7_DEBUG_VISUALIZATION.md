# Phase 7: Debug Visualization System — PLANNED

**Last Updated**: 2026-02-10
**Depends On**: Core features (Phases 1-4). Can be implemented anytime as it's isolated from audio logic.
**Note**: This was part of old Phase 7 (Advanced Features). Old Phase 7 was split — polish/performance moved to Phase 6, debug viz remains Phase 7.

## Goal

Add in-game debug visualization for raytraces (occlusion, reverb bounces, sound repositioning, weather probes) with hot-reloadable toggle commands.

---

## SPR Reference

SPR uses `RaycastRenderer.java` implementing Minecraft's `DebugRenderer.SimpleDebugRenderer`:

```java
// Key patterns from SPR:
public class RaycastRenderer implements DebugRenderer.SimpleDebugRenderer {
    private static final List<Ray> rays = Collections.synchronizedList(new ArrayList<>());
    
    // Thread-safe ray storage with lifespan (ticks until fade out)
    // Rays auto-expire after 2 seconds (lifespan = 20 * 2)
    
    public static void addSoundBounceRay(Vec3 start, Vec3 end, int color) { ... }
    public static void addOcclusionRay(Vec3 start, Vec3 end, int color) { ... }
    
    // Render via Minecraft's Gizmos.line() with fadeOut() effect
}
```

**SPR Config Options:**
- `renderSoundBounces` - Show reverb bounce rays
- `renderOcclusion` - Show occlusion rays

---

## VS Rendering Options

### Option A: IRenderer Interface (Recommended)
VS uses `IRenderer` interface with `RegisterRenderer`:

```csharp
// From WeatherSimulationLightning.cs:
capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "lightning");

// IRenderer requires:
void OnRenderFrame(float deltaTime, EnumRenderStage stage);
void Dispose();
```

**Pros**: Native VS pattern, proper render pipeline integration
**Cons**: Need to build mesh data for lines

### Option B: HighlightBlocks API (Simple)
From `PathFindDebug.cs`:

```csharp
sapi.World.HighlightBlocks(
    player, 
    slotId,                    // Int ID (0-255 for different highlight sets)
    poses,                     // List<BlockPos>
    new List<int>() { ColorUtil.ColorFromRgba(128, 128, 128, 30) },
    EnumHighlightBlocksMode.Absolute, 
    EnumHighlightShape.Arbitrary
);
```

**Pros**: Simple API, no mesh building needed
**Cons**: Block-level only (not arbitrary lines), server-side only

### Option C: Line Mesh Rendering (Most Flexible)
Build line mesh data manually:

```csharp
MeshData mesh = new MeshData(4, 2);
mesh.AddVertex(start.X, start.Y, start.Z, color);
mesh.AddVertex(end.X, end.Y, end.Z, color);
mesh.AddIndex(0); mesh.AddIndex(1);
// Render as GL_LINES
```

**Pros**: Arbitrary 3D lines with any color/width
**Cons**: More complex, need shader setup

---

## Recommended Architecture

### New File: `Core/DebugRayRenderer.cs`

```csharp
public class DebugRayRenderer : IRenderer, IDisposable
{
    // Thread-safe ray storage (audio threads add rays)
    private static readonly ConcurrentQueue<DebugRay> pendingRays = new();
    private readonly List<DebugRay> activeRays = new();
    
    // Config-driven visibility
    public static bool ShowOcclusion => SoundPhysicsAdaptedModSystem.Config.DebugShowOcclusion;
    public static bool ShowReverb => SoundPhysicsAdaptedModSystem.Config.DebugShowReverb;
    public static bool ShowRepositioning => SoundPhysicsAdaptedModSystem.Config.DebugShowRepositioning;
    
    // Static methods called from audio calculation code
    public static void AddOcclusionRay(Vec3d start, Vec3d end, float occlusion) { ... }
    public static void AddReverbBounce(Vec3d start, Vec3d end, int bounceNum) { ... }
    public static void AddRepositionPath(Vec3d original, Vec3d repositioned) { ... }
    
    // IRenderer implementation
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        // 1. Drain pending rays from audio threads
        // 2. Remove expired rays
        // 3. Render active rays as lines
    }
    
    // Color coding:
    // - Occlusion: Green (clear) → Red (heavy occlusion)
    // - Reverb bounces: Blue (1st) → Cyan (2nd) → White (3rd+)
    // - Repositioning: Yellow (original→repositioned line)
}

public struct DebugRay
{
    public Vec3d Start, End;
    public int Color;
    public long ExpireTimeMs;
    public DebugRayType Type;
}

public enum DebugRayType { Occlusion, Reverb, Repositioning }
```

### Config Additions: `SoundPhysicsConfig.cs`

```csharp
// Debug Visualization
public bool DebugShowOcclusion { get; set; } = false;
public bool DebugShowReverb { get; set; } = false;
public bool DebugShowRepositioning { get; set; } = false;
public int DebugRayLifespanMs { get; set; } = 2000;  // 2 seconds
public float DebugRayWidth { get; set; } = 2.0f;
```

### Chat Commands: Hot Toggle

```
.soundphysics debug-rays           # Toggle all visualization
.soundphysics debug-rays occlusion # Toggle occlusion rays only
.soundphysics debug-rays reverb    # Toggle reverb bounces only
.soundphysics debug-rays reposition # Toggle repositioning paths only
.soundphysics debug-rays off       # Disable all
```

---

## Integration Points

### 1. OcclusionCalculator.cs
After each raycast:
```csharp
if (DebugRayRenderer.ShowOcclusion)
{
    DebugRayRenderer.AddOcclusionRay(soundPos, playerPos, totalOcclusion);
}
```

### 2. AcousticRaytracer.cs
After each reverb bounce:
```csharp
if (DebugRayRenderer.ShowReverb)
{
    DebugRayRenderer.AddReverbBounce(bounceStart, bounceEnd, bounceNumber);
}
```

### 3. SoundPathResolver.cs
After repositioning calculation:
```csharp
if (DebugRayRenderer.ShowRepositioning)
{
    DebugRayRenderer.AddRepositionPath(originalPos, repositionedPos);
}
```

### 4. SoundPhysicsAdaptedModSystem.cs
Register renderer on client start:
```csharp
debugRenderer = new DebugRayRenderer();
api.Event.RegisterRenderer(debugRenderer, EnumRenderStage.AfterFinalComposition, "soundphysics-debug");
```

---

## Color Scheme

| Ray Type | Color | Condition |
|----------|-------|-----------|
| Occlusion (clear) | Green | occlusion < 0.3 |
| Occlusion (medium) | Yellow | 0.3 ≤ occlusion < 0.7 |
| Occlusion (heavy) | Red | occlusion ≥ 0.7 |
| Reverb bounce 1 | Blue | First bounce |
| Reverb bounce 2 | Cyan | Second bounce |
| Reverb bounce 3+ | White | Third+ bounce |
| Repositioning | Yellow→Magenta | Original→New position |
| Opening scanner (rain) | Cyan | RainOpeningScanner grid hits + DDA rays + cluster centroids (Phase 5B) |
| Sky search (thunder) | Yellow | Mode B upper hemisphere rays (Phase 5C) |
| Enclosure reading | White | roomVolumePitchLoss value display |

---

## Performance Considerations

1. **Ray Limit**: Cap at 500 active rays to prevent memory/GPU issues
2. **Distance Culling**: Don't render rays >50 blocks from player
3. **Skip When Disabled**: Static bool checks before any ray allocation
4. **Frame-Locked Rendering**: Only add new rays during render, not every audio update

---

## Implementation Order

1. **Step 1**: Add config options (DebugShowOcclusion, etc.)
2. **Step 2**: Create `DebugRayRenderer.cs` with IRenderer skeleton
3. **Step 3**: Implement line mesh rendering (GL_LINES)
4. **Step 4**: Add static `AddRay` methods with thread-safe queue
5. **Step 5**: Hook into OcclusionCalculator, AcousticRaytracer, SoundPathResolver
6. **Step 6**: Add chat commands for hot toggle
7. **Step 7**: Test with `.soundphysics debug-rays` command

---

## Files to Create/Modify

| Action | File | Changes |
|--------|------|---------|
| NEW | `Core/DebugRayRenderer.cs` | Main renderer class |
| MODIFY | `Config/SoundPhysicsConfig.cs` | Add debug viz options |
| MODIFY | `SoundPhysicsAdaptedModSystem.cs` | Register renderer, add commands |
| MODIFY | `Core/OcclusionCalculator.cs` | Hook AddOcclusionRay |
| MODIFY | `Core/AcousticRaytracer.cs` | Hook AddReverbBounce |
| MODIFY | `Core/SoundPathResolver.cs` | Hook AddRepositionPath |

---

## Verification

### Manual Testing
1. Enter game world
2. Run `.soundphysics debug-rays`
3. Place a sound source (beehive, water)
4. Verify colored lines appear from sound to player
5. Move around - lines should update
6. Run `.soundphysics debug-rays off` - lines disappear
7. Performance: Ensure no FPS drop with visualization enabled

---

## Phase 7 Note

This is a **Phase 7 (Debug Visualization)** item. It's isolated from audio logic and can be started anytime after Phase 1. Weather-specific rays (sky probe, sky search) require Phase 5B/5C to exist.
