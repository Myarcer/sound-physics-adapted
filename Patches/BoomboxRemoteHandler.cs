using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted.Patches
{
    /// <summary>
    /// Handles receiving boombox sync packets from other players and creating
    /// local sounds at the remote carrier's position.
    /// 
    /// Each remote carrier gets a RemoteBoombox entry with its own ILoadedSound.
    /// A 50ms tick updates positions (interpolated), applies volume curve, and
    /// cleans up timed-out entries.
    /// 
    /// This class is client-side only. The server never touches it.
    /// </summary>
    public static class BoomboxRemoteHandler
    {
        #region Types

        /// <summary>
        /// Tracks a single remote player's boombox sound on this client.
        /// </summary>
        private class RemoteBoombox
        {
            public ILoadedSound Sound;
            public long CarrierEntityId;
            public string TrackLocation;

            // Current rendered position (interpolated)
            public float CurrentX, CurrentY, CurrentZ;
            // Target position from latest packet
            public float TargetX, TargetY, TargetZ;

            public long LastUpdateMs;
            public float LastSyncedPlaybackPos;
        }

        #endregion

        #region State

        private static ICoreClientAPI capi;
        private static long tickListenerId;

        /// <summary>
        /// Active remote boombox sounds, keyed by carrier entity ID.
        /// </summary>
        private static readonly Dictionary<long, RemoteBoombox> remoteBoomboxes = new Dictionary<long, RemoteBoombox>();

        /// <summary>
        /// How long before a remote boombox is considered timed out (no packets received).
        /// </summary>
        private const long TIMEOUT_MS = 3000;

        /// <summary>
        /// Maximum playback drift (seconds) before we force-seek the remote sound.
        /// </summary>
        private const float MAX_PLAYBACK_DRIFT = 2.0f;

        /// <summary>
        /// Lerp speed for position interpolation (per second).
        /// At 50ms tick = 0.5 per tick, smooth tracking.
        /// </summary>
        private const float LERP_SPEED = 10f;

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the remote boombox handler. Called from StartClientSide.
        /// </summary>
        public static void Initialize(ICoreClientAPI api)
        {
            capi = api;
            tickListenerId = api.Event.RegisterGameTickListener(OnRemoteBoomboxTick, 50);
        }

        #endregion

        #region Packet Handler

        /// <summary>
        /// Called when this client receives a BoomboxSyncPacket relayed by the server.
        /// Creates, updates, or disposes remote boombox sounds.
        /// </summary>
        public static void OnBoomboxSyncReceived(BoomboxSyncPacket packet)
        {
            if (capi == null) return;

            // Ignore packets from ourselves (server shouldn't send these, but safety check)
            var localPlayer = capi.World?.Player?.Entity;
            if (localPlayer != null && localPlayer.EntityId == packet.CarrierEntityId) return;

            if (!packet.IsPlaying)
            {
                // Carrier stopped - dispose remote sound
                RemoveRemoteBoombox(packet.CarrierEntityId);
                return;
            }

            // Playing - create or update
            if (remoteBoomboxes.TryGetValue(packet.CarrierEntityId, out var existing))
            {
                // Update existing
                existing.TargetX = packet.PosX;
                existing.TargetY = packet.PosY;
                existing.TargetZ = packet.PosZ;
                existing.LastUpdateMs = capi.World.ElapsedMilliseconds;
                existing.LastSyncedPlaybackPos = packet.PlaybackPosition;

                // Check playback drift - force seek if too far off
                if (existing.Sound != null && !existing.Sound.IsDisposed && existing.Sound.IsPlaying)
                {
                    float drift = Math.Abs(existing.Sound.PlaybackPosition - packet.PlaybackPosition);
                    if (drift > MAX_PLAYBACK_DRIFT)
                    {
                        existing.Sound.PlaybackPosition = packet.PlaybackPosition;
                    }
                }
            }
            else
            {
                // Create new remote boombox
                CreateRemoteBoombox(packet);
            }
        }

        #endregion

        #region Sound Management

        /// <summary>
        /// Create a new ILoadedSound for a remote carrier's boombox.
        /// </summary>
        private static void CreateRemoteBoombox(BoomboxSyncPacket packet)
        {
            try
            {
                var assetLoc = new AssetLocation(packet.TrackLocation);
                // Ensure proper path format (vanilla resonator adds music/ prefix and .ogg suffix)
                if (!assetLoc.Path.StartsWith("sounds", StringComparison.OrdinalIgnoreCase))
                {
                    assetLoc.WithPathPrefixOnce("music/");
                }
                assetLoc.WithPathAppendixOnce(".ogg");

                var soundParams = new SoundParams(assetLoc)
                {
                    Position = new Vec3f(packet.PosX, packet.PosY, packet.PosZ),
                    RelativePosition = false,
                    ShouldLoop = false,
                    DisposeOnFinish = false,
                    SoundType = EnumSoundType.MusicGlitchunaffected,
                    Volume = 0f, // Start silent, tick will set correct volume
                    Range = 48f,
                    ReferenceDistance = 3f
                };

                var sound = capi.World.LoadSound(soundParams);
                if (sound == null)
                {
                    capi.Logger.Warning($"[SoundPhysicsAdapted] BoomboxRemote: Failed to load sound for carrier {packet.CarrierEntityId}, track={packet.TrackLocation}");
                    return;
                }

                sound.Start();

                // Seek to carrier's current playback position
                if (packet.PlaybackPosition > 0.5f)
                {
                    sound.PlaybackPosition = packet.PlaybackPosition;
                }

                var remote = new RemoteBoombox
                {
                    Sound = sound,
                    CarrierEntityId = packet.CarrierEntityId,
                    TrackLocation = packet.TrackLocation,
                    CurrentX = packet.PosX,
                    CurrentY = packet.PosY,
                    CurrentZ = packet.PosZ,
                    TargetX = packet.PosX,
                    TargetY = packet.PosY,
                    TargetZ = packet.PosZ,
                    LastUpdateMs = capi.World.ElapsedMilliseconds,
                    LastSyncedPlaybackPos = packet.PlaybackPosition
                };

                remoteBoomboxes[packet.CarrierEntityId] = remote;
                SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"BoomboxRemote: Created sound for carrier {packet.CarrierEntityId}, track={packet.TrackLocation}, pos=({packet.PosX:F1},{packet.PosY:F1},{packet.PosZ:F1})");
            }
            catch (Exception ex)
            {
                capi.Logger.Error($"[SoundPhysicsAdapted] BoomboxRemote: Exception creating sound: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop and dispose a remote boombox by carrier entity ID.
        /// </summary>
        private static void RemoveRemoteBoombox(long carrierEntityId)
        {
            if (remoteBoomboxes.TryGetValue(carrierEntityId, out var remote))
            {
                if (remote.Sound != null && !remote.Sound.IsDisposed)
                {
                    remote.Sound.Stop();
                    remote.Sound.Dispose();
                }
                remoteBoomboxes.Remove(carrierEntityId);
                SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"BoomboxRemote: Removed sound for carrier {carrierEntityId}");
            }
        }

        #endregion

        #region Tick

        /// <summary>
        /// Temp list to avoid modifying dictionary during iteration.
        /// </summary>
        private static readonly List<long> toRemove = new List<long>();

        /// <summary>
        /// 50ms tick: interpolate positions, apply volume curve, check timeouts.
        /// </summary>
        private static void OnRemoteBoomboxTick(float dt)
        {
            if (capi == null || remoteBoomboxes.Count == 0) return;

            var localPlayer = capi.World?.Player?.Entity;
            if (localPlayer == null) return;

            var listenerPos = localPlayer.Pos.XYZ.AddCopy(0, localPlayer.LocalEyePos.Y, 0);
            long now = capi.World.ElapsedMilliseconds;

            toRemove.Clear();

            foreach (var kvp in remoteBoomboxes)
            {
                var remote = kvp.Value;

                // Timeout check
                if (now - remote.LastUpdateMs > TIMEOUT_MS)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // Disposed check
                if (remote.Sound == null || remote.Sound.IsDisposed)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // Interpolate position toward target
                float lerpFactor = Math.Min(1f, LERP_SPEED * dt);
                remote.CurrentX += (remote.TargetX - remote.CurrentX) * lerpFactor;
                remote.CurrentY += (remote.TargetY - remote.CurrentY) * lerpFactor;
                remote.CurrentZ += (remote.TargetZ - remote.CurrentZ) * lerpFactor;

                remote.Sound.SetPosition(remote.CurrentX, remote.CurrentY, remote.CurrentZ);

                // Apply vanilla resonator volume curve: 1/log10(max(1, dist*0.7)) - 0.8
                float dist = GameMath.Sqrt(
                    (float)listenerPos.SquareDistanceTo(remote.CurrentX, remote.CurrentY, remote.CurrentZ)
                );
                float volume = GameMath.Clamp(
                    1f / (float)Math.Log10(Math.Max(1, dist * 0.7f)) - 0.8f,
                    0f, 1f
                );
                remote.Sound.SetVolume(volume);

                // Apply glitch pitch (same as vanilla resonator)
                float pitch = GameMath.Clamp(1f - capi.Render.ShaderUniforms.GlitchStrength, 0.1f, 1f);
                remote.Sound.SetPitch(pitch);

                // Track finished playing naturally (disc ended)
                if (remote.Sound.HasStopped)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            // Cleanup
            foreach (var id in toRemove)
            {
                RemoveRemoteBoombox(id);
            }
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Dispose all remote boombox sounds. Called on mod unload.
        /// </summary>
        public static void Cleanup()
        {
            foreach (var kvp in remoteBoomboxes)
            {
                if (kvp.Value.Sound != null && !kvp.Value.Sound.IsDisposed)
                {
                    kvp.Value.Sound.Stop();
                    kvp.Value.Sound.Dispose();
                }
            }
            remoteBoomboxes.Clear();

            if (tickListenerId != 0 && capi != null)
            {
                capi.Event.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }

            capi = null;
        }

        #endregion
    }
}
