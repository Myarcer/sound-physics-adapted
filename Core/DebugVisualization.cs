using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Ray segment data captured during acoustic raytracing for visualization.
    /// </summary>
    public struct RaySegment
    {
        public float StartX, StartY, StartZ;
        public float EndX, EndY, EndZ;
        public int RayIndex;
        public int BounceIndex;
    }

    /// <summary>
    /// Reposition pair: original sound position + apparent (redirected) position.
    /// One per repositioned sound.
    /// </summary>
    public struct RepositionPair
    {
        public float SrcX, SrcY, SrcZ;
        public float AppX, AppY, AppZ;
    }

    /// <summary>
    /// IRenderer-based debug visualization for acoustic raytracing data.
    /// Renders wireframe boxes and lines at sub-block precision using the VS wireframe shader.
    /// 
    /// MULTI-SOUND: Accumulates data from ALL sounds that raytrace each tick.
    /// Data is double-buffered: game tick thread appends to pending, renderer swaps to active.
    /// Mesh rebuilds every render frame with current camera position.
    /// </summary>
    public class DebugVisualization : IRenderer, IDisposable
    {
        public static DebugVisualization Instance { get; private set; }

        // === Mode flags (runtime only, not persisted to config) ===
        // BounceColorMode: 0=off, 1=reflectivity, 2=reverb slots
        public int BounceColorMode { get; set; }
        public bool ShowRays { get; set; }
        public bool ShowOcclusion { get; set; }
        public bool ShowReposition { get; set; }
        public bool ShowOpenings { get; set; }

        public bool AnyActive => BounceColorMode > 0 || ShowRays || ShowOcclusion
            || ShowReposition || ShowOpenings;

        // Subset that needs raytracer data capture
        public bool AnyAcousticVizActive => BounceColorMode > 0 || ShowRays || ShowOcclusion
            || ShowReposition || ShowOpenings;

        // === IRenderer ===
        public double RenderOrder => 0.99;
        public int RenderRange => 999;

        // === Render resources ===
        private ICoreClientAPI capi;
        private MeshRef currentMeshRef;
        private Matrixf mvMat = new Matrixf();

        // === Pending data buffers (accumulated across all sounds in one tick) ===
        private BouncePoint[] pendingBounces = new BouncePoint[2048];
        private int pendingBounceCount;
        private RaySegment[] pendingRays = new RaySegment[2048];
        private int pendingRayCount;
        private OpeningData[] pendingOpenings = new OpeningData[96];
        private int pendingOpeningCount;
        private RepositionPair[] pendingRepositions = new RepositionPair[32];
        private int pendingRepositionCount;
        // Track whether ANY sound captured viz data this tick
        private bool capturedThisTick = false;

        // === Active data (snapshot for rendering) ===
        private BouncePoint[] activeBounces = new BouncePoint[2048];
        private int activeBounceCount;
        private RaySegment[] activeRays = new RaySegment[2048];
        private int activeRayCount;
        private OpeningData[] activeOpenings = new OpeningData[96];
        private int activeOpeningCount;
        private RepositionPair[] activeRepositions = new RepositionPair[32];
        private int activeRepositionCount;

        private volatile bool meshDirty = false;
        private readonly object _swapLock = new object();

        // === Fade / persistence ===
        private float timeSinceCapture = 0f;   // seconds since last SwapBuffers
        private const float VIZ_HOLD_SECONDS = 1.5f;  // full-opacity hold after capture
        private const float VIZ_FADE_SECONDS = 1.5f;  // linear fade-to-zero after hold
        private float currentFadeAlpha = 1f;          // computed once per render frame

        // === Debug logging ===
        private float debugLogTimer = 0f;
        private int debugFramesSinceCapture = 0;
        private int debugSwapCount = 0;
        private int debugCaptureCount = 0;  // number of CaptureFromRaytracer calls per log interval
        private int debugSoundsThisSwap = 0; // how many sounds contributed in the last swap

        // Multi-sound: worst case with all viz on:
        // 2048 bounces * 8 verts = 16K (capped), 2048 rays * 2 verts = 4K,
        // 96 openings * 8 = 768, 32 repos * 18 = 576
        // Cap at reasonable GPU upload
        private const int MAX_VERTICES = 16384;
        private const int MAX_INDICES = 49152;

        // Mesh building state
        private int vertexOffset;
        private int indexOffset;

        // Reusable uniforms
        private Vec3f origin = new Vec3f(0, 0, 0);
        private Vec4f colorIn = new Vec4f(1, 1, 1, 1);

        public DebugVisualization(ICoreClientAPI capi)
        {
            this.capi = capi;
            Instance = this;
            capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "soundphysics-viz");
        }

        /// <summary>
        /// Called by AudioPhysicsSystem at start of each update tick to reset capture tracking.
        /// Clears ALL pending counters so this tick accumulates fresh data from all sounds.
        /// </summary>
        public void ResetTickCapture()
        {
            capturedThisTick = false;
            pendingRayCount = 0;
            pendingBounceCount = 0;
            pendingOpeningCount = 0;
            pendingRepositionCount = 0;
        }

        /// <summary>
        /// Whether any sound has captured viz data this tick.
        /// Used by AcousticRaytracer to know whether to capture rays.
        /// NOTE: Unlike before, this doesn't GATE capture — it just tracks whether any happened.
        /// </summary>
        public bool HasCapturedThisTick => capturedThisTick;

        /// <summary>
        /// Capture bounce and opening data from the raytracer for ONE sound.
        /// Called from AudioPhysicsSystem after each full raytrace.
        /// APPENDS to pending buffers (multi-sound accumulation).
        /// </summary>
        public void CaptureFromRaytracer(
            BouncePoint[] bouncePoints, int bounceCount,
            OpeningData[] openings, int openingCount,
            SoundPathResult? pathResult, Vec3d soundPos)
        {
            lock (_swapLock)
            {
                // Append bounce data
                int copyBounces = Math.Min(bounceCount, pendingBounces.Length - pendingBounceCount);
                if (copyBounces > 0)
                {
                    Array.Copy(bouncePoints, 0, pendingBounces, pendingBounceCount, copyBounces);
                    pendingBounceCount += copyBounces;
                }

                // Append opening data
                int copyOpenings = Math.Min(openingCount, pendingOpenings.Length - pendingOpeningCount);
                if (copyOpenings > 0)
                {
                    Array.Copy(openings, 0, pendingOpenings, pendingOpeningCount, copyOpenings);
                    pendingOpeningCount += copyOpenings;
                }

                // Append reposition pair if this sound has one
                if (pathResult.HasValue && pathResult.Value.RepositionOffset > 0.01
                    && pendingRepositionCount < pendingRepositions.Length)
                {
                    pendingRepositions[pendingRepositionCount] = new RepositionPair
                    {
                        SrcX = (float)soundPos.X, SrcY = (float)soundPos.Y, SrcZ = (float)soundPos.Z,
                        AppX = (float)pathResult.Value.ApparentPosition.X,
                        AppY = (float)pathResult.Value.ApparentPosition.Y,
                        AppZ = (float)pathResult.Value.ApparentPosition.Z
                    };
                    pendingRepositionCount++;
                }

                meshDirty = true;
                capturedThisTick = true;
                debugCaptureCount++;
            }
        }

        /// <summary>
        /// Capture a ray segment for visualization.
        /// Called from AcousticRaytracer during the ray loop.
        /// Lock not needed: rays are written between ResetTickCapture and CaptureFromRaytracer
        /// (which holds _swapLock), and SwapBuffers only runs when meshDirty is true
        /// (set at end of CaptureFromRaytracer under lock).
        /// </summary>
        public void CaptureRaySegment(
            double startX, double startY, double startZ,
            double endX, double endY, double endZ,
            int rayIndex, int bounceIndex)
        {
            if (pendingRayCount >= pendingRays.Length) return;

            pendingRays[pendingRayCount] = new RaySegment
            {
                StartX = (float)startX, StartY = (float)startY, StartZ = (float)startZ,
                EndX = (float)endX, EndY = (float)endY, EndZ = (float)endZ,
                RayIndex = rayIndex,
                BounceIndex = bounceIndex
            };
            pendingRayCount++;
        }

        /// <summary>
        /// Reset ray segment capture at the start of a raytrace for the focused sound.
        /// </summary>
        public void ResetRayCapture()
        {
            pendingRayCount = 0;
        }

        public void ClearAll()
        {
            BounceColorMode = 0;
            ShowRays = false;
            ShowOcclusion = false;
            ShowReposition = false;
            ShowOpenings = false;

            activeBounceCount = 0;
            activeRayCount = 0;
            activeOpeningCount = 0;
            activeRepositionCount = 0;

            currentMeshRef?.Dispose();
            currentMeshRef = null;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (!AnyActive || capi.World?.Player?.Entity == null) return;

            timeSinceCapture += deltaTime;
            debugFramesSinceCapture++;

            // Swap pending->active under lock to prevent mid-capture race
            if (meshDirty)
            {
                lock (_swapLock)
                {
                    if (meshDirty)
                    {
                        SwapBuffers();
                        meshDirty = false;
                        timeSinceCapture = 0f;
                        debugFramesSinceCapture = 0;
                        debugSwapCount++;
                    }
                }
            }

            // Compute fade alpha: hold at full opacity, then linear fade to zero
            if (timeSinceCapture <= VIZ_HOLD_SECONDS)
                currentFadeAlpha = 1f;
            else
                currentFadeAlpha = Math.Max(0f, 1f - (timeSinceCapture - VIZ_HOLD_SECONDS) / VIZ_FADE_SECONDS);

            // Debug logging (1 Hz when debugMode + viz active)
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config != null && config.DebugMode)
            {
                debugLogTimer += deltaTime;
                if (debugLogTimer >= 1f)
                {
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[VIZ-RENDER] rays={activeRayCount} bounces={activeBounceCount} openings={activeOpeningCount} repos={activeRepositionCount} " +
                        $"fade={currentFadeAlpha:F2} age={timeSinceCapture:F1}s framesStale={debugFramesSinceCapture} " +
                        $"swaps={debugSwapCount} captures={debugCaptureCount} sounds={debugSoundsThisSwap}");
                    debugLogTimer = 0f;
                    debugSwapCount = 0;
                    debugCaptureCount = 0;
                    debugSoundsThisSwap = 0;
                }
            }

            // Fully faded out - nothing to render
            if (currentFadeAlpha <= 0f)
            {
                currentMeshRef?.Dispose();
                currentMeshRef = null;
                return;
            }

            // Rebuild mesh every frame with current camera position.
            // Eliminates drift (no stale camera offset) and shutter (no frame gaps).
            // Cost is negligible: ~5K vertices = ~60KB GPU upload per frame.
            RebuildMesh();

            if (currentMeshRef == null) return;

            // Render with wireframe shader
            IRenderAPI rpi = capi.Render;
            var prog = rpi.GetEngineShader(EnumShaderProgram.Wireframe);
            if (prog == null) return;

            prog.Use();

            rpi.LineWidth = 1.6f;
            rpi.GLEnableDepthTest();
            rpi.GLDepthMask(false);
            rpi.GlToggleBlend(true);

            mvMat.Set(rpi.CameraMatrixOriginf);

            prog.Uniform("origin", origin);
            prog.UniformMatrix("projectionMatrix", rpi.CurrentProjectionMatrix);
            prog.UniformMatrix("modelViewMatrix", mvMat.Values);
            prog.Uniform("colorIn", colorIn);

            rpi.RenderMesh(currentMeshRef);

            prog.Stop();
            rpi.GLDepthMask(true);
        }

        private void SwapBuffers()
        {
            // Note: caller holds _swapLock

            // Swap bounce data
            var tmpBounces = activeBounces;
            activeBounces = pendingBounces;
            pendingBounces = tmpBounces;
            activeBounceCount = pendingBounceCount;

            // Swap ray data
            var tmpRays = activeRays;
            activeRays = pendingRays;
            pendingRays = tmpRays;
            activeRayCount = pendingRayCount;

            // Swap opening data
            var tmpOpenings = activeOpenings;
            activeOpenings = pendingOpenings;
            pendingOpenings = tmpOpenings;
            activeOpeningCount = pendingOpeningCount;

            // Swap reposition data
            var tmpRepos = activeRepositions;
            activeRepositions = pendingRepositions;
            pendingRepositions = tmpRepos;
            activeRepositionCount = pendingRepositionCount;

            debugSoundsThisSwap = debugCaptureCount;
        }

        private void RebuildMesh()
        {
            MeshData mesh = new MeshData(MAX_VERTICES, MAX_INDICES, false, false, true, true);
            mesh.SetMode(EnumDrawMode.Lines);
            vertexOffset = 0;
            indexOffset = 0;

            Vec3d cam = capi.World.Player.Entity.CameraPos;

            if (BounceColorMode > 0) AppendBounceBoxes(mesh, cam);
            if (ShowRays) AppendRayLines(mesh, cam);
            if (ShowOcclusion) AppendOcclusionLines(mesh, cam);
            if (ShowReposition && activeRepositionCount > 0) AppendRepositionLines(mesh, cam);
            if (ShowOpenings) AppendOpeningBoxes(mesh, cam);

            if (vertexOffset == 0)
            {
                currentMeshRef?.Dispose();
                currentMeshRef = null;
                return;
            }

            // Set flags required by wireframe shader
            mesh.Flags = new int[mesh.VerticesCount];
            for (int i = 0; i < mesh.VerticesCount; i++)
                mesh.Flags[i] = 1 << 8;

            currentMeshRef?.Dispose();
            currentMeshRef = capi.Render.UploadMesh(mesh);
        }

        // ===== MESH APPEND HELPERS =====

        private void AddVertex(MeshData mesh, float x, float y, float z, int color)
        {
            if (mesh.VerticesCount >= MAX_VERTICES)
                return; // Skip — buffer full, don't attempt dynamic growth (VS GrowVertexBuffer crashes on Rgba mismatch)

            // Apply fade alpha to vertex color
            byte origAlpha = (byte)((color >> 24) & 0xFF);
            byte fadedAlpha = (byte)(origAlpha * currentFadeAlpha);
            color = (fadedAlpha << 24) | (color & 0x00FFFFFF);

            int vi = mesh.VerticesCount;
            mesh.xyz[vi * 3] = x;
            mesh.xyz[vi * 3 + 1] = y;
            mesh.xyz[vi * 3 + 2] = z;
            mesh.Rgba[vi * 4] = (byte)(color & 0xFF);
            mesh.Rgba[vi * 4 + 1] = (byte)((color >> 8) & 0xFF);
            mesh.Rgba[vi * 4 + 2] = (byte)((color >> 16) & 0xFF);
            mesh.Rgba[vi * 4 + 3] = (byte)((color >> 24) & 0xFF);
            mesh.VerticesCount = vi + 1;
            vertexOffset = vi + 1;
        }

        private void AddLineIndices(MeshData mesh, int v1, int v2)
        {
            if (mesh.IndicesCount + 2 > MAX_INDICES)
                return; // Skip — index buffer full

            mesh.Indices[mesh.IndicesCount] = v1;
            mesh.Indices[mesh.IndicesCount + 1] = v2;
            mesh.IndicesCount += 2;
            indexOffset = mesh.IndicesCount;
        }

        private void AppendLine(MeshData mesh, double x1, double y1, double z1,
            double x2, double y2, double z2, int color, Vec3d cam)
        {
            int v = mesh.VerticesCount;
            AddVertex(mesh, (float)(x1 - cam.X), (float)(y1 - cam.Y), (float)(z1 - cam.Z), color);
            AddVertex(mesh, (float)(x2 - cam.X), (float)(y2 - cam.Y), (float)(z2 - cam.Z), color);
            AddLineIndices(mesh, v, v + 1);
        }

        /// <summary>
        /// Appends a wireframe box (12 edges) centered at world position with given half-size.
        /// </summary>
        private void AppendWireframeBox(MeshData mesh, double wx, double wy, double wz,
            float halfSize, int color, Vec3d cam)
        {
            float cx = (float)(wx - cam.X);
            float cy = (float)(wy - cam.Y);
            float cz = (float)(wz - cam.Z);
            float s = halfSize;

            // 8 corner vertices
            int v = mesh.VerticesCount;
            AddVertex(mesh, cx - s, cy - s, cz - s, color); // 0: ---
            AddVertex(mesh, cx + s, cy - s, cz - s, color); // 1: +--
            AddVertex(mesh, cx + s, cy + s, cz - s, color); // 2: ++-
            AddVertex(mesh, cx - s, cy + s, cz - s, color); // 3: -+-
            AddVertex(mesh, cx - s, cy - s, cz + s, color); // 4: --+
            AddVertex(mesh, cx + s, cy - s, cz + s, color); // 5: +-+
            AddVertex(mesh, cx + s, cy + s, cz + s, color); // 6: +++
            AddVertex(mesh, cx - s, cy + s, cz + s, color); // 7: -++

            // 12 edges
            // Bottom face
            AddLineIndices(mesh, v + 0, v + 1);
            AddLineIndices(mesh, v + 1, v + 2);
            AddLineIndices(mesh, v + 2, v + 3);
            AddLineIndices(mesh, v + 3, v + 0);
            // Top face
            AddLineIndices(mesh, v + 4, v + 5);
            AddLineIndices(mesh, v + 5, v + 6);
            AddLineIndices(mesh, v + 6, v + 7);
            AddLineIndices(mesh, v + 7, v + 4);
            // Vertical edges
            AddLineIndices(mesh, v + 0, v + 4);
            AddLineIndices(mesh, v + 1, v + 5);
            AddLineIndices(mesh, v + 2, v + 6);
            AddLineIndices(mesh, v + 3, v + 7);
        }

        // ===== VISUALIZATION MODES =====

        /// <summary>
        /// Bounces: wireframe boxes at each bounce point.
        /// BounceColorMode 1 = reflectivity: White/Yellow/DarkRed by surface reflectivity.
        /// BounceColorMode 2 = reverb slots: Blue/Green/Yellow/Red by which reverb slot the bounce feeds.
        /// </summary>
        private void AppendBounceBoxes(MeshData mesh, Vec3d cam)
        {
            for (int i = 0; i < activeBounceCount; i++)
            {
                ref BouncePoint bp = ref activeBounces[i];
                int color;

                if (BounceColorMode == 2)
                {
                    // Reverb slot coloring
                    float reflectionDelay = bp.TotalDistance * 0.12f;
                    float cross0 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 0f), 0f, 1f);
                    float cross1 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 1f), 0f, 1f);
                    float cross2 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 2f), 0f, 1f);
                    float cross3 = Math.Clamp(reflectionDelay - 2f, 0f, 1f);
                    float maxCross = Math.Max(Math.Max(cross0, cross1), Math.Max(cross2, cross3));

                    if (maxCross == cross0)
                        color = (200 << 24) | (255 << 16) | (100 << 8) | 100;   // Blue
                    else if (maxCross == cross1)
                        color = (200 << 24) | (100 << 16) | (255 << 8) | 100;   // Green
                    else if (maxCross == cross2)
                        color = (200 << 24) | (100 << 16) | (255 << 8) | 255;   // Yellow
                    else
                        color = (200 << 24) | (100 << 16) | (100 << 8) | 255;   // Red
                }
                else
                {
                    // Reflectivity coloring (default)
                    byte alpha = (byte)Math.Clamp((int)(bp.Permeation * 200 + 55), 60, 255);
                    if (bp.Reflectivity > 0.7f)
                        color = (alpha << 24) | (255 << 16) | (255 << 8) | 255; // White
                    else if (bp.Reflectivity > 0.3f)
                        color = (alpha << 24) | (0 << 16) | (200 << 8) | 255;   // Yellow
                    else
                        color = (alpha << 24) | (0 << 16) | (50 << 8) | 180;    // Dark Red
                }

                AppendWireframeBox(mesh, bp.PosX, bp.PosY, bp.PosZ, 0.05f, color, cam);
            }
        }

        /// <summary>
        /// Rays: line segments from source through bounce chain.
        /// Color gradient by bounce depth: Cyan(0) → Green(1) → Yellow(2) → Red(3+).
        /// </summary>
        private void AppendRayLines(MeshData mesh, Vec3d cam)
        {
            for (int i = 0; i < activeRayCount; i++)
            {
                ref RaySegment seg = ref activeRays[i];
                int color;
                switch (seg.BounceIndex)
                {
                    case 0: color = (200 << 24) | (255 << 16) | (255 << 8) | 0;   break; // Cyan
                    case 1: color = (200 << 24) | (0 << 16) | (255 << 8) | 0;     break; // Green
                    case 2: color = (200 << 24) | (0 << 16) | (255 << 8) | 255;   break; // Yellow
                    default: color = (200 << 24) | (0 << 16) | (0 << 8) | 255;    break; // Red
                }

                AppendLine(mesh, seg.StartX, seg.StartY, seg.StartZ,
                    seg.EndX, seg.EndY, seg.EndZ, color, cam);
            }
        }

        /// <summary>
        /// Occlusion: lines from sound source to each of its bounce points.
        /// Multi-sound: each bounce stores the source pos in its fields, so we
        /// need to track which bounces belong to which sound.
        /// For simplicity, draw from each bounce's source position, using the
        /// nearest sound position as approximation. Since bounce points are accumulated
        /// from multiple sounds, we draw from the origin point (0,0,0) of the ray
        /// that created each bounce — but that info isn't in BouncePoint.
        /// Instead, just draw to player camera as visual indicator of occlusion severity.
        /// </summary>
        private void AppendOcclusionLines(MeshData mesh, Vec3d cam)
        {
            // With multi-sound, we no longer have a single activeSoundPos.
            // Draw short ticks from each bounce outward along its normal,
            // colored by occlusion severity. Still useful and doesn't require source pos.
            for (int i = 0; i < activeBounceCount; i++)
            {
                ref BouncePoint bp = ref activeBounces[i];
                int color;
                if (bp.PathOcclusion < 0.3f)
                    color = (180 << 24) | (0 << 16) | (255 << 8) | 0;    // Green
                else if (bp.PathOcclusion < 1.0f)
                    color = (180 << 24) | (0 << 16) | (128 << 8) | 255;  // Orange
                else
                    color = (180 << 24) | (0 << 16) | (0 << 8) | 255;    // Red

                // Draw 0.3-block tick along the surface normal from the bounce point
                double endX = bp.PosX + bp.NormalX * 0.3;
                double endY = bp.PosY + bp.NormalY * 0.3;
                double endZ = bp.PosZ + bp.NormalZ * 0.3;
                AppendLine(mesh, bp.PosX, bp.PosY, bp.PosZ, endX, endY, endZ, color, cam);
            }
        }

        /// <summary>
        /// Reposition: for each repositioned sound, green box at original sound pos,
        /// orange box at apparent pos, yellow line connecting them.
        /// </summary>
        private void AppendRepositionLines(MeshData mesh, Vec3d cam)
        {
            int greenColor = (200 << 24) | (0 << 16) | (255 << 8) | 0;     // Green
            int orangeColor = (200 << 24) | (0 << 16) | (165 << 8) | 255;  // Orange
            int yellowColor = (200 << 24) | (0 << 16) | (255 << 8) | 255;  // Yellow

            for (int i = 0; i < activeRepositionCount; i++)
            {
                ref RepositionPair rp = ref activeRepositions[i];
                AppendWireframeBox(mesh, rp.SrcX, rp.SrcY, rp.SrcZ, 0.08f, greenColor, cam);
                AppendWireframeBox(mesh, rp.AppX, rp.AppY, rp.AppZ, 0.08f, orangeColor, cam);
                AppendLine(mesh, rp.SrcX, rp.SrcY, rp.SrcZ,
                    rp.AppX, rp.AppY, rp.AppZ, yellowColor, cam);
            }
        }

        /// <summary>
        /// Openings: cyan wireframe boxes at each opening probe exit.
        /// Brighter alpha for low OccToPlayer (clear path to player).
        /// </summary>
        private void AppendOpeningBoxes(MeshData mesh, Vec3d cam)
        {
            for (int i = 0; i < activeOpeningCount; i++)
            {
                ref OpeningData op = ref activeOpenings[i];
                byte alpha = (byte)Math.Clamp((int)(255 - op.OccToPlayer * 80), 80, 255);
                int color = (alpha << 24) | (255 << 16) | (255 << 8) | 0; // Cyan

                AppendWireframeBox(mesh, op.PosX, op.PosY, op.PosZ, 0.1f, color, cam);
            }
        }

        public void Dispose()
        {
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
            currentMeshRef?.Dispose();
            currentMeshRef = null;
            if (Instance == this) Instance = null;
        }
    }
}
