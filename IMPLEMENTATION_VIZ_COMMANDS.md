# Sound Physics Adapted — Debug Visualization System

## Architecture: IRenderer + MeshData Lines (Wireframe)

### Why IRenderer Instead of Block Highlights

Block highlights snap to the block grid (integer coordinates). Acoustic data is sub-block:
- Bounce points are 0.15 blocks off surfaces at arbitrary angles
- Sound repositioning produces fractional world positions
- Ray paths traverse continuous space
- Opening probes are at fractional centroids

**IRenderer with `EnumDrawMode.Lines`** gives sub-block precision with the VS wireframe shader.

**Exception**: Weather viz keeps block highlights (slots 90-92) — weather detection IS block-grid based.

---

## Rendering Pipeline (Proven Pattern)

From VS's own `WireframeCube` + `LineMeshUtil` classes:

```csharp
// 1. BUILD MESH
MeshData mesh = new MeshData();
mesh.SetMode(EnumDrawMode.Lines);
mesh.xyz = new float[vertexCount * 3];
mesh.Rgba = new byte[vertexCount * 4];
mesh.Indices = new int[indexCount];
// Fill vertices + indices using LineMeshUtil patterns

// 2. SET FLAGS (required for wireframe shader)
mesh.Flags = new int[mesh.VerticesCount];
for (int i = 0; i < mesh.Flags.Length; i++) 
    mesh.Flags[i] = 1 << 8;

// 3. UPLOAD
MeshRef meshRef = capi.Render.UploadMesh(mesh);

// 4. RENDER (in OnRenderFrame)
var prog = capi.Shader.GetProgram((int)EnumShaderProgram.Wireframe);
prog.Use();
capi.Render.LineWidth = 1.6f;
capi.Render.GLEnableDepthTest();
capi.Render.GLDepthMask(false);
capi.Render.GlToggleBlend(true);
prog.Uniform("origin", 0f, 0f, 0f);
prog.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);
prog.UniformMatrix("modelViewMatrix", mvMatrix.Values);
prog.Uniform("colorIn", colorVec);
capi.Render.RenderMesh(meshRef);
prog.Stop();
capi.Render.GLDepthMask(true);
```

### Key Details

- **Shader**: `EnumShaderProgram.Wireframe` — built into VS, no custom shader needed
- **Draw mode**: `EnumDrawMode.Lines` — each pair of indices is one line segment
- **Render stage**: `EnumRenderStage.Opaque` — renders in world space with depth
- **Camera transform**: `capi.Render.CameraMatrixOrigin` — world coords relative to camera
- **Color**: Can set per-vertex via `mesh.Rgba[]` OR override globally via `prog.Uniform("colorIn", vec4)`
- **Flags**: Must set `mesh.Flags[i] = 1 << 8` for each vertex (shader requires it)

---

## Data Sources (Already Available)

| Data | Source | When Available |
|------|--------|----------------|
| Bounce points (pos, normal, reflectivity, occlusion, bounce index) | `AcousticRaytracer._cacheableBouncePoints[]` | After `CalculateWithPathsCacheable()` |
| Bounce count | `AcousticRaytracer._cacheableBounceCount` | Same |
| Opening probes (pos, occlusion, adjacent air) | `AcousticRaytracer._cacheableOpenings[]` | Same |
| Sound path result (apparent pos, avg occlusion) | `pathResolver.Resolve()` return value | Same |
| Reverb slot distribution | `sendGain0-3` local vars in raytracer | Needs capture |
| Ray directions (fibonacci sphere + bounce chain) | Local vars in ray loop | Needs capture |
| Weather openings | `WeatherAudioManager.openingTracker` | Already exposed |

### What Needs Adding to Raytracer

For **ray path visualization**, we need the sequence of hit positions per ray (not just final bounce points):

```csharp
// New struct for ray segment visualization
public struct RaySegment
{
    public float StartX, StartY, StartZ;
    public float EndX, EndY, EndZ;
    public int RayIndex;      // Which fibonacci ray (0..numRays)
    public int BounceIndex;   // Which bounce in chain (0..bounces)
}
```

The ray loop already has `soundPos` (start), `hit.Value.position` (first hit), then `lastHitPos` → `nextHit.Value.position` for each bounce. Just needs capture into a static array when viz is active.

---

## New File: `Core/DebugVisualization.cs`

### Class Design

```csharp
public class DebugVisualization : IRenderer, IDisposable
{
    // === Mode flags (runtime only, not persisted to config) ===
    public bool ShowBounces { get; set; }      // Wireframe boxes at bounce points
    public bool ShowRays { get; set; }         // Line segments for ray paths
    public bool ShowOcclusion { get; set; }    // DDA path lines bounce→player
    public bool ShowReposition { get; set; }   // Original→apparent position lines
    public bool ShowOpenings { get; set; }     // Wireframe boxes at probe exits
    public bool ShowReverbSlots { get; set; }  // Bounce points colored by slot
    // Weather stays in config.DebugWeatherVisualization (block highlights)
    
    public bool AnyActive => ShowBounces || ShowRays || ShowOcclusion 
        || ShowReposition || ShowOpenings || ShowReverbSlots;
    
    // === Render resources ===
    private ICoreClientAPI capi;
    private MeshRef currentMeshRef;           // Re-uploaded each refresh
    private Matrixf mvMat = new Matrixf();
    
    // === Data capture buffers ===
    // Filled by raytracer, consumed by rendering
    // Double-buffered: raytracer writes to "pending", renderer swaps to "active"
    private BouncePoint[] pendingBounces;
    private int pendingBounceCount;
    private RaySegment[] pendingRays;
    private int pendingRayCount;
    private OpeningData[] pendingOpenings;
    private int pendingOpeningCount;
    private Vec3d pendingSoundPos;            // Source position for reposition line
    private Vec3d pendingApparentPos;         // Apparent position
    private float[] pendingSlotWeights;       // Per-bounce slot assignment
    
    // Active (being rendered)
    private MeshData activeMesh;
    private bool meshDirty = false;
    
    // Rate limiting
    private long lastRebuildMs = 0;
    private const long REBUILD_INTERVAL_MS = 250;  // 4 Hz mesh rebuild
    
    // IRenderer
    public double RenderOrder => 0.99;  // Render after world
    public int RenderRange => 999;
}
```

### Lifecycle

```
ModSystem.StartClientSide()
  → new DebugVisualization(capi)
  → capi.Event.RegisterRenderer(viz, EnumRenderStage.Opaque, "soundphysics-viz")

Per game tick (when AnyActive):
  → AudioPhysicsSystem picks nearest sound
  → AcousticRaytracer captures data to viz buffers
  → viz.meshDirty = true

Per render frame (OnRenderFrame):
  → If meshDirty: rebuild MeshData from buffers, upload, clear dirty flag
  → Render with wireframe shader
  
Toggle command:
  → viz.ShowBounces = !viz.ShowBounces
  → If nothing active: dispose mesh, clear screen

Dispose:
  → capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque)
  → currentMeshRef?.Dispose()
```

### Mesh Building Strategy

All active viz modes combine into **one single MeshData** with per-vertex colors. This means one draw call, one mesh upload. The wireframe shader uses per-vertex RGBA.

```csharp
private void RebuildMesh()
{
    // Count total vertices + indices needed
    int verts = 0, indices = 0;
    if (ShowBounces) { verts += bounceCount * 24; indices += bounceCount * 48; }  // 24 verts per wireframe cube
    if (ShowRays) { verts += rayCount * 2; indices += rayCount * 2; }  // 2 verts per line segment
    if (ShowOcclusion) { /* similar */ }
    if (ShowReposition) { verts += 2; indices += 2; }  // One line
    if (ShowOpenings) { verts += openingCount * 24; indices += openingCount * 48; }
    if (ShowReverbSlots) { /* reuses bounce data with different colors */ }
    
    MeshData mesh = new MeshData();
    mesh.SetMode(EnumDrawMode.Lines);
    mesh.xyz = new float[verts * 3];
    mesh.Rgba = new byte[verts * 4];
    mesh.Indices = new int[indices];
    
    // Camera-relative coordinates (subtract player camera pos)
    Vec3d cam = capi.World.Player.Entity.CameraPos;
    
    if (ShowBounces) AppendBounceBoxes(mesh, cam);
    if (ShowRays) AppendRayLines(mesh, cam);
    if (ShowOcclusion) AppendOcclusionLines(mesh, cam);
    if (ShowReposition) AppendRepositionLine(mesh, cam);
    if (ShowOpenings) AppendOpeningBoxes(mesh, cam);
    if (ShowReverbSlots) AppendReverbSlotBoxes(mesh, cam);
    
    mesh.Flags = new int[mesh.VerticesCount];
    for (int i = 0; i < mesh.Flags.Length; i++) mesh.Flags[i] = 1 << 8;
    
    currentMeshRef?.Dispose();
    currentMeshRef = capi.Render.UploadMesh(mesh);
}
```

### Wireframe Box Helper (Sub-Block Size)

For bounce points — small 0.1-block wireframe cubes at exact positions:

```csharp
private void AppendWireframeBox(MeshData mesh, double wx, double wy, double wz, 
    float halfSize, int color, Vec3d cam)
{
    float cx = (float)(wx - cam.X);
    float cy = (float)(wy - cam.Y);
    float cz = (float)(wz - cam.Z);
    float s = halfSize;
    
    // 6 face loops × 4 verts = 24 verts, 6 × 8 indices = 48 indices
    LineMeshUtil.AddLineLoop(mesh,
        new Vec3f(cx-s, cy-s, cz-s), new Vec3f(cx-s, cy+s, cz-s),
        new Vec3f(cx+s, cy+s, cz-s), new Vec3f(cx+s, cy-s, cz-s), color);
    LineMeshUtil.AddLineLoop(mesh,
        new Vec3f(cx-s, cy-s, cz+s), new Vec3f(cx+s, cy-s, cz+s),
        new Vec3f(cx+s, cy+s, cz+s), new Vec3f(cx-s, cy+s, cz+s), color);
    LineMeshUtil.AddLineLoop(mesh,
        new Vec3f(cx-s, cy-s, cz-s), new Vec3f(cx-s, cy-s, cz+s),
        new Vec3f(cx-s, cy+s, cz+s), new Vec3f(cx-s, cy+s, cz-s), color);
    LineMeshUtil.AddLineLoop(mesh,
        new Vec3f(cx+s, cy-s, cz-s), new Vec3f(cx+s, cy+s, cz-s),
        new Vec3f(cx+s, cy+s, cz+s), new Vec3f(cx+s, cy-s, cz+s), color);
    LineMeshUtil.AddLineLoop(mesh,
        new Vec3f(cx-s, cy+s, cz-s), new Vec3f(cx-s, cy+s, cz+s),
        new Vec3f(cx+s, cy+s, cz+s), new Vec3f(cx+s, cy+s, cz-s), color);
    LineMeshUtil.AddLineLoop(mesh,
        new Vec3f(cx-s, cy-s, cz-s), new Vec3f(cx+s, cy-s, cz-s),
        new Vec3f(cx+s, cy-s, cz+s), new Vec3f(cx-s, cy-s, cz+s), color);
}
```

### Line Segment Helper

For rays and reposition:

```csharp
private void AppendLine(MeshData mesh, 
    double x1, double y1, double z1,
    double x2, double y2, double z2,
    int color, Vec3d cam)
{
    int startVertex = mesh.GetVerticesCount();
    
    LineMeshUtil.AddVertex(mesh, 
        (float)(x1 - cam.X), (float)(y1 - cam.Y), (float)(z1 - cam.Z), color);
    LineMeshUtil.AddVertex(mesh, 
        (float)(x2 - cam.X), (float)(y2 - cam.Y), (float)(z2 - cam.Z), color);
    
    mesh.Indices[mesh.IndicesCount++] = startVertex;
    mesh.Indices[mesh.IndicesCount++] = startVertex + 1;
}
```

---

## Visualization Modes — Detail

### 1. `bounces` — Bounce Point Wireframe Boxes

**Source**: `BouncePoint[]` from `_cacheableBouncePoints`

**Visual**: Small wireframe cubes (0.05 half-size = 0.1 block) at each bounce position.

**Color by reflectivity**:
- White (255,255,255,200) — high reflectivity (>0.7: stone, metal)
- Yellow (255,200,0,200) — medium (0.3-0.7: wood, brick)
- Dark Red (180,50,0,200) — low (<0.3: dirt, cloth, leaves)

**Color for alpha by permeation**:
- Bright = high permeation (clear path to player)
- Dim = low permeation (behind walls)

**Size encoding**: Optionally scale box by bounce index (first bounce = larger, later = smaller) to show decay.

### 2. `rays` — Fibonacci Ray Paths

**Source**: New `RaySegment[]` capture in raytracer

**Visual**: Line segments from sound source through each bounce chain. Each ray is a series of connected lines: source→hit1→hit2→hit3→...

**Color gradient by bounce depth**:
- Bounce 0: Cyan (0,255,255)
- Bounce 1: Green (0,255,0)
- Bounce 2: Yellow (255,255,0)
- Bounce 3+: Red (255,0,0)

**Performance**: With 32 rays × 4 bounces = 128 segments max. Very cheap.

**Data capture needed**: Add to raytracer loop:

```csharp
// At start of ray loop (after RaycastToSurface succeeds):
if (DebugVisualization.Instance?.ShowRays == true)
{
    DebugVisualization.Instance.CaptureRaySegment(
        soundPos.X, soundPos.Y, soundPos.Z,        // start
        hit.Value.position.X, hit.Value.position.Y, hit.Value.position.Z,  // end
        i, 0);  // rayIndex, bounceIndex
}

// At each bounce (after nextHit succeeds):
if (DebugVisualization.Instance?.ShowRays == true)
{
    DebugVisualization.Instance.CaptureRaySegment(
        lastHitPos.X, lastHitPos.Y, lastHitPos.Z,
        nextHit.Value.position.X, nextHit.Value.position.Y, nextHit.Value.position.Z,
        i, bounce + 1);
}
```

### 3. `occlusion` — Bounce-to-Player Occlusion Lines

**Source**: Each bounce point has `PathOcclusion` — but we need the actual DDA line back to player.

**Visual**: Line from each bounce point to player, colored by occlusion severity:
- Green (0,255,0) — clear path (occlusion < 0.3)
- Orange (255,128,0) — partial (0.3-1.0)
- Red (255,0,0) — heavy (>1.0)

**Data**: Just bounce points + player pos. Draw line from each BouncePoint to player, color by `PathOcclusion`.

### 4. `reposition` — Sound Repositioning

**Source**: `SoundPathResult.ApparentPosition` + original `soundPos`

**Visual**:
- Green wireframe box (0.08 half) at original sound position
- Orange wireframe box (0.08 half) at apparent (heard) position
- Yellow line connecting them
- Line length = how much the sound shifted

**Data capture**: Store the last-processed sound's original pos and path result.

### 5. `openings` — Opening Probe Exits

**Source**: `OpeningData[]` from `_cacheableOpenings`

**Visual**: Cyan wireframe boxes (0.1 half) at each opening position.

**Opacity by relevance**: Brighter for low `OccToPlayer` (clear path to player).

### 6. `reverb` — Reverb Slot Assignment

**Source**: Same `BouncePoint[]`, but colored by which reverb slot the bounce feeds.

**Visual**: Wireframe boxes at bounce positions, colored by slot:
- Slot 0: Blue (100,100,255) — early reflections (close)
- Slot 1: Green (100,255,100) — medium
- Slot 2: Yellow (255,255,100) — late
- Slot 3: Red (255,100,100) — very late / tail

**Slot determination**: `reflectionDelay = totalDistance * 0.12f`, then same crossfade logic as raytracer. Use dominant slot for color.

---

## Command Registration

In `SoundPhysicsAdaptedModSystem.RegisterCommands()`:

```csharp
.BeginSubCommand("viz")
    .WithDescription("Toggle debug visualizations.\n" +
        "Modes: bounces | rays | occlusion | reposition | weather | openings | reverb | off\n" +
        "No argument = show status of all modes")
    .WithArgs(api.ChatCommands.Parsers.OptionalWord("mode"))
    .HandleWith((args) =>
    {
        var viz = DebugVisualization.Instance;
        if (viz == null)
            return TextCommandResult.Error("[SPA] Visualization system not initialized");
        
        string mode = (args.Parsers[0].IsMissing) ? null : (string)args[0];
        
        if (mode == null)
        {
            // Show status
            return TextCommandResult.Success(
                $"[SPA] Viz modes:\n" +
                $"  bounces: {(viz.ShowBounces ? "ON" : "off")}\n" +
                $"  rays: {(viz.ShowRays ? "ON" : "off")}\n" +
                $"  occlusion: {(viz.ShowOcclusion ? "ON" : "off")}\n" +
                $"  reposition: {(viz.ShowReposition ? "ON" : "off")}\n" +
                $"  openings: {(viz.ShowOpenings ? "ON" : "off")}\n" +
                $"  reverb: {(viz.ShowReverbSlots ? "ON" : "off")}\n" +
                $"  weather: {(config.DebugWeatherVisualization ? "ON" : "off")}");
        }
        
        switch (mode.ToLower())
        {
            case "bounces":
                viz.ShowBounces = !viz.ShowBounces;
                return TextCommandResult.Success($"[SPA] Bounce viz: {(viz.ShowBounces ? "ON" : "OFF")}");
            case "rays":
                viz.ShowRays = !viz.ShowRays;
                return TextCommandResult.Success($"[SPA] Ray path viz: {(viz.ShowRays ? "ON" : "OFF")}");
            case "occlusion":
                viz.ShowOcclusion = !viz.ShowOcclusion;
                return TextCommandResult.Success($"[SPA] Occlusion path viz: {(viz.ShowOcclusion ? "ON" : "OFF")}");
            case "reposition":
                viz.ShowReposition = !viz.ShowReposition;
                return TextCommandResult.Success($"[SPA] Reposition viz: {(viz.ShowReposition ? "ON" : "OFF")}");
            case "weather":
                config.DebugWeatherVisualization = !config.DebugWeatherVisualization;
                return TextCommandResult.Success($"[SPA] Weather viz: {(config.DebugWeatherVisualization ? "ON" : "OFF")}" +
                    (config.DebugWeatherVisualization ? "\nSky: Blue=covered Yellow=exposed | Paths: White=confirmed Red=blocked | Audio: Magenta=source" : ""));
            case "openings":
                viz.ShowOpenings = !viz.ShowOpenings;
                return TextCommandResult.Success($"[SPA] Opening probe viz: {(viz.ShowOpenings ? "ON" : "OFF")}");
            case "reverb":
                viz.ShowReverbSlots = !viz.ShowReverbSlots;
                return TextCommandResult.Success($"[SPA] Reverb slot viz: {(viz.ShowReverbSlots ? "ON" : "OFF")}");
            case "off":
            case "clear":
                viz.ClearAll();
                config.DebugWeatherVisualization = false;
                return TextCommandResult.Success("[SPA] All visualizations OFF");
            default:
                return TextCommandResult.Error($"Unknown viz mode: {mode}\nValid: bounces | rays | occlusion | reposition | weather | openings | reverb | off");
        }
    })
.EndSubCommand()
```

The existing `.soundphysics weather-viz` command stays as-is (backward compat), but now `.soundphysics viz weather` also works.

---

## File Changes Summary

| File | Change | Complexity |
|------|--------|------------|
| **Core/DebugVisualization.cs** | **NEW** — IRenderer, mesh builder, data capture, all viz modes | Large (~300-400 lines) |
| **Core/AcousticRaytracer.cs** | Add `RaySegment[]` capture + static accessor for viz. ~20 lines in ray loop | Small |
| **SoundPhysicsAdaptedModSystem.cs** | Instantiate `DebugVisualization`, register renderer, add `viz` subcommand, wire dispose | Medium (~60 lines) |

Config/SoundPhysicsConfig.cs — **no changes needed**. Viz flags are runtime-only (toggle in-game, don't persist).

---

## Single-Sound Focus Strategy

The raytracer runs for many sounds per tick. Visualizing ALL of them would be chaos.

**Strategy**: When any acoustic viz mode is active, `AudioPhysicsSystem` picks the **nearest sound** that triggers a full raytrace this tick. Only that sound's data feeds the visualization. The viz system stores a `Vec3d FocusedSoundPos` so the player knows which sound is being visualized.

Implementation in `AudioPhysicsSystem.ProcessSoundRaycast()`:
```csharp
// After raytracing completes for a sound:
if (viz != null && viz.AnyAcousticVizActive)
{
    float dist = (float)soundPos.DistanceTo(playerPos);
    if (dist < viz.NearestSoundDistance)
    {
        viz.NearestSoundDistance = dist;
        viz.CaptureFromRaytracer(bouncePoints, bounceCount, 
            openings, openingCount, pathResult, soundPos);
    }
}
// Reset NearestSoundDistance at start of each tick
```

---

## Mesh Rebuild Strategy

The mesh needs rebuilding when:
1. New raytracer data arrives (every tick a sound is processed)
2. Player moves (camera-relative coordinates shift)

**Approach**: Rebuild mesh every 250ms (4 Hz). Between rebuilds, the existing mesh stays rendered at its last position — acceptable for debug viz. Player movement within 250ms is minor visually.

On rebuild:
1. Dispose old `MeshRef`
2. Build new `MeshData` from current buffers
3. Upload new `MeshRef`

This avoids per-frame mesh creation while keeping viz responsive.

---

## Vertex Budget Estimate

| Mode | Vertices | Indices | Notes |
|------|----------|---------|-------|
| bounces (64 max) | 64 × 24 = 1536 | 64 × 48 = 3072 | 24v per wireframe cube |
| rays (128 segments) | 128 × 2 = 256 | 256 | 32 rays × 4 bounces |
| occlusion (64 lines) | 64 × 2 = 128 | 128 | One line per bounce to player |
| reposition (1 sound) | 2 + 2×24 = 50 | 2 + 96 = 98 | 2 boxes + 1 line |
| openings (16 max) | 16 × 24 = 384 | 16 × 48 = 768 | 24v per wireframe cube |
| reverb (64 max) | 64 × 24 = 1536 | 64 × 48 = 3072 | Same as bounces, different color |
| **ALL at once** | **~3890** | **~7394** | Trivial for GPU |

Pre-allocate arrays for worst case: `xyz[3890*3]`, `Rgba[3890*4]`, `Indices[7394]`.

---

## Implementation Order

1. **DebugVisualization.cs** — skeleton with IRenderer, mesh upload/render, `AppendLine`/`AppendWireframeBox` helpers
2. **Bounces mode** — simplest, uses existing `BouncePoint[]` data directly
3. **Rays mode** — add `RaySegment[]` capture to raytracer
4. **Reposition mode** — store last path result
5. **Occlusion mode** — lines from bounces to player
6. **Openings mode** — uses existing `OpeningData[]`
7. **Reverb mode** — color bounces by slot
8. **Command registration** — `.soundphysics viz <mode>`
9. **Weather alias** — wire `.soundphysics viz weather` to existing flag
