using System;

namespace soundphysicsadapted
{
    /// <summary>
    /// Every temporal constant of the audible chain lives here.
    ///
    /// ARCHITECTURE RULE (audit item A4): each audible quantity has exactly ONE
    /// temporal stage, and that stage runs in <see cref="AudioRenderer.SmoothAll"/>
    /// on the fixed 25 ms tick:
    ///
    ///   filter gain  -> log-domain EMA (this file), asymmetric, delta-adaptive
    ///   reverb sends -> linear EMA (this file), delta-adaptive
    ///   position     -> lerp with speed-of-sound cap (this file)
    ///
    /// The physics tick (50/200/500 ms per sound) computes RAW targets only. It must
    /// never smooth an audible value: its rate changes with distance, so any EMA there
    /// converges at a different wall-clock speed per sound and stacks with the stage
    /// below it. The composite of two such stages matched no documented constant.
    ///
    /// TIME CONSTANTS
    /// The tables below give tau (time to 63 % of a step). 95 % is reached in ~3 tau.
    /// alpha = 1 - exp(-tickMs / tau).
    ///
    /// The tables are indexed by the SIZE of the remaining change, so a large step
    /// (walking through a doorway) converges fast and a small step (ray jitter between
    /// two ticks) is damped hard. Because the step size falls while the value converges,
    /// each transition eases out on its own.
    /// </summary>
    public static class SmoothingCurves
    {
        /// <summary>Fixed smoothing tick. All tau values below assume this rate.</summary>
        public const float TickMs = 25f;

        // === Filter gain (log domain = occlusion units = dB-linear) ===
        // Muffling (sound goes behind a wall) is faster than un-muffling: a sound that
        // appears is more noticeable than a sound that disappears.
        //
        //   delta (occlusion units) | tau down | tau up | 95 % down | 95 % up
        //   > 3.0                   |   60 ms  | 100 ms |   180 ms  |  300 ms
        //   > 1.5                   |   80 ms  | 130 ms |   240 ms  |  390 ms
        //   > 0.5                   |  110 ms  | 180 ms |   330 ms  |  540 ms
        //   else (jitter band)      |  160 ms  | 260 ms |   480 ms  |  780 ms
        private static readonly float[] GainDeltaBands = { 3.0f, 1.5f, 0.5f };
        private static readonly float[] GainTauDown = { 60f, 80f, 110f, 160f };
        private static readonly float[] GainTauUp = { 100f, 130f, 180f, 260f };

        // === Slew ceiling on the filter gain (audit A14) ===
        //
        // The tables above set the SHAPE of a transition. They do not bound its SPEED,
        // and a delta-indexed table makes the speed grow with the size of the step: a
        // 16 dB step at tau 60 ms leaves at about 100 dB per second. Measured rates and
        // how they are heard:
        //
        //     5-20 dB/s   a source that moves behind an edge, reads as physical
        //     > 40 dB/s   reads as a fader someone pulled, not as geometry
        //     > 200 dB/s  reads as a gate; the ear cannot resolve the ramp
        //
        // The ceiling below holds the filter inside the first band. Above the ceiling
        // the value moves at a constant rate in dB; once the table step falls under the
        // ceiling the table takes over again and the transition eases out on its own.
        //
        // WHY A CEILING IS NEEDED AT ALL. Occlusion here is the sum along a DDA ray, and
        // the nine-ray median is a rejection filter, so the target moves in whole-block
        // steps and never in between. Engines whose occlusion input is a ray FRACTION
        // are spatially smooth already and can converge in 50 ms (Steam Audio). Engines
        // whose input is a line-of-sight test need a long fade to hide the same step:
        // the Wwise Unreal integration fades occlusion over 500 ms, and the FMOD Unreal
        // integration over 200 ms. This mod is the second kind.
        //
        // DISTANCE. A sound goes quiet over the width of the shadow boundary, and that
        // width is the first Fresnel radius, sqrt(lambda * d / 4). It grows with the
        // square root of the distance: about 0.9 m at 5 m and 1.9 m at 20 m for 1 kHz,
        // which a walking player crosses in 0.9 s and 1.8 s. The ceiling therefore
        // falls with the square root of the distance as well. A door at arm's length
        // keeps the snap it has today; a sound 20 m off inside rock no longer drops
        // 16 dB in a third of a second.
        /// <summary>Slew ceiling for muffling at <see cref="SlewReferenceDistance"/>, in dB/s.</summary>
        public const float SlewCeilingDownDbPerSec = 20f;
        /// <summary>Slew ceiling for un-muffling. Lower: a sound that returns must not pump.</summary>
        public const float SlewCeilingUpDbPerSec = 15f;
        /// <summary>
        /// Distance the two ceilings above are quoted at, in blocks. Nearer than this the
        /// ceiling stays flat: the shadow boundary of a sound at arm's length is already
        /// narrower than one block, so the geometry, not the ceiling, ends the transition.
        /// </summary>
        public const float SlewReferenceDistance = 5f;

        /// <summary>
        /// Listener speed the two ceilings above are quoted at, in blocks per second —
        /// a walking player.
        ///
        /// The ceilings are written in dB per second because that is the unit the ear
        /// judges, but the thing being crossed is a DISTANCE, the shadow boundary. So the
        /// honest ceiling is dB per METRE of listener travel, and dB per second only
        /// follows once you know how fast the listener moves. A player who drops down a
        /// hole crosses the boundary about eight times faster than one who walks, and the
        /// sound has to seal about eight times faster with them. Holding a walking rate
        /// there took more than six seconds to reach full occlusion.
        /// </summary>
        public const float SlewReferenceSpeed = 1.5f;

        /// <summary>
        /// Hard ceiling whatever the speed, in dB/s. Past about 200 dB/s a level change
        /// is heard as a gate rather than as a ramp, so the speed term stops short of it.
        /// </summary>
        private const float SlewAbsoluteMaxDbPerSec = 150f;

        /// <summary>Natural-log gain units per dB: dB = 20 * log10(g) = 8.6859 * ln(g).</summary>
        private const float LnPerDb = 1f / 8.685889f;

        /// <summary>
        /// Largest step the filter may take this tick, in natural-log gain units.
        /// </summary>
        /// <param name="distance">Distance to the sound in blocks. A value at or below
        /// <see cref="SlewReferenceDistance"/>, including an unknown 0, selects the
        /// reference ceiling.</param>
        /// <param name="listenerSpeed">Listener speed in blocks per second. Below
        /// <see cref="SlewReferenceSpeed"/>, including an unknown 0, the ceiling holds at
        /// its walking value: a listener who stands still still hears doors close and
        /// blocks break, and those changes are not crossed at any speed.</param>
        /// <param name="muffling">True when the sound is getting quieter.</param>
        public static float MaxLogStepPerTick(float distance, float listenerSpeed, bool muffling)
        {
            float ceiling = muffling ? SlewCeilingDownDbPerSec : SlewCeilingUpDbPerSec;

            if (distance > SlewReferenceDistance)
                ceiling *= MathF.Sqrt(SlewReferenceDistance / distance);

            if (listenerSpeed > SlewReferenceSpeed)
                ceiling *= listenerSpeed / SlewReferenceSpeed;

            if (ceiling > SlewAbsoluteMaxDbPerSec)
                ceiling = SlewAbsoluteMaxDbPerSec;

            return ceiling * (TickMs / 1000f) * LnPerDb;
        }

        // === Reverb send gains (linear 0-1) ===
        // Reverb is less perceptible than the low-pass, so it may converge faster.
        //   delta | tau
        //   > 0.3 |  50 ms
        //   > 0.1 |  85 ms
        //   else  | 140 ms
        private static readonly float[] ReverbDeltaBands = { 0.3f, 0.1f };
        private static readonly float[] ReverbTau = { 50f, 85f, 140f };

        /// <summary>Reverb send change below this is inaudible — no OpenAL write.</summary>
        public const float ReverbConvergeEpsilon = 0.002f;

        // === Position ===
        /// <summary>Exponential approach per 25 ms tick (~70 ms tau, ~0.21 s to 95 %).</summary>
        public const float PositionFactor = 0.3f;
        /// <summary>Speed of sound: 343 m/s = 8.6 m per 25 ms tick.</summary>
        public const float PositionMaxSpeedPerTick = 8.6f;
        /// <summary>Target jump above this is a teleport or a new sound — apply at once.</summary>
        public const float PositionSnapThreshold = 15.0f;
        /// <summary>Stop moving the source when this close to the target.</summary>
        public const float PositionConvergeEpsilon = 0.02f;
        /// <summary>
        /// Stabilizes the TARGET position, not the audible one. Two diffraction paths can
        /// alternate dominance between raycasts and move the raw target by 7 m. This is
        /// input conditioning, not a convergence stage — the rule above still holds.
        /// </summary>
        public const float PositionTargetStabilizer = 0.15f;

        /// <summary>Converts a time constant into the per-tick EMA factor.</summary>
        public static float AlphaForTau(float tauMs)
        {
            if (tauMs <= 0f) return 1f;
            return 1f - MathF.Exp(-TickMs / tauMs);
        }

        /// <summary>
        /// EMA factor for the filter gain, in occlusion units.
        /// </summary>
        /// <param name="occlusionDelta">Absolute remaining change, in occlusion units.</param>
        /// <param name="muffling">True when the sound gets quieter (gain falls).</param>
        public static float GainAlpha(float occlusionDelta, bool muffling)
        {
            var tauTable = muffling ? GainTauDown : GainTauUp;
            for (int i = 0; i < GainDeltaBands.Length; i++)
            {
                if (occlusionDelta > GainDeltaBands[i])
                    return AlphaForTau(tauTable[i]);
            }
            return AlphaForTau(tauTable[GainDeltaBands.Length]);
        }

        /// <summary>EMA factor for one reverb send gain.</summary>
        public static float ReverbAlpha(float gainDelta)
        {
            for (int i = 0; i < ReverbDeltaBands.Length; i++)
            {
                if (gainDelta > ReverbDeltaBands[i])
                    return AlphaForTau(ReverbTau[i]);
            }
            return AlphaForTau(ReverbTau[ReverbDeltaBands.Length]);
        }
    }
}
