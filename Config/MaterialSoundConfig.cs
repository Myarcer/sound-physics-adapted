using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;

namespace soundphysicsadapted
{
    /// <summary>
    /// Configuration for material-based sound properties.
    /// Saved to ModConfig/soundphysicsadapted_materials.json
    /// </summary>
    public class MaterialSoundConfig
    {
        /// <summary>Config version for migration</summary>
        public int Version { get; set; } = 1;

        /// <summary>Occlusion settings (how much sound is blocked)</summary>
        public OcclusionSection Occlusion { get; set; } = new OcclusionSection();

        /// <summary>Reflectivity settings (Phase 3 - how much sound bounces)</summary>
        public ReflectivitySection Reflectivity { get; set; } = new ReflectivitySection();

        // Cached compiled patterns for block overrides
        private List<(Regex pattern, float value)> _compiledOcclusionOverrides;
        private List<Regex> _compiledTreatAsFullCube;

        // Per-block-ID result cache for GetOcclusion (avoids repeated regex matching).
        // Regex is expensive (~1.8ms per block type with 60+ patterns). This cache ensures
        // each unique block type only runs regex ONCE, then all subsequent calls are O(1).
        private const int OCCLUSION_RESULT_CACHE_SIZE = 16384;
        private readonly float[] _occlusionResultCache = new float[OCCLUSION_RESULT_CACHE_SIZE];
        private readonly bool[] _occlusionResultCached = new bool[OCCLUSION_RESULT_CACHE_SIZE];

        // Pre-cached material name lookups (avoids ToString().ToLowerInvariant() per call)
        private Dictionary<EnumBlockMaterial, float> _materialOcclusionLookup;

        /// <summary>
        /// Clear the per-block-ID result cache. Called when overrides change.
        /// </summary>
        private void ClearOcclusionResultCache()
        {
            Array.Clear(_occlusionResultCached, 0, OCCLUSION_RESULT_CACHE_SIZE);
        }

        /// <summary>
        /// Get occlusion multiplier for a block.
        /// Checks block code overrides first, then falls back to material.
        /// Results are cached per block ID to avoid repeated regex evaluation.
        /// </summary>
        public float GetOcclusion(Block block)
        {
            if (block == null) return 0.5f;

            // Fast path: check per-block-ID cache first
            int blockId = block.Id;
            if (blockId >= 0 && blockId < OCCLUSION_RESULT_CACHE_SIZE && _occlusionResultCached[blockId])
            {
                return _occlusionResultCache[blockId];
            }

            // Cache miss — compute via regex overrides + material lookup
            float result = ComputeOcclusion(block);

            // Store in cache
            if (blockId >= 0 && blockId < OCCLUSION_RESULT_CACHE_SIZE)
            {
                _occlusionResultCache[blockId] = result;
                _occlusionResultCached[blockId] = true;
            }

            return result;
        }

        /// <summary>
        /// Compute occlusion multiplier (expensive — runs regex patterns).
        /// Only called on cache miss per block type.
        /// </summary>
        private float ComputeOcclusion(Block block)
        {
            // Check block code overrides first
            string blockCode = block.Code?.ToString() ?? "";
            if (!string.IsNullOrEmpty(blockCode) && Occlusion.BlockOverrides != null)
            {
                // Lazy compile patterns
                if (_compiledOcclusionOverrides == null)
                {
                    _compiledOcclusionOverrides = new List<(Regex, float)>();
                    foreach (var kvp in Occlusion.BlockOverrides)
                    {
                        // Convert wildcard pattern to regex
                        string pattern = "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*") + "$";
                        _compiledOcclusionOverrides.Add((new Regex(pattern, RegexOptions.Compiled), kvp.Value));
                    }
                }

                foreach (var (pattern, value) in _compiledOcclusionOverrides)
                {
                    if (pattern.IsMatch(blockCode))
                        return value;
                }
            }

            // Fall back to pre-cached material lookup (avoids ToString + ToLower per call)
            if (_materialOcclusionLookup == null && Occlusion.Materials != null)
            {
                _materialOcclusionLookup = new Dictionary<EnumBlockMaterial, float>();
                foreach (EnumBlockMaterial mat in Enum.GetValues(typeof(EnumBlockMaterial)))
                {
                    string matName = mat.ToString().ToLowerInvariant();
                    if (Occlusion.Materials.TryGetValue(matName, out float val))
                        _materialOcclusionLookup[mat] = val;
                }
            }

            if (_materialOcclusionLookup != null && _materialOcclusionLookup.TryGetValue(block.BlockMaterial, out float occlusion))
                return occlusion;

            return 0.5f; // Default for unknown materials
        }

        /// <summary>
        /// Get reflectivity multiplier for a block (Phase 3).
        /// </summary>
        public float GetReflectivity(Block block)
        {
            if (block == null || Reflectivity?.Materials == null) return 0.5f;

            string materialName = block.BlockMaterial.ToString().ToLowerInvariant();
            if (Reflectivity.Materials.TryGetValue(materialName, out float reflectivity))
                return reflectivity;

            return 0.5f; // Default
        }

        /// <summary>
        /// Check if a block should be treated as a full cube (skip AABB collision testing).
        /// Used for partial blocks like leaded glass panes that fill most of the space.
        /// </summary>
        public bool ShouldTreatAsFullCube(Block block)
        {
            if (block == null) return false;

            string blockCode = block.Code?.ToString() ?? "";
            if (string.IsNullOrEmpty(blockCode) || Occlusion.TreatAsFullCube == null || Occlusion.TreatAsFullCube.Count == 0)
                return false;

            // Lazy compile patterns
            if (_compiledTreatAsFullCube == null)
            {
                _compiledTreatAsFullCube = new List<Regex>();
                foreach (var wildcardPattern in Occlusion.TreatAsFullCube)
                {
                    string pattern = "^" + Regex.Escape(wildcardPattern).Replace("\\*", ".*") + "$";
                    _compiledTreatAsFullCube.Add(new Regex(pattern, RegexOptions.Compiled));
                }
            }

            foreach (var pattern in _compiledTreatAsFullCube)
            {
                if (pattern.IsMatch(blockCode))
                    return true;
            }

            return false;
        }

        // ════════════════════════════════════════════════════════════════
        // Runtime API — for other mods to register overrides
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Add or overwrite an occlusion block override at runtime.
        /// Pattern supports * wildcards (e.g. "game:mymod-wall-*").
        /// Invalidates compiled regex cache so changes take effect immediately.
        /// </summary>
        /// <param name="blockPattern">Block code pattern with * wildcards</param>
        /// <param name="occlusionValue">Occlusion multiplier (0=transparent, 1=full)</param>
        public void SetOcclusionOverride(string blockPattern, float occlusionValue)
        {
            Occlusion.BlockOverrides ??= new Dictionary<string, float>();
            Occlusion.BlockOverrides[blockPattern] = occlusionValue;
            _compiledOcclusionOverrides = null; // Force recompile
            ClearOcclusionResultCache();         // Invalidate per-block-ID cache
            BlockClassification.ClearCache();    // Cached values may be stale
        }

        /// <summary>
        /// Add or overwrite a material occlusion value at runtime.
        /// Material name should be lowercase (e.g. "stone", "wood", "cloth").
        /// </summary>
        /// <param name="materialName">Material name (lowercase)</param>
        /// <param name="occlusionValue">Occlusion multiplier (0=transparent, 1=full)</param>
        public void SetMaterialOcclusion(string materialName, float occlusionValue)
        {
            Occlusion.Materials ??= new Dictionary<string, float>();
            Occlusion.Materials[materialName] = occlusionValue;
            _materialOcclusionLookup = null;     // Force rebuild enum lookup
            ClearOcclusionResultCache();         // Invalidate per-block-ID cache
            BlockClassification.ClearCache();
        }

        /// <summary>
        /// Add or overwrite a material reflectivity value at runtime.
        /// Affects reverb calculations — higher values = more reflective surface.
        /// </summary>
        /// <param name="materialName">Material name (lowercase)</param>
        /// <param name="reflectivityValue">Reflectivity multiplier (e.g. stone=1.5, wood=0.4)</param>
        public void SetMaterialReflectivity(string materialName, float reflectivityValue)
        {
            Reflectivity ??= new ReflectivitySection();
            Reflectivity.Materials ??= new Dictionary<string, float>();
            Reflectivity.Materials[materialName] = reflectivityValue;
        }

        /// <summary>
        /// Add a block pattern to the TreatAsFullCube list.
        /// Blocks matching this pattern skip AABB collision testing and are treated as full cubes.
        /// </summary>
        /// <param name="blockPattern">Block code pattern with * wildcards</param>
        public void AddTreatAsFullCube(string blockPattern)
        {
            Occlusion.TreatAsFullCube ??= new List<string>();
            if (!Occlusion.TreatAsFullCube.Contains(blockPattern))
            {
                Occlusion.TreatAsFullCube.Add(blockPattern);
                _compiledTreatAsFullCube = null; // Force recompile
                ClearOcclusionResultCache();      // TreatAsFullCube affects solid classification → occlusion
                BlockClassification.ClearCache();
            }
        }

        /// <summary>
        /// Create default config with all VS materials
        /// </summary>
        public static MaterialSoundConfig CreateDefault()
        {
            return new MaterialSoundConfig
            {
                Version = 4,
                Occlusion = new OcclusionSection
                {
                    Materials = new Dictionary<string, float>
                    {
                        // All 22 EnumBlockMaterial values
                        { "air", 0.0f },
                        { "soil", 0.8f },
                        { "gravel", 0.4f },
                        { "sand", 0.3f },
                        { "wood", 0.6f },
                        { "leaves", 0.05f },
                        { "stone", 1.0f },
                        { "ore", 1.0f },
                        { "liquid", 0.8f },      // Water significantly blocks sound (air-water boundary)
                        { "snow", 0.25f },
                        { "ice", 0.7f },
                        { "metal", 0.95f },
                        { "mantle", 1.0f },      // Bedrock-like
                        { "plant", 0.02f },
                        { "glass", 0.8f },       // Glass blocks most sound
                        { "ceramic", 0.8f },
                        { "cloth", 0.3f },
                        { "lava", 0.3f },        // Molten rock
                        { "brick", 0.9f },
                        { "fire", 0.0f },        // No occlusion
                        { "meta", 0.5f },        // Special blocks
                        { "other", 0.5f }        // Catch-all
                    },
                    BlockOverrides = new Dictionary<string, float>
                    {
                        // Doors
                        { "game:door-*-closed-*", 0.8f },
                        { "game:door-*-opened-*", 0.05f },
                        // Trapdoors
                        { "game:trapdoor-*-closed-*", 0.7f },
                        { "game:trapdoor-*-opened-*", 0.05f },
                        // Soft materials
                        { "game:wool-*", 0.4f },
                        { "game:carpet-*", 0.3f },
                        // Containers (hollow inside)
                        { "game:chest-*", 0.5f },
                        { "game:barrel-*", 0.5f },
                        // Furniture
                        { "game:bed-*", 0.3f },
                        // Leaves — dense canopy muffles sound (higher than Plant default)
                        { "game:leaves-*", 0.08f },
                        { "game:leavesbranchy-*", 0.12f },
                        // Berry bushes — thick foliage muffles sound
                        // Vanilla VS
                        { "game:bigberrybush-*", 0.06f },
                        { "game:smallberrybush-*", 0.04f },
                        // Wildcraft Fruit & Nuts mod (wildcraftfruit)
                        { "wildcraftfruit:berrybush-*", 0.06f },
                        { "wildcraftfruit:shortberrybush-*", 0.04f },
                        { "wildcraftfruit:shrubberrybush-*", 0.06f },
                        { "wildcraftfruit:pricklyberrybush-*", 0.06f },
                        { "wildcraftfruit:pricklyshortbush-*", 0.04f },
                        { "wildcraftfruit:topberrybush-*", 0.06f },
                        { "wildcraftfruit:bottomberrybush-*", 0.06f },
                        { "wildcraftfruit:toppricklybush-*", 0.06f },
                        { "wildcraftfruit:bottompricklybush-*", 0.06f },
                        { "wildcraftfruit:bottomtreebush-*", 0.06f },
                        { "wildcraftfruit:groundberryplant-*", 0.02f },

                        // Path blocks - flat ground surface, solid bottom face but shouldn't occlude
                        { "game:woodenpath-*", 0.0f },
                        // Baskets and traps - small open containers on the ground
                        { "game:basket*", 0.0f },
                        // Shelves - open furniture (not bookshelves which are denser)
                        { "game:shelf-*", 0.0f },
                        // Firepits
                        { "game:firepit-*", 0.0f },
                        // Tool racks
                        { "game:toolrack-*", 0.0f },

                        // Anvils — override for testing, unsure if to keep?
                        { "game:anvil-*", 0.0f },
                        // Ingot piles, plate piles — flat ground stacks
                        { "game:ingotpile-*", 0.0f },
                        { "game:platepile-*", 0.0f },
                        // Support beams — narrow wooden frames
                        { "game:supportbeam-*", 0.0f },
                        // Stationary baskets
                        { "game:stationarybasket-*", 0.0f },

                        // Wildgrass mod — mod sets SideSolid on grass blocks,
                        // causing them to take the solid fast path with occ=1.0.
                        // Override to near-zero so they behave like normal foliage.
                        { "wildgrass:*", 0.02f },
                        // Stone paths — flat ground surface like wooden paths
                        { "game:stonepath*", 0.0f },

                        // Structural plant blocks — thatch/sod roofing and hay bales
                        // VS classifies these as BlockMaterial.Plant (0.02) but they're
                        // dense packed building materials that should block sound/rain.
                        { "game:slantedroofing-thatch*", 0.55f },
                        { "game:slantedroofing-sod*", 0.55f },
                        { "game:slantedroofingbottom-thatch*", 0.55f },
                        { "game:slantedroofingbottom-sod*", 0.55f },
                        { "game:slantedroofingcornerinner-thatch*", 0.55f },
                        { "game:slantedroofingcornerinner-sod*", 0.55f },
                        { "game:slantedroofingcornerouter-thatch*", 0.55f },
                        { "game:slantedroofingcornerouter-sod*", 0.55f },
                        { "game:slantedroofingridge-thatch*", 0.55f },
                        { "game:slantedroofingridge-sod*", 0.55f },
                        { "game:slantedroofingridgeend-thatch*", 0.55f },
                        { "game:slantedroofingridgeend-sod*", 0.55f },
                        { "game:slantedroofingtip-thatch*", 0.55f },
                        { "game:slantedroofingtip-sod*", 0.55f },
                        // Half-roof edge caps — thinner but still structural
                        { "game:slantedroofinghalfleft-*", 0.45f },
                        { "game:slantedroofinghalfright-*", 0.45f },
                        // Hay bales — packed dry grass blocks
                        { "game:hay-*", 0.4f }
                    },
                    TreatAsFullCube = new List<string>
                    {
                        // Leaded glass panes fill most of the block - skip expensive AABB testing
                        "game:glasspane-leaded-*",
                        // ALL slanted roofing: collision boxes are sloped geometry that
                        // diagonal DDA rays frequently miss. Treat as full cube so the
                        // block's occlusion value (override or material) is always applied.
                        "game:slantedroofing*"
                    }
                },
                Reflectivity = new ReflectivitySection
                {
                    Materials = new Dictionary<string, float>
                    {
                        // Phase 3 - reverb reflectivity
                        { "stone", 1.5f },
                        { "ore", 1.5f },
                        { "metal", 1.3f },
                        { "brick", 1.3f },
                        { "ceramic", 1.1f },
                        { "glass", 1.1f },
                        { "ice", 1.0f },
                        { "wood", 0.4f },
                        { "soil", 0.3f },
                        { "liquid", 0.5f },
                        { "cloth", 0.1f },
                        { "snow", 0.15f },
                        { "leaves", 0.1f },
                        { "plant", 0.1f }
                    }
                }
            };
        }
    }

    public class OcclusionSection
    {
        /// <summary>Occlusion multiplier per material (0=none, 1=full)</summary>
        public Dictionary<string, float> Materials { get; set; } = new Dictionary<string, float>();

        /// <summary>Block code pattern overrides (supports * wildcards)</summary>
        public Dictionary<string, float> BlockOverrides { get; set; } = new Dictionary<string, float>();

        /// <summary>
        /// Block code patterns that should skip AABB collision testing and be treated as full cubes.
        /// Use for partial blocks like leaded glass panes that fill most of the block space.
        /// Patterns support * wildcards.
        /// </summary>
        public List<string> TreatAsFullCube { get; set; } = new List<string>();
    }

    public class ReflectivitySection
    {
        /// <summary>Reflectivity multiplier per material for reverb (Phase 3)</summary>
        public Dictionary<string, float> Materials { get; set; } = new Dictionary<string, float>();
    }
}
