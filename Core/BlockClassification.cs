using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Shared block classification and occlusion helpers.
    /// Single source of truth for block solidity checks and occlusion lookups,
    /// used by both OcclusionCalculator (sound occlusion) and
    /// WeatherEnclosureCalculator (weather enclosure rays).
    ///
    /// All caches live here — one set of caches, not duplicated per system.
    /// </summary>
    public static class BlockClassification
    {
        // === Block caches by block ID ===
        // VS typically has <8192 unique block IDs.
        private const int BLOCK_CACHE_SIZE = 8192;

        // Block occlusion value cache (avoids repeated MaterialConfig lookups)
        private static readonly float[] blockOcclusionCache = new float[BLOCK_CACHE_SIZE];
        private static readonly bool[] blockOcclusionCached = new bool[BLOCK_CACHE_SIZE];
        private static float cachedOcclusionPerSolidBlock = -1f; // Track config changes

        // Cache for TreatAsFullCube pattern matching (avoids repeated regex checks)
        // 0 = not cached, 1 = should treat as full cube, 2 = should NOT treat as full cube
        private static readonly byte[] treatAsFullCubeCache = new byte[BLOCK_CACHE_SIZE];

        // Cache for HasMultipleSolidFaces (stairs count as blocking, slabs fall back to AABB)
        // 0 = not cached, 1 = has solid faces, 2 = no solid faces
        private static readonly byte[] hasMultipleSolidFacesCache = new byte[BLOCK_CACHE_SIZE];

        // Cache for IsWeatherInteractable (doors, trapdoors — state-changing blocks)
        // 0 = not cached, 1 = is interactable, 2 = is NOT interactable
        private static readonly byte[] isWeatherInteractableCache = new byte[BLOCK_CACHE_SIZE];

        // Cache for IsOpenInteractable (door/gate in opened state — skip collision)
        // 0 = not cached, 1 = is open interactable, 2 = is NOT
        private static readonly byte[] isOpenInteractableCache = new byte[BLOCK_CACHE_SIZE];

        // Cache for IsChiseledBlock (custom voxel geometry — needs AABB path)
        // 0 = not cached, 1 = is chiseled, 2 = is NOT chiseled
        private static readonly byte[] isChiseledBlockCache = new byte[BLOCK_CACHE_SIZE];

        /// <summary>
        /// Clear all block caches. Call when config reloads or materials change.
        /// </summary>
        public static void ClearCache()
        {
            Array.Clear(blockOcclusionCached, 0, BLOCK_CACHE_SIZE);
            Array.Clear(treatAsFullCubeCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(hasMultipleSolidFacesCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(isWeatherInteractableCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(isOpenInteractableCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(isChiseledBlockCache, 0, BLOCK_CACHE_SIZE);
            cachedOcclusionPerSolidBlock = -1f;
        }

        /// <summary>
        /// Check if a block is a full cube (all 6 faces solid).
        /// Full cubes always occlude — no collision check needed (fast path).
        /// </summary>
        public static bool IsFullCube(Block block)
        {
            return block.SideSolid[BlockFacing.indexUP] &&
                   block.SideSolid[BlockFacing.indexDOWN] &&
                   block.SideSolid[BlockFacing.indexNORTH] &&
                   block.SideSolid[BlockFacing.indexSOUTH] &&
                   block.SideSolid[BlockFacing.indexEAST] &&
                   block.SideSolid[BlockFacing.indexWEST];
        }

        /// <summary>
        /// Check if a block has MULTIPLE solid faces (>= 2).
        /// Catches stairs (solid back/bottom), etc.
        /// Slabs (1 solid face) will fail this and fall back to accurate AABB raycasting.
        /// Excludes fences (no fully solid faces), flowers, grass.
        /// Used by DDA where ray is blocked by any substantial surface,
        /// not just perfect cubes. Cached per block ID for performance.
        /// </summary>
        public static bool HasMultipleSolidFaces(Block block)
        {
            if (block == null) return false;

            int blockId = block.Id;
            if (blockId >= 0 && blockId < BLOCK_CACHE_SIZE)
            {
                byte cached = hasMultipleSolidFacesCache[blockId];
                if (cached != 0)
                    return cached == 1;

                bool result = CheckMultipleSolidFaces(block);
                hasMultipleSolidFacesCache[blockId] = result ? (byte)1 : (byte)2;
                return result;
            }

            return CheckMultipleSolidFaces(block);
        }

        private static bool CheckMultipleSolidFaces(Block block)
        {
            int count = 0;
            if (block.SideSolid[BlockFacing.indexUP]) count++;
            if (block.SideSolid[BlockFacing.indexDOWN]) count++;
            if (block.SideSolid[BlockFacing.indexNORTH]) count++;
            if (block.SideSolid[BlockFacing.indexSOUTH]) count++;
            if (block.SideSolid[BlockFacing.indexEAST]) count++;
            if (block.SideSolid[BlockFacing.indexWEST]) count++;
            return count >= 2;
        }

        /// <summary>
        /// Check if a block should be treated as a full cube (skip AABB collision testing).
        /// Uses config pattern matching with per-block-ID caching for performance.
        /// Examples: leaded glass panes that fill most of the block space.
        /// </summary>
        public static bool ShouldTreatAsFullCube(Block block)
        {
            if (block == null) return false;

            int blockId = block.Id;
            if (blockId >= 0 && blockId < BLOCK_CACHE_SIZE)
            {
                byte cached = treatAsFullCubeCache[blockId];
                if (cached != 0)
                    return cached == 1; // 1 = true, 2 = false

                // Cache miss — check config
                var materialConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;
                bool result = materialConfig != null && materialConfig.ShouldTreatAsFullCube(block);
                treatAsFullCubeCache[blockId] = result ? (byte)1 : (byte)2;

                // Debug log first time we check a block type
                if (block.Code?.ToString()?.Contains("glasspane") == true)
                {
                    SoundPhysicsAdaptedModSystem.OcclusionDebugLog($"TreatAsFullCube check: {block.Code} => {result}");
                }

                return result;
            }

            // Block ID out of cache range — check directly
            var matConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;
            return matConfig != null && matConfig.ShouldTreatAsFullCube(block);
        }

        /// <summary>
        /// Check if a block is "solid enough" to occlude sound/weather.
        /// Combines all three checks: full cube, any solid face, or config override.
        /// Excludes chiseled blocks — they report SideSolid based on the shared Block type,
        /// not the per-instance voxel shape. Must use AABB collision path instead.
        /// This is the standard check used by both sound and weather DDA systems.
        /// </summary>
        public static bool IsSolidForOcclusion(Block block)
        {
            if (IsChiseledBlock(block)) return false;
            return IsFullCube(block) || HasMultipleSolidFaces(block) || ShouldTreatAsFullCube(block);
        }

        /// <summary>
        /// Check if a block is a chiseled block (custom voxel geometry).
        /// Chiseled blocks share a single Block type but each instance has unique geometry
        /// stored in the BlockEntity. SideSolid is unreliable — always route through AABB.
        /// Cached per block ID.
        /// </summary>
        public static bool IsChiseledBlock(Block block)
        {
            if (block == null) return false;

            int blockId = block.Id;
            if (blockId >= 0 && blockId < BLOCK_CACHE_SIZE)
            {
                byte cached = isChiseledBlockCache[blockId];
                if (cached != 0)
                    return cached == 1;

                bool result = block.Code?.Path?.StartsWith("chiseledblock") == true;
                isChiseledBlockCache[blockId] = result ? (byte)1 : (byte)2;
                return result;
            }

            return block.Code?.Path?.StartsWith("chiseledblock") == true;
        }

        /// <summary>
        /// Get occlusion value for a specific block.
        /// Uses MaterialSoundConfig for all lookups — checks block overrides first, then material.
        /// Results cached by block.Id to avoid repeated config lookups.
        /// </summary>
        public static float GetBlockOcclusion(Block block, SoundPhysicsConfig config)
        {
            // Air blocks have no occlusion
            if (block.BlockMaterial == EnumBlockMaterial.Air)
                return 0f;

            int blockId = block.Id;

            // Invalidate cache if OcclusionPerSolidBlock config changed
            if (cachedOcclusionPerSolidBlock != config.OcclusionPerSolidBlock)
            {
                Array.Clear(blockOcclusionCached, 0, BLOCK_CACHE_SIZE);
                cachedOcclusionPerSolidBlock = config.OcclusionPerSolidBlock;
            }

            // Check cache first (fast path for hot loop)
            if (blockId >= 0 && blockId < BLOCK_CACHE_SIZE && blockOcclusionCached[blockId])
            {
                return blockOcclusionCache[blockId];
            }

            // Cache miss — compute occlusion
            float occlusion;
            var materialConfig = SoundPhysicsAdaptedModSystem.MaterialConfig;
            if (materialConfig == null)
            {
                // Fallback to hardcoded defaults if config not loaded
                occlusion = config.OcclusionPerSolidBlock * GetMaterialMultiplierFallback(block.BlockMaterial);
            }
            else
            {
                // MaterialSoundConfig handles both block overrides AND material lookup.
                float occlusionMultiplier = materialConfig.GetOcclusion(block);
                occlusion = config.OcclusionPerSolidBlock * occlusionMultiplier;
            }

            // Store in cache
            if (blockId >= 0 && blockId < BLOCK_CACHE_SIZE)
            {
                blockOcclusionCache[blockId] = occlusion;
                blockOcclusionCached[blockId] = true;
            }

            return occlusion;
        }

        /// <summary>
        /// Simplified GetBlockOcclusion that auto-fetches config.
        /// Used by weather system which doesn't carry config references around.
        /// </summary>
        public static float GetBlockOcclusion(Block block)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null) return 1f;

            if (block.BlockMaterial == EnumBlockMaterial.Air)
                return 0f;

            return GetBlockOcclusion(block, config);
        }

        /// <summary>
        /// Check if a block is a liquid material (water or lava).
        /// Future-proof helper for VS 1.22: EnumBlockMaterial.Liquid is renamed to .Water.
        /// When upgrading, only this method needs to change (Liquid → Water).
        /// </summary>
        public static bool IsLiquidMaterial(Block block)
        {
            var mat = block.BlockMaterial;
            return mat == EnumBlockMaterial.Liquid
                || mat == EnumBlockMaterial.Lava;
        }

        /// <summary>
        /// Check if a block is solid enough to reflect sound (for reverb raytracing).
        /// Different from IsSolidForOcclusion: reverb needs a physical surface to bounce off,
        /// so we exclude materials that are too sparse/soft to create reflections.
        /// Used by AcousticRaytracer for bounce rays and shared airspace checks.
        /// </summary>
        public static bool IsSolidForReverb(Block block)
        {
            if (block.BlockMaterial == EnumBlockMaterial.Air ||
                block.BlockMaterial == EnumBlockMaterial.Fire)
                return false;

            // Liquids don't create meaningful reflections
            if (IsLiquidMaterial(block))
                return false;

            // Plants and leaves are too sparse to reflect
            if (block.BlockMaterial == EnumBlockMaterial.Plant ||
                block.BlockMaterial == EnumBlockMaterial.Leaves)
                return false;

            return true;
        }

        /// <summary>
        /// Check if a block is a weather-interactable (can change state to open/close).
        /// Doors and trapdoors are transparent for weather source SPAWNING but still
        /// contribute their occlusion for MUFFLING. This allows rain sources to exist
        /// behind closed doors with appropriate occlusion applied.
        /// Cached per block ID for hot-path performance.
        /// </summary>
        public static bool IsWeatherInteractable(Block block)
        {
            if (block == null) return false;

            int blockId = block.Id;
            if (blockId >= 0 && blockId < BLOCK_CACHE_SIZE)
            {
                byte cached = isWeatherInteractableCache[blockId];
                if (cached != 0)
                    return cached == 1;

                bool result = CheckWeatherInteractable(block);
                isWeatherInteractableCache[blockId] = result ? (byte)1 : (byte)2;
                return result;
            }

            return CheckWeatherInteractable(block);
        }

        private static bool CheckWeatherInteractable(Block block)
        {
            // --- Behavior check (universal, works for ALL mods) ---
            // Any block using VS door/trapdoor mechanics will have these behaviors.
            var behaviors = block.BlockBehaviors;
            if (behaviors != null && behaviors.Length > 0)
            {
                for (int i = 0; i < behaviors.Length; i++)
                {
                    string typeName = behaviors[i].GetType().Name;
                    if (typeName == "BlockBehaviorDoor" || typeName == "BlockBehaviorTrapDoor")
                        return true;
                }
            }

            // --- Fallback: block code substring check ---
            // Catches edge cases where mods don't use standard behaviors
            // but follow naming conventions (e.g. "gate3x3", "portcullis").
            string path = block.Code?.Path;
            if (path == null) return false;

            return path.Contains("door") || path.Contains("gate") || path.Contains("portcullis");
        }

        /// <summary>
        /// Check if a door/gate/trapdoor block is in the opened state.
        /// Modded multi-block gates (e.g., Medieval Expansion) use "spacer" blocks
        /// that retain collision boxes even when the gate is opened. This check lets
        /// the DDA skip collision testing and treat them as pass-through.
        /// Cached per block ID (each opened/closed variant has a unique ID).
        /// </summary>
        public static bool IsOpenInteractable(Block block)
        {
            if (block == null) return false;

            int blockId = block.Id;
            if (blockId >= 0 && blockId < BLOCK_CACHE_SIZE)
            {
                byte cached = isOpenInteractableCache[blockId];
                if (cached != 0)
                    return cached == 1;

                bool result = CheckOpenInteractable(block);
                isOpenInteractableCache[blockId] = result ? (byte)1 : (byte)2;
                return result;
            }

            return CheckOpenInteractable(block);
        }

        private static bool CheckOpenInteractable(Block block)
        {
            if (!IsWeatherInteractable(block)) return false;
            string path = block.Code?.Path;
            if (path == null) return false;
            // VS door/gate variants encode state: "door-oak-opened-north", "gate3x3-spacer-oak-opened-north"
            return path.Contains("opened") || path.Contains("-open-");
        }

        /// <summary>
        /// Check if a block is a multiblock spacer belonging to a door/gate controller.
        /// Vanilla 2x3 gates and similar multi-block doors use BlockMultiblock placeholders
        /// ("multiblock-monolithic-*") for their upper blocks. These spacers have no collision
        /// geometry of their own but still have a non-Air material, causing phantom occlusion
        /// in the DDA. Resolving to the controller lets us skip the spacer entirely — the
        /// actual door panel collision lives on the controller block position.
        /// NOT cached — requires blockAccessor + position context per call.
        /// </summary>
        public static bool IsMultiblockDoorSpacer(Block block, IBlockAccessor blockAccessor, int x, int y, int z)
        {
            string path = block.Code?.Path;
            if (path == null || !path.StartsWith("multiblock-")) return false;

            // Parse variant offsets to find the controller block position
            var variant = block.Variant;
            if (variant == null) return false;

            string dxStr, dyStr, dzStr;
            if (!variant.TryGetValue("dx", out dxStr) ||
                !variant.TryGetValue("dy", out dyStr) ||
                !variant.TryGetValue("dz", out dzStr))
                return false;

            int cdx = ParseVariantOffset(dxStr);
            int cdy = ParseVariantOffset(dyStr);
            int cdz = ParseVariantOffset(dzStr);

            // Controller = spacer position - offset
            Block controller = blockAccessor.GetBlock(
                new BlockPos(x - cdx, y - cdy, z - cdz, 0));

            if (controller == null || controller.Id == 0) return false;
            if (controller.Code?.Path?.StartsWith("multiblock-") == true) return false;

            return IsWeatherInteractable(controller);
        }

        /// <summary>
        /// Parse a VS multiblock variant offset string.
        /// "0" -> 0, "p1" -> +1, "n1" -> -1, "p2" -> +2, etc.
        /// </summary>
        private static int ParseVariantOffset(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "0") return 0;
            if (s.StartsWith("n") && int.TryParse(s.Substring(1), out int nv)) return -nv;
            if (s.StartsWith("p") && int.TryParse(s.Substring(1), out int pv)) return pv;
            if (int.TryParse(s, out int v)) return v;
            return 0;
        }

        /// <summary>
        /// Fallback material-based occlusion multiplier when config not loaded.
        /// Based on Sound Physics Remastered defaults.
        /// </summary>
        public static float GetMaterialMultiplierFallback(EnumBlockMaterial material)
        {
            return material switch
            {
                EnumBlockMaterial.Stone => 1.0f,
                EnumBlockMaterial.Ore => 1.0f,
                EnumBlockMaterial.Metal => 0.95f,
                EnumBlockMaterial.Brick => 0.9f,
                EnumBlockMaterial.Ceramic => 0.8f,
                EnumBlockMaterial.Ice => 0.7f,
                EnumBlockMaterial.Soil => 0.6f,
                EnumBlockMaterial.Wood => 0.5f,
                EnumBlockMaterial.Gravel => 0.4f,
                EnumBlockMaterial.Cloth => 0.3f,
                EnumBlockMaterial.Sand => 0.3f,
                EnumBlockMaterial.Snow => 0.15f,
                EnumBlockMaterial.Glass => 0.1f,
                EnumBlockMaterial.Leaves => 0.05f,
                EnumBlockMaterial.Plant => 0.02f,
                EnumBlockMaterial.Liquid => 0.2f,
                EnumBlockMaterial.Lava => 0.25f,
                EnumBlockMaterial.Air => 0.0f,
                _ => 0.5f
            };
        }
    }
}
