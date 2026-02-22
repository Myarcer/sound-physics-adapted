using ProtoBuf;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    [ProtoContract]
    public class ResonatorSyncPacket
    {
        [ProtoMember(1)]
        public BlockPos Pos;

        [ProtoMember(2)]
        public float PlaybackPosition;

        [ProtoMember(3)]
        public bool IsPlaying;

        /// <summary>
        /// True if the disc is paused (has saved position to restore on resume).
        /// Distinct from IsPlaying=false which could also mean stopped/no disc.
        /// </summary>
        [ProtoMember(4)]
        public bool IsPaused;

        /// <summary>
        /// The frozen rotation angle of the tuning cylinder when paused (radians).
        /// Used to restore visual disc position on chunk reload.
        /// </summary>
        [ProtoMember(5)]
        public float FrozenRotation;
    }
}
