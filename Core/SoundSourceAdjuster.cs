using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Adjusts raw sound source positions provided by Vintage Story to improve
    /// occlusion accuracy. Handles multi-block-tall doors whose sound spawns at
    /// the bottom block, causing false occlusion from adjacent floor-level
    /// objects (chests, barrels, etc.).
    ///
    /// Key capability: resolves BlockMultiblock placeholder blocks back to their
    /// controller block to correctly read door dimensions, even when the sound
    /// position lands on an invisible proxy block.
    ///
    /// Designed for future expandability — add new block-type checks as needed
    /// without touching AudioPhysicsSystem.
    /// </summary>
    public static class SoundSourceAdjuster
    {
        // Reusable BlockPos instances to avoid allocation per call
        private static readonly BlockPos _checkPos = new BlockPos(0, 0, 0, 0);
        private static readonly BlockPos _controllerPos = new BlockPos(0, 0, 0, 0);

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

            string code = block.Code?.Path ?? "(null)";
            string blockClass = block.Class ?? "(none)";

            // --- Step 1: Multiblock placeholder resolution ---
            // If we hit a BlockMultiblock placeholder (invisible proxy), trace back
            // to the controller block using the offset encoded in its variant.
            // e.g. "multiblock-monolithic-0-p1-0" has dy=+1, meaning controller is 1 below.
            bool wasMultiblock = false;
            string originalCode = code;

            if (code.StartsWith("multiblock-"))
            {
                Block controllerBlock = ResolveMultiblockController(block, blockAccessor);
                if (controllerBlock != null && controllerBlock.Id != 0)
                {
                    wasMultiblock = true;
                    block = controllerBlock;
                    code = block.Code?.Path ?? "(null)";
                    blockClass = block.Class ?? "(none)";

                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[SoundAdjust] Multiblock resolved: '{originalCode}' -> '{code}' " +
                        $"(class={blockClass}) at ({_controllerPos.X},{_controllerPos.Y},{_controllerPos.Z})");
                }
                else
                {
                    SoundPhysicsAdaptedModSystem.DebugLog(
                        $"[SoundAdjust] Multiblock '{originalCode}' at ({_checkPos.X},{_checkPos.Y},{_checkPos.Z}) " +
                        $"-> controller resolution failed (block={controllerBlock?.Code?.Path ?? "null"})");
                }
            }

            // --- Step 2: Door adjustment ---
            // VS plays door sounds at (pos.X+0.5, pos.InternalY+0.5, pos.Z+0.5)
            // For multi-block-tall doors, shift Y to the top block center.
            float doorShift = GetDoorHeightShift(block);
            if (doorShift > 0f)
            {
                Vec3d adjusted = soundPos.Clone();
                adjusted.Y += doorShift;

                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[SoundAdjust] Door '{code}' (class={blockClass}) " +
                    $"{(wasMultiblock ? "via multiblock '" + originalCode + "' " : "")}" +
                    $"at ({_checkPos.X},{_checkPos.Y},{_checkPos.Z}) " +
                    $"shifted Y +{doorShift:F1} -> ({adjusted.X:F1},{adjusted.Y:F1},{adjusted.Z:F1})");

                return adjusted;
            }

            // Debug: log door blocks that didn't need shifting (height <= 1, e.g. trapdoors)
            if (code.Contains("door"))
            {
                int height = block.Attributes?["height"]?.AsInt(0) ?? 0;
                SoundPhysicsAdaptedModSystem.DebugLog(
                    $"[SoundAdjust] Found door block '{code}' (class={blockClass}) " +
                    $"at ({_checkPos.X},{_checkPos.Y},{_checkPos.Z}) " +
                    $"height={height} (no shift needed)");
            }

            // --- Future: add other multi-block adjustments here ---

            return soundPos;
        }

        /// <summary>
        /// Resolves a BlockMultiblock placeholder back to its controller block.
        /// Parses the offset from the block's variant keys (dx, dy, dz) and
        /// applies OffsetInv to find the controller position.
        ///
        /// BlockMultiblock variant format: multiblock-{type}-{dx}-{dy}-{dz}
        /// where dx/dy/dz use "n" prefix for negative, "p" for positive, bare for zero.
        /// e.g. "multiblock-monolithic-0-p1-0" means Offset=(0,+1,0), so
        /// OffsetInv=(0,-1,0) and controller is 1 block below.
        ///
        /// Sets _controllerPos as a side effect for debug logging.
        /// </summary>
        private static Block ResolveMultiblockController(Block multiblock, IBlockAccessor blockAccessor)
        {
            // Parse offset from variant dictionary
            // BlockMultiblock stores: Variant["dx"], Variant["dy"], Variant["dz"]
            // Format: "n1" = -1, "p1" = +1, "0" = 0
            var variant = multiblock.Variant;
            if (variant == null) return null;

            string dxStr, dyStr, dzStr;
            if (!variant.TryGetValue("dx", out dxStr) ||
                !variant.TryGetValue("dy", out dyStr) ||
                !variant.TryGetValue("dz", out dzStr))
            {
                return null;
            }

            int dx = ParseVariantOffset(dxStr);
            int dy = ParseVariantOffset(dyStr);
            int dz = ParseVariantOffset(dzStr);

            // OffsetInv = -Offset; controller = placeholder + OffsetInv = placeholder - Offset
            _controllerPos.Set(
                _checkPos.X - dx,
                _checkPos.Y - dy,
                _checkPos.Z - dz
            );

            Block controller = blockAccessor.GetBlock(_controllerPos);

            // Safety: if controller is itself a multiblock, don't recurse (corrupted world)
            if (controller != null && controller.Code?.Path?.StartsWith("multiblock-") == true)
            {
                return null;
            }

            return controller;
        }

        /// <summary>
        /// Parses a VS multiblock variant offset string.
        /// "0" -> 0, "p1" -> +1, "n1" -> -1, "p2" -> +2, etc.
        /// </summary>
        private static int ParseVariantOffset(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (s == "0") return 0;

            if (s.StartsWith("n"))
            {
                if (int.TryParse(s.Substring(1), out int val))
                    return -val;
            }
            else if (s.StartsWith("p"))
            {
                if (int.TryParse(s.Substring(1), out int val))
                    return val;
            }
            else
            {
                // Bare number (shouldn't happen for non-zero, but handle gracefully)
                if (int.TryParse(s, out int val))
                    return val;
            }

            return 0;
        }

        /// <summary>
        /// Returns the Y-shift needed to center the sound for a door block.
        /// Returns 0 if the block is not a door or is only 1 block tall.
        /// Works with both direct door blocks and controller blocks resolved
        /// from multiblock placeholders.
        /// </summary>
        private static float GetDoorHeightShift(Block block)
        {
            string code = block.Code?.Path;
            if (code == null) return 0f;

            // Match door blocks: "door-*", "roughhewndoor-*", gate blocks, etc.
            // Also matches "cokeovendoor", "dvnum-door-*" (mod doors)
            // Excludes trapdoors implicitly via height check (trapdoors have height=1)
            if (!code.Contains("door")) return 0f;

            // Read the height attribute — VS doors define this in block JSON.
            // Standard doors: height=2, large/castle doors: height=3+
            // Trapdoors: height=1 (or no height attribute, defaults to 1)
            int height = block.Attributes?["height"]?.AsInt(1) ?? 1;
            if (height <= 1) return 0f;

            // Shift sound to the TOP block center, not geometric center.
            // VS places sound at bottom_block_Y + 0.5. We want top_block_Y + 0.5.
            // height=2: shift +1.0 (from Y+0.5 to Y+1.5, top block center)
            // height=3: shift +2.0 (from Y+0.5 to Y+2.5, top block center)
            // This clears floor-level obstacles (chests, barrels) that a geometric
            // center shift (+0.5 for height=2) would still clip at short range.
            return (float)(height - 1);
        }
    }
}
