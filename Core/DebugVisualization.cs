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
    /// IRenderer-based debug visualization for acoustic raytracing data.
    /// Renders wireframe boxes and lines at sub-block precision using the VS wireframe shader.
    /// 
    /// All active viz modes combine into one MeshData with per-vertex colors = one draw call.
    /// Data is double-buffered: raytracer writes to pending buffers, renderer swaps to active.
    /// Mesh rebuilds at 4 Hz to balance responsiveness vs GPU upload cost.
    /// </summary>
    public class DebugVisualization : IRenderer, IDisposable
    {
        public static DebugVisualization Instance { get; private set; }

        // === Mode flags (runtime only, not persisted to config) ===
        public bool ShowBounces { get; set; }
        public bool ShowRays { get; set; }
        public bool ShowOcclusion { get; set; }
        public bool ShowReposition { get; set; }
        public bool ShowOpenings { get; set; }
        public bool ShowReverbSlots { get; set; }

        public bool AnyActive => ShowBounces || ShowRays || ShowOcclusion
            || ShowReposition || ShowOpenings || ShowReverbSlots;

        // Subset that needs raytracer data capture
        public bool AnyAcousticVizActive => ShowBounces || ShowRays || ShowOcclusion
            || ShowReposition || ShowOpenings || ShowReverbSlots;

        // === IRenderer ===
        public double RenderOrder => 0.99;
        public int RenderRange => 999;

        // === Render resources ===
        private ICoreClientAPI capi;
        private MeshRef currentMeshRef;
        private Matrixf mvMat = new Matrixf();

        // === Pending data buffers (written by raytracer on game tick thread) ===
        private BouncePoint[] pendingBounces = new BouncePoint[256];
        private int pendingBounceCount;
        private RaySegment[] pendingRays = new RaySegment[256];
        private int pendingRayCount;
        private OpeningData[] pendingOpenings = new OpeningData[24];
        private int pendingOpeningCount;
        private double pendingSoundPosX, pendingSoundPosY, pendingSoundPosZ;
        private double pendingApparentPosX, pendingApparentPosY, pendingApparentPosZ;
        private bool pendingHasReposition;
        // Track whether we've captured viz data this tick (first full-raytrace sound wins)
        private bool capturedThisTick = false;

        // === Active data (snapshot for rendering) ===
        private BouncePoint[] activeBounces = new BouncePoint[256];
        private int activeBounceCount;
        private RaySegment[] activeRays = new RaySegment[256];
        private int activeRayCount;
        private OpeningData[] activeOpenings = new OpeningData[24];
        private int activeOpeningCount;
        private double activeSoundPosX, activeSoundPosY, activeSoundPosZ;
        private double activeApparentPosX, activeApparentPosY, activeApparentPosZ;
        private bool activeHasReposition;

        private bool meshDirty = false;
        private long lastRebuildMs = 0;
        private const long REBUILD_INTERVAL_MS = 250; // 4 Hz mesh rebuild

        // Pre-allocated mesh arrays (worst case: all modes on)
        // bounces: 64*24=1536v, rays: 256*2=512v, occlusion: 64*2=128v, reposition: 50v, openings: 24*24=576v, reverb: reuses bounces
        private const int MAX_VERTICES = 4096;
        private const int MAX_INDICES = 8192;

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
        /// </summary>
        public void ResetTickCapture()
        {
            capturedThisTick = false;
            pendingRayCount = 0;
        }

        /// <summary>
        /// Whether we've already captured viz data this tick.
        /// Sounds are sorted closest-first, so the first full-raytrace sound is the nearest.
        /// </summary>
        public bool HasCapturedThisTick => capturedThisTick;

        /// <summary>
        /// Capture bounce and opening data from the raytracer for the nearest sound.
        /// Called from AudioPhysicsSystem after a full raytrace completes.
        /// </summary>
        public void CaptureFromRaytracer(
            BouncePoint[] bouncePoints, int bounceCount,
            OpeningData[] openings, int openingCount,
            SoundPathResult? pathResult, Vec3d soundPos)
        {
            // Copy bounce data
            int copyBounces = Math.Min(bounceCount, pendingBounces.Length);
            Array.Copy(bouncePoints, pendingBounces, copyBounces);
            pendingBounceCount = copyBounces;

            // Copy opening data
            int copyOpenings = Math.Min(openingCount, pendingOpenings.Length);
            Array.Copy(openings, pendingOpenings, copyOpenings);
            pendingOpeningCount = copyOpenings;

            // Capture reposition data
            pendingSoundPosX = soundPos.X;
            pendingSoundPosY = soundPos.Y;
            pendingSoundPosZ = soundPos.Z;

            if (pathResult.HasValue && pathResult.Value.RepositionOffset > 0.01)
            {
                pendingApparentPosX = pathResult.Value.ApparentPosition.X;
                pendingApparentPosY = pathResult.Value.ApparentPosition.Y;
                pendingApparentPosZ = pathResult.Value.ApparentPosition.Z;
                pendingHasReposition = true;
            }
            else
            {
                pendingHasReposition = false;
            }

            meshDirty = true;
            capturedThisTick = true;
        }

        /// <summary>
        /// Capture a ray segment for visualization.
        /// Called from AcousticRaytracer during the ray loop.
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
            ShowBounces = false;
            ShowRays = false;
            ShowOcclusion = false;
            ShowReposition = false;
            ShowOpenings = false;
            ShowReverbSlots = false;

            activeBounceCount = 0;
            activeRayCount = 0;
            activeOpeningCount = 0;
            activeHasReposition = false;

            currentMeshRef?.Dispose();
            currentMeshRef = null;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (!AnyActive || capi.World?.Player?.Entity == null) return;

            long nowMs = capi.ElapsedMilliseconds;

            // Swap pending→active and rebuild mesh at 4 Hz
            if (meshDirty && nowMs - lastRebuildMs >= REBUILD_INTERVAL_MS)
            {
                SwapBuffers();
                RebuildMesh();
                lastRebuildMs = nowMs;
                meshDirty = false;
            }

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

            // Camera-relative model-view matrix
            Vec3d camPos = capi.World.Player.Entity.CameraPos;
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

            // Copy reposition data
            activeSoundPosX = pendingSoundPosX;
            activeSoundPosY = pendingSoundPosY;
            activeSoundPosZ = pendingSoundPosZ;
            activeApparentPosX = pendingApparentPosX;
            activeApparentPosY = pendingApparentPosY;
            activeApparentPosZ = pendingApparentPosZ;
            activeHasReposition = pendingHasReposition;
        }

        private void RebuildMesh()
        {
            MeshData mesh = new MeshData(MAX_VERTICES, MAX_INDICES, false, false, true, true);
            mesh.SetMode(EnumDrawMode.Lines);
            vertexOffset = 0;
            indexOffset = 0;

            Vec3d cam = capi.World.Player.Entity.CameraPos;

            if (ShowBounces) AppendBounceBoxes(mesh, cam);
            if (ShowRays) AppendRayLines(mesh, cam);
            if (ShowOcclusion) AppendOcclusionLines(mesh, cam);
            if (ShowReposition && activeHasReposition) AppendRepositionLine(mesh, cam);
            if (ShowOpenings) AppendOpeningBoxes(mesh, cam);
            if (ShowReverbSlots) AppendReverbSlotBoxes(mesh, cam);

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
            if (mesh.VerticesCount >= mesh.XyzCount / 3)
            {
                mesh.GrowVertexBuffer();
                mesh.GrowNormalsBuffer();
            }

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
            if (mesh.IndicesCount + 2 > mesh.Indices.Length)
            {
                mesh.GrowIndexBuffer();
            }

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
        /// Bounces: wireframe boxes at each bounce point, colored by reflectivity.
        /// White=high(>0.7), Yellow=medium(0.3-0.7), DarkRed=low(<0.3).
        /// Alpha modulated by permeation (bright=clear path, dim=behind wall).
        /// </summary>
        private void AppendBounceBoxes(MeshData mesh, Vec3d cam)
        {
            for (int i = 0; i < activeBounceCount; i++)
            {
                ref BouncePoint bp = ref activeBounces[i];
                byte alpha = (byte)Math.Clamp((int)(bp.Permeation * 200 + 55), 60, 255);
                int color;
                if (bp.Reflectivity > 0.7f)
                    color = (alpha << 24) | (255 << 16) | (255 << 8) | 255; // White
                else if (bp.Reflectivity > 0.3f)
                    color = (alpha << 24) | (0 << 16) | (200 << 8) | 255;   // Yellow (RGBA)
                else
                    color = (alpha << 24) | (0 << 16) | (50 << 8) | 180;    // Dark Red

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
        /// Occlusion: lines from each bounce point to player, colored by occlusion severity.
        /// Green=clear(<0.3), Orange=partial(0.3-1.0), Red=heavy(>1.0).
        /// </summary>
        private void AppendOcclusionLines(MeshData mesh, Vec3d cam)
        {
            Vec3d playerPos = capi.World.Player.Entity.Pos.XYZ;
            playerPos.Add(capi.World.Player.Entity.LocalEyePos);

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

                AppendLine(mesh, bp.PosX, bp.PosY, bp.PosZ,
                    playerPos.X, playerPos.Y, playerPos.Z, color, cam);
            }
        }

        /// <summary>
        /// Reposition: green box at original sound pos, orange box at apparent pos,
        /// yellow line connecting them.
        /// </summary>
        private void AppendRepositionLine(MeshData mesh, Vec3d cam)
        {
            int greenColor = (200 << 24) | (0 << 16) | (255 << 8) | 0;     // Green
            int orangeColor = (200 << 24) | (0 << 16) | (165 << 8) | 255;  // Orange
            int yellowColor = (200 << 24) | (0 << 16) | (255 << 8) | 255;  // Yellow

            AppendWireframeBox(mesh, activeSoundPosX, activeSoundPosY, activeSoundPosZ,
                0.08f, greenColor, cam);
            AppendWireframeBox(mesh, activeApparentPosX, activeApparentPosY, activeApparentPosZ,
                0.08f, orangeColor, cam);
            AppendLine(mesh, activeSoundPosX, activeSoundPosY, activeSoundPosZ,
                activeApparentPosX, activeApparentPosY, activeApparentPosZ, yellowColor, cam);
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

        /// <summary>
        /// Reverb slots: bounce points colored by which reverb slot they feed.
        /// Slot 0=Blue, 1=Green, 2=Yellow, 3=Red.
        /// Slot determined by reflectionDelay = totalDistance * 0.12.
        /// </summary>
        private void AppendReverbSlotBoxes(MeshData mesh, Vec3d cam)
        {
            for (int i = 0; i < activeBounceCount; i++)
            {
                ref BouncePoint bp = ref activeBounces[i];
                float reflectionDelay = bp.TotalDistance * 0.12f;

                // Dominant slot: same crossfade logic as raytracer, pick highest weight
                float cross0 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 0f), 0f, 1f);
                float cross1 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 1f), 0f, 1f);
                float cross2 = 1f - Math.Clamp(Math.Abs(reflectionDelay - 2f), 0f, 1f);
                float cross3 = Math.Clamp(reflectionDelay - 2f, 0f, 1f);

                int color;
                float maxCross = Math.Max(Math.Max(cross0, cross1), Math.Max(cross2, cross3));
                if (maxCross == cross0)
                    color = (200 << 24) | (255 << 16) | (100 << 8) | 100;   // Blue
                else if (maxCross == cross1)
                    color = (200 << 24) | (100 << 16) | (255 << 8) | 100;   // Green
                else if (maxCross == cross2)
                    color = (200 << 24) | (100 << 16) | (255 << 8) | 255;   // Yellow
                else
                    color = (200 << 24) | (100 << 16) | (100 << 8) | 255;   // Red

                AppendWireframeBox(mesh, bp.PosX, bp.PosY, bp.PosZ, 0.06f, color, cam);
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
