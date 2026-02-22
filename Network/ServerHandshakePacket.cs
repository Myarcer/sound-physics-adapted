using ProtoBuf;

namespace soundphysicsadapted
{
    /// <summary>
    /// Sent from server to client on player join to confirm the server has the mod.
    /// This lets the client enable server-dependent features (resonator pause/resume).
    /// </summary>
    [ProtoContract]
    public class ServerHandshakePacket
    {
        [ProtoMember(1)]
        public string ModVersion;
    }
}
