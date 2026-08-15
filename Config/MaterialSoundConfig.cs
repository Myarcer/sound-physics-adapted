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
        /// <summary>
        /// Current material config version. A saved config below this version is
        /// replaced by fresh defaults. Bump this when the defaults change.
        /// Keep it equal to CurrentConfigVersion in SoundPhysicsAdaptedModSystem —
        /// both config files use one version number.
        /// </summary>
        public const int CurrentVersion = 10;

        /// <summary>Config version for migration</summary>
        public int Version { get; set; } = 1;

        /// <summary>Occlusion settings (how much sound is blocked)</summary>
        public OcclusionSection Occlusion { get; set; } = new OcclusionSection();

        /// <summary>Reflectivity settings (Phase 3 - how much sound bounces)</summary>
        public ReflectivitySection Reflectivity { get; set; } = new ReflectivitySection();

        /// <summary>
        /// Per-sound penetration overrides.
        /// Allows specific sounds (by asset path pattern) to penetrate walls more than physics dictates.
        /// Use for gameplay-critical alert sounds (bells, temporal rifts) that must be audible through walls.
        /// </summary>
        public SoundPenetrationSection SoundPenetration { get; set; } = new SoundPenetrationSection();

        /// <summary>
        /// Block code patterns that trigger rain surface impact sounds.
        /// Matched as prefix against block.Code.Path (e.g., "anvil" matches "anvil-copper").
        /// Add patterns for any block type you want rain impact sounds on.
        /// Works alongside EnableRainSurfaceImpacts and RainSurfaceVolume in the main config.
        /// </summary>
        public string[] RainSurfaceBlockPatterns { get; set; } = null;

        /// <summary>
        /// Block code patterns that are considered lit torches for ambient crackling.
        /// Matched as prefix against block.Code.Path.
        /// Blocks matching these AND containing "extinct" or "burnedout" are excluded.
        /// Add modded torch block codes here to get ambient crackling.
        /// Works alongside EnableTorchAmbient and TorchAmbientVolume in the main config.
        /// </summary>
        public string[] TorchBlockPatterns { get; set; } = new string[] { "torch", "torchholder" };

        // Cached compiled patterns for block overrides
        private List<(Regex pattern, float value)> _compiledOcclusionOverrides;
        private List<Regex> _compiledTreatAsFullCube;

        // Per-block-ID result cache for GetOcclusion (avoids repeated regex matching).
        // Regex is expensive (~1.8ms per block type with 60+ patterns). This cache ensures
        // each unique block type only runs regex ONCE, then all subsequent calls are O(1).
        private const int OCCLUSION_RESULT_CACHE_SIZE = 16384;
        private readonly float[] _occlusionResultCache = new float[OCCLUSION_RESULT_CACHE_SIZE];
        private readonly bool[] _occlusionResultCached = new bool[OCCLUSION_RESULT_CACHE_SIZE];

        // Per-block-ID cache for HasBlockOverride (tracks whether a block matched a BlockOverride pattern).
        private readonly byte[] _hasOverrideCache = new byte[OCCLUSION_RESULT_CACHE_SIZE]; // 0=unknown, 1=yes, 2=no

        // Pre-cached material name lookups (avoids ToString().ToLowerInvariant() per call)
        private Dictionary<EnumBlockMaterial, float> _materialOcclusionLookup;

        // Per-block-ID result cache for GetReflectivity (same pattern as GetOcclusion)
        private readonly float[] _reflectivityResultCache = new float[OCCLUSION_RESULT_CACHE_SIZE];
        private readonly bool[] _reflectivityResultCached = new bool[OCCLUSION_RESULT_CACHE_SIZE];
        // Pre-cached material reflectivity lookup (avoids ToString().ToLowerInvariant() per call)
        private Dictionary<EnumBlockMaterial, float> _materialReflectivityLookup;

        // Cached compiled patterns for sound penetration overrides
        private List<(Regex pattern, SoundPenetrationOverride value)> _compiledPenetrationOverrides;
        // Per-sound-name result cache (avoids repeated regex matching per tick)
        private readonly Dictionary<string, SoundPenetrationOverride> _penetrationResultCache = new Dictionary<string, SoundPenetrationOverride>(64);
        private static readonly SoundPenetrationOverride _noPenetrationOverride = new SoundPenetrationOverride { OcclusionMultiplier = 1.0f, MinFilterFloor = -1f };

        /// <summary>
        /// Clear the per-block-ID result cache. Called when overrides change.
        /// </summary>
        private void ClearOcclusionResultCache()
        {
            Array.Clear(_occlusionResultCached, 0, OCCLUSION_RESULT_CACHE_SIZE);
            Array.Clear(_hasOverrideCache, 0, OCCLUSION_RESULT_CACHE_SIZE);
            Array.Clear(_reflectivityResultCached, 0, OCCLUSION_RESULT_CACHE_SIZE);
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
        /// Check if a block has an explicit BlockOverride in config.
        /// Blocks with overrides have intentionally set occlusion values that
        /// should not be modified by volume scaling.
        /// Cached per block ID.
        /// </summary>
        public bool HasBlockOverride(Block block)
        {
            if (block == null) return false;

            int blockId = block.Id;
            if (blockId >= 0 && blockId < OCCLUSION_RESULT_CACHE_SIZE)
            {
                byte cached = _hasOverrideCache[blockId];
                if (cached != 0)
                    return cached == 1;
            }

            // Compute — reuses the same compiled regex list as GetOcclusion
            bool result = ComputeHasBlockOverride(block);

            if (blockId >= 0 && blockId < OCCLUSION_RESULT_CACHE_SIZE)
                _hasOverrideCache[blockId] = result ? (byte)1 : (byte)2;

            return result;
        }

        private bool ComputeHasBlockOverride(Block block)
        {
            string blockCode = block.Code?.ToString() ?? "";
            if (string.IsNullOrEmpty(blockCode) || Occlusion.BlockOverrides == null)
                return false;

            EnsureOverridesCompiled();

            foreach (var (pattern, _) in _compiledOcclusionOverrides)
            {
                if (pattern.IsMatch(blockCode))
                    return true;
            }
            return false;
        }

        private void EnsureOverridesCompiled()
        {
            if (_compiledOcclusionOverrides != null) return;

            _compiledOcclusionOverrides = new List<(Regex, float)>();
            if (Occlusion.BlockOverrides == null) return;

            foreach (var kvp in Occlusion.BlockOverrides)
            {
                string pattern = "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*") + "$";
                _compiledOcclusionOverrides.Add((new Regex(pattern, RegexOptions.Compiled), kvp.Value));
            }
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
                EnsureOverridesCompiled();

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
                    // Backward compat: VS 1.22 renamed EnumBlockMaterial.Liquid -> .Water.
                    // Old user configs use "liquid" key; map it onto Water.
                    else if (mat == EnumBlockMaterial.Water && Occlusion.Materials.TryGetValue("liquid", out float legacyVal))
                        _materialOcclusionLookup[mat] = legacyVal;
                }
            }

            if (_materialOcclusionLookup != null && _materialOcclusionLookup.TryGetValue(block.BlockMaterial, out float occlusion))
                return occlusion;

            return 0.5f; // Default for unknown materials
        }

        /// <summary>
        /// Get reflectivity multiplier for a block (Phase 3).
        /// Results are cached per block ID to avoid ToString/ToLower per call.
        /// </summary>
        public float GetReflectivity(Block block)
        {
            if (block == null || Reflectivity?.Materials == null) return 0.5f;

            int blockId = block.Id;
            if (blockId >= 0 && blockId < OCCLUSION_RESULT_CACHE_SIZE && _reflectivityResultCached[blockId])
            {
                return _reflectivityResultCache[blockId];
            }

            // Cache miss — compute via pre-cached enum lookup
            if (_materialReflectivityLookup == null)
            {
                _materialReflectivityLookup = new Dictionary<EnumBlockMaterial, float>();
                foreach (EnumBlockMaterial mat in Enum.GetValues(typeof(EnumBlockMaterial)))
                {
                    string matName = mat.ToString().ToLowerInvariant();
                    if (Reflectivity.Materials.TryGetValue(matName, out float val))
                        _materialReflectivityLookup[mat] = val;
                    // Backward compat: VS 1.22 renamed EnumBlockMaterial.Liquid -> .Water.
                    else if (mat == EnumBlockMaterial.Water && Reflectivity.Materials.TryGetValue("liquid", out float legacyVal))
                        _materialReflectivityLookup[mat] = legacyVal;
                }
            }

            float result = _materialReflectivityLookup.TryGetValue(block.BlockMaterial, out float reflectivity)
                ? reflectivity : 0.5f;

            if (blockId >= 0 && blockId < OCCLUSION_RESULT_CACHE_SIZE)
            {
                _reflectivityResultCache[blockId] = result;
                _reflectivityResultCached[blockId] = true;
            }

            return result;
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

        // ════════════════════════════════════════════════════════════════
        // Sound Penetration Overrides — per-sound-asset occlusion scaling
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get penetration override for a sound by its asset path.
        /// Returns a cached override with OcclusionMultiplier and MinFilterFloor.
        /// Sounds without overrides get multiplier=1.0 and floor=-1 (no override).
        /// Cached per unique sound name to avoid repeated regex on every tick.
        /// </summary>
        public SoundPenetrationOverride GetSoundPenetration(string soundName)
        {
            if (string.IsNullOrEmpty(soundName) || SoundPenetration?.Overrides == null || SoundPenetration.Overrides.Count == 0)
                return _noPenetrationOverride;

            if (_penetrationResultCache.TryGetValue(soundName, out var cached))
                return cached;

            // Cache miss — compile patterns if needed and run regex
            EnsurePenetrationOverridesCompiled();

            foreach (var (pattern, value) in _compiledPenetrationOverrides)
            {
                if (pattern.IsMatch(soundName))
                {
                    _penetrationResultCache[soundName] = value;
                    return value;
                }
            }

            _penetrationResultCache[soundName] = _noPenetrationOverride;
            return _noPenetrationOverride;
        }

        private void EnsurePenetrationOverridesCompiled()
        {
            if (_compiledPenetrationOverrides != null) return;

            _compiledPenetrationOverrides = new List<(Regex, SoundPenetrationOverride)>();
            if (SoundPenetration?.Overrides == null) return;

            foreach (var kvp in SoundPenetration.Overrides)
            {
                // Convert wildcard pattern to regex: "game:sounds/effect/bell*" → "^game:sounds/effect/bell.*$"
                string pattern = "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*") + "$";
                _compiledPenetrationOverrides.Add((new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase), kvp.Value));
            }
        }

        /// <summary>
        /// Add or overwrite a sound penetration override at runtime.
        /// </summary>
        public void SetSoundPenetration(string soundPattern, float occlusionMultiplier, float minFilterFloor)
        {
            SoundPenetration ??= new SoundPenetrationSection();
            SoundPenetration.Overrides ??= new Dictionary<string, SoundPenetrationOverride>();
            SoundPenetration.Overrides[soundPattern] = new SoundPenetrationOverride
            {
                OcclusionMultiplier = occlusionMultiplier,
                MinFilterFloor = minFilterFloor
            };
            _compiledPenetrationOverrides = null; // Force recompile
            _penetrationResultCache.Clear();
        }

        /// <summary>
        /// Create default config with all VS materials
        /// </summary>
        public static MaterialSoundConfig CreateDefault()
        {
            return new MaterialSoundConfig
            {
                Version = CurrentVersion,
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
                        { "water", 0.8f },       // Water significantly blocks sound (air-water boundary). VS 1.22: was "liquid".
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
                        // Doors — broad patterns match all states (open/closed handled by geometry)
                        // Override prevents volume scaling from clobbering thin panel occlusion
                        { "game:door-*", 0.8f },
                        { "game:metaldoor-*", 0.9f },
                        // Trapdoors — solid panels vs open grating/bars
                        { "game:trapdoor-solid-*", 0.75f },
                        { "game:trapdoor-plate-*", 0.85f },
                        { "game:trapdoor-grated-*", 0.3f },
                        { "game:trapdoor-bars-*", 0.2f },
                        // Industrial doors (coke oven, kiln) — thick sealed doors
                        { "game:cokeovendoor-*", 0.85f },
                        { "game:doorkiln-*", 0.85f },
                        // Gates — fencegate, gate3x3, wicketgate, portcullis
                        { "game:*gate*", 0.8f },
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

                        // Wildgrass mod — mod sets SideSolid on grass blocks,
                        // causing them to take the solid fast path with occ=1.0.
                        // Override to near-zero so they behave like normal foliage.
                        { "wildgrass:*", 0.02f },
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
                        { "game:hay-*", 0.4f },
                        // Medieval Expansion — gates, portcullis, drawbridges
                        // Open state has null collision in JSON → foliage path → zero occlusion.
                        // Closed state has collision → override applied (IsDoorOpen returns false
                        // for custom Gate/Portcullis/Drawbridge entities, assumes closed).
                        { "medievalexpansion:gate*", 0.8f },
                        { "medievalexpansion:portcullis*", 0.85f },
                        { "medievalexpansion:drawbridge*", 0.8f }
                    },
                    TreatAsFullCube = new List<string>
                    {
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
                        { "water", 0.5f },       // VS 1.22: was "liquid".
                        { "cloth", 0.1f },
                        { "snow", 0.15f },
                        { "leaves", 0.1f },
                        { "plant", 0.1f }
                    }
                },
                TorchBlockPatterns = new string[]
                {
                    "torch",
                    "walltorch"
                },
                RainSurfaceBlockPatterns = new string[]
                {
                    // --- Smithing / storage piles ---
                    "anvil",            // game:anvil-{metal}, game:anvilpart-{base|top}-{metal}
                    "ingotpile",        // game:ingotpile
                    "platepile",        // game:platepile
                    "metalpartpile",    // game:metalpartpile (scraps/parts pile)
                    "metalsheet",       // game:metalsheet-{metal}-{facing}

                    // --- Metal blocks / plates / sheets ---
                    "metalblock",       // game:metalblock-{type}-{metal}
                    "metalplate",       // game:metalplate-{metal}  (if used by mods)

                    // --- Metal machines / containers ---
                    "hopper",           // game:hopper-{metal}-{facing}
                    "chute",            // game:chute, chute-cross, chute-straight, chute-t
                    "verticalboiler",   // game:verticalboiler
                    "condenser",        // game:condenser
                    "cokeovendoor",     // game:cokeovendoor-{metal}

                    // --- Metal furniture / decor ---
                    "ironfence",        // game:ironfence-{metal}-{config}
                    "supportchain",     // game:supportchain-{metal}-{facing}
                    "supportbeam-tarnishedmetal", // game:supportbeam-tarnishedmetal-{config}
                    "chandelier",       // game:chandelier-{metal}
                    "lantern",          // game:lantern-{metal}-{facing}   (TODO: own sound?)
                    "metaldoor",        // game:metaldoor-{metal}-{config}
                    "trapdoor",         // game:trapdoor-{metal}-{config}
                    "plaque",           // game:plaque-{metal}-{facing}
                    "shingleblock",     // game:shingleblock-{metal}-{facing}  (metal roof shingles)
                    "lightningrod",     // game:lightningrod-{metal}

                    // Note: mechanics (angledgears, largegear3, helvehammerbase, transmission,
                    // brake, crank, pulverizerframe etc.) are all wood — skip.
                    // bloomerybase/bloomerychimney are clay/stone — skip.
                    // forge is stone — skip.
                    // Add any of these back when a rain-on-wood / rain-on-stone sound is available.
                },
                SoundPenetration = new SoundPenetrationSection
                {
                    Overrides = new Dictionary<string, SoundPenetrationOverride>
                    {
                        // Bell creature — proximity alert that spawns enemies through walls.
                        // Sound path: sounds/creature/bell/alarm.ogg (and walk, bell, etc.)
                        // Players behind 4-5 stone walls must still hear the activation.
                        // 0.25 multiplier: 4 stone walls (occ=4.0) acts like 1 wall (occ=1.0).
                        // 0.15 floor: never drops below 15% audibility.
                        { "sounds/creature/bell/*", new SoundPenetrationOverride { OcclusionMultiplier = 0.25f, MinFilterFloor = 0.15f } },
                        // Deep bell effect sound
                        { "sounds/effect/deepbell*", new SoundPenetrationOverride { OcclusionMultiplier = 0.3f, MinFilterFloor = 0.12f } },
                        // Temporal rift — warning sound near rifts
                        { "sounds/effect/rift*", new SoundPenetrationOverride { OcclusionMultiplier = 0.3f, MinFilterFloor = 0.10f } },
                        // Temporal stability warnings — drain and low stability alerts
                        { "sounds/effect/tempstab-*", new SoundPenetrationOverride { OcclusionMultiplier = 0.35f, MinFilterFloor = 0.08f } },
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

    /// <summary>
    /// Per-sound penetration overrides.
    /// Allows specific sounds to be heard through walls more than physics dictates.
    /// Use for gameplay-critical alert sounds that the game expects the player to hear.
    /// </summary>
    public class SoundPenetrationSection
    {
        /// <summary>
        /// Sound asset path pattern → penetration override.
        /// Patterns support * wildcards (matched against sound Location path).
        /// Example: "game:sounds/effect/bell*" matches all bell sound variants.
        /// </summary>
        public Dictionary<string, SoundPenetrationOverride> Overrides { get; set; } = new Dictionary<string, SoundPenetrationOverride>();
    }

    /// <summary>
    /// Penetration override values for a matched sound pattern.
    /// Both fields work together: multiplier reduces computed occlusion,
    /// floor guarantees minimum audibility.
    /// </summary>
    public class SoundPenetrationOverride
    {
        /// <summary>
        /// Multiplier applied to computed occlusion before filter conversion.
        /// 0.0 = full bypass (no occlusion), 1.0 = normal physics (no change).
        /// 0.25 = 4 stone walls act like 1 wall.
        /// </summary>
        public float OcclusionMultiplier { get; set; } = 1.0f;

        /// <summary>
        /// Minimum lowpass filter value for this sound, overriding the global MinLowPassFilter.
        /// -1 = use global default. 0.15 = never drop below 15% audibility.
        /// Range: -1 to 1.0.
        /// </summary>
        public float MinFilterFloor { get; set; } = -1f;
    }
}
