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
    /// 1. Auto-detection: positional + multi-channel + non-relative → needs mono
    /// 2. Cached downmix: each multi-channel asset is only converted once, then cached
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
        /// Returns true if: positional (non-null, non-zero position) + non-relative + multi-channel.
        /// Handles stereo (2ch), 5.1 (6ch), 7.1 (8ch), and any other multi-channel format.
        /// </summary>
        public static bool ShouldAutoDownmix(SoundParams sparams, AudioData audiodata)
        {
            if (sparams == null || audiodata == null) return false;

            // Must have a world position (not listener-relative)
            if (sparams.RelativePosition) return false;
            if (sparams.Position == null) return false;
            if (sparams.Position.X == 0f && sparams.Position.Y == 0f && sparams.Position.Z == 0f) return false;

            // Check if the audio data is multi-channel
            var meta = audiodata as AudioMetaData;
            if (meta == null) return false;

            // Ensure loaded enough to check channels
            if (meta.Loaded < 2 && meta.Loaded != 0)
            {
                // Partially loaded — can't determine channels yet, skip
                return false;
            }

            // Downmix any multi-channel source (stereo, 5.1, 7.1, etc.)
            return meta.Channels >= 2;
        }

        #endregion

        #region Core Downmix

        /// <summary>
        /// Unified entry point: ensure AudioData is mono if the sound should be positional.
        /// Returns the original AudioData if already mono or non-positional.
        /// Returns a cached mono clone if multi-channel and positional.
        /// Zero-cost passthrough for already-mono sounds.
        /// </summary>
        public static AudioData EnsureMono(AudioData audiodata, SoundParams sparams)
        {
            // Explicit requests are PEEKED, not consumed. Consuming up front threw the
            // request away whenever the swap could not happen (data not an AudioMetaData,
            // or a decode that never produced PCM), and the sound then played stereo with
            // no log line to say so. The per-asset request is consumed on commit only.
            bool explicitRequest = ForceMonoNextLoad;

            // ForceMonoNextLoad is a one-shot for this load call by contract — clear it
            // now so a failed downmix cannot leak the flag onto the next sound.
            ForceMonoNextLoad = false;

            if (!explicitRequest && sparams?.Location != null)
            {
                explicitRequest = MatchMonoRequest(sparams.Location, consume: false);
            }

            // Auto-detection: positional + multi-channel → needs mono
            if (!explicitRequest && !ShouldAutoDownmix(sparams, audiodata))
            {
                return audiodata; // No downmix needed
            }

            // Get the multi-channel AudioMetaData
            var sourceMeta = audiodata as AudioMetaData;
            if (sourceMeta == null)
            {
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[MonoDownmix] MISS: '{sparams?.Location}' is {audiodata?.GetType().Name ?? "null"}, " +
                    "not AudioMetaData — sound stays stereo and will not be positional");
                return audiodata;
            }

            var result = GetOrCreateMonoVersion(sourceMeta);

            if (!ReferenceEquals(result, sourceMeta) || sourceMeta.Channels < 2)
            {
                // Downmixed, or the asset was mono to begin with — the request is served.
                if (sparams?.Location != null) MatchMonoRequest(sparams.Location, consume: true);
            }
            else
            {
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[MonoDownmix] MISS: downmix of '{sparams?.Location}' failed " +
                    $"(channels={sourceMeta.Channels}, loaded={sourceMeta.Loaded}) — sound stays stereo");
            }

            return result;
        }

        /// <summary>
        /// Get or create a mono-downmixed clone of the given multi-channel AudioMetaData.
        /// The clone is cached by asset location to avoid repeated downmixing.
        /// The original AudioMetaData is NOT modified (other sounds keep using their native format).
        /// Handles stereo (2ch), 5.1 surround (6ch), 7.1 (8ch), and any N-channel layout.
        /// </summary>
        public static AudioMetaData GetOrCreateMonoVersion(AudioMetaData sourceMeta)
        {
            if (sourceMeta == null) return null;

            string key = sourceMeta.Asset?.Location?.ToString() ?? "";

            if (monoCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // Ensure the source data is loaded
            if (sourceMeta.Loaded < 2)
            {
                sourceMeta.Load();
            }

            if (sourceMeta.Channels < 2 || sourceMeta.Pcm == null)
            {
                // Already mono or no data — return as-is
                return sourceMeta;
            }

            try
            {
                int sourceChannels = sourceMeta.Channels;

                // Create a new AudioMetaData with mono PCM
                // Uses the same asset reference (just for metadata — won't trigger re-decode
                // because we're setting Loaded=2 which skips DoLoad)
                var monoMeta = new AudioMetaData(sourceMeta.Asset);
                monoMeta.Pcm = DownmixToMono(sourceMeta.Pcm, sourceChannels);
                monoMeta.Channels = 1;
                monoMeta.Rate = sourceMeta.Rate;
                monoMeta.BitsPerSample = sourceMeta.BitsPerSample;
                monoMeta.Loaded = 2; // Mark as loaded — skip DoLoad(), go straight to createSoundSource()

                monoCache[key] = monoMeta;

                autoDownmixCount++;
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[MonoDownmix] Created mono version #{autoDownmixCount} for '{key}' " +
                    $"({sourceChannels}ch {sourceMeta.Pcm.Length}B -> mono {monoMeta.Pcm.Length}B, " +
                    $"rate={monoMeta.Rate}, bits={monoMeta.BitsPerSample})");

                return monoMeta;
            }
            catch (Exception ex)
            {
                SoundPhysicsAdaptedModSystem.DebugLog($"[MonoDownmix] Failed to create mono version of '{key}': {ex.Message}");
                return sourceMeta; // Fallback to original
            }
        }

        /// <summary>
        /// Convert N-channel 16-bit PCM to mono by averaging all channels per frame.
        /// Handles stereo (2ch), 5.1 (6ch), 7.1 (8ch), and any arbitrary channel count.
        /// For 5.1 surround: averages FL+FR+FC+LFE+RL+RR equally (simple mean).
        /// </summary>
        public static byte[] DownmixToMono(byte[] pcmData, int channels)
        {
            const int bytesPerSample = 2; // 16-bit PCM
            int frameSize = channels * bytesPerSample;
            int frameCount = pcmData.Length / frameSize;
            byte[] monoPcm = new byte[frameCount * bytesPerSample];

            for (int i = 0; i < frameCount; i++)
            {
                int sum = 0;
                int frameOffset = i * frameSize;
                for (int ch = 0; ch < channels; ch++)
                {
                    int sampleOffset = frameOffset + ch * bytesPerSample;
                    sum += (short)(pcmData[sampleOffset] | (pcmData[sampleOffset + 1] << 8));
                }
                short mono = (short)(sum / channels);
                int monoOffset = i * bytesPerSample;
                monoPcm[monoOffset] = (byte)(mono & 0xFF);
                monoPcm[monoOffset + 1] = (byte)((mono >> 8) & 0xFF);
            }

            return monoPcm;
        }

        /// <summary>
        /// Legacy wrapper: Convert stereo 16-bit PCM to mono.
        /// Delegates to the generalized DownmixToMono with channels=2.
        /// </summary>
        public static byte[] DownmixStereoToMono(byte[] stereoPcm)
        {
            return DownmixToMono(stereoPcm, 2);
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
            return MatchMonoRequest(location, consume: true);
        }

        /// <summary>
        /// Match an asset against the pending mono request set, optionally consuming it.
        /// Peek (consume: false) lets a caller decide to downmix and only then remove the
        /// request, so a failed swap does not silently discard it.
        /// </summary>
        public static bool MatchMonoRequest(AssetLocation location, bool consume)
        {
            if (location == null) return false;

            lock (monoLock)
            {
                if (pendingMonoAssets.Count == 0) return false;

                // Try path directly
                string path = location.Path?.ToLowerInvariant() ?? "";
                if (Match(path, consume)) return true;

                // Try with .ogg
                if (!path.EndsWith(".ogg") && Match(path + ".ogg", consume)) return true;

                // Try with music/ prefix
                if (!path.StartsWith("sounds", StringComparison.Ordinal) && !path.StartsWith("music/", StringComparison.Ordinal))
                {
                    string musicPath = "music/" + path;
                    if (Match(musicPath, consume)) return true;
                    if (!musicPath.EndsWith(".ogg") && Match(musicPath + ".ogg", consume)) return true;
                }

                // Try full location string (domain:path)
                return Match(location.ToString(), consume);
            }
        }

        /// <summary>Set lookup helper. Caller holds monoLock.</summary>
        private static bool Match(string key, bool consume)
        {
            if (!pendingMonoAssets.Contains(key)) return false;
            if (consume) pendingMonoAssets.Remove(key);
            return true;
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
