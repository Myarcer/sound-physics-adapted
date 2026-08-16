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
