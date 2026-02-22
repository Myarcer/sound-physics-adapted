using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace soundphysicsadapted
{
    /// <summary>
    /// Centralized stereo-to-mono downmix manager for positional 3D audio.
    /// 
    /// OpenAL does NOT spatialize stereo sources — only mono sources get 3D positioning.
    /// Many vanilla VS sounds ship as stereo .ogg but are played positionally via PlaySoundAt,
    /// resulting in flat 2D audio with no directionality (explosions, block sounds, etc.).
    /// 
    /// This manager provides:
    /// 1. Auto-detection: positional + stereo + non-relative → needs mono
    /// 2. Cached downmix: each stereo asset is only converted once, then cached
    /// 3. Universal hook: patches StartPlaying(AudioData, SoundParams, AssetLocation)
    ///    which is the convergence point for BOTH PlaySoundAtInternal and LoadSound paths
    /// 4. Legacy support: ForceMonoNextLoad and RequestMonoForAsset still work
    ///    for explicit requests (weather pools, resonator), but are now redundant
    ///    for positional sounds since auto-detection handles them.
    /// 
    /// Performance: Zero cost for already-mono sounds (int comparison + null check).
    /// First stereo downmix per asset incurs one-time CPU cost, then cached forever.
    /// </summary>
    public static class MonoDownmixManager
    {
        /// <summary>
        /// Cache of mono-downmixed AudioMetaData per asset location string.
        /// Avoids re-downmixing on every positional source creation.
        /// Thread-safe: only accessed from main thread (VS enforces this for audio).
        /// </summary>
        private static readonly Dictionary<string, AudioMetaData> monoCache = new Dictionary<string, AudioMetaData>();

        /// <summary>
        /// Per-asset explicit mono request set.
        /// Used by resonator patches to request mono for specific music tracks
        /// before the async load pipeline fires.
        /// Thread-safe via lock since StartMusic and LoadSound may run on different frames.
        /// </summary>
        private static readonly HashSet<string> pendingMonoAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object monoLock = new object();

        /// <summary>
        /// Thread-local flag for explicit mono requests (weather positional pools).
        /// When set, the next sound load will force mono regardless of auto-detection.
        /// Consumed synchronously during the same LoadSound call.
        /// </summary>
        [ThreadStatic]
        public static bool ForceMonoNextLoad;

        /// <summary>
        /// Tracks total number of auto-downmixed sounds for logging.
        /// </summary>
        private static int autoDownmixCount = 0;

        #region Auto-Detection

        /// <summary>
        /// Check if a sound should be auto-downmixed to mono for proper 3D spatialization.
        /// Returns true if: positional (non-null, non-zero position) + non-relative + stereo.
        /// </summary>
        public static bool ShouldAutoDownmix(SoundParams sparams, AudioData audiodata)
        {
            if (sparams == null || audiodata == null) return false;

            // Must have a world position (not listener-relative)
            if (sparams.RelativePosition) return false;
            if (sparams.Position == null) return false;
            if (sparams.Position.X == 0f && sparams.Position.Y == 0f && sparams.Position.Z == 0f) return false;

            // Check if the audio data is stereo
            var meta = audiodata as AudioMetaData;
            if (meta == null) return false;

            // Ensure loaded enough to check channels
            if (meta.Loaded < 2 && meta.Loaded != 0)
            {
                // Partially loaded — can't determine channels yet, skip
                return false;
            }

            // Only downmix if stereo (2 channels)
            return meta.Channels == 2;
        }

        #endregion

        #region Core Downmix

        /// <summary>
        /// Unified entry point: ensure AudioData is mono if the sound should be positional.
        /// Returns the original AudioData if already mono or non-positional.
        /// Returns a cached mono clone if stereo and positional.
        /// Zero-cost passthrough for already-mono sounds.
        /// </summary>
        public static AudioData EnsureMono(AudioData audiodata, SoundParams sparams)
        {
            // Fast path: check explicit requests first
            bool explicitRequest = false;

            if (ForceMonoNextLoad)
            {
                ForceMonoNextLoad = false;
                explicitRequest = true;
            }

            if (!explicitRequest && sparams?.Location != null)
            {
                explicitRequest = CheckAndConsumeMonoRequest(sparams.Location);
            }

            // Auto-detection: positional + stereo → needs mono
            if (!explicitRequest && !ShouldAutoDownmix(sparams, audiodata))
            {
                return audiodata; // No downmix needed
            }

            // Get the stereo AudioMetaData
            var stereoMeta = audiodata as AudioMetaData;
            if (stereoMeta == null) return audiodata;

            // Already mono — no conversion needed
            if (stereoMeta.Channels != 2) return audiodata;

            // Get or create mono version
            return GetOrCreateMonoVersion(stereoMeta);
        }

        /// <summary>
        /// Get or create a mono-downmixed clone of the given stereo AudioMetaData.
        /// The clone is cached by asset location to avoid repeated downmixing.
        /// The original AudioMetaData is NOT modified (other sounds keep using stereo).
        /// </summary>
        public static AudioMetaData GetOrCreateMonoVersion(AudioMetaData stereoMeta)
        {
            if (stereoMeta == null) return null;

            string key = stereoMeta.Asset?.Location?.ToString() ?? "";

            if (monoCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // Ensure the source data is loaded
            if (stereoMeta.Loaded < 2)
            {
                stereoMeta.Load();
            }

            if (stereoMeta.Channels != 2 || stereoMeta.Pcm == null)
            {
                // Already mono or no data — return as-is
                return stereoMeta;
            }

            try
            {
                // Create a new AudioMetaData with mono PCM
                // Uses the same asset reference (just for metadata — won't trigger re-decode
                // because we're setting Loaded=2 which skips DoLoad)
                var monoMeta = new AudioMetaData(stereoMeta.Asset);
                monoMeta.Pcm = DownmixStereoToMono(stereoMeta.Pcm);
                monoMeta.Channels = 1;
                monoMeta.Rate = stereoMeta.Rate;
                monoMeta.BitsPerSample = stereoMeta.BitsPerSample;
                monoMeta.Loaded = 2; // Mark as loaded — skip DoLoad(), go straight to createSoundSource()

                monoCache[key] = monoMeta;

                autoDownmixCount++;
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[MonoDownmix] Created mono version #{autoDownmixCount} for '{key}' " +
                    $"(stereo {stereoMeta.Pcm.Length}B -> mono {monoMeta.Pcm.Length}B, " +
                    $"rate={monoMeta.Rate}, bits={monoMeta.BitsPerSample})");

                return monoMeta;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[MonoDownmix] Failed to create mono version of '{key}': {ex.Message}");
                return stereoMeta; // Fallback to stereo
            }
        }

        /// <summary>
        /// Convert stereo 16-bit PCM to mono by averaging L+R channels.
        /// </summary>
        public static byte[] DownmixStereoToMono(byte[] stereoPcm)
        {
            int sampleCount = stereoPcm.Length / 2; // 2 bytes per sample
            int monoSampleCount = sampleCount / 2;  // Half the samples for mono
            byte[] monoPcm = new byte[monoSampleCount * 2]; // 2 bytes per sample

            for (int i = 0; i < monoSampleCount; i++)
            {
                int leftIndex = i * 4;
                short left = (short)(stereoPcm[leftIndex] | (stereoPcm[leftIndex + 1] << 8));
                short right = (short)(stereoPcm[leftIndex + 2] | (stereoPcm[leftIndex + 3] << 8));
                short mono = (short)((left + right) / 2);
                int monoIndex = i * 2;
                monoPcm[monoIndex] = (byte)(mono & 0xFF);
                monoPcm[monoIndex + 1] = (byte)((mono >> 8) & 0xFF);
            }

            return monoPcm;
        }

        #endregion

        #region Explicit Request API (Legacy + Resonator)

        /// <summary>
        /// Register an asset path for mono conversion on next LoadSound.
        /// Called by resonator before StartMusic triggers the async load pipeline.
        /// </summary>
        public static void RequestMonoForAsset(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath)) return;
            lock (monoLock)
            {
                pendingMonoAssets.Add(normalizedPath);
            }
            SoundPhysicsAdaptedModSystem.DebugLog($"[MonoDownmix] Registered explicit mono request for: {normalizedPath}");
        }

        /// <summary>
        /// Check if an asset has a pending mono request and consume it.
        /// Checks both the AssetLocation path variants.
        /// </summary>
        public static bool CheckAndConsumeMonoRequest(AssetLocation location)
        {
            if (location == null) return false;

            lock (monoLock)
            {
                if (pendingMonoAssets.Count == 0) return false;

                // Try path directly
                string path = location.Path?.ToLowerInvariant() ?? "";
                if (pendingMonoAssets.Remove(path)) return true;

                // Try with .ogg
                if (!path.EndsWith(".ogg"))
                {
                    if (pendingMonoAssets.Remove(path + ".ogg")) return true;
                }

                // Try with music/ prefix
                if (!path.StartsWith("sounds") && !path.StartsWith("music/"))
                {
                    string musicPath = "music/" + path;
                    if (pendingMonoAssets.Remove(musicPath)) return true;
                    if (!musicPath.EndsWith(".ogg") && pendingMonoAssets.Remove(musicPath + ".ogg")) return true;
                }

                // Try full location string (domain:path)
                return pendingMonoAssets.Remove(location.ToString());
            }
        }

        /// <summary>
        /// Check and consume a raw string path request (legacy compatibility).
        /// </summary>
        public static bool CheckAndConsumeMonoRequest(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (monoLock)
            {
                return pendingMonoAssets.Remove(path);
            }
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// Clear all caches. Called on mod dispose.
        /// </summary>
        public static void ClearCache()
        {
            monoCache.Clear();
            lock (monoLock)
            {
                pendingMonoAssets.Clear();
            }
            autoDownmixCount = 0;
            ForceMonoNextLoad = false;
        }

        /// <summary>
        /// Get stats for debug logging.
        /// </summary>
        public static string GetStats()
        {
            return $"MonoDownmixManager: {monoCache.Count} cached, {autoDownmixCount} total conversions";
        }

        #endregion
    }
}
