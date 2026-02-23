using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.GameContent;
using soundphysicsadapted.Patches;

namespace soundphysicsadapted
{
    /// <summary>
    /// Sound Physics Adapted - Adds realistic sound occlusion and reverb to Vintage Story
    ///
    /// Phase 1 Features:
    /// - Raycast-based sound occlusion through blocks
    /// - Lowpass filtering for muffled sounds
    /// - Per-sound OpenAL filters
    ///
    /// Phase 3 Features (SPR-Style):
    /// - Completely replaces vanilla reverb
    /// - Multi-slot EAX reverb with different decay times
    /// - Works for ALL materials (wood, glass, soil, etc.)
    /// - Works for small spaces (tunnels) unlike vanilla
    ///
    /// Algorithm based on Sound Physics Remastered (GPL)
    /// </summary>
    public class SoundPhysicsAdaptedModSystem : ModSystem
    {
        private static SoundPhysicsAdaptedModSystem instance;
        private Harmony harmony;
        private static SoundPhysicsConfig config;
        private static MaterialSoundConfig materialConfig;
        private static ICoreClientAPI clientApi;
        private static AudioPhysicsSystem acousticsManager;
        private static SoundPlaybackThrottle soundThrottle;
        
        // Phase 5A: Weather audio manager
        private static WeatherAudioManager weatherManager;
        private long weatherTimerId = 0;
        
        // Mod compatibility detection
        private static bool carryOnModLoaded = false;
        
        /// <summary>
        /// True if the "Carry On" mod is loaded - changes resonator pause/resume from Shift+RMB to Ctrl+RMB
        /// </summary>
        public static bool CarryOnModLoaded => carryOnModLoaded;

        public static SoundPhysicsConfig Config => config;
        public static MaterialSoundConfig MaterialConfig => materialConfig;
        public static ICoreClientAPI ClientApi => clientApi;
        public static SoundPhysicsAdaptedModSystem Instance => instance;
        public static AudioPhysicsSystem Acoustics => acousticsManager;
        public static WeatherAudioManager Weather => weatherManager;
        public static SoundPlaybackThrottle Throttle => soundThrottle;

        public const string HARMONY_ID = "com.soundphysicsadapted.mod";
        public const string MOD_ID = "soundphysicsadapted";

        // Networking
        public static IClientNetworkChannel ClientChannel;
        public static IServerNetworkChannel ServerChannel;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            instance = this;

            // Register Network Channel
            api.Network.RegisterChannel("soundphysicsadapted")
                .RegisterMessageType(typeof(ResonatorSyncPacket))
                .RegisterMessageType(typeof(ServerHandshakePacket))
                .RegisterMessageType(typeof(BoomboxSyncPacket));

            // Load main configuration
            try
            {
                config = api.LoadModConfig<SoundPhysicsConfig>("soundphysicsadapted.json");
                if (config == null)
                {
                    config = new SoundPhysicsConfig();
                }
                // Always re-save to add any new properties from updates
                api.StoreModConfig(config, "soundphysicsadapted.json");
                api.Logger.Notification($"[SoundPhysicsAdapted] Config loaded - Enabled: {config.Enabled}, DebugMode: {config.DebugMode}");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to load config: {ex.Message}");
                config = new SoundPhysicsConfig();
            }

            // Load material sound configuration
            try
            {
                materialConfig = api.LoadModConfig<MaterialSoundConfig>("soundphysicsadapted_materials.json");
                if (materialConfig == null)
                {
                    materialConfig = MaterialSoundConfig.CreateDefault();
                }
                else
                {
                    // Migration: ensure TreatAsFullCube list exists for older configs
                    if (materialConfig.Occlusion.TreatAsFullCube == null || materialConfig.Occlusion.TreatAsFullCube.Count == 0)
                    {
                        materialConfig.Occlusion.TreatAsFullCube = new System.Collections.Generic.List<string>
                        {
                            "game:glasspane-leaded-*"
                        };
                        api.Logger.Notification("[SoundPhysicsAdapted] Migrated config: added TreatAsFullCube defaults");
                    }
                    
                    // Migration: add snowlayer override if missing
                    if (materialConfig.Occlusion.BlockOverrides != null && 
                        !materialConfig.Occlusion.BlockOverrides.ContainsKey("game:snowlayer-*"))
                    {
                        materialConfig.Occlusion.BlockOverrides["game:snowlayer-*"] = 0.0f;
                        api.Logger.Notification("[SoundPhysicsAdapted] Migrated config: added snowlayer override");
                    }
                    
                    // Migration: update glass to 0.8 if still at old default 0.5
                    if (materialConfig.Occlusion.Materials.TryGetValue("glass", out float glassVal) && glassVal <= 0.5f)
                    {
                        materialConfig.Occlusion.Materials["glass"] = 0.8f;
                        api.Logger.Notification("[SoundPhysicsAdapted] Migrated config: updated glass occlusion to 0.8");
                    }
                }
                // Always re-save to add any new properties from updates
                api.StoreModConfig(materialConfig, "soundphysicsadapted_materials.json");
                api.Logger.Notification($"[SoundPhysicsAdapted] Material config loaded - {materialConfig.Occlusion.Materials.Count} materials, {materialConfig.Occlusion.BlockOverrides.Count} overrides, {materialConfig.Occlusion.TreatAsFullCube?.Count ?? 0} full-cube patterns");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to load material config: {ex.Message}");
                materialConfig = MaterialSoundConfig.CreateDefault();
            }

            // Initialize sound override manager (logs active overrides)
            Core.SoundOverrideManager.Initialize(api, config);
        }

        // Server-side Harmony instance for resonator patches
        private Harmony serverHarmony;
        private static ICoreServerAPI serverApi;

        /// <summary>
        /// Distance filter for boombox sync relay (blocks).
        /// Matches the point where vanilla resonator volume curve hits ~0.
        /// </summary>
        private const float BOOMBOX_RELAY_RANGE = 48f;

        public override void StartServerSide(ICoreServerAPI api)
        {
            serverApi = api;

            ServerChannel = api.Network.GetChannel("soundphysicsadapted")
                .RegisterMessageType(typeof(ResonatorSyncPacket))
                .SetMessageHandler<ResonatorSyncPacket>(OnResonatorSyncPacket);

            // Register boombox sync packet (carrier -> server -> nearby clients)
            api.Network.GetChannel("soundphysicsadapted")
                .RegisterMessageType(typeof(BoomboxSyncPacket))
                .SetMessageHandler<BoomboxSyncPacket>(OnBoomboxSyncPacket);

            // Register handshake packet (server sends, no handler needed)
            api.Network.GetChannel("soundphysicsadapted")
                .RegisterMessageType(typeof(ServerHandshakePacket));

            // Apply server-side Harmony patches for resonator interaction
            // This ensures OnInteract prefix fires on the server too (critical for multiplayer)
            // Only apply if enabled in config
            if (config.EnableResonatorFix)
            {
                serverHarmony = new Harmony(HARMONY_ID + ".server");
                ResonatorPatches.ApplyShared(serverHarmony, api);
            }
            else
            {
                api.Logger.Notification("[SoundPhysicsAdapted] Server resonator patches DISABLED by config");
            }

            // Send handshake to clients when they join
            api.Event.PlayerJoin += (byPlayer) =>
            {
                ServerChannel.SendPacket(new ServerHandshakePacket { ModVersion = Mod.Info.Version }, byPlayer);
                api.Logger.Debug($"[SoundPhysicsAdapted] Sent handshake to {byPlayer.PlayerName}");
            };
        }

        private void OnResonatorSyncPacket(IServerPlayer player, ResonatorSyncPacket packet)
        {
            if (player?.Entity?.World?.BlockAccessor == null) return;

            // ALWAYS store by BlockPos first - this survives chunk reload
            // The weak table entries are lost when chunk unloads (instance destroyed)
            ResonatorPatches.serverPositionsByPos[packet.Pos.Copy()] = packet.PlaybackPosition;
            ResonatorPatches.serverPausedByPos[packet.Pos.Copy()] = packet.IsPaused;
            ResonatorPatches.serverRotationsByPos[packet.Pos.Copy()] = packet.FrozenRotation;
            
            player.Entity.World.Logger.Debug($"[SoundPhysicsAdapted] Server received sync: pos={packet.Pos}, position={packet.PlaybackPosition:F2}s, isPaused={packet.IsPaused}, rotation={packet.FrozenRotation:F2}");

            BlockEntity be = player.Entity.World.BlockAccessor.GetBlockEntity(packet.Pos);
            if (be is BlockEntityResonator resonator)
            {
                // Also update weak table for current instance (used during same session)
                ResonatorPatches.savedPositions.Remove(resonator);
                ResonatorPatches.savedPositions.Add(resonator, new PlaybackPosition(packet.PlaybackPosition));

                // Update paused state for persistence
                ResonatorPatches.pausedStates.Remove(resonator);
                ResonatorPatches.pausedStates.Add(resonator, new PausedState(packet.IsPaused));
            }
        }

        /// <summary>
        /// Server relay for boombox sync: forward to nearby players (except sender).
        /// </summary>
        private void OnBoomboxSyncPacket(IServerPlayer sender, BoomboxSyncPacket packet)
        {
            if (sender?.Entity == null || serverApi == null) return;

            var senderPos = sender.Entity.Pos.XYZ;
            var allPlayers = serverApi.World.AllOnlinePlayers;

            for (int i = 0; i < allPlayers.Length; i++)
            {
                var plr = allPlayers[i] as IServerPlayer;
                if (plr == null || plr == sender || plr.Entity == null) continue;

                double dist = plr.Entity.Pos.XYZ.DistanceTo(senderPos);
                if (dist <= BOOMBOX_RELAY_RANGE)
                {
                    ServerChannel.SendPacket(packet, plr);
                }
            }
        }

        private void OnServerHandshake(ServerHandshakePacket packet)
        {
            ResonatorPatches.ServerHasMod = true;
            clientApi?.Logger.Notification($"[SoundPhysicsAdapted] Server has mod v{packet.ModVersion} — resonator pause/resume enabled.");
        }

        // Cleanup timer
        private long cleanupTimerId = 0;
        private const float CLEANUP_INTERVAL_SEC = 1.0f;

        // Occlusion update timer - AcousticsManager handles interval logic internally
        private long occlusionUpdateTimerId = 0;
        private const float OCCLUSION_UPDATE_INTERVAL_MS = 50f; // 20 times per second

        // Smoothing tick - decoupled from raycast, runs fast for all sounds
        private long smoothingTimerId = 0;

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            clientApi = api;
            ClientChannel = api.Network.GetChannel("soundphysicsadapted")
                .RegisterMessageType(typeof(ResonatorSyncPacket));  // Must register to send!

            // Listen for server handshake — confirms server has the mod
            ClientChannel.SetMessageHandler<ServerHandshakePacket>(OnServerHandshake);

            // Listen for boombox sync from other players carrying resonators
            ClientChannel.SetMessageHandler<BoomboxSyncPacket>(BoomboxRemoteHandler.OnBoomboxSyncReceived);
            BoomboxRemoteHandler.Initialize(api);

            // In singleplayer, client and server share the same process
            // so the server-side patches already cover both sides.
            // Set the flag immediately for SP.
            if (api.IsSinglePlayer)
            {
                ResonatorPatches.ServerHasMod = true;
            }
            
            // Detect Carry On mod - it hijacks Shift+RMB for block pickup, conflicting with our resonator pause/resume
            carryOnModLoaded = api.ModLoader.Mods.Any(m => m.Info.ModID.Equals("carryon", StringComparison.OrdinalIgnoreCase));
            if (carryOnModLoaded)
            {
                api.Logger.Notification("[SoundPhysicsAdapted] Carry On mod detected - resonator pause/resume changed to Ctrl+RMB");
            }

            // Apply Harmony patches (client-side only - sounds are client-side)
            // Using manual patching since ClientMain is internal to VintagestoryLib
            try
            {
                harmony = new Harmony(HARMONY_ID);

                // Phase 1: Occlusion patches (manual - targets internal types)
                LoadSoundPatch.ApplyPatches(harmony, api);

                // Resonator patches (consolidated - logic, audio, networking)
                // Only apply if enabled in config
                if (config.EnableResonatorFix)
                {
                    // Carry On compatibility: Boombox feature
                    // MUST be applied BEFORE ResonatorPatches so its StopMusicPrefix runs first
                    // (checks pausingResonators before ResonatorPatches clears it)
                    if (carryOnModLoaded)
                    {
                        CarryOnCompatPatches.ApplyPatches(harmony, api);
                    }

                    ResonatorPatches.ApplyShared(harmony, api);
                    ResonatorPatches.ApplyClient(harmony, api);
                }
                else
                {
                    api.Logger.Notification("[SoundPhysicsAdapted] Resonator enhancements DISABLED by config");
                }

                // Phase 3: Reverb patches (disable vanilla, enable our system)
                ReverbPatch.ApplyPatches(harmony, api);

                // Phase 5A: Weather sound suppression patches
                WeatherSoundPatches.ApplyPatches(harmony, api);
            }
            catch (Exception ex)
            {
                api.Logger.Error("[SoundPhysicsAdapted] Failed to apply manual Harmony patches: " + ex.Message);
                api.Logger.Error("[SoundPhysicsAdapted] Stack: " + ex.StackTrace);
            }

            // Attribute-based patches (AudioLoaderPatch etc.) - isolated so failures
            // here can't block the manual patches above
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception ex)
            {
                api.Logger.Warning($"[SoundPhysicsAdapted] PatchAll failed (non-critical): {ex.Message}");
            }

            // Note: Universal mono downmix is handled by MonoDownmixManager +
            // LoadSoundPatch.StartPlayingAudioMonoPrefix (auto-detects positional stereo sounds)
            // and LoadSoundPatch.LoadSoundMonoPrefix (explicit requests from weather/resonator)
            
            // Phase X: Execution Trace
            if (config.EnableExecutionTracer)
            {
                soundphysicsadapted.Core.ExecutionTracer.Initialize();
                api.Logger.Notification("[SoundPhysicsAdapted] Execution tracer started. Writing to trace.csv");
            }

            // Initialize AudioPhysicsSystem (optimization: time-slicing, sky probe, static cache)
            acousticsManager = new AudioPhysicsSystem();

            // Initialize sound playback throttle (limits concurrent OpenAL sources)
            soundThrottle = new SoundPlaybackThrottle();

            // Register periodic cleanup for disposed sounds
            if (AudioRenderer.IsInitialized)
            {
                cleanupTimerId = api.Event.RegisterGameTickListener(OnCleanupTick, (int)(CLEANUP_INTERVAL_SEC * 1000));
                occlusionUpdateTimerId = api.Event.RegisterGameTickListener(OnOcclusionUpdateTick, (int)OCCLUSION_UPDATE_INTERVAL_MS);
                smoothingTimerId = api.Event.RegisterGameTickListener(OnSmoothingTick, (int)AudioRenderer.SmoothTickIntervalMs);
                api.Logger.Debug("[SoundPhysicsAdapted] Registered cleanup, occlusion, and smoothing tick handlers");

                // Optimization: Invalidate cache on block changes
                api.Event.BlockChanged += OnBlockChanged;
                api.Logger.Debug("[SoundPhysicsAdapted] Hooked BlockChanged event");
            }

            // Phase 3: Initialize reverb effect system
            if (config.EnableCustomReverb)
            {
                if (ReverbEffects.Initialize())
                {
                    api.Logger.Notification("[SoundPhysicsAdapted] Reverb effects initialized");
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Reverb effects initialization failed - reverb disabled");
                    config.EnableCustomReverb = false;
                }
            }

            // Phase 5A: Initialize weather audio manager
            if (config.EnableWeatherEnhancement && WeatherSoundPatches.IsActive)
            {
                weatherManager = new WeatherAudioManager(api);
                if (weatherManager.Initialize())
                {
                    weatherTimerId = api.Event.RegisterGameTickListener(OnWeatherTick, config.WeatherTickIntervalMs);
                    api.Logger.Notification("[SoundPhysicsAdapted] Weather audio system initialized");
                }
                else
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] Weather audio initialization failed");
                    weatherManager = null;
                }
            }

            // Register debug commands
            RegisterCommands(api);

            string reverbStatus = config.EnableCustomReverb
                ? (ReverbPatch.VanillaReverbDisabled ? "SPR-style reverb ENABLED (vanilla disabled)" : "Reverb enabled (vanilla NOT disabled)")
                : "Reverb disabled";
            api.Logger.Notification($"[SoundPhysicsAdapted] Initialized - Occlusion enabled, {reverbStatus}");
        }

        /// <summary>
        /// Periodic cleanup of disposed sound filters
        /// </summary>
        private void OnCleanupTick(float dt)
        {
            AudioRenderer.CleanupDisposed();
        }

        /// <summary>
        /// Fast 25ms tick - smooths all filter values toward their targets.
        /// Decoupled from raycast so convergence is consistent regardless of distance.
        /// </summary>
        private void OnSmoothingTick(float dt)
        {
            if (!config.Enabled || !AudioRenderer.IsInitialized)
                return;

            AudioRenderer.SmoothAll();
        }

        /// <summary>
        /// Phase 5A: Update weather audio (~100ms tick).
        /// Reads VS weather state, applies LPF to replacement sounds.
        /// </summary>
        private void OnWeatherTick(float dt)
        {
            if (!config.Enabled || !config.EnableWeatherEnhancement)
                return;

            weatherManager?.OnGameTick(dt);
        }

        /// <summary>
        /// Recalculate occlusion for sounds using AudioPhysicsSystem.
        /// All sounds checked every tick; raycast gated by distance-based intervals.
        /// </summary>
        private void OnOcclusionUpdateTick(float dt)
        {
            if (!config.Enabled || clientApi?.World?.Player?.Entity == null)
                return;

            // Always update underwater state
            UpdateUnderwaterState();

            var player = clientApi.World.Player.Entity;
            Vec3d currentPos = player.Pos.XYZ.Add(player.LocalEyePos);

            // AudioPhysicsSystem: all sounds every tick, raycast gated by distance intervals
            long currentTimeMs = clientApi.World.ElapsedMilliseconds;
            acousticsManager?.Update(currentPos, clientApi.World.BlockAccessor, currentTimeMs);
        }

        private void RegisterCommands(ICoreClientAPI api)
        {
            api.ChatCommands.Create("soundphysics")
                .WithDescription("Sound Physics Adapted commands")
                .BeginSubCommand("debug")
                    .WithDescription("Toggle master debug mode (gates all sub-debug flags)")
                    .HandleWith((args) =>
                    {
                        config.DebugMode = !config.DebugMode;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        string status = config.DebugMode
                            ? $"[SoundPhysicsAdapted] Debug mode: ON\n" +
                              $"  Sub-debug flags (edit config or use commands):\n" +
                              $"  Occlusion: {(config.DebugOcclusion ? "ON" : "off")}\n" +
                              $"  Reverb: {(config.DebugReverb ? "ON" : "off")}\n" +
                              $"  SoundPaths: {(config.DebugSoundPaths ? "ON" : "off")}\n" +
                              $"  Weather: {(config.DebugWeather ? "ON" : "off")}\n" +
                              $"  WeatherViz: {(config.DebugWeatherVisualization ? "ON" : "off")}\n" +
                              $"  PositionalWeather: {(config.DebugPositionalWeather ? "ON" : "off")}\n" +
                              $"  Resonator: {(config.DebugResonator ? "ON" : "off")}\n" +
                              $"  Verbose: {(config.DebugVerbose ? "ON" : "off")}"
                            : "[SoundPhysicsAdapted] Debug mode: OFF (all sub-debug logging disabled)";
                        return TextCommandResult.Success(status);
                    })
                .EndSubCommand()
                .BeginSubCommand("status")
                    .WithDescription("Show current config")
                    .HandleWith((args) =>
                    {
                        return TextCommandResult.Success(
                            $"[SoundPhysicsAdapted] Config:\n" +
                            $"  Enabled: {config.Enabled}\n" +
                            $"  DebugMode: {config.DebugMode}\n" +
                            $"  MaxOcclusion: {config.MaxOcclusion}\n" +
                            $"  OcclusionPerBlock: {config.OcclusionPerSolidBlock}\n" +
                            $"  BlockAbsorption: {config.BlockAbsorption}\n" +
                            $"  PermeationBase: {config.PermeationBase}\n" +
                            $"  PermeationOccThreshold: {config.PermeationOcclusionThreshold}\n" +
                            $"  MaxDistance: {config.MaxSoundDistance}\n" +
                            $"  MinFilter: {config.MinLowPassFilter}\n" +
                            $"  OcclusionVariation: {config.OcclusionVariation} (0=strict, 0.35=soft)"
                        );
                    })
                .EndSubCommand()
                .BeginSubCommand("toggle")
                    .WithDescription("Enable/disable sound physics")
                    .HandleWith((args) =>
                    {
                        config.Enabled = !config.Enabled;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Sound physics: {(config.Enabled ? "ENABLED" : "DISABLED")}");
                    })
                .EndSubCommand()
                .BeginSubCommand("set")
                    .WithDescription("Set config value: .soundphysics set <param> <value>")
                    .WithArgs(api.ChatCommands.Parsers.Word("param"), api.ChatCommands.Parsers.Float("value"))
                    .HandleWith((args) =>
                    {
                        string param = (string)args[0];
                        float value = (float)args[1];
                        
                        switch (param.ToLower())
                        {
                            case "maxocclusion":
                                config.MaxOcclusion = value;
                                break;
                            case "occlusionperblock":
                            case "occlusion":
                                config.OcclusionPerSolidBlock = value;
                                break;
                            case "absorption":
                            case "blockabsorption":
                                config.BlockAbsorption = value;
                                break;
                            case "maxdistance":
                            case "distance":
                                config.MaxSoundDistance = value;
                                break;
                            case "minfilter":
                                config.MinLowPassFilter = value;
                                break;
                            case "maxrays":
                                config.MaxOcclusionRays = (int)value;
                                break;
                            case "variation":
                            case "occlusionvariation":
                                config.OcclusionVariation = value;
                                break;
                            case "permeation":
                            case "permeationbase":
                                config.PermeationBase = value;
                                break;
                            case "permeationthreshold":
                            case "permthreshold":
                                config.PermeationOcclusionThreshold = value;
                                break;
                            default:
                                return TextCommandResult.Error($"Unknown param: {param}\nValid: maxocclusion, occlusionperblock, absorption, maxdistance, minfilter, maxrays, variation, permeation, permeationthreshold");
                        }
                        
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Set {param} = {value}");
                    })
                .EndSubCommand()
                .BeginSubCommand("reset")
                    .WithDescription("Reset config to defaults")
                    .HandleWith((args) =>
                    {
                        config = new SoundPhysicsConfig();
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        return TextCommandResult.Success("[SoundPhysicsAdapted] Config reset to defaults");
                    })
                .EndSubCommand()
                .BeginSubCommand("stats")
                    .WithDescription("Show filter statistics")
                    .HandleWith((args) =>
                    {
                        string filterStats = AudioRenderer.IsInitialized
                            ? AudioRenderer.GetStats()
                            : "Not initialized";
                        return TextCommandResult.Success(
                            $"[SoundPhysicsAdapted] Filter Stats:\n" +
                            $"  Per-sound filters: {(AudioRenderer.IsInitialized ? "ENABLED" : "DISABLED")}\n" +
                            $"  {filterStats}"
                        );
                    })
                .EndSubCommand()
                .BeginSubCommand("trace")
                    .WithDescription("Toggle the execution performance tracer")
                    .HandleWith((args) =>
                    {
                        config.EnableExecutionTracer = !config.EnableExecutionTracer;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        
                        if (config.EnableExecutionTracer)
                        {
                            soundphysicsadapted.Core.ExecutionTracer.Initialize();
                        }
                        else
                        {
                            soundphysicsadapted.Core.ExecutionTracer.Close();
                        }
                        
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Execution Tracer: {(config.EnableExecutionTracer ? "ENABLED (recording)" : "DISABLED (stopped)")}");
                    })
                .EndSubCommand()
                // Phase 3: Reverb commands
                .BeginSubCommand("reverb")
                    .WithDescription("Show current room reverb values")
                    .HandleWith((args) =>
                    {
                        if (!config.EnableCustomReverb)
                        {
                            return TextCommandResult.Success("[SoundPhysicsAdapted] Reverb is DISABLED");
                        }

                        var player = api.World?.Player?.Entity;
                        if (player == null)
                        {
                            return TextCommandResult.Error("[SoundPhysicsAdapted] Player not found");
                        }

                        Vec3d playerPos = player.Pos.XYZ.Add(player.LocalEyePos);
                        string roomStats = AcousticRaytracer.GetRoomStats(playerPos, api.World.BlockAccessor);

                        return TextCommandResult.Success(
                            $"[SoundPhysicsAdapted] {roomStats}\n" +
                            $"  ---\n" +
                            $"  Config: rays={config.ReverbRayCount}, bounces={config.ReverbBounces}, maxDist={config.ReverbMaxDistance}"
                        );
                    })
                .EndSubCommand()
                .BeginSubCommand("reverb-toggle")
                    .WithDescription("Toggle our reverb system")
                    .HandleWith((args) =>
                    {
                        config.EnableCustomReverb = !config.EnableCustomReverb;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Reverb: {(config.EnableCustomReverb ? "ENABLED" : "DISABLED")}");
                    })
                .EndSubCommand()
                .BeginSubCommand("reverb-vanilla")
                    .WithDescription("Toggle vanilla reverb (for comparison)")
                    .HandleWith((args) =>
                    {
                        config.DisableVanillaReverb = !config.DisableVanillaReverb;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Vanilla reverb: {(config.DisableVanillaReverb ? "DISABLED" : "ENABLED (for comparison)")}");
                    })
                .EndSubCommand()
                .BeginSubCommand("reverb-debug")
                    .WithDescription("Toggle reverb debug logging (requires debug mode)")
                    .HandleWith((args) =>
                    {
                        config.DebugReverb = !config.DebugReverb;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        string gate = config.DebugMode ? "" : " (DebugMode is OFF — enable with /soundphysics debug)";
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Reverb debug: {(config.DebugReverb ? "ON" : "OFF")}{gate}");
                    })
                .EndSubCommand()
                .BeginSubCommand("occlusion-debug")
                    .WithDescription("Toggle occlusion debug logging (requires debug mode)")
                    .HandleWith((args) =>
                    {
                        config.DebugOcclusion = !config.DebugOcclusion;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        string gate = config.DebugMode ? "" : " (DebugMode is OFF — enable with /soundphysics debug)";
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Occlusion debug: {(config.DebugOcclusion ? "ON" : "OFF")}{gate}");
                    })
                .EndSubCommand()
                .BeginSubCommand("debugpaths")
                    .WithDescription("Toggle sound path debug logging (requires debug mode)")
                    .HandleWith((args) =>
                    {
                        config.DebugSoundPaths = !config.DebugSoundPaths;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        string gate = config.DebugMode ? "" : " (DebugMode is OFF — enable with /soundphysics debug)";
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Sound paths debug: {(config.DebugSoundPaths ? "ON" : "OFF")}{gate}");
                    })
                .EndSubCommand()
                .BeginSubCommand("acoustics")
                    .WithDescription("Show AudioPhysicsSystem optimization stats")
                    .HandleWith((args) =>
                    {
                        string stats = acousticsManager != null
                            ? acousticsManager.GetStats()
                            : "Not initialized";
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Acoustics: {stats}");
                    })
                .EndSubCommand()
                // Phase 5A: Weather commands
                .BeginSubCommand("weather")
                    .WithDescription("Show weather audio status")
                    .HandleWith((args) =>
                    {
                        string status = weatherManager != null
                            ? weatherManager.GetStatus()
                            : "Weather audio: NOT INITIALIZED (check EnableWeatherEnhancement in config)";
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] {status}");
                    })
                .EndSubCommand()
                .BeginSubCommand("weather-toggle")
                    .WithDescription("Toggle weather audio enhancement")
                    .HandleWith((args) =>
                    {
                        config.EnableWeatherEnhancement = !config.EnableWeatherEnhancement;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        if (!config.EnableWeatherEnhancement)
                        {
                            weatherManager?.OnGameTick(0); // Triggers StopAll path
                        }
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Weather enhancement: {(config.EnableWeatherEnhancement ? "ENABLED" : "DISABLED")}");
                    })
                .EndSubCommand()
                .BeginSubCommand("weather-debug")
                    .WithDescription("Toggle weather debug logging (requires debug mode)")
                    .HandleWith((args) =>
                    {
                        config.DebugWeather = !config.DebugWeather;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        string gate = config.DebugMode ? "" : " (DebugMode is OFF — enable with /soundphysics debug)";
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Weather debug: {(config.DebugWeather ? "ON" : "OFF")}{gate}");
                    })
                .EndSubCommand()
                .BeginSubCommand("weather-viz")
                    .WithDescription("Toggle weather DDA visualization (block highlights showing detection pipeline)")
                    .HandleWith((args) =>
                    {
                        config.DebugWeatherVisualization = !config.DebugWeatherVisualization;
                        api.StoreModConfig(config, "soundphysicsadapted.json");
                        string legend = config.DebugWeatherVisualization
                            ? "\nSky: Blue=covered Yellow=exposed | Paths: White=confirmed DimOrange=over-budget Red=blocked Orange=partial Cyan=neighbor | Audio: Magenta=source"
                            : "";
                        return TextCommandResult.Success($"[SoundPhysicsAdapted] Weather visualization: {(config.DebugWeatherVisualization ? "ON" : "OFF")}{legend}");
                    })
                .EndSubCommand();
        }

        public override void Dispose()
        {
            // Unregister tick listeners
            if (clientApi != null)
            {
                if (cleanupTimerId != 0)
                {
                    clientApi.Event.UnregisterGameTickListener(cleanupTimerId);
                    cleanupTimerId = 0;
                }
                if (occlusionUpdateTimerId != 0)
                {
                    clientApi.Event.UnregisterGameTickListener(occlusionUpdateTimerId);
                    occlusionUpdateTimerId = 0;
                }
                if (smoothingTimerId != 0)
                {
                    clientApi.Event.UnregisterGameTickListener(smoothingTimerId);
                    smoothingTimerId = 0;
                }
                if (weatherTimerId != 0)
                {
                    clientApi.Event.UnregisterGameTickListener(weatherTimerId);
                    weatherTimerId = 0;
                }

                // Cleanup Carry On boombox feature
                if (carryOnModLoaded)
                {
                    CarryOnCompatPatches.Cleanup();
                }

                // Cleanup remote boombox sounds from other players
                BoomboxRemoteHandler.Cleanup();

                clientApi.Event.BlockChanged -= OnBlockChanged;
            }

            // Dispose AudioPhysicsSystem
            acousticsManager?.Dispose();
            acousticsManager = null;

            // Dispose sound throttle
            soundThrottle?.Dispose();
            soundThrottle = null;

            // Phase 5A: Dispose weather audio
            weatherManager?.Dispose();
            weatherManager = null;

            // Dispose all per-sound filters
            AudioRenderer.Dispose();

            // Phase 3: Dispose reverb effects
            ReverbEffects.Dispose();

            // Clear mono downmix cache
            MonoDownmixManager.ClearCache();

            // Unpatch Harmony
            harmony?.UnpatchAll(HARMONY_ID);
            serverHarmony?.UnpatchAll(HARMONY_ID + ".server");

            // Reset server detection flag
            ResonatorPatches.ServerHasMod = false;

            // Dispose sound override manager
            Core.SoundOverrideManager.Dispose();
            
            soundphysicsadapted.Core.ExecutionTracer.Close();

            base.Dispose();
        }

        // === Rate-limited debug logging ===
        // Prevents debug mode from generating 1.5GB+ of logs in heavy scenes.
        // Normal: 200/sec, Verbose: 5000/sec (higher cap for DDA-level debugging).
        private static int debugLogCount = 0;
        private static int debugLogSuppressed = 0;
        private static long debugLogWindowStart = 0;
        private const int DEBUG_LOG_MAX_PER_SECOND = 200;
        private const int DEBUG_VERBOSE_MAX_PER_SECOND = 5000;
        private const long DEBUG_LOG_WINDOW_MS = 1000;

        private static int EffectiveLogLimit => config?.DebugVerbose == true
            ? DEBUG_VERBOSE_MAX_PER_SECOND
            : DEBUG_LOG_MAX_PER_SECOND;

        /// <summary>
        /// Core rate-limited logging. All debug methods delegate here.
        /// Handles window check, suppression counter, and flush summary.
        /// </summary>
        private static void RateLimitedLog(string message, string prefix = null)
        {
            long now = clientApi.ElapsedMilliseconds;
            if (now - debugLogWindowStart >= DEBUG_LOG_WINDOW_MS)
            {
                // New window - flush suppression summary from previous window
                if (debugLogSuppressed > 0)
                {
                    clientApi.Logger.Debug($"[SoundPhysicsAdapted] [Rate limit] Suppressed {debugLogSuppressed} debug messages in last {DEBUG_LOG_WINDOW_MS}ms");
                }
                debugLogCount = 0;
                debugLogSuppressed = 0;
                debugLogWindowStart = now;
            }

            if (debugLogCount < EffectiveLogLimit)
            {
                debugLogCount++;
                clientApi.Logger.Debug(prefix != null
                    ? $"[SoundPhysicsAdapted] {prefix}{message}"
                    : $"[SoundPhysicsAdapted] {message}");
            }
            else
            {
                debugLogSuppressed++;
            }
        }

        /// <summary>
        /// Debug logging helper - only logs when DebugMode is enabled.
        /// Rate-limited to prevent log flooding in heavy scenes.
        /// Shows occlusion results, path resolution, filter values.
        /// </summary>
        public static void DebugLog(string message)
        {
            if (config?.DebugMode != true || clientApi == null) return;
            RateLimitedLog(message);
        }

        /// <summary>
        /// Occlusion-specific debug logging - per-sound occlusion results.
        /// Only logs when BOTH DebugMode AND DebugOcclusion are enabled.
        /// </summary>
        public static void OcclusionDebugLog(string message)
        {
            if (config?.DebugMode != true || config?.DebugOcclusion != true || clientApi == null) return;
            RateLimitedLog(message, "[Occlusion] ");
        }

        /// <summary>
        /// Verbose debug logging - per-block DDA hits, individual ray steps.
        /// Only logs when DebugMode, DebugOcclusion AND DebugVerbose are all enabled.
        /// WARNING: Generates 300+ lines per sound per update. Use sparingly.
        /// </summary>
        public static void VerboseDebugLog(string message)
        {
            if (config?.DebugMode != true || config?.DebugOcclusion != true || config?.DebugVerbose != true || clientApi == null) return;
            RateLimitedLog(message);
        }

        /// <summary>
        /// Reverb-specific debug logging - uses DebugReverb flag.
        /// Requires DebugMode=true as master gate.
        /// Rate-limited via shared counter.
        /// </summary>
        public static void ReverbDebugLog(string message)
        {
            if (config?.DebugMode != true || config?.DebugReverb != true || clientApi == null) return;
            RateLimitedLog(message);
        }

        /// <summary>
        /// Resonator-specific debug logging - uses DebugResonator flag.
        /// Requires DebugMode=true as master gate.
        /// Shows pause/resume events, Carry On pickup/placement, boombox state.
        /// Rate-limited via shared counter.
        /// </summary>
        public static void ResonatorDebugLog(string message)
        {
            if (config?.DebugMode != true || config?.DebugResonator != true || clientApi == null) return;
            RateLimitedLog(message, "[Resonator] ");
        }

        /// <summary>
        /// Always log (for important events)
        /// </summary>
        public static void Log(string message)
        {
            clientApi?.Logger.Notification($"[SoundPhysicsAdapted] {message}");
        }

        // Submersion state cache (updated each tick)
        private static bool isPlayerUnderwater = false;
        private static bool isPlayerInLava = false;

        /// <summary>
        /// Check if player is currently underwater (head submerged in water).
        /// Cached and updated in tick handler for performance.
        /// </summary>
        public static bool IsPlayerUnderwater => isPlayerUnderwater;

        /// <summary>
        /// Check if player is currently submerged in lava.
        /// Cached and updated in tick handler for performance.
        /// </summary>
        public static bool IsPlayerInLava => isPlayerInLava;

        /// <summary>
        /// Check if player is submerged in any liquid (water or lava).
        /// </summary>
        public static bool IsPlayerSubmerged => isPlayerUnderwater || isPlayerInLava;

        /// <summary>
        /// Get the submersion filter multiplier (water or lava).
        /// Returns 1.0 if not submerged or ReplaceVanillaLowpass is disabled.
        /// </summary>
        public static float GetUnderwaterMultiplier()
        {
            return GetUnderwaterMultiplier(isMusic: false);
        }

        /// <summary>
        /// Get the submersion filter multiplier with music handling.
        /// Lava uses heavier filter values when EnableLavaFilter is true.
        /// </summary>
        public static float GetUnderwaterMultiplier(bool isMusic)
        {
            if (!config.ReplaceVanillaLowpass)
                return 1.0f;

            // Lava submersion (heavier filter)
            if (isPlayerInLava)
            {
                if (!config.EnableLavaFilter)
                    return isPlayerUnderwater ? config.UnderwaterFilterValue : 1.0f;
                if (isMusic && !config.UnderwaterFilterAffectsMusic)
                    return 1.0f;
                return config.LavaFilterValue;
            }

            // Water submersion
            if (!isPlayerUnderwater)
                return 1.0f;
            if (isMusic && !config.UnderwaterFilterAffectsMusic)
                return 1.0f;
            return config.UnderwaterFilterValue;
        }

        /// <summary>
        /// Get the submersion pitch offset (water or lava).
        /// Returns 0 if not submerged or ReplaceVanillaLowpass is disabled.
        /// </summary>
        public static float GetUnderwaterPitchOffset()
        {
            if (!config.ReplaceVanillaLowpass)
                return 0f;

            if (isPlayerInLava && config.EnableLavaFilter)
                return config.LavaPitchOffset;

            if (!isPlayerUnderwater)
                return 0f;

            return config.UnderwaterPitchOffset;
        }

        /// <summary>
        /// Get the submersion reverb cutoff multiplier.
        /// Returns 1.0 if not submerged.
        /// </summary>
        public static float GetSubmersionReverbCutoff()
        {
            if (isPlayerInLava && config.EnableLavaFilter)
                return config.LavaReverbCutoff;
            if (isPlayerUnderwater)
                return config.UnderwaterReverbCutoff;
            return 1.0f;
        }

        /// <summary>
        /// Get the submersion reverb gain multiplier.
        /// Returns 1.0 if not submerged.
        /// </summary>
        public static float GetSubmersionReverbMultiplier()
        {
            if (isPlayerInLava && config.EnableLavaFilter)
                return config.LavaReverbMultiplier;
            if (isPlayerUnderwater)
                return config.UnderwaterReverbMultiplier;
            return 1.0f;
        }

        /// <summary>
        /// Update submersion state - called from tick handler.
        /// Uses LiquidCode to distinguish water from lava.
        /// </summary>
        private void UpdateUnderwaterState()
        {
            if (clientApi?.World?.Player?.Entity == null)
            {
                isPlayerUnderwater = false;
                isPlayerInLava = false;
                return;
            }

            var player = clientApi.World.Player.Entity;
            Vec3d eyePos = player.Pos.XYZ.Add(player.LocalEyePos);

            // Check fluid layer at eye position for water/lava detection.
            // Using BlockLayersAccess.Fluid ensures we detect water even when
            // waterlogged blocks (Milfoil, seaweed, kelp, etc.) occupy the solid layer.
            BlockPos blockPos = new BlockPos((int)eyePos.X, (int)eyePos.Y, (int)eyePos.Z);
            var fluidBlock = clientApi.World.BlockAccessor.GetBlock(blockPos, BlockLayersAccess.Fluid);

            bool wasUnderwater = isPlayerUnderwater;
            bool wasInLava = isPlayerInLava;

            // Use LiquidCode to distinguish liquid types
            // Water: "water", "saltwater", "seawater"
            // Lava: "lava"
            // Lava also uses block.Code.PathStartsWith("lava") in vanilla
            string liquidCode = fluidBlock?.LiquidCode;
            bool isLiquid = fluidBlock != null && fluidBlock.IsLiquid();

            if (isLiquid && liquidCode == "lava")
            {
                isPlayerUnderwater = false;
                isPlayerInLava = true;
            }
            else if (isLiquid)
            {
                // water, saltwater, seawater, boilingwater, etc.
                isPlayerUnderwater = true;
                isPlayerInLava = false;
            }
            else
            {
                // Also check solid layer for lava (some lava blocks use solid layer)
                var solidBlock = clientApi.World.BlockAccessor.GetBlock(blockPos, BlockLayersAccess.Solid);
                string blockPath = solidBlock?.Code?.Path;
                if (blockPath != null && blockPath.StartsWith("lava"))
                {
                    isPlayerUnderwater = false;
                    isPlayerInLava = true;
                }
                else
                {
                    isPlayerUnderwater = false;
                    isPlayerInLava = false;
                }
            }

            // On state change, update ALL playing sounds (including music)
            bool stateChanged = (isPlayerUnderwater != wasUnderwater) || (isPlayerInLava != wasInLava);
            if (stateChanged)
            {
                if (isPlayerInLava)
                    DebugLog($"SUBMERSION: ENTERED lava - filter={config.LavaFilterValue:F2}, pitch={config.LavaPitchOffset:F2}");
                else if (isPlayerUnderwater)
                    DebugLog($"SUBMERSION: ENTERED water - filter={config.UnderwaterFilterValue:F2}, pitch={config.UnderwaterPitchOffset:F2}, music={config.UnderwaterFilterAffectsMusic}");
                else
                    DebugLog($"SUBMERSION: EXITED liquid");

                // Force recalculate all sounds to apply/remove submersion filter
                // This affects already-playing sounds including music
                AudioRenderer.RecalculateAllUnderwater();
            }
        }

        // Debounce block change events
        private long lastBlockChangeTimeMs = 0;
        private const long BLOCK_CHANGE_DEBOUNCE_MS = 200;

        /// <summary>
        /// Hook for block changes to invalidate acoustics cache.
        /// Debounced to avoid thrashing during chunk loads or large updates.
        /// </summary>
        private void OnBlockChanged(BlockPos pos, Block oldBlock)
        {
            if (acousticsManager == null || clientApi == null) return;

            // Always do cell-targeted invalidation (cheap, O(1) per cell)
            acousticsManager.CellCache?.InvalidateCellAt(pos.X, pos.Y, pos.Z);

            // Always notify weather system for entry-point proximity checks (cheap, O(n*m))
            weatherManager?.NotifyBlockChanged(pos);

            long currentTime = clientApi.World.ElapsedMilliseconds;
            
            // Debounce: if we recently invalidated, don't do it again immediately.
            if (currentTime - lastBlockChangeTimeMs < BLOCK_CHANGE_DEBOUNCE_MS)
            {
                return;
            }

            lastBlockChangeTimeMs = currentTime;
            acousticsManager.InvalidateCache();
        }
    }
}
