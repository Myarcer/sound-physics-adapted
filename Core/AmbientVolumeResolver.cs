using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Per-sound memory of the ambient resolver. One instance per ambient volume sound,
    /// created when the first resolve runs.
    /// </summary>
    internal sealed class AmbientVolumeState
    {
        /// <summary>Face the sound is currently locked to (hysteresis).</summary>
        public Vec3d LockedFaceCenter;

        /// <summary>Face center after input stabilization.</summary>
        public Vec3d StabilizedPos;

        public bool HasStabilizedPos;
    }

    /// <summary>
    /// Finds where an ambient VOLUME sound must be heard from, and how occluded it is.
    ///
    /// Vintage Story plays beehives, water, lava and rainwindow as bounding-box volumes
    /// whose position tracks the player (the nearest point on the box). That point often
    /// sits on a face the player cannot hear through. This resolver samples every
    /// player-facing face and keeps the clearest one, so the direct ray already carries
    /// the correct occlusion and no second DDA is needed.
    ///
    /// Extracted from AudioPhysicsSystem.ProcessSoundRaycast (audit item A4).
    ///
    /// NOTE ON THE POSITION EMA BELOW: it conditions the INPUT of the face choice, it is
    /// not a convergence stage for an audible value — the audible position is converged
    /// once, in AudioRenderer.SmoothAll. See <see cref="SmoothingCurves"/>.
    /// </summary>
    internal static class AmbientVolumeResolver
    {
        /// <summary>Result of one resolve pass.</summary>
        internal struct Result
        {
            /// <summary>
            /// False when the sound has the Ambient type but no volume and no samples
            /// (resonators and other point sources). The caller must then treat it as a
            /// normal point source so probe rays can reposition it around walls.
            /// </summary>
            public bool IsVolume;

            /// <summary>Where the sound is heard from.</summary>
            public Vec3d AcousticPos;

            /// <summary>Occlusion taken from the face samples. -1 = no value, run the normal DDA.</summary>
            public float DerivedOcclusion;
        }

        /// <summary>Do not leave the locked face unless the new one is this much clearer.</summary>
        private const double FACE_SWITCH_THRESHOLD = 0.15;

        /// <summary>Face-center stabilization, about 300 ms at the 50 ms ambient cadence.</summary>
        private const float FACE_POS_STABILIZER = 0.15f;

        /// <summary>Panning starts to build up at this distance from the surface.</summary>
        private const float BLEND_START = 2.5f;

        /// <summary>True for the sound types Vintage Story plays as volumes.</summary>
        internal static bool IsVolumeSoundType(EnumSoundType? soundType)
        {
            // Rainwindow uses SoundType.Weather, not Ambient — both must be covered.
            return soundType == EnumSoundType.Ambient
                || soundType == EnumSoundType.AmbientGlitchunaffected
                || soundType == EnumSoundType.Weather;
        }

        internal static Result Resolve(ILoadedSound sound, Vec3d soundPos, Vec3d playerPos,
            IBlockAccessor blockAccessor, AmbientVolumeState state, string soundName, bool logThisTick)
        {
            var result = new Result { IsVolume = true, AcousticPos = soundPos, DerivedOcclusion = -1f };

            var samples = AmbientSoundPatches.GetFaceSamples(sound, out int sampleCount, out bool playerInside);
            var volBboxes = AmbientSoundPatches.GetBboxes(sound, out int volBboxCount);

            if (playerInside)
            {
                // Inside the volume: put the sound on the player. This removes left/right
                // panning completely and gives the enveloping effect. The proximity blend
                // below makes the way in smooth.
                result.AcousticPos = playerPos;
                result.DerivedOcclusion = 0f;

                if (logThisTick && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"[AMBIENT-INSIDE] {soundName} playerInside=true bboxes={volBboxCount}, occ=0, centered on player " +
                        $"plr=({playerPos.X:F2},{playerPos.Y:F2},{playerPos.Z:F2})");
            }
            else if (samples != null && sampleCount > 0)
            {
                ResolveFromFaces(samples, sampleCount, volBboxes, volBboxCount,
                    playerPos, soundPos, blockAccessor, state, soundName, logThisTick, ref result);
            }
            else if (volBboxes != null && volBboxCount > 0)
            {
                // No face samples (rainwindow) but the volume is known. Keep the position
                // Vintage Story chose and exclude the volume's own blocks so it does not
                // occlude itself.
                result.AcousticPos = soundPos;
                result.DerivedOcclusion = OcclusionCalculator.CalculatePathOcclusionExcludingBboxes(
                    soundPos, playerPos, blockAccessor, volBboxes, volBboxCount);

                if (logThisTick && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"[AMBIENT-FALLBACK] {soundName} no samples, using bbox-excluded DDA, occ={result.DerivedOcclusion:F2}");
            }

            if (result.DerivedOcclusion < 0f)
            {
                // Ambient type, but neither a volume nor samples: a point source.
                result.IsVolume = false;

                if (logThisTick && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                        $"[AMBIENT-DOWNGRADE] {soundName} has SoundType.Ambient but no bbox/samples — treating as point source");
                return result;
            }

            ApplyProximityBlend(playerPos, soundName, logThisTick, sound, ref result);
            return result;
        }

        /// <summary>
        /// Picks the clearest player-facing face and derives the occlusion from it.
        /// </summary>
        private static void ResolveFromFaces(AmbientSoundPatches.FaceSample[] samples, int sampleCount,
            Cuboidi[] volBboxes, int volBboxCount, Vec3d playerPos, Vec3d soundPos,
            IBlockAccessor blockAccessor, AmbientVolumeState state, string soundName,
            bool logThisTick, ref Result result)
        {
            // One multi-ray voted DDA per face center, with the volume's own boxes excluded.
            // A single ray per sample was unstable: edge clipping on one wall gave occ 1, 2
            // or 3 depending on the exact angle through the block corners. The voting keeps
            // the wall count stable — the center ray decides, the offsets find thin walls.
            //
            // The occlusion is the BEST face, never the average of all faces. Back faces
            // cross the whole volume and would pull an average up, which muffles a sound
            // the player stands right next to.
            Vec3d bestFaceCenter = null;
            double bestFaceClarity = -1;
            double bestFaceRawOcc = 0;
            double bestFaceDist = double.MaxValue;
            int facesTested = 0;

            double lockedFaceClarity = -1;
            double lockedFaceRawOcc = 0;

            Vec3d prevFaceCenter = null;
            for (int i = 0; i < sampleCount; i++)
            {
                var fc = samples[i].FaceCenter;
                if (prevFaceCenter != null && fc == prevFaceCenter)
                    continue; // same face — one DDA per face center is enough
                prevFaceCenter = fc;

                float faceOcc = (volBboxes != null && volBboxCount > 0)
                    ? OcclusionCalculator.CalculateExcludingBboxes(fc, playerPos, blockAccessor, volBboxes, volBboxCount)
                    : OcclusionCalculator.Calculate(fc, playerPos, blockAccessor);
                float clarity = Math.Max(0f, 1f - faceOcc);
                double faceDist = fc.DistanceTo(playerPos);
                facesTested++;

                // Order of preference: more clarity, then less raw occlusion (0.01 margin),
                // then the face closest to the player. The distance rule matters for volumes
                // with more than one box (a beehive has two): with equal clarity in open air
                // the first face in iteration order wins without it, which is often a far
                // box face. The proximity blend then misses and a step to the left or right
                // flips the face and the stereo image with it.
                if (clarity > bestFaceClarity ||
                    (clarity == bestFaceClarity && faceOcc < bestFaceRawOcc - 0.01f) ||
                    (clarity == bestFaceClarity && Math.Abs(faceOcc - bestFaceRawOcc) <= 0.01f
                     && faceDist < bestFaceDist))
                {
                    bestFaceClarity = clarity;
                    bestFaceRawOcc = faceOcc;
                    bestFaceCenter = fc;
                    bestFaceDist = faceDist;
                }

                if (state.LockedFaceCenter != null &&
                    fc.X == state.LockedFaceCenter.X &&
                    fc.Y == state.LockedFaceCenter.Y &&
                    fc.Z == state.LockedFaceCenter.Z)
                {
                    lockedFaceClarity = clarity;
                    lockedFaceRawOcc = faceOcc;
                }
            }

            if (bestFaceCenter == null || facesTested == 0)
            {
                // Every sample is fully occluded — keep the vanilla position.
                result.AcousticPos = soundPos;
                result.DerivedOcclusion = 1f;
                return;
            }

            Vec3d chosenFace = bestFaceCenter;
            double chosenRawOcc = bestFaceRawOcc;

            // Hysteresis: hold the current face unless the new one is clearly better.
            if (state.LockedFaceCenter != null && lockedFaceClarity >= 0)
            {
                double clarityDelta = bestFaceClarity - lockedFaceClarity;
                if (clarityDelta < FACE_SWITCH_THRESHOLD)
                {
                    // Clarity is close. Two tiebreakers decide.
                    //
                    // 1. RAW OCCLUSION — when all faces are occluded (clarity 0), take the
                    //    lower raw value. Side faces send diagonal rays through the wall
                    //    (occ 2+), the player-facing face goes straight through it (occ 1).
                    //    The 0.3 threshold keeps this from flipping on jitter.
                    double occDelta = lockedFaceRawOcc - bestFaceRawOcc;
                    if (occDelta <= 0.3)
                    {
                        // 2. DISTANCE — with clarity and occlusion both close, move to the
                        //    nearer face if it is really nearer. The threshold is low (0.3)
                        //    because the stabilizer below already stops position jumps.
                        //    Strong hysteresis here would block face tracking along a
                        //    multi-box volume and lock the sound to the wrong box.
                        double bestDist = bestFaceCenter.DistanceTo(playerPos);
                        double lockedDist = state.LockedFaceCenter.DistanceTo(playerPos);

                        if (bestDist >= lockedDist - 0.3)
                        {
                            chosenFace = state.LockedFaceCenter;
                            chosenRawOcc = lockedFaceRawOcc;
                        }
                    }
                }
            }
            state.LockedFaceCenter = chosenFace;

            Vec3d acousticPos;
            if (state.HasStabilizedPos)
            {
                var prev = state.StabilizedPos;
                acousticPos = new Vec3d(
                    prev.X + (chosenFace.X - prev.X) * FACE_POS_STABILIZER,
                    prev.Y + (chosenFace.Y - prev.Y) * FACE_POS_STABILIZER,
                    prev.Z + (chosenFace.Z - prev.Z) * FACE_POS_STABILIZER);
            }
            else
            {
                acousticPos = chosenFace;
            }
            state.StabilizedPos = acousticPos;
            state.HasStabilizedPos = true;

            // The raw, unclamped occlusion of the chosen face is used on purpose.
            // clarity = max(0, 1 - occ) stops at 0 for occ above 1, so (1 - clarity) caps
            // at 1.0. OcclusionToFilter is exponential and expects the accumulated value
            // (6.0 for six stone blocks). A cap at 1.0 would muffle far less than a normal
            // sound that passes the same formula.
            result.AcousticPos = acousticPos;
            result.DerivedOcclusion = (float)chosenRawOcc;

            if (logThisTick && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"[AMBIENT-BLEND] {soundName} tested {facesTested} faces (multi-ray voted), " +
                    $"bestFaceClarity={bestFaceClarity:F2} bestRawOcc={bestFaceRawOcc:F2} " +
                    $"derivedOcc={result.DerivedOcclusion:F2} " +
                    $"pos=({acousticPos.X:F2},{acousticPos.Y:F2},{acousticPos.Z:F2})");
        }

        /// <summary>
        /// Moves the acoustic position toward the player as the player comes closer
        /// (the Steam Audio approach). Panning falls off near the surface, which removes
        /// the left/right flip at box boundaries and between adjacent boxes. Fully inside,
        /// the sound is centered.
        /// </summary>
        private static void ApplyProximityBlend(Vec3d playerPos, string soundName, bool logThisTick,
            ILoadedSound sound, ref Result result)
        {
            float distToSound = (float)playerPos.DistanceTo(result.AcousticPos);
            float blendT = -1f; // -1 = out of range, no blend

            if (distToSound < BLEND_START)
            {
                // t = 0 at the surface (centered), t = 1 at BLEND_START (fully directional).
                // The square keeps the sound centered longer as the player walks away.
                float t = distToSound / BLEND_START;
                t = t * t;
                blendT = t;
                result.AcousticPos = new Vec3d(
                    playerPos.X + (result.AcousticPos.X - playerPos.X) * t,
                    playerPos.Y + (result.AcousticPos.Y - playerPos.Y) * t,
                    playerPos.Z + (result.AcousticPos.Z - playerPos.Z) * t);
            }

            if (logThisTick && SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
            {
                float dx = (float)(result.AcousticPos.X - playerPos.X);
                float dz = (float)(result.AcousticPos.Z - playerPos.Z);
                AmbientSoundPatches.GetFaceSamples(sound, out int diagSampleCount, out bool diagPlayerInside);
                AmbientSoundPatches.GetBboxes(sound, out int diagBboxCount);
                SoundPhysicsAdaptedModSystem.OcclusionDebugLog(
                    $"[AMBIENT-POS] {soundName} inside={diagPlayerInside} bboxes={diagBboxCount} " +
                    $"dist={distToSound:F2} blendT={blendT:F3} " +
                    $"stereoXZ={MathF.Sqrt(dx * dx + dz * dz):F3} (dx={dx:F2} dz={dz:F2}) " +
                    $"acPos=({result.AcousticPos.X:F2},{result.AcousticPos.Y:F2},{result.AcousticPos.Z:F2}) " +
                    $"plr=({playerPos.X:F2},{playerPos.Y:F2},{playerPos.Z:F2})");
            }
        }
    }
}
