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
    /// VS repositions these sounds to the nearest bbox surface point each tick.
    /// That point can land on an occluded face even when other faces have clear
    /// line-of-sight. We capture ALL player-facing face centers so
    /// AudioPhysicsSystem can pick the least-occluded one as acoustic origin.
    /// </summary>
    internal static class AmbientSoundPatches
    {
        // Per-sound player-facing face centers, updated every updatePosition call.
        // Key: ILoadedSound from AmbientSound.Sound
        // Value: array of face center points (inset 0.1 from surface to avoid boundary issues)
        private static readonly Dictionary<ILoadedSound, FaceCandidateData> _faceCandidates = new();

        // Cached reflection for AmbientSound fields
        private static FieldInfo _soundField;
        private static FieldInfo _bboxField;
        private static bool _reflectionFailed;

        // Reusable list to avoid allocation per tick
        private static readonly List<Vec3d> _tempCandidates = new(18); // 6 faces * ~3 bboxes max

        // Inset from face surface to avoid block-boundary DDA issues
        private const double FACE_INSET = 0.15;

        internal struct FaceCandidateData
        {
            public Vec3d[] Candidates;
            public int Count;
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
        /// Get pre-computed player-facing face candidates for an ambient sound.
        /// Returns null if no data available.
        /// </summary>
        public static Vec3d[] GetFaceCandidates(ILoadedSound sound, out int count)
        {
            count = 0;
            if (sound == null) return null;
            if (_faceCandidates.TryGetValue(sound, out var data))
            {
                count = data.Count;
                return data.Candidates;
            }
            return null;
        }

        /// <summary>
        /// Remove tracking for a disposed sound. Called during periodic cleanup.
        /// </summary>
        public static void RemoveSound(ILoadedSound sound)
        {
            if (sound != null)
                _faceCandidates.Remove(sound);
        }

        /// <summary>
        /// Clear all tracked data (mod dispose).
        /// </summary>
        public static void Clear()
        {
            _faceCandidates.Clear();
        }

        /// <summary>
        /// Postfix on AmbientSound.updatePosition(EntityPos).
        /// Extracts bounding boxes and computes player-facing face centers.
        /// These are used by AudioPhysicsSystem for best-LOS occlusion sampling.
        /// </summary>
        public static void UpdatePositionPostfix(object __instance, object position)
        {
            if (_reflectionFailed || !SoundPhysicsAdaptedModSystem.IsWorldReady) return;

            try
            {
                var sound = _soundField.GetValue(__instance) as ILoadedSound;
                if (sound == null) return;

                // EntityPos — get X/Y/Z via cast or reflection
                var entityPos = position as EntityPos;
                if (entityPos == null) return;

                var bboxes = _bboxField.GetValue(__instance) as System.Collections.IList;
                if (bboxes == null || bboxes.Count == 0) return;

                double playerX = entityPos.X;
                double playerY = entityPos.Y;
                double playerZ = entityPos.Z;

                _tempCandidates.Clear();

                foreach (var bboxObj in bboxes)
                {
                    if (bboxObj is not Cuboidi bbox) continue;

                    double cx = (bbox.X1 + bbox.X2) * 0.5;
                    double cy = (bbox.Y1 + bbox.Y2) * 0.5;
                    double cz = (bbox.Z1 + bbox.Z2) * 0.5;

                    // For each of 6 faces, check if player-facing (normal dot toPlayer > 0).
                    // Inset from surface by FACE_INSET to avoid block-boundary DDA issues.
                    // Face center uses bbox midpoints on the two non-normal axes.

                    // -X face: player-facing if player is on the -X side
                    if (playerX < cx)
                        _tempCandidates.Add(new Vec3d(bbox.X1 + FACE_INSET, cy, cz));
                    // +X face
                    if (playerX > cx)
                        _tempCandidates.Add(new Vec3d(bbox.X2 - FACE_INSET, cy, cz));
                    // -Y face
                    if (playerY < cy)
                        _tempCandidates.Add(new Vec3d(cx, bbox.Y1 + FACE_INSET, cz));
                    // +Y face
                    if (playerY > cy)
                        _tempCandidates.Add(new Vec3d(cx, bbox.Y2 - FACE_INSET, cz));
                    // -Z face
                    if (playerZ < cz)
                        _tempCandidates.Add(new Vec3d(cx, cy, bbox.Z1 + FACE_INSET));
                    // +Z face
                    if (playerZ > cz)
                        _tempCandidates.Add(new Vec3d(cx, cy, bbox.Z2 - FACE_INSET));
                }

                if (_tempCandidates.Count > 0)
                {
                    // Reuse existing array if same size, else allocate
                    Vec3d[] arr;
                    if (_faceCandidates.TryGetValue(sound, out var existing) &&
                        existing.Candidates != null &&
                        existing.Candidates.Length >= _tempCandidates.Count)
                    {
                        arr = existing.Candidates;
                    }
                    else
                    {
                        arr = new Vec3d[_tempCandidates.Count];
                    }

                    for (int i = 0; i < _tempCandidates.Count; i++)
                        arr[i] = _tempCandidates[i];

                    _faceCandidates[sound] = new FaceCandidateData
                    {
                        Candidates = arr,
                        Count = _tempCandidates.Count
                    };
                }
                else
                {
                    _faceCandidates.Remove(sound);
                }
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog($"ERROR in AmbientSound updatePosition postfix: {ex.Message}");
            }
        }
    }
}
