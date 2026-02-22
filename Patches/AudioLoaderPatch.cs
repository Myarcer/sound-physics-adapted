using HarmonyLib;
using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace soundphysicsadapted
{
    /// <summary>
    /// Harmony patch on OggDecoder.OggToWav — kept as hook point.
    /// All mono downmix logic has been centralized in MonoDownmixManager.
    /// This class provides backwards-compatible static API that delegates to the manager.
    /// </summary>
    [HarmonyPatch(typeof(OggDecoder), "OggToWav")]
    public static class AudioLoaderPatch
    {
        /// <summary>
        /// Tracks whether the next sound load should force mono downmix.
        /// Delegates to MonoDownmixManager.ForceMonoNextLoad.
        /// </summary>
        public static bool ForceMonoNextLoad
        {
            get => MonoDownmixManager.ForceMonoNextLoad;
            set => MonoDownmixManager.ForceMonoNextLoad = value;
        }

        public static void Postfix(AudioMetaData __result, IAsset asset)
        {
            // No blanket downmix — mono conversion is handled by MonoDownmixManager
            // at the StartPlaying/LoadSound level when specifically needed.
        }

        /// <summary>
        /// Register an asset path for mono conversion on next LoadSound.
        /// Delegates to MonoDownmixManager.RequestMonoForAsset.
        /// </summary>
        public static void RequestMonoForAsset(string normalizedPath)
        {
            MonoDownmixManager.RequestMonoForAsset(normalizedPath);
        }

        /// <summary>
        /// Check if an asset path has a pending mono request and consume it.
        /// Delegates to MonoDownmixManager.CheckAndConsumeMonoRequest.
        /// </summary>
        public static bool CheckAndConsumeMonoRequest(string path)
        {
            return MonoDownmixManager.CheckAndConsumeMonoRequest(path);
        }

        /// <summary>
        /// Get or create a mono-downmixed clone of the given stereo AudioMetaData.
        /// Delegates to MonoDownmixManager.GetOrCreateMonoVersion.
        /// </summary>
        public static AudioMetaData GetOrCreateMonoVersion(AudioMetaData stereoMeta)
        {
            return MonoDownmixManager.GetOrCreateMonoVersion(stereoMeta);
        }

        /// <summary>
        /// Clear the mono downmix cache. Called on mod dispose.
        /// Delegates to MonoDownmixManager.ClearCache.
        /// </summary>
        public static void ClearMonoCache()
        {
            MonoDownmixManager.ClearCache();
        }

        /// <summary>
        /// Convert stereo 16-bit PCM to mono by averaging L+R channels.
        /// Delegates to MonoDownmixManager.DownmixStereoToMono.
        /// </summary>
        public static byte[] DownmixStereoToMono(byte[] stereoPcm)
        {
            return MonoDownmixManager.DownmixStereoToMono(stereoPcm);
        }
    }

    // NOTE: MonoBufferPatch (constructor patching approach) was removed.
    // Universal mono downmix is now handled by MonoDownmixManager + 
    // LoadSoundPatch.StartPlayingAudioMonoPrefix which catches ALL positional
    // sounds at the convergence point before CreateAudio.
}