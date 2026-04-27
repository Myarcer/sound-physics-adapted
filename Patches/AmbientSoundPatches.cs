using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Patches AmbientSound.updatePosition to capture bounding box data
    /// for ambient volume sounds (beehives, water, lava).
    ///
    /// Stores the volume's bounding boxes so AudioPhysicsSystem can exclude
    /// them from DDA occlusion rays (preventing self-occlusion).
    ///
    /// VS handles all sound positioning natively — we only capture bbox geometry.
    /// </summary>
    internal static class AmbientSoundPatches
    {
        // Per-sound bbox data, updated every updatePosition call.
        private static readonly Dictionary<ILoadedSound, BboxData> _bboxCache = new();

        // Cached reflection for AmbientSound fields
        private static FieldInfo _soundField;
        private static FieldInfo _bboxField;
        private static bool _reflectionFailed;

        internal struct BboxData
        {
            public Cuboidi[] Bboxes;
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

                api.Logger.Notification("[SoundPhysicsAdapted] Ambient sound bbox capture patch applied");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to apply ambient sound patches: {ex.Message}");
                _reflectionFailed = true;
            }
        }

        /// <summary>
        /// Get bounding boxes for an ambient sound (for DDA exclusion).
        /// </summary>
        public static Cuboidi[] GetBboxes(ILoadedSound sound, out int bboxCount)
        {
            bboxCount = 0;
            if (sound == null) return null;
            if (_bboxCache.TryGetValue(sound, out var data))
            {
                bboxCount = data.BboxCount;
                return data.Bboxes;
            }
            return null;
        }

        // Legacy API stubs — kept for compatibility with callers that haven't been updated.
        public static FaceSample[] GetFaceSamples(ILoadedSound sound, out int count, out bool playerInside)
        {
            count = 0;
            playerInside = false;
            return null;
        }

        public static Vec3d[] GetFaceCandidates(ILoadedSound sound, out int count)
        {
            count = 0;
            return null;
        }

        // Legacy struct — kept for compile compatibility (unused)
        internal struct FaceSample { }

        /// <summary>
        /// Remove tracking for a disposed sound. Called during periodic cleanup.
        /// </summary>
        public static void RemoveSound(ILoadedSound sound)
        {
            if (sound != null)
                _bboxCache.Remove(sound);
        }

        /// <summary>
        /// Clear all tracked data (mod dispose).
        /// </summary>
        public static void Clear()
        {
            _bboxCache.Clear();
        }

        /// <summary>
        /// Postfix on AmbientSound.updatePosition(EntityPos).
        /// Captures bounding box geometry for DDA exclusion.
        /// </summary>
        public static void UpdatePositionPostfix(object __instance)
        {
            if (_reflectionFailed || !SoundPhysicsAdaptedModSystem.IsWorldReady) return;

            try
            {
                var sound = _soundField.GetValue(__instance) as ILoadedSound;
                if (sound == null) return;

                var bboxes = _bboxField.GetValue(__instance) as System.Collections.IList;
                if (bboxes == null || bboxes.Count == 0) return;

                // Convert bboxes to inclusive coords for DDA exclusion.
                // VS stores half-open intervals: [X1, X2) where X2 = X1 + blockCount.
                // Subtract 1 from upper bounds so DDA only excludes blocks actually
                // inside the volume, not adjacent blocks at +X/+Y/+Z.
                var bboxArr = new Cuboidi[bboxes.Count];
                int count = 0;
                foreach (var bboxObj in bboxes)
                {
                    if (bboxObj is Cuboidi bbox)
                    {
                        bboxArr[count++] = new Cuboidi(
                            bbox.X1, bbox.Y1, bbox.Z1,
                            bbox.X2 - 1, bbox.Y2 - 1, bbox.Z2 - 1);
                    }
                }

                if (count > 0)
                {
                    _bboxCache[sound] = new BboxData
                    {
                        Bboxes = bboxArr,
                        BboxCount = count
                    };
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
