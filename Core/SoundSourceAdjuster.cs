using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Adjusts raw sound source positions provided by Vintage Story to improve
    /// occlusion accuracy. Currently handles multi-block-tall doors whose sound
    /// spawns at the bottom block, causing false occlusion from adjacent
    /// floor-level objects (chests, barrels, etc.).
    ///
    /// Designed for future expandability — add new block-type checks as needed
    /// without touching AudioPhysicsSystem.
    /// </summary>
    public static class SoundSourceAdjuster
    {
        // Reusable BlockPos to avoid allocation per call
        private static readonly BlockPos _checkPos = new BlockPos(0, 0, 0, 0);

        /// <summary>
        /// Given a raw sound position from VS, return an adjusted position
        /// that better represents the acoustic center of the source.
        /// Returns the original position unmodified for most blocks.
        /// </summary>
        public static Vec3d Adjust(Vec3d soundPos, IBlockAccessor blockAccessor)
        {
            if (soundPos == null || blockAccessor == null) return soundPos;

            _checkPos.Set((int)soundPos.X, (int)soundPos.Y, (int)soundPos.Z);
            Block block = blockAccessor.GetBlock(_checkPos);
            if (block == null || block.Id == 0) return soundPos;

            // --- Door adjustment ---
            // VS plays door sounds at (pos.X+0.5, pos.InternalY+0.5, pos.Z+0.5)
            // For multi-block-tall doors, shift Y to the vertical center.
            float doorShift = GetDoorHeightShift(block);
            if (doorShift > 0f)
            {
                Vec3d adjusted = soundPos.Clone();
                adjusted.Y += doorShift;

                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[SoundAdjust] Door at ({_checkPos.X},{_checkPos.Y},{_checkPos.Z}) " +
                    $"shifted Y +{doorShift:F1} -> ({adjusted.X:F1},{adjusted.Y:F1},{adjusted.Z:F1})");

                return adjusted;
            }

            // --- Future: add other multi-block adjustments here ---

            return soundPos;
        }

        /// <summary>
        /// Returns the Y-shift needed to center the sound for a door block.
        /// Returns 0 if the block is not a door or is only 1 block tall.
        /// </summary>
        private static float GetDoorHeightShift(Block block)
        {
            string code = block.Code?.Path;
            if (code == null) return 0f;

            // Match door blocks: "door-*", "roughhewndoor-*", gate blocks, etc.
            // Also matches "cokeovendoor", "dvnum-door-*" (mod doors)
            if (!code.Contains("door")) return 0f;

            // Read the height attribute — VS doors define this in block JSON.
            // Standard doors: height=2, large/castle doors: height=3+
            int height = block.Attributes?["height"]?.AsInt(1) ?? 1;
            if (height <= 1) return 0f;

            // Shift up by half the extra height to center between all blocks.
            // height=2: shift +0.5 (center between Y+0.5 and Y+1.5)
            // height=3: shift +1.0 (center between Y+0.5 and Y+2.5)
            return (height - 1) * 0.5f;
        }
    }
}
