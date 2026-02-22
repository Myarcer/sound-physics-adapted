using ProtoBuf;

namespace soundphysicsadapted
{
    /// <summary>
    /// Packet for synchronizing boombox (carried resonator) audio between clients.
    /// The carrier client sends this to the server every 500ms while carrying.
    /// The server relays it to nearby players who create local sounds at the carrier's position.
    /// </summary>
    [ProtoContract]
    public class BoomboxSyncPacket
    {
        /// <summary>
        /// Entity ID of the player carrying the resonator.
        /// Remote clients use this to track the carrier entity for position interpolation.
        /// </summary>
        [ProtoMember(1)]
        public long CarrierEntityId;

        /// <summary>
        /// The music track asset path (e.g. "music/lament").
        /// Remote clients use this to create their own ILoadedSound instance.
        /// </summary>
        [ProtoMember(2)]
        public string TrackLocation;

        /// <summary>
        /// Current playback position in seconds.
        /// Remote clients use this to sync their local sound's position.
        /// </summary>
        [ProtoMember(3)]
        public float PlaybackPosition;

        /// <summary>
        /// True = actively playing, false = stopped (carrier placed/dropped resonator).
        /// When false, remote clients dispose their local sound for this carrier.
        /// </summary>
        [ProtoMember(4)]
        public bool IsPlaying;

        /// <summary>
        /// Sound position X (already offset-adjusted by carrier's boombox tick).
        /// </summary>
        [ProtoMember(5)]
        public float PosX;

        /// <summary>
        /// Sound position Y (already offset-adjusted by carrier's boombox tick).
        /// </summary>
        [ProtoMember(6)]
        public float PosY;

        /// <summary>
        /// Sound position Z (already offset-adjusted by carrier's boombox tick).
        /// </summary>
        [ProtoMember(7)]
        public float PosZ;
    }
}
