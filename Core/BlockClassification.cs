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
        private const int BLOCK_CACHE_SIZE = 16384;

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



        // Cache for IsChiseledBlock (custom voxel geometry — needs AABB path)
        // 0 = not cached, 1 = is chiseled, 2 = is NOT chiseled
        private static readonly byte[] isChiseledBlockCache = new byte[BLOCK_CACHE_SIZE];



        // Cache for NeedsSourceAdjustment (multiblock placeholder, or door / gate / portcullis)
        // 0 = not cached, 1 = needs the adjust path, 2 = plain block
        private static readonly byte[] needsSourceAdjustCache = new byte[BLOCK_CACHE_SIZE];

        // Cache for IsSolidForOcclusion (composite: !chiseled && (fullCube || multipleSolid || treatAsFull))
        // 0 = not cached, 1 = is solid, 2 = is NOT solid
        // This is the hottest check in the DDA — caching the composite result avoids
        // calling IsChiseledBlock + IsFullCube + HasMultipleSolidFaces + ShouldTreatAsFullCube every time.
        private static readonly byte[] isSolidForOcclusionCache = new byte[BLOCK_CACHE_SIZE];



        /// <summary>
        /// Clear all block caches. Call when config reloads or materials change.
        /// </summary>
        public static void ClearCache()
        {
            Array.Clear(blockOcclusionCached, 0, BLOCK_CACHE_SIZE);
            Array.Clear(treatAsFullCubeCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(hasMultipleSolidFacesCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(isChiseledBlockCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(isSolidForOcclusionCache, 0, BLOCK_CACHE_SIZE);
            Array.Clear(needsSourceAdjustCache, 0, BLOCK_CACHE_SIZE);
            cachedOcclusionPerSolidBlock = -1f;
        }

        /// <summary>
        /// True when <see cref="SoundSourceAdjuster"/> must run its multiblock step and
        /// its door step for this block. False for every other block, which lets the
        /// adjuster go straight to block-corner centering.
        ///
        /// The answer follows from the block code alone, so it is cached per block ID.
        /// Without the cache, three substring searches run for every active sound on
        /// every 50 ms tick, before any distance gate or interval gate.
        /// </summary>
        public static bool NeedsSourceAdjustment(Block block)
        {
            if (block == null) return false;

            int blockId = block.Id;
            bool cacheable = blockId >= 0 && blockId < BLOCK_CACHE_SIZE;
            if (cacheable)
            {
                byte cached = needsSourceAdjustCache[blockId];
                if (cached != 0) return cached == 1;
            }

            string code = block.Code?.Path;
            bool needs = code != null
                && (code.StartsWith("multiblock-", StringComparison.Ordinal)
                    || code.Contains("door")
                    || code.Contains("gate")
                    || code.Contains("portcullis"));

            if (cacheable)
                needsSourceAdjustCache[blockId] = (byte)(needs ? 1 : 2);

            return needs;
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
        /// Check if a block has MULTIPLE solid faces (>= 3).
        /// Catches stairs (solid back/bottom/top), thick blocks, etc.
        /// Slabs (exactly 2 solid faces: UP+DOWN) will fail this and fall back to
        /// accurate AABB raycasting. The original comment said "slabs have 1 solid face"
        /// but VS marks both the top and bottom faces of a slab as SideSolid, giving count=2.
        /// With threshold ≥3, slab rays correctly miss when above the collision geometry.
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
            return count >= 3;
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
                    if (SoundPhysicsAdaptedModSystem.IsOcclusionDebugEnabled)
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
            if (block == null) return false;

            int blockId = block.Id;
            if (blockId >= 0 && blockId < BLOCK_CACHE_SIZE)
            {
                byte cached = isSolidForOcclusionCache[blockId];
                if (cached != 0)
                    return cached == 1;

                // Cache miss — compute composite result once
                bool result = !IsChiseledBlock(block)
                    && (IsFullCube(block) || HasMultipleSolidFaces(block) || ShouldTreatAsFullCube(block));
                isSolidForOcclusionCache[blockId] = result ? (byte)1 : (byte)2;
                return result;
            }

            // Fallback for out-of-range block IDs
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

                // Cache miss — do the string check once, cache result forever
                string path = block.Code?.Path;
                bool result = path != null && path.StartsWith("chiseledblock", StringComparison.Ordinal);
                isChiseledBlockCache[blockId] = result ? (byte)1 : (byte)2;
                return result;
            }

            string fallbackPath = block.Code?.Path;
            return fallbackPath != null && fallbackPath.StartsWith("chiseledblock", StringComparison.Ordinal);
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
        /// VS 1.22: EnumBlockMaterial.Liquid was renamed to .Water.
        /// </summary>
        public static bool IsLiquidMaterial(Block block)
        {
            var mat = block.BlockMaterial;
            return mat == EnumBlockMaterial.Water
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
                EnumBlockMaterial.Water => 0.2f,
                EnumBlockMaterial.Lava => 0.25f,
                EnumBlockMaterial.Air => 0.0f,
                _ => 0.5f
            };
        }
    }
}
