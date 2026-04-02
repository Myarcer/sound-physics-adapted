using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Patches AmbientSound.updatePosition to capture bounding box face data
    /// for ambient volume sounds (beehives, water, lava).
    ///
    /// Produces multi-sample points across each player-facing face, scaled by
    /// face size. AudioPhysicsSystem uses these for weighted occlusion blending
    /// to compute a stable acoustic origin without face-flip artifacts.
    ///
    /// Also detects when the player is inside the volume — in that case,
    /// no face sampling is needed (VS handles inside-volume positioning natively).
    /// </summary>
    internal static class AmbientSoundPatches
    {
        // Per-sound face sample data, updated every updatePosition call.
        private static readonly Dictionary<ILoadedSound, FaceSampleData> _faceSamples = new();

        // Cached reflection for AmbientSound fields
        private static FieldInfo _soundField;
        private static FieldInfo _bboxField;
        private static bool _reflectionFailed;

        // Reusable lists to avoid allocation per tick
        private static readonly List<FaceSample> _tempSamples = new(64);
        private static readonly List<Cuboidi> _tempBboxes = new(8);

        // Inset from face surface to avoid block-boundary DDA issues
        private const double FACE_INSET = 0.15;

        // Max samples per face axis (3x3 = 9 max per face)
        private const int MAX_SAMPLES_PER_AXIS = 3;

        /// <summary>A single sample point on a face, with the face center for blending.</summary>
        internal struct FaceSample
        {
            public Vec3d SamplePoint;  // The point to DDA-check for occlusion
            public Vec3d FaceCenter;   // The face center this sample belongs to (for position blending)
        }

        internal struct FaceSampleData
        {
            public FaceSample[] Samples;
            public int Count;
            public bool PlayerInside;  // True if player is inside any bbox
            public Cuboidi[] Bboxes;       // Volume bounding boxes for DDA exclusion
            public int BboxCount;
        }

        public static void ApplyPatches(Harmony harmony, ICoreClientAPI api)
        {
            try
            {
                var ambientSoundType = AccessTools.TypeByName("Vintagestory.Client.NoObf.AmbientSound");
                if (ambientSoundType == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find AmbientSound type for bbox patch");
                    return;
                }

                var updatePosMethod = AccessTools.Method(ambientSoundType, "updatePosition");
                if (updatePosMethod == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find updatePosition method");
                    return;
                }

                // Cache reflection fields
                _soundField = AccessTools.Field(ambientSoundType, "Sound");
                _bboxField = AccessTools.Field(ambientSoundType, "BoundingBoxes");

                if (_soundField == null || _bboxField == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Could not find Sound/BoundingBoxes fields on AmbientSound");
                    _reflectionFailed = true;
                    return;
                }

                var postfix = new HarmonyMethod(typeof(AmbientSoundPatches), nameof(UpdatePositionPostfix));
                harmony.Patch(updatePosMethod, postfix: postfix);

                api.Logger.Notification("[SoundPhysicsAdapted] Ambient sound bbox face-sampling patch applied");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to apply ambient sound patches: {ex.Message}");
                _reflectionFailed = true;
            }
        }

        /// <summary>
        /// Get pre-computed face sample data for an ambient sound.
        /// Returns null if no data available.
        /// </summary>
        public static FaceSample[] GetFaceSamples(ILoadedSound sound, out int count, out bool playerInside)
        {
            count = 0;
            playerInside = false;
            if (sound == null) return null;
            if (_faceSamples.TryGetValue(sound, out var data))
            {
                count = data.Count;
                playerInside = data.PlayerInside;
                return data.Samples;
            }
            return null;
        }

        /// <summary>
        /// Get the bounding boxes for an ambient sound (for DDA exclusion).
        /// </summary>
        public static Cuboidi[] GetBboxes(ILoadedSound sound, out int bboxCount)
        {
            bboxCount = 0;
            if (sound == null) return null;
            if (_faceSamples.TryGetValue(sound, out var data))
            {
                bboxCount = data.BboxCount;
                return data.Bboxes;
            }
            return null;
        }

        // Legacy compatibility — old API still used elsewhere
        public static Vec3d[] GetFaceCandidates(ILoadedSound sound, out int count)
        {
            count = 0;
            return null; // No longer used — callers should use GetFaceSamples
        }

        /// <summary>
        /// Remove tracking for a disposed sound. Called during periodic cleanup.
        /// </summary>
        public static void RemoveSound(ILoadedSound sound)
        {
            if (sound != null)
                _faceSamples.Remove(sound);
        }

        /// <summary>
        /// Clear all tracked data (mod dispose).
        /// </summary>
        public static void Clear()
        {
            _faceSamples.Clear();
        }

        /// <summary>
        /// Postfix on AmbientSound.updatePosition(EntityPos).
        /// Produces multi-sample points across each player-facing face.
        /// Sample density scales with face size: 1x1=1, 3x3=4, 5x5=9.
        /// </summary>
        public static void UpdatePositionPostfix(object __instance, object position)
        {
            if (_reflectionFailed || !SoundPhysicsAdaptedModSystem.IsWorldReady) return;

            try
            {
                var sound = _soundField.GetValue(__instance) as ILoadedSound;
                if (sound == null) return;

                var entityPos = position as EntityPos;
                if (entityPos == null) return;

                var bboxes = _bboxField.GetValue(__instance) as System.Collections.IList;
                if (bboxes == null || bboxes.Count == 0) return;

                double playerX = entityPos.X;
                double playerY = entityPos.Y;
                double playerZ = entityPos.Z;

                // EntityPos.Y = feet position, but audio system uses eye position
                // (Pos.XYZ + LocalEyePos). Read dynamically so mods that change
                // player model height or allow crawling get correct results.
                var capi = SoundPhysicsAdaptedModSystem.ClientApi;
                double eyeOffsetY = capi?.World?.Player?.Entity?.LocalEyePos?.Y ?? 1.52;
                double playerEyeY = playerY + eyeOffsetY;

                _tempSamples.Clear();
                _tempBboxes.Clear();
                bool playerInside = false;

                foreach (var bboxObj in bboxes)
                {
                    if (bboxObj is not Cuboidi bbox) continue;

                    // VS stores bboxes as half-open intervals: (X, Y, Z, X+1, Y+1, Z+1)
                    // representing block [X,X+1) x [Y,Y+1) x [Z,Z+1).
                    // Convert to INCLUSIVE coords for DDA exclusion (subtract 1 from upper bounds).
                    // This ensures we only exclude blocks actually inside the sound volume —
                    // NOT adjacent blocks at +X/+Y/+Z which would cause false occ=0.
                    var exclusionBbox = new Cuboidi(
                        bbox.X1, bbox.Y1, bbox.Z1,
                        bbox.X2 - 1, bbox.Y2 - 1, bbox.Z2 - 1);
                    _tempBboxes.Add(exclusionBbox);

                    // Check if player is inside this bbox.
                    // Player position is float; use original VS coords (half-open interval).
                    // Upper bound is exclusive at blockCoord+1.
                    // Use eye Y for vertical check — that's where the listener/camera is.
                    if (playerX >= bbox.X1 && playerX < bbox.X2 &&
                        playerEyeY >= bbox.Y1 && playerEyeY < bbox.Y2 &&
                        playerZ >= bbox.Z1 && playerZ < bbox.Z2)
                    {
                        playerInside = true;
                        break; // No need to sample faces when inside
                    }

                    // VS uses half-open interval: [X1, X2) where X2 = X1 + blockCount.
                    // For single-block beehive at X=511910: X1=511910, X2=511911 → [511910, 511911).
                    // World center = (X1 + X2) / 2 = 511910.5 (NOT X1 + X2 + 1).
                    double cx = (bbox.X1 + bbox.X2) * 0.5;
                    double cy = (bbox.Y1 + bbox.Y2) * 0.5;
                    double cz = (bbox.Z1 + bbox.Z2) * 0.5;

                    // Size in world blocks: X2 - X1 (half-open, NOT X2 - X1 + 1).
                    int sizeX = bbox.X2 - bbox.X1;
                    int sizeY = bbox.Y2 - bbox.Y1;
                    int sizeZ = bbox.Z2 - bbox.Z1;

                    // -X face (world x = bbox.X1)
                    if (playerX < cx)
                        AddFaceSamples(bbox.X1 + FACE_INSET, cy, cz,
                            sizeX, sizeY, sizeZ, 'X', false);
                    // +X face (world x = bbox.X2)
                    if (playerX > cx)
                        AddFaceSamples(bbox.X2 - FACE_INSET, cy, cz,
                            sizeX, sizeY, sizeZ, 'X', true);
                    // -Y face (world y = bbox.Y1)
                    if (playerEyeY < cy)
                        AddFaceSamples(cx, bbox.Y1 + FACE_INSET, cz,
                            sizeX, sizeY, sizeZ, 'Y', false);
                    // +Y face (world y = bbox.Y2)
                    if (playerEyeY > cy)
                        AddFaceSamples(cx, bbox.Y2 - FACE_INSET, cz,
                            sizeX, sizeY, sizeZ, 'Y', true);
                    // -Z face (world z = bbox.Z1)
                    if (playerZ < cz)
                        AddFaceSamples(cx, cy, bbox.Z1 + FACE_INSET,
                            sizeX, sizeY, sizeZ, 'Z', false);
                    // +Z face (world z = bbox.Z2)
                    if (playerZ > cz)
                        AddFaceSamples(cx, cy, bbox.Z2 - FACE_INSET,
                            sizeX, sizeY, sizeZ, 'Z', true);
                }

                // Build bbox array for DDA exclusion
                Cuboidi[] bboxArr = _tempBboxes.Count > 0 ? _tempBboxes.ToArray() : null;
                int bboxArrCount = _tempBboxes.Count;

                if (playerInside)
                {
                    // Player inside volume — store flag, no samples needed
                    _faceSamples[sound] = new FaceSampleData
                    {
                        Samples = null,
                        Count = 0,
                        PlayerInside = true,
                        Bboxes = bboxArr,
                        BboxCount = bboxArrCount
                    };
                }
                else if (_tempSamples.Count > 0)
                {
                    // Reuse existing array if large enough
                    FaceSample[] arr;
                    if (_faceSamples.TryGetValue(sound, out var existing) &&
                        existing.Samples != null &&
                        existing.Samples.Length >= _tempSamples.Count)
                    {
                        arr = existing.Samples;
                    }
                    else
                    {
                        arr = new FaceSample[_tempSamples.Count];
                    }

                    for (int i = 0; i < _tempSamples.Count; i++)
                        arr[i] = _tempSamples[i];

                    _faceSamples[sound] = new FaceSampleData
                    {
                        Samples = arr,
                        Count = _tempSamples.Count,
                        PlayerInside = false,
                        Bboxes = bboxArr,
                        BboxCount = bboxArrCount
                    };
                }
                else
                {
                    _faceSamples.Remove(sound);
                }
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ERROR in AmbientSound updatePosition postfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate sample points across a face. The face is defined by its center
        /// and the two tangent axis sizes. Sample count scales with face area.
        /// </summary>
        private static void AddFaceSamples(
            double faceX, double faceY, double faceZ,
            int bboxSizeX, int bboxSizeY, int bboxSizeZ,
            char normalAxis, bool positive)
        {
            // Determine the two tangent axes and their sizes
            int tangent1Size, tangent2Size;
            switch (normalAxis)
            {
                case 'X': tangent1Size = bboxSizeY; tangent2Size = bboxSizeZ; break;
                case 'Y': tangent1Size = bboxSizeX; tangent2Size = bboxSizeZ; break;
                case 'Z': tangent1Size = bboxSizeX; tangent2Size = bboxSizeY; break;
                default: return;
            }

            // For 1-block faces, single center sample
            // For larger faces, grid: min(ceil(size/2), MAX) samples per axis
            int samplesT1 = Math.Min(Math.Max(1, (tangent1Size + 1) / 2), MAX_SAMPLES_PER_AXIS);
            int samplesT2 = Math.Min(Math.Max(1, (tangent2Size + 1) / 2), MAX_SAMPLES_PER_AXIS);

            // Face center for blending target
            var faceCenter = new Vec3d(faceX, faceY, faceZ);

            // Half-extents for sample distribution (inset slightly from edges)
            double halfT1 = tangent1Size * 0.5 - FACE_INSET;
            double halfT2 = tangent2Size * 0.5 - FACE_INSET;
            if (halfT1 < 0) halfT1 = 0;
            if (halfT2 < 0) halfT2 = 0;

            for (int i = 0; i < samplesT1; i++)
            {
                // Distribute samples evenly: -half to +half
                double t1 = samplesT1 == 1 ? 0.0
                    : halfT1 * (2.0 * i / (samplesT1 - 1) - 1.0);

                for (int j = 0; j < samplesT2; j++)
                {
                    double t2 = samplesT2 == 1 ? 0.0
                        : halfT2 * (2.0 * j / (samplesT2 - 1) - 1.0);

                    double sx, sy, sz;
                    switch (normalAxis)
                    {
                        case 'X': sx = faceX; sy = faceY + t1; sz = faceZ + t2; break;
                        case 'Y': sx = faceX + t1; sy = faceY; sz = faceZ + t2; break;
                        case 'Z': sx = faceX + t1; sy = faceY + t2; sz = faceZ; break;
                        default: continue;
                    }

                    _tempSamples.Add(new FaceSample
                    {
                        SamplePoint = new Vec3d(sx, sy, sz),
                        FaceCenter = faceCenter
                    });
                }
            }
        }

    }
}
