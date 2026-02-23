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

        /// <summary>
        /// Get occlusion multiplier for a block.
        /// Checks block code overrides first, then falls back to material.
        /// </summary>
        public float GetOcclusion(Block block)
        {
            if (block == null) return 0.5f;

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

            // Fall back to material lookup
            string materialName = block.BlockMaterial.ToString().ToLowerInvariant();
            if (Occlusion.Materials.TryGetValue(materialName, out float occlusion))
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
                Version = 1,
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
                        // Vegetation - walkable but slightly muffles sound
                        { "game:tallgrass-*", 0.01f },
                        { "game:flower-*", 0.01f },
                        { "game:fern-*", 0.02f },
                        { "game:waterlily-*", 0.01f },
                        { "game:mushroom-*", 0.01f },
                        // Leaves - denser foliage, more muffling
                        { "game:leaves-*", 0.08f },
                        { "game:leavesbranchy-*", 0.12f },
                        // Snow layers - ignored for occlusion (too thin)
                        { "game:snowlayer-*", 0.0f },
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
                        // Ground storage (flat piles)
                        { "game:groundstorage*", 0.0f },
                        // Placed grass
                        { "game:placeddrygrass-*", 0.0f },
                        { "game:drygrass-*", 0.0f }
                    },
                    TreatAsFullCube = new List<string>
                    {
                        // Leaded glass panes fill most of the block - skip expensive AABB testing
                        "game:glasspane-leaded-*"
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
