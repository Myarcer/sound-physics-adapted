using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace soundphysicsadapted
{
    /// <summary>
    /// Limits how many positional sounds get full processing (raycasting + reverb).
    /// Sounds beyond the budget are "throttled" — muted via heavy LPF instead of stopped.
    /// This is safe because VS manages the sound lifecycle; we only control the filter.
    /// When the player moves, the budget re-evaluates and previously-muted sounds can unmute.
    /// 
    /// IMPORTANT: This does NOT block Start() or call Stop(). It works with AudioPhysicsSystem's
    /// existing tick loop to decide which sounds deserve full processing vs heavy muffling.
    /// </summary>
    public class SoundPlaybackThrottle
    {
        /// <summary>
        /// Set of sounds currently being throttled (heavy LPF applied, no raycast processing).
        /// AudioPhysicsSystem checks this to skip raycasting for throttled sounds.
        /// </summary>
        private readonly HashSet<ILoadedSound> _throttledSounds = new HashSet<ILoadedSound>();

        // Reusable structures for per-tick evaluation
        private readonly List<SoundDistanceEntry> _allSounds = new List<SoundDistanceEntry>();
        private readonly List<ILoadedSound> _purgeList = new List<ILoadedSound>();
        private readonly HashSet<ILoadedSound> _newThrottled = new HashSet<ILoadedSound>();

        // Stats
        private int _throttledCount;
        private int _unthrottledCount;

        private struct SoundDistanceEntry
        {
            public ILoadedSound Sound;
            public float Distance;
        }

        /// <summary>
        /// Check if a sound is currently throttled (should be muted, skip raycasting).
        /// Called from AudioPhysicsSystem during the tick loop.
        /// </summary>
        public bool IsThrottled(ILoadedSound sound)
        {
            return _throttledSounds.Contains(sound);
        }

        /// <summary>
        /// Re-evaluate which sounds should be throttled based on current distances.
        /// Called once per AudioPhysicsSystem tick with all active positional sounds.
        /// Sounds beyond the budget get throttled; closest sounds always get full processing.
        /// </summary>
        public void EvaluateThrottle(Dictionary<ILoadedSound, float> soundDistances)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableSoundThrottle)
            {
                // Throttle disabled — unthrottle everything
                if (_throttledSounds.Count > 0)
                    _throttledSounds.Clear();
                return;
            }

            int max = config.MaxConcurrentSounds;
            if (max <= 0)
            {
                _throttledSounds.Clear();
                return;
            }

            // Build sorted list of sounds by distance
            _allSounds.Clear();
            foreach (var kvp in soundDistances)
            {
                _allSounds.Add(new SoundDistanceEntry { Sound = kvp.Key, Distance = kvp.Value });
            }

            // If under budget, nothing to throttle
            if (_allSounds.Count <= max)
            {
                if (_throttledSounds.Count > 0)
                {
                    _unthrottledCount += _throttledSounds.Count;
                    _throttledSounds.Clear();
                }
                return;
            }

            // Sort by distance — closest first
            // Hysteresis: sounds that are currently playing (unthrottled) get a distance bonus
            // to prevent rapid muting/unmuting when multiple sounds are hovering around the budget cutoff distance.
            const float HYSTERESIS_BONUS = 3.0f; // Blocks
            _allSounds.Sort((a, b) => 
            {
                float distA = _throttledSounds.Contains(a.Sound) ? a.Distance : a.Distance - HYSTERESIS_BONUS;
                float distB = _throttledSounds.Contains(b.Sound) ? b.Distance : b.Distance - HYSTERESIS_BONUS;
                return distA.CompareTo(distB);
            });

            // First 'max' sounds get full processing; the rest get throttled
            _newThrottled.Clear();
            for (int i = max; i < _allSounds.Count; i++)
            {
                _newThrottled.Add(_allSounds[i].Sound);
            }

            // Track stats: newly throttled vs unthrottled
            foreach (var sound in _newThrottled)
            {
                if (!_throttledSounds.Contains(sound))
                {
                    _throttledCount++;
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[THROTTLE] Muted {GetSoundName(sound)} (budget {max}, total {_allSounds.Count})");
                }
            }
            foreach (var sound in _throttledSounds)
            {
                if (!_newThrottled.Contains(sound))
                {
                    _unthrottledCount++;
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[THROTTLE] Unmuted {GetSoundName(sound)}");
                }
            }

            _throttledSounds.Clear();
            foreach (var s in _newThrottled)
                _throttledSounds.Add(s);
        }

        /// <summary>
        /// Reset per-tick stats.
        /// </summary>
        public void ResetTickStats()
        {
            _throttledCount = 0;
            _unthrottledCount = 0;
        }

        public int ThrottledCount => _throttledSounds.Count;

        public string GetStats()
        {
            return $"Throttled={_throttledSounds.Count}, MutedThisTick={_throttledCount}, UnmutedThisTick={_unthrottledCount}";
        }

        public void Dispose()
        {
            _throttledSounds.Clear();
            _allSounds.Clear();
        }

        private static string GetSoundName(ILoadedSound sound)
        {
            try { return sound?.Params?.Location?.ToShortString() ?? "unknown"; }
            catch { return "unknown"; }
        }
    }
}
