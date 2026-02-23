using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace soundphysicsadapted
{
    /// <summary>
    /// Thunder audio handler — full custom Layer 1 + Layer 2 + delayed crack.
    ///
    /// ALL thunder sounds (ambient + bolt) are fully replaced by our system.
    /// Ambient thunder: Harmony prefix on WeatherSimulationLightning.ClientTick.
    /// Bolt thunder: Harmony transpiler suppresses VS's PlaySoundAt + postfix plays ours.
    ///
    /// Sound assets and distance tiers (bolt thunder — matches vanilla thresholds):
    ///   verynear.ogg  — sharp crack, plays for bolts &lt; 150m
    ///   near.ogg      — balanced crack + rumble, plays for bolts 150-200m
    ///   distant.ogg   — rolling rumble, plays for bolts 200-320m
    ///   nodistance.ogg — pure instant crack, plays as delayed extra layer for bolts &lt; 200m
    ///
    /// Volume curves (bolt thunder — per-tier linear, matching vanilla):
    ///   verynear:  1 - dist/180  (1.0 at 0m, 0.17 at 149m)
    ///   near:      1 - dist/250  (0.40 at 150m, 0.20 at 200m)
    ///   distant:   1 - dist/500  (0.60 at 200m, 0.36 at 320m)
    ///   crack:     pow(1-dist/200, 1.5) — realistic atmospheric HF absorption
    ///
    /// Three modes based on enclosure state:
    ///
    /// OUTDOOR (skyCoverage below threshold):
    ///   Bolt: positioned one-shot toward strike, rolloff=0, our volume curve only.
    ///   Ambient: positioned one-shot in random sky direction (hill-aware).
    ///   Crack: delayed nodistance.ogg positioned toward bolt direction.
    ///   No LPF — full spectrum thunder with 3D directionality.
    ///
    /// INDOOR WITH OPENINGS (partial enclosure + tracked openings):
    ///   Layer 1: Omnidirectional rumble, listener-relative, LPF from enclosure.
    ///   Layer 2: Positional nodistance.ogg one-shot at best opening (bolt-biased).
    ///   Crack: delayed nodistance.ogg as Layer 1 with LPF.
    ///   The contrast between muffled rumble and clear crack from the doorway is the effect.
    ///
    /// FULLY ENCLOSED (no openings):
    ///   Layer 1 only: Deep bass rumble, maximum LPF, reduced volume.
    ///   No Layer 2 — no openings to hear through.
    ///
    /// Architecture:
    /// - Ambient thunder (WeatherSimulationLightning.ClientTick): fully replaced via prefix.
    /// - Bolt thunder (LightningFlash.ClientInit): VS suppressed via transpiler, postfix plays ours.
    /// - Bolt crack (LightningFlash.Render): VS suppressed, we queue delayed crack via OnGameTick.
    /// - Layer 2 uses PositionalSourcePool (OneShot, nodistance.ogg) with pre-applied LPF.
    /// - Enclosure attenuation (sky/occlusion/rain) applies on top of vanilla volume curves.
    /// </summary>
    public class ThunderAudioHandler : IDisposable
    {
        private readonly ICoreClientAPI capi;

        // Layer 2: One-shot positional sources at openings
        private PositionalSourcePool oneShotPool;

        // Layer 1: Managed one-shot sounds with EFX lowpass
        // We create+start+dispose per event (thunder is infrequent)
        private int layer1FilterId = 0;

        private bool initialized = false;
        private bool disposed = false;

        // Tracked active Layer 1 sounds for cleanup
        private readonly List<ManagedThunderSound> activeLayer1Sounds = new List<ManagedThunderSound>();
        private const int MAX_ACTIVE_LAYER1 = 4;

        // Delayed crack queue (nodistance.ogg fired ~50ms after bolt spawn)
        private readonly List<PendingDelayedCrack> pendingCracks = new List<PendingDelayedCrack>();
        private const long DELAYED_CRACK_MS = 50; // ~0.03-0.05s in VS, we use 50ms

        // Random for outdoor direction
        private readonly Random rand = new Random();

        // Sound assets
        private static readonly AssetLocation DISTANT = new AssetLocation("sounds/weather/lightning-distant.ogg");
        private static readonly AssetLocation NEAR = new AssetLocation("sounds/weather/lightning-near.ogg");
        private static readonly AssetLocation VERY_NEAR = new AssetLocation("sounds/weather/lightning-verynear.ogg");
        private static readonly AssetLocation NODISTANCE = new AssetLocation("sounds/weather/lightning-nodistance.ogg");

        /// <summary>Tracks a Layer 1 one-shot sound for cleanup and dynamic enclosure.</summary>
        private class ManagedThunderSound
        {
            public ILoadedSound Sound;
            public long StartTimeMs;
            public int FilterId; // 0 = no filter (outdoor), >0 = managed by handler's shared filter
            public float BaseVolume;      // The un-attenuated volume at spawn
            public float NearestRainDist; // Rain distance at spawn time
        }

        /// <summary>Queued delayed crack sound (nodistance.ogg), fired ~50ms after bolt spawn.</summary>
        private class PendingDelayedCrack
        {
            public long SpawnTimeMs;
            public float Distance;
            public Vec3d BoltWorldPos;
            public Vec3d PlayerEarPos;
            public float SkyCoverage;
            public float OcclusionFactor;
            public float CombinedEnclosure; // Pre-computed for LPF
            public float NearestRainDistance; // Stored at queue time for volume calc
            public bool IsOutdoor;
            // For outdoor: direction + placement info
            public Vec3d OutdoorDir;
            public float OutdoorPlaceDist;
        }

        public ThunderAudioHandler(ICoreClientAPI api)
        {
            capi = api;
        }

        public void Initialize()
        {
            if (initialized) return;

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null) return;

            // Layer 2 pool: one-shot positional sources at openings
            int maxSources = config.MaxThunderSources;
            oneShotPool = new PositionalSourcePool(
                capi,
                maxSources: maxSources,
                debugTag: "THUNDER",
                mode: PositionalSourcePool.PoolMode.OneShot);

            // Layer 2 crack at openings: use NODISTANCE for snappy impact
            oneShotPool.AssetResolver = (_) => NODISTANCE;
            oneShotPool.SoundRange = 64f;

            // Layer 1 EFX filter (shared, reused across events)
            if (EfxHelper.IsAvailable)
            {
                layer1FilterId = EfxHelper.GenFilter();
                if (layer1FilterId > 0)
                {
                    EfxHelper.ConfigureLowpass(layer1FilterId, 1.0f);
                }
            }

            initialized = true;
            ThunderDebugLog("ThunderAudioHandler initialized");
        }

        // ════════════════════════════════════════════════════════════════
        // AMBIENT THUNDER — fully replaces VS's ClientTick thunder
        // Called from our Harmony prefix with the same random roll results
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Play an ambient thunder event (replaces VS's WeatherSimulationLightning.ClientTick sounds).
        /// Ambient thunder has no bolt position — direction is random.
        /// Volume is computed entirely from our enclosure system (sky + occlusion + rain distance).
        /// </summary>
        /// <param name="asset">Sound asset (distant/near/verynear)</param>
        /// <param name="rawIntensity">Lightning type intensity: 0.3=distant, 0.7=near, 1.0=verynear</param>
        /// <param name="basePitch">Random base pitch (no depth influence)</param>
        /// <param name="trackedOpenings">Currently tracked openings from Phase 5B</param>
        /// <param name="playerEarPos">Player ear position</param>
        /// <param name="skyCoverage">Current sky coverage (0=outdoor, 1=covered)</param>
        /// <param name="occlusionFactor">Current occlusion factor (0=air, 1=material)</param>
        public void PlayAmbientThunder(
            AssetLocation asset,
            float rawIntensity,
            float basePitch,
            IReadOnlyList<TrackedOpening> trackedOpenings,
            Vec3d playerEarPos,
            float skyCoverage,
            float occlusionFactor)
        {
            if (!initialized || disposed) return;

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableThunderPositioning) return;

            // Compute volume from our own enclosure system — no VS deepnessSub dependency
            float nearestRainDist = GetNearestRainDistance(trackedOpenings, playerEarPos);
            float volume = CalculateThunderVolume(rawIntensity, skyCoverage, occlusionFactor, nearestRainDist);

            if (volume < 0.01f) return;

            // Pitch: slight occlusion damping (muffled indoors)
            float pitch = basePitch * (1f - occlusionFactor * 0.15f);

            long gameTimeMs = capi.World.ElapsedMilliseconds;
            float minSky = config.PositionalMinSkyCoverage;

            if (skyCoverage < minSky)
            {
                // OUTDOOR: Position thunder in random sky direction
                PlayOutdoorThunder(asset, volume, pitch, playerEarPos);
            }
            else if (trackedOpenings != null && trackedOpenings.Count > 0)
            {
                // INDOOR WITH OPENINGS: Layer 1 rumble + Layer 2 at opening
                float combined = CalculateCombinedEnclosure(skyCoverage, occlusionFactor, config);
                PlayLayer1Rumble(asset, rawIntensity, pitch, skyCoverage, occlusionFactor, nearestRainDist, gameTimeMs);
                PlayLayer2AtBestOpening(null, trackedOpenings, playerEarPos, volume, combined, config);
            }
            else
            {
                // FULLY ENCLOSED: Layer 1 rumble only (heavy LPF)
                PlayLayer1Rumble(asset, rawIntensity, pitch, skyCoverage, occlusionFactor, nearestRainDist, gameTimeMs);
            }

            ThunderDebugLog(
                $"AMBIENT: asset={asset.Path} rawInt={rawIntensity:F2} vol={volume:F2} pitch={pitch:F2} " +
                $"sky={skyCoverage:F2} occl={occlusionFactor:F2} rainDist={nearestRainDist:F1} " +
                $"openings={trackedOpenings?.Count ?? 0} mode=" +
                (skyCoverage < minSky ? "OUTDOOR" :
                 trackedOpenings?.Count > 0 ? "INDOOR+OPENINGS" : "ENCLOSED"));
        }

        // ════════════════════════════════════════════════════════════════
        // BOLT THUNDER — full custom Layer 1 + Layer 2 (VS sounds suppressed)
        // Called from our Harmony postfix on LightningFlash.ClientInit
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Play custom bolt thunder replacing VS's suppressed sounds.
        /// VS's PlaySoundAt calls in ClientInit/Render are NOPped by transpiler.
        /// We play our own positioned/filtered versions for all three modes.
        /// </summary>
        /// <param name="boltWorldPos">World position of the lightning strike</param>
        /// <param name="distance">Distance from player to bolt</param>
        /// <param name="trackedOpenings">Currently tracked openings</param>
        /// <param name="playerEarPos">Player ear position</param>
        /// <param name="skyCoverage">Current sky coverage</param>
        /// <param name="occlusionFactor">Current occlusion factor (0=air, 1=material)</param>
        public void PlayBoltThunder(
            Vec3d boltWorldPos,
            float distance,
            IReadOnlyList<TrackedOpening> trackedOpenings,
            Vec3d playerEarPos,
            float skyCoverage,
            float occlusionFactor)
        {
            if (!initialized || disposed) return;

            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null || !config.EnableThunderPositioning) return;

            float minSky = config.PositionalMinSkyCoverage;
            long gameTimeMs = capi.World.ElapsedMilliseconds;

            // Vanilla cutoff: no bolt sound past 320m
            if (distance >= 320f) return;

            // Select asset + base volume by distance (vanilla per-tier curves)
            AssetLocation asset = GetAssetForDistance(distance);
            float baseVolume = CalculateBoltIntensity(distance);
            if (baseVolume <= 0f) return;

            // Apply our enclosure system to bolt volume
            float nearestRainDist = GetNearestRainDistance(trackedOpenings, playerEarPos);
            float enclosureMod = CalculateThunderVolume(1.0f, skyCoverage, occlusionFactor, nearestRainDist);
            float volume = baseVolume * enclosureMod;
            if (volume < 0.01f) return;

            if (skyCoverage < minSky)
            {
                // OUTDOOR: Position bolt thunder toward strike location (full custom L1)
                PlayOutdoorBoltThunder(boltWorldPos, distance, playerEarPos);

                // Queue delayed nodistance.ogg crack (outdoor positioned)
                if (distance < 200)
                {
                    Vec3d dir = NormalizeBoltDirection(boltWorldPos, playerEarPos);
                    if (dir != null)
                    {
                        dir = FindClearSkyDirection(playerEarPos, dir);
                        float placeDist = Math.Min(distance, 30f);
                        pendingCracks.Add(new PendingDelayedCrack
                        {
                            SpawnTimeMs = gameTimeMs,
                            Distance = distance,
                            BoltWorldPos = boltWorldPos,
                            PlayerEarPos = playerEarPos,
                            SkyCoverage = skyCoverage,
                            OcclusionFactor = occlusionFactor,
                            CombinedEnclosure = 0f,
                            NearestRainDistance = nearestRainDist,
                            IsOutdoor = true,
                            OutdoorDir = dir,
                            OutdoorPlaceDist = placeDist
                        });
                    }
                }
            }
            else
            {
                // INDOOR (with or without openings): Full custom Layer 1 + optional Layer 2
                float combined = CalculateCombinedEnclosure(skyCoverage, occlusionFactor, config);

                // Layer 1: Muffled rumble with LPF
                PlayLayer1Rumble(asset, baseVolume, 1f, skyCoverage, occlusionFactor, nearestRainDist, gameTimeMs);

                // Layer 2: Crack at best opening (if any exist)
                if (trackedOpenings != null && trackedOpenings.Count > 0)
                {
                    PlayLayer2AtBestOpening(boltWorldPos, trackedOpenings, playerEarPos, volume, combined, config);
                }

                // Queue delayed nodistance.ogg crack (indoor, same LPF as L1)
                if (distance < 200)
                {
                    pendingCracks.Add(new PendingDelayedCrack
                    {
                        SpawnTimeMs = gameTimeMs,
                        Distance = distance,
                        BoltWorldPos = boltWorldPos,
                        PlayerEarPos = playerEarPos,
                        SkyCoverage = skyCoverage,
                        OcclusionFactor = occlusionFactor,
                        CombinedEnclosure = combined,
                        NearestRainDistance = nearestRainDist,
                        IsOutdoor = false
                    });
                }
            }

            string mode = skyCoverage < minSky ? "OUTDOOR" :
                           (trackedOpenings?.Count > 0 ? "INDOOR+L1+L2" : "ENCLOSED+L1");
            ThunderDebugLog(
                $"BOLT: dist={distance:F0} baseVol={baseVolume:F2} vol={volume:F2} " +
                $"sky={skyCoverage:F2} occl={occlusionFactor:F2} rainDist={nearestRainDist:F1} " +
                $"openings={trackedOpenings?.Count ?? 0} mode={mode}");
        }

        // ════════════════════════════════════════════════════════════════
        // OUTDOOR: Positioned thunder in the sky
        // ════════════════════════════════════════════════════════════════

        private void PlayOutdoorThunder(AssetLocation asset, float volume, float pitch, Vec3d playerEarPos)
        {
            // Random horizontal direction + slight elevation for ambient thunder
            double azimuth = rand.NextDouble() * Math.PI * 2;
            double elevation = 0.2 + rand.NextDouble() * 0.5; // 12-40 degrees above horizon

            double cosElev = Math.Cos(elevation);
            double sinElev = Math.Sin(elevation);
            Vec3d dir = new Vec3d(
                Math.Cos(azimuth) * cosElev,
                sinElev,
                Math.Sin(azimuth) * cosElev);

            // Hill-aware: if random direction hits terrain, arc over the hill
            dir = FindClearSkyDirection(playerEarPos, dir);

            // Placement distance based on asset type — closer thunder = closer source
            float placeDist = GetPlacementDistForAsset(asset);
            double x = playerEarPos.X + dir.X * placeDist;
            double y = playerEarPos.Y + dir.Y * placeDist;
            double z = playerEarPos.Z + dir.Z * placeDist;

            // Use LoadSound + rolloff=0 to disable OpenAL distance attenuation.
            // Our volume already encodes distance falloff — don't double-attenuate.
            float range = Math.Max(placeDist * 4f, 200f);

            try
            {
                var soundParams = new SoundParams()
                {
                    Location = asset,
                    ShouldLoop = false,
                    RelativePosition = false,
                    Position = new Vec3f((float)x, (float)y, (float)z),
                    DisposeOnFinish = true,
                    Volume = volume,
                    Pitch = pitch,
                    SoundType = EnumSoundType.Weather,
                    Range = range
                };

                ILoadedSound sound = capi.World.LoadSound(soundParams);
                if (sound != null)
                {
                    sound.Start();

                    // Disable OpenAL distance attenuation AFTER Start()
                    int sourceId = AudioRenderer.GetSourceId(sound);
                    if (sourceId > 0)
                    {
                        EfxHelper.ALSetSourceRolloff(sourceId, 0f);
                    }
                }
            }
            catch (Exception ex)
            {
                ThunderDebugLog($"  OUTDOOR ambient FAILED: {ex.Message}");
            }

            ThunderDebugLog($"  OUTDOOR ambient: pos=({x:F0},{y:F0},{z:F0}) vol={volume:F2} placeDist={placeDist:F0} range={range:F0} rolloff=0");
        }

        private void PlayOutdoorBoltThunder(Vec3d boltWorldPos, float distance, Vec3d playerEarPos)
        {
            // Direction from player to bolt
            Vec3d dir = new Vec3d(
                boltWorldPos.X - playerEarPos.X,
                boltWorldPos.Y - playerEarPos.Y,
                boltWorldPos.Z - playerEarPos.Z);
            double len = dir.Length();

            if (len < 1.0) return;

            dir.X /= len;
            dir.Y /= len;
            dir.Z /= len;

            // Hill-aware: find the lowest unoccluded path toward the bolt
            dir = FindClearSkyDirection(playerEarPos, dir);

            // Placement distance scales with actual bolt distance:
            // Close bolts (<10 blocks): place at actual distance for visceral impact
            // Medium-far: scale linearly up to 200 blocks max
            // 200 blocks = can't outrun during 5-8s thunder playback
            // OpenAL rolloff disabled (factor=0) so distance attenuation comes solely
            // from our volume curve — no double-attenuation at large distances
            float placeDist;
            if (distance < 10f)
            {
                // Very close strike — place nearly at actual position (min 3 for spatialization)
                placeDist = Math.Max(3f, distance);
            }
            else
            {
                // Scale: 10→10, 200→200, beyond 200 cap at 200
                placeDist = Math.Min(distance, 200f);
            }

            double x = playerEarPos.X + dir.X * placeDist;
            double y = playerEarPos.Y + dir.Y * placeDist;
            double z = playerEarPos.Z + dir.Z * placeDist;

            // Select asset by distance — matches vanilla LightningFlash.ClientInit thresholds
            AssetLocation asset = GetAssetForDistance(distance);

            // Volume curve per tier — matches vanilla's linear falloff per asset
            float volume = CalculateBoltIntensity(distance);

            if (volume <= 0f) return;

            // Use LoadSound instead of PlaySoundAt so we can set rolloff factor = 0.
            // This disables OpenAL's distance attenuation entirely — our volume curve
            // is the SOLE source of distance falloff. No double-attenuation.
            // Range is still needed as the hard cutoff boundary for OpenAL.
            float range = Math.Max(placeDist * 4f, 200f);

            try
            {
                var soundParams = new SoundParams()
                {
                    Location = asset,
                    ShouldLoop = false,
                    RelativePosition = false,
                    Position = new Vec3f((float)x, (float)y, (float)z),
                    DisposeOnFinish = true,
                    Volume = volume,
                    SoundType = EnumSoundType.Weather,
                    Range = range
                };

                ILoadedSound sound = capi.World.LoadSound(soundParams);
                if (sound != null)
                {
                    sound.Start();

                    // Disable OpenAL distance attenuation AFTER Start() —
                    // GetSourceId may not work until the source is playing
                    int sourceId = AudioRenderer.GetSourceId(sound);
                    if (sourceId > 0)
                    {
                        EfxHelper.ALSetSourceRolloff(sourceId, 0f);
                    }

                    ThunderDebugLog($"  OUTDOOR bolt: dist={distance:F0} placeDist={placeDist:F0} range={range:F0} asset={asset.Path} vol={volume:F2} rolloff=0 srcId={sourceId} dir=({dir.X:F2},{dir.Y:F2},{dir.Z:F2})");
                }
            }
            catch (Exception ex)
            {
                ThunderDebugLog($"  OUTDOOR bolt FAILED: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        // LAYER 1: Omnidirectional rumble with LPF (indoor)
        // ════════════════════════════════════════════════════════════════

        private void PlayLayer1Rumble(AssetLocation asset, float baseVolume, float pitch,
            float currentSkyCoverage, float currentOcclusionFactor, float nearestRainDist, long gameTimeMs)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config == null) return;

            // Calculate initial volume for the first frame
            float enclosureMod = CalculateThunderVolume(1.0f, currentSkyCoverage, currentOcclusionFactor, nearestRainDist);
            float currentVol = baseVolume * enclosureMod;
            float layer1Vol = GameMath.Clamp(currentVol * config.ThunderLayer1Volume, 0f, 1f);

            if (layer1Vol < 0.01f) return;

            // Calculate LPF gainHF from occlusion
            float gainHF = CalculateThunderGainHF(currentOcclusionFactor, config);

            try
            {
                // Create a non-looping listener-relative sound
                var soundParams = new SoundParams()
                {
                    Location = asset,
                    ShouldLoop = false,
                    RelativePosition = true,
                    Position = new Vec3f(0, 0, 0),
                    DisposeOnFinish = true,
                    Volume = layer1Vol,
                    Pitch = pitch,
                    Range = 32f,
                    SoundType = EnumSoundType.Weather
                };

                ILoadedSound sound = capi.World.LoadSound(soundParams);
                if (sound == null) return;

                // Apply LPF filter before starting
                if (layer1FilterId > 0 && EfxHelper.IsAvailable)
                {
                    int sourceId = AudioRenderer.GetSourceId(sound);
                    if (sourceId > 0)
                    {
                        EfxHelper.SetLowpassGainHF(layer1FilterId, gainHF);
                        AudioRenderer.AttachFilter(sourceId, layer1FilterId);
                    }
                }

                sound.Start();

                // Track for cleanup and dynamic updates
                activeLayer1Sounds.Add(new ManagedThunderSound
                {
                    Sound = sound,
                    StartTimeMs = gameTimeMs,
                    FilterId = layer1FilterId, // 0 if no EFX, otherwise shared filter
                    BaseVolume = baseVolume,
                    NearestRainDist = nearestRainDist
                });

                ThunderDebugLog($"  L1 RUMBLE: base={baseVolume:F2} vol={layer1Vol:F2} gainHF={gainHF:F3} occl={currentOcclusionFactor:F2}");
            }
            catch (Exception ex)
            {
                ThunderDebugLog($"  L1 RUMBLE FAILED: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert occlusion factor to LPF gainHF for thunder.
        /// Thunder is already bass-heavy, so the LPF curve is moderate.
        /// </summary>
        private float CalculateThunderGainHF(float occlusionFactor, SoundPhysicsConfig config)
        {
            // Quadratic curve: occlusionFactor 0→1 maps to gainHF 1.0→min
            float t = occlusionFactor * occlusionFactor;

            // Convert min cutoff Hz to approximate gainHF (0-1)
            // gainHF ≈ cutoff / 22000 (rough linear approximation for perception)
            float minGainHF = config.ThunderLPFMinCutoff / 22000f;
            minGainHF = GameMath.Clamp(minGainHF, 0.005f, 1f);

            float gainHF = 1f - t * (1f - minGainHF);
            return GameMath.Clamp(gainHF, minGainHF, 1f);
        }

        // ════════════════════════════════════════════════════════════════
        // LAYER 2: Positional crack at best opening
        // ════════════════════════════════════════════════════════════════

        private void PlayLayer2AtBestOpening(
            Vec3d boltWorldPos,
            IReadOnlyList<TrackedOpening> trackedOpenings,
            Vec3d playerEarPos,
            float intensity,
            float combinedEnclosure,
            SoundPhysicsConfig config)
        {
            if (oneShotPool == null || trackedOpenings == null || trackedOpenings.Count == 0)
                return;

            // Calculate bolt direction from player (null for ambient thunder)
            Vec3d boltDir = null;
            if (boltWorldPos != null)
            {
                boltDir = new Vec3d(
                    boltWorldPos.X - playerEarPos.X,
                    boltWorldPos.Y - playerEarPos.Y,
                    boltWorldPos.Z - playerEarPos.Z);
                double boltDist = boltDir.Length();

                if (boltDist > 0.01)
                {
                    boltDir.X /= boltDist;
                    boltDir.Y /= boltDist;
                    boltDir.Z /= boltDist;
                }
                else
                {
                    boltDir = null;
                }
            }

            // Score each opening
            float bestScore = -2f;
            int bestIndex = -1;

            for (int i = 0; i < trackedOpenings.Count; i++)
            {
                var opening = trackedOpenings[i];
                Vec3d openingDir = new Vec3d(
                    opening.WorldPos.X - playerEarPos.X,
                    opening.WorldPos.Y - playerEarPos.Y,
                    opening.WorldPos.Z - playerEarPos.Z);
                double openingDist = openingDir.Length();

                if (openingDist < 0.01) continue;

                openingDir.X /= openingDist;
                openingDir.Y /= openingDist;
                openingDir.Z /= openingDist;

                float score;
                if (boltDir != null)
                {
                    // Bolt direction bias: 70% alignment + 30% cluster weight
                    float dot = (float)(boltDir.X * openingDir.X +
                                       boltDir.Y * openingDir.Y +
                                       boltDir.Z * openingDir.Z);
                    float sizeWeight = MathF.Sqrt(Math.Min(opening.SmoothedClusterWeight / 8f, 1f));

                    // For overhead bolts (dot near 0 for horizontal openings):
                    // increase size weight to avoid all openings scoring poorly
                    float verticalness = Math.Abs(boltDir != null ? (float)boltDir.Y : 0f);
                    float dotWeight = 0.7f * (1f - verticalness * 0.5f);
                    float sizeW = 1f - dotWeight;

                    score = dot * dotWeight + sizeWeight * sizeW;
                }
                else
                {
                    // No bolt direction (ambient): score by cluster weight + proximity
                    float sizeWeight = MathF.Sqrt(Math.Min(opening.SmoothedClusterWeight / 8f, 1f));
                    float proxWeight = 1f / (1f + (float)openingDist * 0.1f);
                    score = sizeWeight * 0.6f + proxWeight * 0.4f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) return;

            var bestOpening = trackedOpenings[bestIndex];
            float layer2Vol = intensity * config.ThunderLayer2Volume * Math.Max(bestScore, 0.2f);
            layer2Vol = GameMath.Clamp(layer2Vol, 0f, 1f);

            // Pre-apply LPF filter to one-shot BEFORE Start() to prevent transient bypass.
            // Thunder cracks are transient — if filter arrives a frame late, the initial crack
            // is heard unfiltered, destroying the indoor illusion.
            int preFilterId = 0;
            if (combinedEnclosure > 0.05f && layer1FilterId > 0 && EfxHelper.IsAvailable)
            {
                float gainHF = CalculateThunderGainHF(combinedEnclosure, config);
                EfxHelper.SetLowpassGainHF(layer1FilterId, gainHF);
                preFilterId = layer1FilterId;
            }

            // Push the L2 source outward from the opening through air blocks.
            // This makes thunder sound like it's coming from BEYOND the door/window,
            // not at the doorframe. Prevents walking past the source mid-thunder.
            Vec3d pushDir;
            if (boltWorldPos != null)
            {
                // Bolt direction: push from opening toward bolt
                pushDir = new Vec3d(
                    boltWorldPos.X - playerEarPos.X,
                    boltWorldPos.Y - playerEarPos.Y,
                    boltWorldPos.Z - playerEarPos.Z);
            }
            else
            {
                // Ambient: push away from player through the opening
                pushDir = new Vec3d(
                    bestOpening.WorldPos.X - playerEarPos.X,
                    bestOpening.WorldPos.Y - playerEarPos.Y,
                    bestOpening.WorldPos.Z - playerEarPos.Z);
            }
            double pushLen = pushDir.Length();
            if (pushLen > 0.01)
            {
                pushDir.X /= pushLen;
                pushDir.Y /= pushLen;
                pushDir.Z /= pushLen;
            }

            Vec3d l2Pos = PushThroughAir(bestOpening.WorldPos, pushDir, 15f, capi.World.BlockAccessor);
            float pushDist = (float)l2Pos.DistanceTo(bestOpening.WorldPos);

            oneShotPool.PlayOneShot(l2Pos, layer2Vol, false, preFilterId);

            ThunderDebugLog(
                $"  L2 at opening trackId={bestOpening.TrackingId} " +
                $"score={bestScore:F2} vol={layer2Vol:F3} encl={combinedEnclosure:F2} " +
                $"preFilter={preFilterId > 0} hasBolt={boltWorldPos != null} " +
                $"pushed={pushDist:F1}blocks");
        }

        // ════════════════════════════════════════════════════════════════
        // TICK + CLEANUP
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tick: clean up finished one-shot sources + expired Layer 1 sounds.
        /// Also handles indoor→outdoor transition: kills L2 one-shots when
        /// the player moves outside, letting L1 (listener-relative) take over.
        /// Call each weather tick.
        /// </summary>
        /// <param name="currentSkyCoverage">Current smoothed sky coverage from WeatherAudioManager (0=outdoors, 1=covered)</param>
        /// <param name="currentOcclusionFactor">Current smoothed occlusion factor</param>
        public void OnGameTick(float currentSkyCoverage, float currentOcclusionFactor)
        {
            if (!initialized || disposed) return;

            // Indoor→outdoor transition: kill L2 one-shots when player goes outside.
            // L2 sources are static world-positioned — if the player walks past them
            // (through a door), the thunder would pan behind them unrealistically.
            // L1 (listener-relative) continues seamlessly.
            var config = SoundPhysicsAdaptedModSystem.Config;
            float minSky = config?.PositionalMinSkyCoverage ?? 0.15f;
            if (currentSkyCoverage < minSky)
            {
                // Player is now outdoors — kill any lingering L2 thunder
                if (oneShotPool?.ActiveCount > 0)
                {
                    oneShotPool.StopAll();
                    ThunderDebugLog($"  L2 OUTDOOR KILL: sky={currentSkyCoverage:F2} < {minSky:F2} — killed L2 one-shots");
                }
            }

            oneShotPool?.TickOneShotSources();

            // Clean up finished Layer 1 sounds
            long gameTimeMs = capi.World.ElapsedMilliseconds;
            for (int i = activeLayer1Sounds.Count - 1; i >= 0; i--)
            {
                var managed = activeLayer1Sounds[i];

                // Thunder sounds are ~3-8 seconds. Give 15s max lifetime for safety.
                bool expired = (gameTimeMs - managed.StartTimeMs) > 15000;
                bool finished = managed.Sound == null || !managed.Sound.IsPlaying;

                if (expired || finished)
                {
                    try
                    {
                        managed.Sound?.Stop();
                        managed.Sound?.Dispose();
                    }
                    catch { }
                    activeLayer1Sounds.RemoveAt(i);
                }
            }

            // Cap active Layer 1 sounds
            while (activeLayer1Sounds.Count > MAX_ACTIVE_LAYER1)
            {
                var oldest = activeLayer1Sounds[0];
                try
                {
                    oldest.Sound?.Stop();
                    oldest.Sound?.Dispose();
                }
                catch { }
                activeLayer1Sounds.RemoveAt(0);
            }

            // Update dynamic enclosure for all active Layer 1 rumbles
            if (activeLayer1Sounds.Count > 0)
            {
                // 1. Update shared EFX filter LPF if supported (applies instantly to all L1 sources)
                if (layer1FilterId > 0 && EfxHelper.IsAvailable)
                {
                    float gainHF = CalculateThunderGainHF(currentOcclusionFactor, config);
                    EfxHelper.SetLowpassGainHF(layer1FilterId, gainHF);
                }

                // 2. Update individual volumes based on dynamic enclosure
                for (int i = 0; i < activeLayer1Sounds.Count; i++)
                {
                    var managed = activeLayer1Sounds[i];
                    if (managed.Sound != null && managed.Sound.IsPlaying)
                    {
                        float enclosureMod = CalculateThunderVolume(1.0f, currentSkyCoverage, currentOcclusionFactor, managed.NearestRainDist);
                        float currentVol = managed.BaseVolume * enclosureMod;
                        float layer1Vol = GameMath.Clamp(currentVol * config.ThunderLayer1Volume, 0f, 1f);
                        managed.Sound.SetVolume(layer1Vol);
                    }
                }
            }

            // Fire pending delayed cracks (nodistance.ogg ~50ms after bolt spawn)
            for (int i = pendingCracks.Count - 1; i >= 0; i--)
            {
                var crack = pendingCracks[i];
                long elapsed = gameTimeMs - crack.SpawnTimeMs;

                if (elapsed >= DELAYED_CRACK_MS)
                {
                    pendingCracks.RemoveAt(i);
                    FireDelayedCrack(crack, gameTimeMs);
                }
                else if (elapsed > 5000)
                {
                    // Safety timeout — should never happen, but don't leak
                    pendingCracks.RemoveAt(i);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // DELAYED CRACK PLAYBACK
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Fire a delayed nodistance.ogg crack.
        /// Outdoor: positioned toward bolt direction.
        /// Indoor/Enclosed: as Layer 1 with LPF.
        /// This closes the gap left by transpiler-suppressing VS's Render() crack.
        /// </summary>
        private void FireDelayedCrack(PendingDelayedCrack crack, long gameTimeMs)
        {
            // Realistic crack falloff: pow(1.5) curve over 200m range.
            // Real thunder cracks (>2kHz) attenuate ~6dB per distance doubling.
            // pow(1.5) gives fast initial drop + gentle tail — audible to ~200m.
            float crackVol = MathF.Pow(Math.Max(0f, 1f - crack.Distance / 200f), 1.5f);

            // Apply our enclosure attenuation on top
            float enclosureMod = CalculateThunderVolume(1.0f, crack.SkyCoverage, crack.OcclusionFactor, crack.NearestRainDistance);
            float volume = crackVol * enclosureMod;
            if (volume < 0.01f) return;

            if (crack.IsOutdoor && crack.OutdoorDir != null)
            {
                // Outdoor: position the crack at 30 blocks toward the bolt
                // with rolloff=0 so volume is fully controlled by our curve
                float crackPlaceDist = Math.Min(crack.OutdoorPlaceDist, 30f);
                double x = crack.PlayerEarPos.X + crack.OutdoorDir.X * crackPlaceDist;
                double y = crack.PlayerEarPos.Y + crack.OutdoorDir.Y * crackPlaceDist;
                double z = crack.PlayerEarPos.Z + crack.OutdoorDir.Z * crackPlaceDist;
                float range = Math.Max(crackPlaceDist * 4f, 200f);

                // Slight volume falloff at the outer edge of placement
                float crackFalloff = crackPlaceDist < 20f ? 1f : 1f - (crackPlaceDist - 20f) / 30f;
                float finalVol = volume * Math.Max(crackFalloff, 0.5f);

                try
                {
                    var soundParams = new SoundParams()
                    {
                        Location = NODISTANCE,
                        ShouldLoop = false,
                        RelativePosition = false,
                        Position = new Vec3f((float)x, (float)y, (float)z),
                        DisposeOnFinish = true,
                        Volume = finalVol,
                        SoundType = EnumSoundType.Weather,
                        Range = range
                    };

                    ILoadedSound sound = capi.World.LoadSound(soundParams);
                    if (sound != null)
                    {
                        sound.Start();
                        int sourceId = AudioRenderer.GetSourceId(sound);
                        if (sourceId > 0)
                        {
                            EfxHelper.ALSetSourceRolloff(sourceId, 0f);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ThunderDebugLog($"  DELAYED CRACK FAILED: {ex.Message}");
                }

                ThunderDebugLog($"  DELAYED CRACK (outdoor): vol={finalVol:F2} placeDist={crackPlaceDist:F0} rolloff=0");
            }
            else
            {
                // Indoor/Enclosed: play as L1 with LPF
                PlayLayer1Rumble(NODISTANCE, crackVol, 1f, crack.SkyCoverage, crack.OcclusionFactor, crack.NearestRainDistance, gameTimeMs);
                ThunderDebugLog($"  DELAYED CRACK (indoor): baseVol={crackVol:F2} encl={crack.CombinedEnclosure:F2}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Compute thunder volume entirely from our enclosure metrics — no VS deepnessSub.
        /// Uses sky coverage, occlusion factor, and nearest rain distance as a hard gate.
        /// </summary>
        /// <param name="rawIntensity">Base intensity (0.3=distant, 0.7=near, 1.0=verynear, or bolt intensity)</param>
        /// <param name="skyCoverage">Sky coverage 0=outdoor, 1=fully covered</param>
        /// <param name="occlusionFactor">Block occlusion 0=air, 1=solid</param>
        /// <param name="nearestRainDistance">Distance to nearest rain-exposed point</param>
        private float CalculateThunderVolume(float rawIntensity, float skyCoverage, float occlusionFactor, float nearestRainDistance)
        {
            // Sky factor: less covered sky = louder. At full sky coverage, 30% remains (bass penetration)
            float skyFactor = 1.0f - skyCoverage * 0.7f;

            // Occlusion multiplier: more material = quieter. At full occlusion, 50% remains
            float occlusionMult = 1.0f - occlusionFactor * 0.5f;

            // Rain distance factor: hard gate — no thunder if far from any rain column
            float rainDistFactor;
            if (nearestRainDistance <= 3f)
            {
                rainDistFactor = 1.0f;
            }
            else if (nearestRainDistance <= 20f)
            {
                // Lerp 1.0 → 0.3 over 3..20 blocks
                rainDistFactor = 1.0f - (nearestRainDistance - 3f) / 17f * 0.7f;
            }
            else if (nearestRainDistance <= 40f)
            {
                // Soft tail: lerp 0.3 → 0.0 over 20..40 blocks
                rainDistFactor = 0.3f - (nearestRainDistance - 20f) / 20f * 0.3f;
            }
            else
            {
                // Hard gate: >40 blocks from rain = silence
                rainDistFactor = 0f;
            }

            return rawIntensity * skyFactor * occlusionMult * rainDistFactor;
        }

        /// <summary>
        /// Find the nearest distance to a rain-exposed point from the player.
        /// Primary: uses tracked openings (known paths to sky).
        /// Fallback: samples 32 heightmap columns in 8 directions at 4 distances.
        /// Returns float.MaxValue if no rain-exposed point found.
        /// </summary>
        private float GetNearestRainDistance(IReadOnlyList<TrackedOpening> trackedOpenings, Vec3d playerEarPos)
        {
            float nearest = float.MaxValue;

            // Primary: check tracked opening distances
            if (trackedOpenings != null && trackedOpenings.Count > 0)
            {
                for (int i = 0; i < trackedOpenings.Count; i++)
                {
                    float dist = (float)trackedOpenings[i].WorldPos.DistanceTo(playerEarPos);
                    if (dist < nearest)
                        nearest = dist;
                }
            }

            // Also sample heightmap columns — gives smoother gradient than openings alone
            float sampled = SampleAverageRainDistance(playerEarPos);
            if (sampled < nearest)
                nearest = sampled;

            return nearest;
        }

        /// <summary>
        /// Sample 32 heightmap columns (8 directions x 4 distances) for rain exposure.
        /// Collects the 9 nearest rain-exposed columns and returns their average distance.
        /// Averaging prevents a single nearby column from dominating — gives smooth gradient
        /// as player moves deeper into a cave or building.
        /// Returns float.MaxValue if no rain-exposed columns found.
        /// </summary>
        private float SampleAverageRainDistance(Vec3d playerEarPos)
        {
            var blockAccessor = capi.World.BlockAccessor;
            int playerX = (int)Math.Floor(playerEarPos.X);
            int playerY = (int)Math.Floor(playerEarPos.Y);
            int playerZ = (int)Math.Floor(playerEarPos.Z);

            const int MAX_SAMPLES = 9;

            // Fixed-size array to avoid allocation — sorted insert keeps nearest N
            float[] nearestDists = new float[MAX_SAMPLES];
            for (int i = 0; i < MAX_SAMPLES; i++)
                nearestDists[i] = float.MaxValue;
            int foundCount = 0;

            // 8 cardinal + diagonal directions
            int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
            int[] dz = { 0, 1, 1, 1, 0, -1, -1, -1 };

            // 4 distance steps: 3, 6, 9, 12 blocks
            int[] distances = { 3, 6, 9, 12 };

            for (int d = 0; d < 8; d++)
            {
                for (int r = 0; r < 4; r++)
                {
                    int sampleX = playerX + dx[d] * distances[r];
                    int sampleZ = playerZ + dz[d] * distances[r];

                    try
                    {
                        int rainHeight = blockAccessor.GetRainMapHeightAt(sampleX, sampleZ);

                        // If player Y is at or above rain height, this column is rain-exposed
                        if (playerY >= rainHeight - 1)
                        {
                            float hDist = distances[r];
                            float vDist = Math.Abs(playerY - rainHeight);
                            float dist = MathF.Sqrt(hDist * hDist + vDist * vDist);

                            // Insert into sorted array if closer than the current worst
                            if (dist < nearestDists[MAX_SAMPLES - 1])
                            {
                                nearestDists[MAX_SAMPLES - 1] = dist;
                                if (foundCount < MAX_SAMPLES) foundCount++;

                                // Bubble sort the new entry into place
                                for (int j = MAX_SAMPLES - 1; j > 0 && nearestDists[j] < nearestDists[j - 1]; j--)
                                {
                                    float tmp = nearestDists[j];
                                    nearestDists[j] = nearestDists[j - 1];
                                    nearestDists[j - 1] = tmp;
                                }
                            }

                            // No need to check further distances in this direction
                            break;
                        }
                    }
                    catch
                    {
                        // Chunk not loaded — skip
                    }
                }
            }

            if (foundCount == 0) return float.MaxValue;

            // Average the nearest N distances found
            float sum = 0f;
            for (int i = 0; i < foundCount; i++)
                sum += nearestDists[i];

            return sum / foundCount;
        }

        /// <summary>
        /// Combine sky coverage and block occlusion into a single enclosure factor.
        /// Takes the maximum of:
        /// - Raw block occlusion (structural walls)
        /// - Sky-derived contribution (open sky without occlusion is still "outside")
        /// Cap at 0.85 so a sealed room with no sky gets massive LPF,
        /// but an open-top room with little block occlusion still gets some.
        /// </summary>
        private float CalculateCombinedEnclosure(float skyCoverage, float occlusionFactor, SoundPhysicsConfig config)
        {
            float minSky = config.PositionalMinSkyCoverage;
            // skyContribution: 0 at minSky threshold (barely indoor), rises toward 0.85 at full coverage
            float skyContribution = Math.Max(0f, (skyCoverage - minSky) / (1f - minSky));
            skyContribution *= 0.85f;

            return Math.Max(occlusionFactor, skyContribution);
        }

        /// <summary>
        /// Normalize bolt direction from player ear, returning null if too close.
        /// </summary>
        private Vec3d NormalizeBoltDirection(Vec3d boltWorldPos, Vec3d playerEarPos)
        {
            Vec3d dir = new Vec3d(
                boltWorldPos.X - playerEarPos.X,
                boltWorldPos.Y - playerEarPos.Y,
                boltWorldPos.Z - playerEarPos.Z);
            double len = dir.Length();
            if (len < 1.0) return null;
            dir.X /= len;
            dir.Y /= len;
            dir.Z /= len;
            return dir;
        }

        /// <summary>
        /// Hill-aware direction finding for outdoor thunder.
        /// Given a target direction (toward bolt or random sky), check if terrain blocks
        /// the path. If so, arc upward in the same horizontal direction until finding
        /// a clear sky path — the lowest elevation that clears the hill.
        /// Sound in reality travels through air over terrain obstacles, not through them.
        /// </summary>
        /// <param name="playerEarPos">Player ear position</param>
        /// <param name="targetDir">Desired direction (normalized)</param>
        /// <returns>Adjusted direction that clears terrain, or original if already clear</returns>
        private Vec3d FindClearSkyDirection(Vec3d playerEarPos, Vec3d targetDir)
        {
            var blockAccessor = capi.World.BlockAccessor;

            // Check if direct path is clear (fast DDA, 30 blocks)
            float checkDist = 30f;
            Vec3d endpoint = new Vec3d(
                playerEarPos.X + targetDir.X * checkDist,
                playerEarPos.Y + targetDir.Y * checkDist,
                playerEarPos.Z + targetDir.Z * checkDist);

            float occlusion = OcclusionCalculator.CalculateWeatherPathOcclusion(
                playerEarPos, endpoint, blockAccessor);

            if (occlusion < 0.3f)
            {
                // Direct path is clear — no hill in the way
                return targetDir;
            }

            // Terrain blocks the direct path — find the lowest clear elevation.
            // Keep the same horizontal (XZ) direction, step elevation upward.
            double horizX = targetDir.X;
            double horizZ = targetDir.Z;
            double horizLen = Math.Sqrt(horizX * horizX + horizZ * horizZ);

            if (horizLen < 0.01)
            {
                // Direction is nearly vertical already — nothing to adjust
                return targetDir;
            }

            // Normalize horizontal component
            horizX /= horizLen;
            horizZ /= horizLen;

            // Try elevations from 15° to 75° in 10° steps
            // (original direction's elevation is probably low since bolt is at ground level)
            const int STEPS = 7;
            const double START_ELEV = 15.0 * Math.PI / 180.0; // 15 degrees
            const double STEP_ELEV = 10.0 * Math.PI / 180.0;  // 10 degree increments

            for (int i = 0; i < STEPS; i++)
            {
                double elevation = START_ELEV + i * STEP_ELEV;
                double cosElev = Math.Cos(elevation);
                double sinElev = Math.Sin(elevation);

                Vec3d testDir = new Vec3d(
                    horizX * cosElev,
                    sinElev,
                    horizZ * cosElev);

                Vec3d testEnd = new Vec3d(
                    playerEarPos.X + testDir.X * checkDist,
                    playerEarPos.Y + testDir.Y * checkDist,
                    playerEarPos.Z + testDir.Z * checkDist);

                float testOcclusion = OcclusionCalculator.CalculateWeatherPathOcclusion(
                    playerEarPos, testEnd, blockAccessor);

                if (testOcclusion < 0.3f)
                {
                    ThunderDebugLog($"  HILL FIX: elevation={elevation * 180 / Math.PI:F0}deg clears terrain (occl={testOcclusion:F2})");
                    return testDir;
                }
            }

            // Nothing cleared — use high elevation fallback (nearly overhead)
            ThunderDebugLog($"  HILL FIX: no clear path found, using 75deg fallback");
            double fallbackElev = 75.0 * Math.PI / 180.0;
            return new Vec3d(
                horizX * Math.Cos(fallbackElev),
                Math.Sin(fallbackElev),
                horizZ * Math.Cos(fallbackElev));
        }

        /// <summary>Calculate bolt intensity from distance.
        /// Matches vanilla LightningFlash.ClientInit per-tier linear falloff.
        /// Each tier has its own denominator — vanilla uses hard transitions.
        /// Our enclosure system (sky/occlusion/rain) applies on top.</summary>
        private float CalculateBoltIntensity(float distance)
        {
            if (distance < 150f)
                return Math.Max(0.1f, 1f - distance / 180f);   // verynear tier
            if (distance < 200f)
                return Math.Max(0.1f, 1f - distance / 250f);   // near tier
            if (distance < 320f)
                return Math.Max(0.1f, 1f - distance / 500f);   // distant tier
            return 0f;  // beyond 320 = no bolt sound (vanilla cutoff)
        }

        /// <summary>Select thunder asset by distance — matches vanilla LightningFlash.ClientInit thresholds.</summary>
        public static AssetLocation GetAssetForDistance(float distance)
        {
            if (distance < 150) return VERY_NEAR;
            if (distance < 200) return NEAR;
            return DISTANT;
        }

        /// <summary>
        /// Get placement distance for ambient thunder based on the sound asset.
        /// Distances far enough that walking can't outrun the thunder.
        /// Rolloff=0 ensures OpenAL won't double-attenuate at these distances.
        /// </summary>
        private static float GetPlacementDistForAsset(AssetLocation asset)
        {
            string path = asset?.Path ?? "";
            if (path.Contains("verynear")) return 40f;
            if (path.Contains("near")) return 80f;
            return 120f; // distant
        }

        /// <summary>
        /// Push a position outward through air blocks along a direction vector.
        /// Steps 1 block at a time, stopping at the first solid block or maxDist.
        /// Used to push L2 thunder one-shots beyond openings so the player
        /// can't walk past the source mid-thunder.
        /// </summary>
        /// <param name="startPos">Starting world position (opening centroid)</param>
        /// <param name="dir">Normalized push direction</param>
        /// <param name="maxDist">Maximum push distance in blocks</param>
        /// <param name="blockAccessor">Block accessor for solid checks</param>
        /// <returns>Furthest valid air position along the push direction</returns>
        private static Vec3d PushThroughAir(Vec3d startPos, Vec3d dir, float maxDist, IBlockAccessor blockAccessor)
        {
            Vec3d best = startPos;
            int steps = (int)maxDist;

            for (int i = 1; i <= steps; i++)
            {
                double x = startPos.X + dir.X * i;
                double y = startPos.Y + dir.Y * i;
                double z = startPos.Z + dir.Z * i;

                int bx = (int)Math.Floor(x);
                int by = (int)Math.Floor(y);
                int bz = (int)Math.Floor(z);

                try
                {
                    Block block = blockAccessor.GetBlock(new BlockPos(bx, by, bz, 0));
                    if (block == null || block.Id == 0)
                    {
                        // Air block — valid push position
                        best = new Vec3d(x, y, z);
                        continue;
                    }

                    // Non-solid blocks (plants, leaves, snow layers) are passable
                    if (block.BlockMaterial == EnumBlockMaterial.Air
                        || block.BlockMaterial == EnumBlockMaterial.Plant
                        || block.BlockMaterial == EnumBlockMaterial.Leaves)
                    {
                        best = new Vec3d(x, y, z);
                        continue;
                    }

                    // Hit a solid block — stop here
                    break;
                }
                catch
                {
                    // BlockAccessor may fail at chunk boundaries — stop pushing
                    break;
                }
            }

            return best;
        }

        public string GetDebugStatus()
        {
            if (!initialized) return "Thunder: NOT INITIALIZED";
            return $"Thunder: L2active={oneShotPool?.ActiveCount ?? 0} L1active={activeLayer1Sounds.Count}";
        }

        private void ThunderDebugLog(string message)
        {
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config?.DebugMode != true) return;
            if (config?.DebugThunder != true && config?.DebugWeather != true) return;

            SoundPhysicsAdaptedModSystem.DebugLog($"[Thunder] {message}");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            // Clean up Layer 1 sounds
            foreach (var managed in activeLayer1Sounds)
            {
                try
                {
                    managed.Sound?.Stop();
                    managed.Sound?.Dispose();
                }
                catch { }
            }
            activeLayer1Sounds.Clear();

            // Clear pending delayed cracks
            pendingCracks.Clear();

            // Clean up Layer 1 filter
            if (layer1FilterId > 0)
            {
                EfxHelper.DeleteFilter(layer1FilterId);
                layer1FilterId = 0;
            }

            oneShotPool?.Dispose();
            oneShotPool = null;

            initialized = false;
            ThunderDebugLog("ThunderAudioHandler disposed");
        }
    }
}
