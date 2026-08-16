using System;

namespace soundphysicsadapted
{
    /// <summary>
    /// Volume envelope for a sound that loses or regains a processing slot.
    ///
    /// <see cref="SoundPlaybackThrottle"/> keeps only the closest N sounds in full
    /// processing. A sound near the boundary (a beehive at 40 blocks that loses its slot
    /// each time a boar grunts) must not switch between full and silent at once, so the
    /// envelope ramps between them.
    ///
    /// The envelope is stepped by <see cref="AudioRenderer.SmoothAll"/> on the fixed
    /// 25 ms tick, together with the filter it multiplies. It was previously stepped in
    /// the physics tick, which forced throttled sounds through the full per-sound update
    /// path (and a cache-gate bypass) only to keep the ramp moving.
    ///
    /// 1.0 = full processing, 0.0 = fully throttled.
    /// </summary>
    public sealed class ThrottleFadeState
    {
        /// <summary>Current envelope value, 0-1.</summary>
        public float Fade { get; private set; } = 1.0f;

        /// <summary>True while the envelope is held because the state oscillates.</summary>
        public bool IsFrozen => frozen;

        private bool lastThrottled;
        private long lastTransitionMs;
        private long windowStartMs;
        private int transitionCount;
        private bool frozen;

        // Three flips inside this window count as oscillation.
        private const long OSCILLATION_WINDOW_MS = 10000;
        private const int OSCILLATION_TRANSITIONS = 3;
        // No flip for this long releases the hold.
        private const long STABLE_RELEASE_MS = 5000;

        /// <summary>
        /// Advances the envelope by one tick.
        /// </summary>
        /// <param name="throttled">Throttle verdict for this sound right now.</param>
        /// <param name="nowMs">Current game time in milliseconds.</param>
        /// <param name="elapsedMs">Time since the last step (the smoothing tick).</param>
        /// <param name="fadeDurationMs">Time for a complete 0-1 ramp.</param>
        /// <param name="soundName">For the debug log only.</param>
        /// <returns>The new envelope value.</returns>
        public float Step(bool throttled, long nowMs, float elapsedMs, float fadeDurationMs, string soundName)
        {
            if (throttled != lastThrottled)
            {
                lastThrottled = throttled;
                lastTransitionMs = nowMs;

                if (nowMs - windowStartMs > OSCILLATION_WINDOW_MS)
                {
                    transitionCount = 0;
                    windowStartMs = nowMs;
                }
                transitionCount++;

                if (transitionCount >= OSCILLATION_TRANSITIONS && !frozen)
                {
                    frozen = true;
                    if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                        SoundPhysicsAdaptedModSystem.DebugLog(
                            $"[THROTTLE] Froze fade for {soundName} at {Fade:F2} " +
                            $"({transitionCount} transitions in {(nowMs - windowStartMs) / 1000f:F1}s)");
                }
            }

            // The budget settled — resume from the held value, no jump.
            if (frozen && lastTransitionMs > 0 && nowMs - lastTransitionMs > STABLE_RELEASE_MS)
            {
                frozen = false;
                transitionCount = 0;
                windowStartMs = nowMs;
                if (SoundPhysicsAdaptedModSystem.IsDebugEnabled)
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[THROTTLE] Unfroze fade for {soundName} (stable 5s, fade={Fade:F2})");
            }

            if (!frozen)
            {
                float step = fadeDurationMs > 0f ? Math.Min(1f, elapsedMs / fadeDurationMs) : 1f;
                Fade = throttled
                    ? Math.Max(0f, Fade - step)
                    : Math.Min(1f, Fade + step);
            }

            return Fade;
        }

        /// <summary>True when the envelope neither holds down a sound nor moves.</summary>
        public bool IsIdle => !frozen && Fade >= 1f && !lastThrottled;
    }
}
