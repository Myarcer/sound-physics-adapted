using System;
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

        // Cache for HasAnySolidFace (slabs, stairs count as blocking)
        // 0 = not cached, 1 = has solid face, 2 = no solid faces
        private static readonly byte[] hasAnySolidFaceCache = new byte[BLOCK_CACHE_SIZE];

        // Cache for IsWeatherInteractable (doors, trapdoors — state-changing blocks)
        // 0 = not cached, 1 = is interactable, 2 = is NOT interactable
        private static readonly byte[] isWeatherInteractableCache = new byte[BLOCK_CACHE_SIZE];

        /// <summary>
        /// Clear all block caches. Call when config reloads or materials change.
        /// </summary>
        public static void ClearCache()
        {
            Array.Clear(blockOcclusionCached, 0, BLOCK_CACHE_SIZE);
            Array.Clear(treatAsFullCubeCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(hasAnySolidFaceCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(isWeatherInteractableCache, 0, BLOCK_CACHE_SIZE);
            cachedOcclusionPerSolidBlock = -1f;
        }

        /// <summary>
        /// Check if a block is a full cube (all 6 faces solid).
        /// Full cubes always occlude — no collision check needed (fast path).
        /// </summary>
        public static bool IsFullCube(Block block)
        {
            if (block.SideSolid == null) return false;
            return block.SideSolid[BlockFacing.indexUP] &&
                   block.SideSolid[BlockFacing.indexDOWN] &&
                   block.SideSolid[BlockFacing.indexNORTH] &&
                   block.SideSolid[BlockFacing.indexSOUTH] &&
                   block.SideSolid[BlockFacing.indexEAST] &&
                   block.SideSolid[BlockFacing.indexWEST];
        }

        /// <summary>
        /// Check if a block has ANY solid face.
        /// Catches slabs (solid top/bottom), stairs (solid back face), etc.
        /// Excludes fences (no fully solid faces), flowers, grass.
        /// Used by weather DDA where rain is blocked by any substantial surface,
        /// not just perfect cubes. Cached per block ID for performance.
        /// </summary>
        public static bool HasAnySolidFace(Block block)
        {
            if (block == null || block.SideSolid == null) return false;

            int blockId = block.Id;
            if (blockId >= 0 && blockId < BLOCK_CACHE_SIZE)
            {
                byte cached = hasAnySolidFaceCache[blockId];
                if (cached != 0)
                    return cached == 1;

                bool result = CheckAnySolidFace(block);
                hasAnySolidFaceCache[blockId] = result ? (byte)1 : (byte)2;
                return result;
            }

            return CheckAnySolidFace(block);
        }

        private static bool CheckAnySolidFace(Block block)
        {
            return block.SideSolid[BlockFacing.indexUP] ||
                   block.SideSolid[BlockFacing.indexDOWN] ||
                   block.SideSolid[BlockFacing.indexNORTH] ||
                   block.SideSolid[BlockFacing.indexSOUTH] ||
                   block.SideSolid[BlockFacing.indexEAST] ||
                   block.SideSolid[BlockFacing.indexWEST];
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
        /// This is the standard check used by both sound and weather DDA systems.
        /// </summary>
        public static bool IsSolidForOcclusion(Block block)
        {
            return IsFullCube(block) || HasAnySolidFace(block) || ShouldTreatAsFullCube(block);
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
            string path = block.Code?.Path;
            if (path == null) return false;
            // Matches "door-roughhewn-closed-north", "trapdoor-aged-opened-up", etc.
            return path.StartsWith("door-") || path.StartsWith("trapdoor-");
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
