using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Finds the world point of a listener-relative sound that a mod placed away from the
    /// head, for example to pan another player's sound toward that player. Walls between
    /// the listener and that point can then muffle it like any world sound.
    /// </summary>
    public static class ListenerRelativeSource
    {
        // Closer than this the sound is meant to play in the head (a held tool, a Geiger
        // counter). Tracing it would let the block above the head muffle it.
        private const double MinOffset = 2.0;

        /// <summary>
        /// Writes the world point that OpenAL plays a listener-relative sound from into
        /// <paramref name="into"/>. VS puts the listener at the eye and turns it with the
        /// flat view direction (up is always +Y), so the offset maps back exactly:
        /// x along right, y along up, -z along forward.
        /// False for a world sound, an offset under <see cref="MinOffset"/>, or a view
        /// straight up or down, where the flat direction has no length and OpenAL has no
        /// frame either.
        /// </summary>
        public static bool TryLocate(SoundParams soundParams, Vec3d eye, Vec3f view, Vec3d into)
        {
            if (soundParams == null || !soundParams.RelativePosition || eye == null || view == null) return false;

            var offset = soundParams.Position;
            if (offset == null) return false;
            if (offset.X * offset.X + offset.Y * offset.Y + offset.Z * offset.Z < MinOffset * MinOffset) return false;

            double len = Math.Sqrt(view.X * view.X + view.Z * view.Z);
            if (len < 1e-3) return false;
            double fx = view.X / len, fz = view.Z / len;

            // OpenAL's right axis is forward x up = (-fz, 0, fx).
            into.Set(
                eye.X - offset.X * fz - offset.Z * fx,
                eye.Y + offset.Y,
                eye.Z + offset.X * fx - offset.Z * fz);
            return true;
        }
    }
}
