using System;

namespace soundphysicsadapted
{
    /// <summary>
    /// Turns acoustic measurements into the low-pass gain a sound must reach.
    ///
    /// Pure math: no state, no smoothing, no OpenAL. The physics tick calls this to get
    /// the TARGET gain and hands that target to <see cref="AudioRenderer"/>, which owns
    /// the only temporal stage (see <see cref="SmoothingCurves"/>).
    ///
    /// Gain 1.0 = fully open, MinLowPassFilter = fully muffled.
    /// </summary>
    public static class FilterPipeline
    {
        /// <summary>Values behind one gain result, for the debug log.</summary>
        public struct Diagnostics
        {
            public float AirspaceFloor;
            public float DiffractionFloor;
            public float DiffractionDarkening;
            public float BendRatio;
            public float BestOpeningOcclusion;
            public int OpenPaths;
            public float AirspaceRatio;
        }

        /// <summary>Gain of the direct path alone.</summary>
        public static float DirectGain(float occlusion)
        {
            return occlusion <= 0f ? 1.0f : OcclusionCalculator.OcclusionToFilter(occlusion);
        }

        /// <summary>
        /// Gain of an occluded sound that path resolution routed around the obstacle.
        ///
        /// The direct occlusion always drives the base gain (SPR behaviour). Three floors
        /// lift it again where the geometry proves the sound has a way through:
        ///
        /// 1. SHARED AIRSPACE — the fraction of bounce rays that reach the player through
        ///    open air. SPR: directCutoff = max(directCutoff, sqrt(sharedAirspace) * 0.2).
        /// 2. PROBE OPENING (Issue 19) — a doorway is about 0.4 % of the ray sphere, so the
        ///    statistical bounces often miss it and leave the airspace ratio at 0 while the
        ///    sound is already repositioned to that doorway. A DDA-verified opening gets its
        ///    own floor at 75 % weight, capped below the full-airspace floor.
        /// 3. DIFFRACTION — bounce rays that find an L-corridor let more high frequency
        ///    through than the direct path does. Simplified UTD/Maekawa: about 8-10 dB per
        ///    90 degree bend. Guards keep a sealed sound out of this.
        ///
        /// Finally the gain is darkened in proportion to how far the sound had to bend.
        /// </summary>
        public static float OccludedGain(float occlusion, in SoundPathResult path, float distance,
            SoundPhysicsConfig config, out Diagnostics diag)
        {
            diag = default;

            float airspaceRatio = path.SharedAirspaceRatio;
            int openPaths = path.PathCount;
            float reposOffset = (float)path.RepositionOffset;

            // 1. Shared airspace floor
            float floor = MathF.Sqrt(airspaceRatio) * 0.2f;

            // 2. Probe-verified opening floor
            float bestOpeningOcc = path.BestOpeningOcclusion;
            if (bestOpeningOcc < float.MaxValue)
            {
                float openingGain = OcclusionCalculator.OcclusionToFilter(bestOpeningOcc);
                floor = Math.Max(floor, Math.Min(openingGain, 0.2f) * 0.75f);
            }

            float gain = Math.Max(DirectGain(occlusion), floor);

            // 3. Diffraction floor
            float diffractionFloor = 0f;
            bool hasDiffractionEvidence =
                openPaths >= 2 &&           // at least two open bounce paths
                airspaceRatio >= 0.05f &&   // more than 5 % shared airspace (not sealed)
                occlusion > 0.5f;           // the direct path is really obstructed

            if (hasDiffractionEvidence)
            {
                // Take the better of the measured indirect path and the physical minimum
                // for a single 90 degree bend. The maximum is on the gain, not on the
                // occlusion — pick the less muffled of the two.
                float measuredGain = OcclusionCalculator.OcclusionToFilter((float)path.BlendedOcclusion);
                float minBendGain = OcclusionCalculator.OcclusionToFilter(config.MinDiffractionOcclusion);
                float bOccGain = Math.Max(measuredGain, minBendGain);

                // Confidence from three independent signals:
                //   airspace 25 %+   = strong shared volume
                //   4+ open paths    = consistent indirect routing
                //   3 m+ offset      = the sound visibly bends around the corner
                float airspaceConf = Math.Min(airspaceRatio * 4f, 1f);
                float pathConf = Math.Min(openPaths / 4f, 1f);
                float reposConf = Math.Clamp(reposOffset / 3f, 0f, 1f);
                float confidence = Math.Min(1f, airspaceConf * pathConf + reposConf * 0.3f);

                diffractionFloor = Math.Min(bOccGain * confidence, config.MaxDiffractionFilter);
                gain = Math.Max(gain, diffractionFloor);
            }

            // Bend darkening: high frequency is lost in proportion to the bend.
            float bendRatio = distance > 0.1f ? Math.Clamp(reposOffset / distance, 0f, 1f) : 0f;
            float darkening = 1f - bendRatio * 0.3f;
            gain *= darkening;

            diag.AirspaceFloor = floor;
            diag.DiffractionFloor = diffractionFloor;
            diag.DiffractionDarkening = darkening;
            diag.BendRatio = bendRatio;
            diag.BestOpeningOcclusion = bestOpeningOcc;
            diag.OpenPaths = openPaths;
            diag.AirspaceRatio = airspaceRatio;

            return gain;
        }

        /// <summary>
        /// True when the sound sits on an acoustic edge (corner, doorway). Such a sound
        /// gets the fast update interval at any distance: the band between "muffled behind
        /// a wall" and "clear through the opening" is a few blocks wide, and any step of
        /// the player inside it changes the sound completely.
        /// </summary>
        public static bool NearAcousticBoundary(float airspaceRatio, float occlusion)
        {
            return (airspaceRatio > 0.02f && airspaceRatio < 0.5f)
                || (airspaceRatio < 0.02f && occlusion < 3.0f);
        }

        /// <summary>
        /// Keeps a sound audible that the player must hear (bells, temporal rifts).
        /// A floor of 0 or less does nothing.
        /// </summary>
        public static float ApplyPenetrationFloor(float gain, float floor)
        {
            return floor > 0f ? Math.Max(gain, floor) : gain;
        }
    }
}
