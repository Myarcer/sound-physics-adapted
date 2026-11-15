using Vintagestory.API.Client;

namespace soundphysicsadapted
{
    /// <summary>
    /// Decides whether a sound's Position is a point in the world.
    /// </summary>
    public static class SoundPosition
    {
        /// <summary>
        /// True for a non-zero Position on a sound that is not listener-relative.
        /// A listener-relative sound gives an offset from the ear, not world coordinates.
        /// Read as a world point, the offset traces from near the map origin and the sound
        /// goes silent. Read the flag live, because VS clears RelativePosition when
        /// SetPosition runs on a sound with no Position.
        /// </summary>
        public static bool IsWorld(SoundParams soundParams)
        {
            if (soundParams == null || soundParams.RelativePosition) return false;
            var pos = soundParams.Position;
            return pos != null && (pos.X != 0 || pos.Y != 0 || pos.Z != 0);
        }
    }
}
