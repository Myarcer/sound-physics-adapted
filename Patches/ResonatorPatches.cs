using HarmonyLib;
using System;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace soundphysicsadapted.Patches
{
    #region Wrapper Classes

    /// <summary>
    /// Wrapper for float value in ConditionalWeakTable (requires reference type).
    /// </summary>
    public class PlaybackPosition
    {
        public float Value;
        public PlaybackPosition(float value) { Value = value; }
    }

    /// <summary>
    /// Wrapper for boolean paused state in ConditionalWeakTable.
    /// </summary>
    public class PausedState
    {
        public bool IsPaused;
        public PausedState(bool isPaused) { IsPaused = isPaused; }
    }

    #endregion

    #region Shared Reflection Helper

    /// <summary>
    /// Centralized reflection access for BlockEntityResonator fields.
    /// Caches reflection lookups to avoid repeated AccessTools calls.
    /// </summary>
    internal static class ResonatorReflection
    {
        public static Type ResonatorType { get; private set; }
        public static FieldInfo TrackField { get; private set; }
        public static FieldInfo SoundField { get; private set; }
        public static FieldInfo RendererField { get; private set; }
        public static PropertyInfo AnimUtilProperty { get; private set; }
        
        public static bool IsInitialized { get; private set; }

        /// <summary>
        /// Initialize reflection helpers. Safe to call multiple times.
        /// </summary>
        public static bool Initialize(ICoreAPI api)
        {
            if (IsInitialized) return true;

            try
            {
                // Find BlockEntityResonator type
                ResonatorType = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityResonator");

                if (ResonatorType == null)
                {
                    // Fallback: search all assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var type = asm.GetType("Vintagestory.GameContent.BlockEntityResonator");
                        if (type != null)
                        {
                            ResonatorType = type;
                            api?.Logger.Debug($"[SoundPhysicsAdapted] Found BlockEntityResonator in {asm.FullName}");
                            break;
                        }
                    }
                }

                if (ResonatorType == null)
                {
                    api?.Logger.Warning("[SoundPhysicsAdapted] BlockEntityResonator type NOT FOUND. Resonator patches disabled.");
                    return false;
                }

                // Cache commonly used fields
                TrackField = AccessTools.Field(ResonatorType, "track");
                if (TrackField == null)
                {
                    api?.Logger.Error($"[SoundPhysicsAdapted] 'track' field NOT FOUND in {ResonatorType.FullName}");
                    return false;
                }

                // Sound field is on MusicTrack
                SoundField = AccessTools.Field(TrackField.FieldType, "Sound");
                if (SoundField == null)
                {
                    api?.Logger.Error($"[SoundPhysicsAdapted] 'Sound' field NOT FOUND in {TrackField.FieldType.FullName}");
                    return false;
                }

                // Renderer field for disc rotation capture
                RendererField = AccessTools.Field(ResonatorType, "renderer");

                // AnimUtil property for animation control
                AnimUtilProperty = ResonatorType.GetProperty("animUtil", BindingFlags.NonPublic | BindingFlags.Instance);

                IsInitialized = true;
                api?.Logger.Debug("[SoundPhysicsAdapted] ResonatorReflection initialized successfully.");
                return true;
            }
            catch (Exception ex)
            {
                api?.Logger.Error($"[SoundPhysicsAdapted] ResonatorReflection init failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get ILoadedSound from a BlockEntityResonator's track.
        /// </summary>
        public static ILoadedSound GetSound(BlockEntityResonator resonator)
        {
            if (!IsInitialized || TrackField == null || SoundField == null) return null;

            object track = TrackField.GetValue(resonator);
            if (track == null) return null;

            return SoundField.GetValue(track) as ILoadedSound;
        }

        /// <summary>
        /// Get the animUtil from a BlockEntityResonator.
        /// </summary>
        public static object GetAnimUtil(BlockEntityResonator resonator)
        {
            return AnimUtilProperty?.GetValue(resonator);
        }
    }

    #endregion

    /// <summary>
    /// Combined Resonator patches for logic, audio, and networking.
    /// Handles pause/resume, persistence, and positional audio.
    /// </summary>
    [HarmonyPatch]
    public static class ResonatorPatches
    {
        #region State Storage

        // Store playback positions (weak: survives while instance alive)
        public static System.Runtime.CompilerServices.ConditionalWeakTable<BlockEntityResonator, PlaybackPosition> savedPositions = 
            new System.Runtime.CompilerServices.ConditionalWeakTable<BlockEntityResonator, PlaybackPosition>();

        // Store paused state separately
        public static System.Runtime.CompilerServices.ConditionalWeakTable<BlockEntityResonator, PausedState> pausedStates = 
            new System.Runtime.CompilerServices.ConditionalWeakTable<BlockEntityResonator, PausedState>();

        // SERVER: Position storage by BlockPos (survives chunk reload)
        public static System.Collections.Generic.Dictionary<BlockPos, float> serverPositionsByPos = 
            new System.Collections.Generic.Dictionary<BlockPos, float>();
        public static System.Collections.Generic.Dictionary<BlockPos, bool> serverPausedByPos = 
            new System.Collections.Generic.Dictionary<BlockPos, bool>();
        public static System.Collections.Generic.Dictionary<BlockPos, float> serverRotationsByPos = 
            new System.Collections.Generic.Dictionary<BlockPos, float>();

        /// <summary>
        /// True when the server also has the mod installed.
        /// </summary>
        public static bool ServerHasMod = false;

        // Flag lists for pause/resume state tracking
        private static System.Collections.Generic.List<BlockPos> pausingResonators = 
            new System.Collections.Generic.List<BlockPos>();
        private static System.Collections.Generic.List<BlockPos> resumingResonators = 
            new System.Collections.Generic.List<BlockPos>();

        // Store animation frame when pausing
        private static System.Collections.Generic.Dictionary<string, float> savedAnimFramesByPos = 
            new System.Collections.Generic.Dictionary<string, float>();
        
        /// <summary>
        /// Flag to swap next StartTrack sound type from MusicGlitchunaffected to AmbientGlitchunaffected.
        /// Set by StartMusicPrefix, consumed by StartTrackSoundTypePrefix, safety-cleared by StartMusicPostfix.
        /// Moves the resonator from the Music volume slider to the Ambient volume slider.
        /// </summary>
        public static bool NextStartTrackUseAmbient = false;

        /// <summary>
        /// Flag to indicate we're currently doing a pause/resume action.
        /// Prevents CarryOnCompatPatches from stealing the sound.
        /// </summary>
        public static bool IsPausingOrResuming = false;
        
        /// <summary>
        /// Check if a position is currently being paused (for CarryOnCompatPatches).
        /// </summary>
        public static bool IsPositionPausing(BlockPos pos)
        {
            foreach (var p in pausingResonators)
            {
                if (p.Equals(pos)) return true;
            }
            return false;
        }
        
        /// <summary>
        /// Check if a position is currently being resumed (for CarryOnCompatPatches).
        /// </summary>
        public static bool IsPositionResuming(BlockPos pos)
        {
            foreach (var p in resumingResonators)
            {
                if (p.Equals(pos)) return true;
            }
            return false;
        }

        #endregion

        #region Patch Registration

        /// <summary>
        /// Apply patches that must run on BOTH client and server.
        /// Primarily OnInteract for pause/resume without ghost items.
        /// </summary>
        public static void ApplyShared(Harmony harmony, ICoreAPI api)
        {
            if (!ResonatorReflection.Initialize(api)) return;

            try
            {
                var onInteract = AccessTools.Method(typeof(BlockEntityResonator), "OnInteract");
                if (onInteract == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] BlockEntityResonator.OnInteract not found. Pause/resume disabled.");
                    return;
                }

                var prefix = typeof(ResonatorPatches).GetMethod("OnInteractPrefix");
                harmony.Patch(onInteract, prefix: new HarmonyMethod(prefix));
                api.Logger.Notification($"[SoundPhysicsAdapted] Resonator OnInteract patch applied ({api.Side}).");

                // StopMusic/StartMusic patches on client for animation control
                var stopMusic = AccessTools.Method(typeof(BlockEntityResonator), "StopMusic");
                if (stopMusic != null)
                {
                    var stopPrefix = typeof(ResonatorPatches).GetMethod("StopMusicPrefix");
                    var stopPostfix = typeof(ResonatorPatches).GetMethod("StopMusicPostfix");
                    harmony.Patch(stopMusic, prefix: new HarmonyMethod(stopPrefix), postfix: new HarmonyMethod(stopPostfix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Resonator StopMusic patch applied ({api.Side}).");
                }

                var startMusic = AccessTools.Method(typeof(BlockEntityResonator), "StartMusic");
                if (startMusic != null)
                {
                    var startPrefix = typeof(ResonatorPatches).GetMethod("StartMusicPrefix");
                    var startPostfix = typeof(ResonatorPatches).GetMethod("StartMusicPostfix");
                    harmony.Patch(startMusic, prefix: new HarmonyMethod(startPrefix), postfix: new HarmonyMethod(startPostfix));
                    api.Logger.Notification($"[SoundPhysicsAdapted] Resonator StartMusic patch applied ({api.Side}).");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to apply shared patches: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply patches that run on CLIENT only.
        /// Audio positioning, interaction help, renderer patches.
        /// </summary>
        public static void ApplyClient(Harmony harmony, ICoreClientAPI api)
        {
            if (!ResonatorReflection.Initialize(api)) return;

            try
            {
                // OnClientTick - positional audio override
                var tickMethod = AccessTools.Method(typeof(BlockEntityResonator), "OnClientTick");
                if (tickMethod != null)
                {
                    var prefix = typeof(ResonatorPatches).GetMethod("OnClientTickPrefix");
                    harmony.Patch(tickMethod, prefix: new HarmonyMethod(prefix));
                    api.Logger.Notification("[SoundPhysicsAdapted] Resonator OnClientTick patch applied.");
                }

                // StartTrack sound type swap: Music slider → Ambient slider
                PatchStartTrackSoundType(harmony, api);

                // Block interaction help (Shift+RMB tooltip)
                PatchBlockInteractionHelp(harmony, api);

                // Renderer patch (disc freeze) is separate class
                ResonatorRendererPatch.ApplyPatches(harmony, api);

                api.Logger.Notification("[SoundPhysicsAdapted] Resonator client patches applied.");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to apply client patches: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply patches that run on SERVER only.
        /// Currently none - server uses ApplyShared for OnInteract.
        /// </summary>
        public static void ApplyServer(Harmony harmony, ICoreAPI api)
        {
            // Server-specific patches would go here
            // Currently all server-side logic is in ApplyShared (OnInteract)
        }

        /// <summary>
        /// Patches ClientCoreAPI.StartTrack to swap MusicGlitchunaffected → AmbientGlitchunaffected
        /// when the resonator flag is set. This moves the resonator from the Music volume slider
        /// to the Ambient volume slider without changing any other logic.
        /// </summary>
        private static void PatchStartTrackSoundType(Harmony harmony, ICoreClientAPI api)
        {
            try
            {
                // api.GetType() returns ClientCoreAPI at runtime (the ICoreClientAPI implementation)
                var clientApiType = api.GetType();
                var startTrackMethod = clientApiType.GetMethod("StartTrack",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(AssetLocation), typeof(float), typeof(EnumSoundType), typeof(Action<ILoadedSound>) },
                    null);

                if (startTrackMethod == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] ClientCoreAPI.StartTrack not found. Resonator ambient slider patch disabled.");
                    return;
                }

                var prefix = typeof(ResonatorPatches).GetMethod("StartTrackSoundTypePrefix");
                harmony.Patch(startTrackMethod, prefix: new HarmonyMethod(prefix));
                api.Logger.Notification("[SoundPhysicsAdapted] Resonator ambient sound type patch applied (Music slider -> Ambient slider).");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to patch StartTrack sound type: {ex.Message}");
            }
        }

        private static void PatchBlockInteractionHelp(Harmony harmony, ICoreClientAPI api)
        {
            try
            {
                Type blockResonatorType = AccessTools.TypeByName("Vintagestory.GameContent.BlockResonator");

                if (blockResonatorType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var type = asm.GetType("Vintagestory.GameContent.BlockResonator");
                        if (type != null)
                        {
                            blockResonatorType = type;
                            break;
                        }
                    }
                }

                if (blockResonatorType == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] BlockResonator type NOT FOUND. Interaction help patch disabled.");
                    return;
                }

                var helpMethod = AccessTools.Method(blockResonatorType, "GetPlacedBlockInteractionHelp");
                if (helpMethod == null)
                {
                    api.Logger.Warning("[SoundPhysicsAdapted] GetPlacedBlockInteractionHelp method NOT FOUND.");
                    return;
                }

                var postfix = typeof(ResonatorPatches).GetMethod("GetPlacedBlockInteractionHelpPostfix");
                harmony.Patch(helpMethod, postfix: new HarmonyMethod(postfix));
                api.Logger.Notification("[SoundPhysicsAdapted] BlockResonator interaction help patch applied.");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to patch interaction help: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix for ClientCoreAPI.StartTrack — swaps MusicGlitchunaffected to AmbientGlitchunaffected
        /// when triggered by resonator's StartMusic. This moves the resonator audio from the Music
        /// volume slider to the Ambient volume slider, while vanilla music remains on the Music slider.
        /// </summary>
        public static void StartTrackSoundTypePrefix(ref EnumSoundType __2)
        {
            if (NextStartTrackUseAmbient && __2 == EnumSoundType.MusicGlitchunaffected)
            {
                __2 = EnumSoundType.AmbientGlitchunaffected;
                NextStartTrackUseAmbient = false;
            }
        }

        #endregion

        #region Client Audio Logic

        /// <summary>
        /// Prefix for OnClientTick - forces positional audio and physics-based attenuation.
        /// </summary>
        public static bool OnClientTickPrefix(object __instance, float dt)
        {
            try
            {
                var resonator = __instance as BlockEntityResonator;
                if (resonator == null) return true;

                ILoadedSound sound = ResonatorReflection.GetSound(resonator);
                if (sound == null || !sound.IsPlaying) return true;

                ICoreClientAPI capi = resonator.Api as ICoreClientAPI;
                if (capi == null) return true;

                var pos = resonator.Pos;
                Vec3d soundPos = new Vec3d(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5);

                // 1. POSITION - Set to center of block
                sound.SetPosition((float)soundPos.X, (float)soundPos.Y, (float)soundPos.Z);

                // 1b. Update AudioRenderer position for occlusion tracking
                AudioRenderer.UpdateStoredPosition(sound, soundPos);

                // 1c. CRITICAL: Ensure resonator sound is registered in activeFilters
                // The resonator goes through MusicEngine.StartTrack() which creates a non-positional
                // sound. SoundStartPrefix sees isNonPositional and returns early without registering
                // in activeFilters. This makes the sound invisible to the occlusion AND underwater pipeline.
                // Fix: register it here on first tick, after we've set the position.
                // Note: SoundType is AmbientGlitchunaffected (patched from MusicGlitchunaffected)
                // so volume is controlled by the Ambient slider, not the Music slider.
                if (!AudioRenderer.IsRegistered(sound))
                {
                    AudioRenderer.SetOcclusion(sound, 1.0f, soundPos, "resonator-music");
                    // Mark as positional so RecalculateAllUnderwater uses non-music underwater filter
                    AudioRenderer.MarkAsPositional(sound);
                    capi.Logger.Debug($"[SoundPhysicsAdapted] [Resonator] Registered sound in activeFilters for occlusion/underwater at {pos}");
                }

                // 2. VOLUME - Force to 1.0 (physics system handles attenuation)
                sound.SetVolume(1.0f);

                // 3. PITCH - Keep vanilla glitch effect
                float pitch = GameMath.Clamp(1 - capi.Render.ShaderUniforms.GlitchStrength, 0.1f, 1);
                sound.SetPitch(pitch);

                // Debug occasionally
                if (SoundPhysicsAdaptedModSystem.Config?.DebugMode == true && resonator.Api.World.ElapsedMilliseconds % 1000 < 50)
                {
                    resonator.Api.Logger.Debug($"[SoundPhysicsAdapted] Resonator active at {pos}. Vol overrides vanilla.");
                }

                return false; // Skip vanilla distance attenuation
            }
            catch
            {
                // Squelch errors in render loop
            }
            return true;
        }

        /// <summary>
        /// Postfix for interaction help - adds Shift+RMB tooltip.
        /// </summary>
        public static void GetPlacedBlockInteractionHelpPostfix(object __instance, IWorldAccessor world, BlockSelection selection, IPlayer forPlayer, ref WorldInteraction[] __result)
        {
            try
            {
                if (!ServerHasMod) return;
                if (selection?.Position == null || __result == null) return;

                var be = world.BlockAccessor.GetBlockEntity(selection.Position);
                if (be == null) return;

                var hasDiscProp = be.GetType().GetProperty("HasDisc");
                if (hasDiscProp == null) return;

                bool hasDisc = (bool)hasDiscProp.GetValue(be);
                if (!hasDisc) return;

                // Use Ctrl instead of Shift when Carry On mod is loaded (it hijacks Shift+RMB)
                string modifierKey = SoundPhysicsAdaptedModSystem.CarryOnModLoaded ? "ctrl" : "shift";
                var pauseResumeInteraction = new WorldInteraction()
                {
                    ActionLangCode = "soundphysicsadapted:blockhelp-resonator-pauseresume",
                    HotKeyCode = modifierKey,
                    MouseButton = EnumMouseButton.Right,
                };

                __result = __result.Append(pauseResumeInteraction);
            }
            catch { }
        }

        #endregion

        #region Pause/Resume Logic

        /// <summary>
        /// Prefix for OnInteract - handles Shift+RMB (or Ctrl+RMB if Carry On mod present) pause/resume on both client and server.
        /// </summary>
        public static bool OnInteractPrefix(BlockEntityResonator __instance, IWorldAccessor world, IPlayer byPlayer)
        {
            // Use Ctrl instead of Shift when Carry On mod is loaded (it hijacks Shift+RMB for block pickup)
            bool ctrlPressed = byPlayer.Entity.Controls.CtrlKey;
            bool shiftPressed = byPlayer.Entity.Controls.ShiftKey;
            bool modifierKeyPressed = SoundPhysicsAdaptedModSystem.CarryOnModLoaded 
                ? ctrlPressed 
                : shiftPressed;
            
            // Debug log modifier state
            if (SoundPhysicsAdaptedModSystem.Config?.DebugResonator == true)
            {
                world.Logger.Debug($"[SoundPhysicsAdapted] [Resonator] OnInteract: CarryOnLoaded={SoundPhysicsAdaptedModSystem.CarryOnModLoaded}, Ctrl={ctrlPressed}, Shift={shiftPressed}, modifierKeyPressed={modifierKeyPressed}");
            }
            
            if (modifierKeyPressed)
            {
                if (__instance.Inventory[0] != null && !__instance.Inventory[0].Empty)
                {
                    // CLIENT: Prepare for pause/resume
                    if (world.Side == EnumAppSide.Client)
                    {
                        // Flag to prevent CarryOnCompatPatches from stealing the sound
                        IsPausingOrResuming = true;
                        world.Logger.Debug($"[SoundPhysicsAdapted] OnInteract CLIENT: IsPausingOrResuming=true at {__instance.Pos}");
                        
                        try
                        {
                            if (__instance.IsPlaying)
                            {
                                // Capture position FIRST (before StopMusic disposes sound)
                                CaptureAndSyncPlaybackPosition(__instance, isPausing: true);
                                
                                // Now stop the sound on client - pausingResonators list prevents boombox stealing
                                world.Logger.Debug($"[SoundPhysicsAdapted] OnInteract CLIENT: Calling StopMusic for pause at {__instance.Pos}");
                                AccessTools.Method(typeof(BlockEntityResonator), "StopMusic").Invoke(__instance, null);
                            }
                            else
                            {
                                resumingResonators.Add(__instance.Pos.Copy());
                                world.Logger.Debug($"[SoundPhysicsAdapted] OnInteract CLIENT: Marked as resuming at {__instance.Pos}");
                            }
                        }
                        finally
                        {
                            IsPausingOrResuming = false;
                            world.Logger.Debug($"[SoundPhysicsAdapted] OnInteract CLIENT: IsPausingOrResuming=false");
                        }
                    }

                    // SERVER: Toggle state
                    if (world.Side == EnumAppSide.Server)
                    {
                        bool isPlaying = __instance.IsPlaying;

                        if (isPlaying)
                        {
                            pausedStates.Remove(__instance);
                            pausedStates.Add(__instance, new PausedState(true));

                            __instance.IsPlaying = false;
                            AccessTools.Method(typeof(BlockEntityResonator), "StopMusic").Invoke(__instance, null);
                            __instance.MarkDirty(true);
                        }
                        else
                        {
                            pausedStates.Remove(__instance);
                            pausedStates.Add(__instance, new PausedState(false));

                            __instance.IsPlaying = true;
                            AccessTools.Method(typeof(BlockEntityResonator), "StartMusic").Invoke(__instance, null);
                            __instance.MarkDirty(true);
                        }
                    }

                    return false; // Block vanilla to prevent ghost items
                }
            }

            // Not shift+click - vanilla handles disc insert/eject
            if (__instance.Inventory[0] != null && !__instance.Inventory[0].Empty)
            {
                // About to eject - clear saved state
                savedPositions.Remove(__instance);
                pausedStates.Remove(__instance);

                world.Logger.Debug($"[SoundPhysicsAdapted] OnInteract: Disc eject - clearing saved position ({world.Side})");

                if (world.Side == EnumAppSide.Client)
                {
                    ResonatorRendererPatch.ClearSavedRotation(__instance.Pos);
                    string posKey = $"{__instance.Pos.X},{__instance.Pos.Y},{__instance.Pos.Z}";
                    savedAnimFramesByPos.Remove(posKey);
                }

                if (world.Side == EnumAppSide.Server && __instance.Pos != null)
                {
                    BlockPos posToRemove = null;
                    foreach (var kvp in serverPositionsByPos)
                    {
                        if (kvp.Key.Equals(__instance.Pos))
                        {
                            posToRemove = kvp.Key;
                            break;
                        }
                    }
                    if (posToRemove != null)
                    {
                        serverPositionsByPos.Remove(posToRemove);
                        serverPausedByPos.Remove(posToRemove);
                    }
                }
            }

            return true;
        }

        private static void CaptureAndSyncPlaybackPosition(BlockEntityResonator resonator, bool isPausing)
        {
            if (resonator.Api?.Side != EnumAppSide.Client) return;

            ILoadedSound sound = ResonatorReflection.GetSound(resonator);
            if (sound == null) return;

            float currentPos = sound.PlaybackPosition;

            savedPositions.Remove(resonator);
            savedPositions.Add(resonator, new PlaybackPosition(currentPos));

            float frozenRotation = 0f;
            if (isPausing)
            {
                var posCopy = resonator.Pos.Copy();
                pausingResonators.Add(posCopy);
                resonator.Api.Logger.Debug($"[SoundPhysicsAdapted] CaptureAndSync: Marked as pausing at {resonator.Pos}");

                var renderer = ResonatorReflection.RendererField?.GetValue(resonator);
                if (renderer != null)
                {
                    var discRotRadField = renderer.GetType().GetField("discRotRad", BindingFlags.Public | BindingFlags.Instance);
                    var discRotRad = discRotRadField?.GetValue(renderer) as Vec3f;
                    if (discRotRad != null)
                    {
                        frozenRotation = discRotRad.Y;
                        ResonatorRendererPatch.SetSavedRotation(resonator.Pos, frozenRotation);
                        resonator.Api.Logger.Debug($"[SoundPhysicsAdapted] CaptureAndSync: Captured discRotRad.Y={frozenRotation:F2}");
                    }
                }
            }

            if (ServerHasMod && SoundPhysicsAdaptedModSystem.ClientChannel != null)
            {
                SoundPhysicsAdaptedModSystem.ClientChannel.SendPacket(new ResonatorSyncPacket()
                {
                    Pos = resonator.Pos,
                    PlaybackPosition = currentPos,
                    IsPlaying = !isPausing,
                    IsPaused = isPausing,
                    FrozenRotation = frozenRotation
                });
            }
        }

        #endregion

        #region Animation Control

        public static void StopMusicPrefix(BlockEntityResonator __instance, out bool __state)
        {
            __state = false;
            if (__instance.Api?.Side != EnumAppSide.Client) return;

            __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StopMusicPrefix: Called for {__instance.Pos}");

            bool isPausing = false;
            BlockPos foundPos = null;
            foreach (var pos in pausingResonators)
            {
                if (pos.Equals(__instance.Pos)) { isPausing = true; foundPos = pos; break; }
            }

            if (isPausing)
            {
                __state = true;
                __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StopMusicPrefix: PAUSING - will freeze animation");

                if (foundPos != null) pausingResonators.Remove(foundPos);

                var animUtil = ResonatorReflection.GetAnimUtil(__instance);
                if (animUtil != null)
                {
                    // Capture current frame
                    var animatorProp = animUtil.GetType().GetProperty("animator", BindingFlags.Public | BindingFlags.Instance);
                    var animator = animatorProp?.GetValue(animUtil);
                    if (animator != null)
                    {
                        var getStateMethod = animator.GetType().GetMethod("GetAnimationState", new[] { typeof(string) });
                        var runningAnimState = getStateMethod?.Invoke(animator, new object[] { "running" });
                        if (runningAnimState != null)
                        {
                            var currentFrameField = runningAnimState.GetType().GetField("CurrentFrame", BindingFlags.Public | BindingFlags.Instance);
                            if (currentFrameField != null)
                            {
                                float savedFrame = (float)currentFrameField.GetValue(runningAnimState);
                                string posKey = $"{__instance.Pos.X},{__instance.Pos.Y},{__instance.Pos.Z}";
                                savedAnimFramesByPos[posKey] = savedFrame;
                                __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StopMusicPrefix: Captured animation frame={savedFrame:F2}");
                            }
                        }
                    }

                    var activeAnims = animUtil.GetType().GetField("activeAnimationsByAnimCode", BindingFlags.Public | BindingFlags.Instance)?.GetValue(animUtil) as System.Collections.IDictionary;
                    if (activeAnims != null && activeAnims.Contains("running"))
                    {
                        var runningAnim = activeAnims["running"];
                        var speedField = runningAnim.GetType().GetField("AnimationSpeed", BindingFlags.Public | BindingFlags.Instance);
                        if (speedField != null)
                        {
                            speedField.SetValue(runningAnim, 0f);
                            __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StopMusicPrefix: Set running animation speed to 0");
                        }
                    }
                }
            }
        }

        public static void StopMusicPostfix(BlockEntityResonator __instance, bool __state)
        {
            if (__instance.Api?.Side != EnumAppSide.Client) return;

            if (__state)
            {
                var animUtil = ResonatorReflection.GetAnimUtil(__instance);
                if (animUtil != null)
                {
                    var activeAnims = animUtil.GetType().GetField("activeAnimationsByAnimCode", BindingFlags.Public | BindingFlags.Instance)?.GetValue(animUtil) as System.Collections.IDictionary;
                    if (activeAnims == null || !activeAnims.Contains("running"))
                    {
                        __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StopMusicPostfix: Restarting animation at speed 0");
                        var startAnimMethod = animUtil.GetType().GetMethod("StartAnimation", new[] { typeof(AnimationMetaData) });
                        if (startAnimMethod != null)
                        {
                            var animMeta = new AnimationMetaData()
                            {
                                Animation = "running",
                                Code = "running",
                                AnimationSpeed = 0f,
                                EaseOutSpeed = 8,
                                EaseInSpeed = 8
                            };
                            startAnimMethod.Invoke(animUtil, new object[] { animMeta });

                            // Restore saved frame
                            string posKey = $"{__instance.Pos.X},{__instance.Pos.Y},{__instance.Pos.Z}";
                            if (savedAnimFramesByPos.TryGetValue(posKey, out float savedFrame))
                            {
                                var animatorProp = animUtil.GetType().GetProperty("animator", BindingFlags.Public | BindingFlags.Instance);
                                var animator = animatorProp?.GetValue(animUtil);
                                if (animator != null)
                                {
                                    var getStateMethod = animator.GetType().GetMethod("GetAnimationState", new[] { typeof(string) });
                                    var runningAnimState = getStateMethod?.Invoke(animator, new object[] { "running" });
                                    if (runningAnimState != null)
                                    {
                                        var currentFrameField = runningAnimState.GetType().GetField("CurrentFrame", BindingFlags.Public | BindingFlags.Instance);
                                        currentFrameField?.SetValue(runningAnimState, savedFrame);
                                        __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StopMusicPostfix: Restored animation frame={savedFrame:F2}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Prefix for StartMusic — registers the disc's music track for mono downmix.
        /// This runs BEFORE StartMusic calls capi.StartTrack(), which enqueues the track
        /// for async loading. By the time LoadSound fires on the main thread,
        /// LoadSoundMonoPrefix will find the pending request and swap to mono.
        /// </summary>
        public static void StartMusicPrefix(BlockEntityResonator __instance)
        {
            if (__instance.Api?.Side != EnumAppSide.Client) return;
            if (__instance.Inventory?[0]?.Itemstack?.ItemAttributes == null) return;

            string trackString = __instance.Inventory[0].Itemstack.ItemAttributes["musicTrack"].AsString(null);
            if (trackString == null) return;

            // Normalize the path the same way MusicTrack.Initialize does:
            // lowercase, prefix with "music/" if not starting with "sounds", append ".ogg"
            string normalized = trackString.ToLowerInvariant();
            if (!normalized.StartsWith("sounds"))
            {
                if (!normalized.StartsWith("music/")) normalized = "music/" + normalized;
            }
            if (!normalized.EndsWith(".ogg")) normalized += ".ogg";

            AudioLoaderPatch.RequestMonoForAsset(normalized);

            // Flag: next StartTrack call should use Ambient slider instead of Music slider
            NextStartTrackUseAmbient = true;

            __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StartMusicPrefix: Registered mono request for '{normalized}', ambient slider flag set");
        }

        public static void StartMusicPostfix(BlockEntityResonator __instance)
        {
            // Safety clear - flag should already be consumed by StartTrackSoundTypePrefix
            NextStartTrackUseAmbient = false;

            if (__instance.Api?.Side != EnumAppSide.Client) return;

            __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StartMusicPostfix: Called for {__instance.Pos}");

            bool isResuming = false;
            BlockPos foundPos = null;
            foreach (var pos in resumingResonators)
            {
                if (pos.Equals(__instance.Pos)) { isResuming = true; foundPos = pos; break; }
            }

            if (isResuming)
            {
                __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StartMusicPostfix: RESUMING - setting animation speed to 1");

                if (foundPos != null) resumingResonators.Remove(foundPos);

                var animUtil = ResonatorReflection.GetAnimUtil(__instance);
                if (animUtil != null)
                {
                    var activeAnims = animUtil.GetType().GetField("activeAnimationsByAnimCode", BindingFlags.Public | BindingFlags.Instance)?.GetValue(animUtil) as System.Collections.IDictionary;
                    if (activeAnims != null && activeAnims.Contains("running"))
                    {
                        var runningAnim = activeAnims["running"];
                        var speedField = runningAnim.GetType().GetField("AnimationSpeed", BindingFlags.Public | BindingFlags.Instance);
                        speedField?.SetValue(runningAnim, 1f);
                        __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StartMusicPostfix: Set running animation speed to 1");
                    }

                    // Restore saved frame
                    string posKey = $"{__instance.Pos.X},{__instance.Pos.Y},{__instance.Pos.Z}";
                    if (savedAnimFramesByPos.TryGetValue(posKey, out float savedFrame))
                    {
                        var animatorProp = animUtil.GetType().GetProperty("animator", BindingFlags.Public | BindingFlags.Instance);
                        var animator = animatorProp?.GetValue(animUtil);
                        if (animator != null)
                        {
                            var getStateMethod = animator.GetType().GetMethod("GetAnimationState", new[] { typeof(string) });
                            var runningAnimState = getStateMethod?.Invoke(animator, new object[] { "running" });
                            if (runningAnimState != null)
                            {
                                var currentFrameField = runningAnimState.GetType().GetField("CurrentFrame", BindingFlags.Public | BindingFlags.Instance);
                                currentFrameField?.SetValue(runningAnimState, savedFrame);
                                __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] StartMusicPostfix: Restored animation frame={savedFrame:F2}");
                            }
                        }
                        savedAnimFramesByPos.Remove(posKey);
                    }
                }
            }
        }

        #endregion

        #region Persistence (Harmony Attributed)

        [HarmonyPatch(typeof(BlockEntityResonator), "GetBlockInfo")]
        [HarmonyPostfix]
        public static void GetBlockInfoPostfix(BlockEntityResonator __instance, IPlayer forPlayer, StringBuilder dsc)
        {
            if (!ServerHasMod) return;

            if (__instance.Inventory != null && !__instance.Inventory[0].Empty)
            {
                if (__instance.IsPlaying)
                {
                    dsc.AppendLine("Shift + Right click to Pause.");
                }
                else
                {
                    dsc.AppendLine("Shift + Right click to Resume.");
                    dsc.AppendLine("(Paused)");
                }
            }
        }

        [HarmonyPatch(typeof(BlockEntityResonator), "ToTreeAttributes")]
        [HarmonyPostfix]
        public static void ToTreeAttributesPostfix(BlockEntityResonator __instance, ITreeAttribute tree)
        {
            float? posToSave = null;
            bool? pausedToSave = null;

            if (savedPositions.TryGetValue(__instance, out PlaybackPosition pos) && pos != null)
            {
                posToSave = pos.Value;
            }
            else if (__instance.Api?.Side == EnumAppSide.Server && __instance.Pos != null)
            {
                foreach (var kvp in serverPositionsByPos)
                {
                    if (kvp.Key.Equals(__instance.Pos))
                    {
                        posToSave = kvp.Value;
                        __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] ToTreeAttributes: Found position {kvp.Value:F2}s from BlockPos dictionary");
                        break;
                    }
                }
            }

            if (posToSave.HasValue)
            {
                tree.SetFloat("savedPlaybackPos", posToSave.Value);
                __instance.Api?.Logger.Debug($"[SoundPhysicsAdapted] ToTreeAttributes: Saved position {posToSave.Value:F2}s");
            }
            else
            {
                tree.RemoveAttribute("savedPlaybackPos");
            }

            if (pausedStates.TryGetValue(__instance, out PausedState paused) && paused != null)
            {
                pausedToSave = paused.IsPaused;
            }
            else if (__instance.Api?.Side == EnumAppSide.Server && __instance.Pos != null)
            {
                foreach (var kvp in serverPausedByPos)
                {
                    if (kvp.Key.Equals(__instance.Pos))
                    {
                        pausedToSave = kvp.Value;
                        break;
                    }
                }
            }

            if (pausedToSave.HasValue)
            {
                tree.SetBool("isPaused", pausedToSave.Value);
            }
            else
            {
                tree.RemoveAttribute("isPaused");
            }

            // Save frozen rotation
            float? rotationToSave = null;
            if (__instance.Api?.Side == EnumAppSide.Client)
            {
                rotationToSave = ResonatorRendererPatch.GetSavedRotation(__instance.Pos);
            }
            else if (__instance.Api?.Side == EnumAppSide.Server && __instance.Pos != null)
            {
                foreach (var kvp in serverRotationsByPos)
                {
                    if (kvp.Key.Equals(__instance.Pos))
                    {
                        rotationToSave = kvp.Value;
                        break;
                    }
                }
            }

            if (rotationToSave.HasValue)
            {
                tree.SetFloat("frozenRotation", rotationToSave.Value);
            }
            else
            {
                tree.RemoveAttribute("frozenRotation");
            }
        }

        [HarmonyPatch(typeof(BlockEntityResonator), "FromTreeAttributes")]
        [HarmonyPostfix]
        public static void FromTreeAttributesPostfix(BlockEntityResonator __instance, ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            bool hasSavedPos = tree.HasAttribute("savedPlaybackPos");
            float savedPos = tree.GetFloat("savedPlaybackPos", 0f);

            worldForResolving.Logger.Debug($"[SoundPhysicsAdapted] FromTreeAttributes ({worldForResolving.Side}): hasSavedPos={hasSavedPos}, savedPos={savedPos:F2}s");

            if (hasSavedPos)
            {
                float posVal = tree.GetFloat("savedPlaybackPos");
                savedPositions.Remove(__instance);
                savedPositions.Add(__instance, new PlaybackPosition(posVal));
            }
            else
            {
                savedPositions.Remove(__instance);
            }

            if (tree.HasAttribute("isPaused"))
            {
                bool isPaused = tree.GetBool("isPaused");
                pausedStates.Remove(__instance);
                pausedStates.Add(__instance, new PausedState(isPaused));
            }
            else
            {
                pausedStates.Remove(__instance);
            }

            if (tree.HasAttribute("frozenRotation"))
            {
                float frozenRot = tree.GetFloat("frozenRotation");
                ResonatorRendererPatch.SetSavedRotation(__instance.Pos, frozenRot);
            }
            else
            {
                ResonatorRendererPatch.ClearSavedRotation(__instance.Pos);
            }
        }

        [HarmonyPatch(typeof(BlockEntityResonator), "onTrackLoaded")]
        [HarmonyPostfix]
        public static void onTrackLoadedPostfix(BlockEntityResonator __instance, ILoadedSound sound)
        {
            try
            {
                if (sound == null) return;
                if (__instance?.Api == null) return;

                bool hasPos = savedPositions.TryGetValue(__instance, out PlaybackPosition checkPos);
                __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] onTrackLoaded: length={sound.SoundLengthSeconds:F2}s, hasPosition={hasPos}");

                if (savedPositions.TryGetValue(__instance, out PlaybackPosition pos) && pos != null)
                {
                    if (pos.Value < sound.SoundLengthSeconds - 1)
                    {
                        sound.PlaybackPosition = pos.Value;
                        __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] onTrackLoaded: Restored position to {pos.Value:F2}s");
                    }
                }
            }
            catch (Exception ex)
            {
                __instance?.Api?.Logger.Debug($"[SoundPhysicsAdapted] onTrackLoaded: Caught exception: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(BlockEntityResonator), "OnClientTick")]
        [HarmonyPostfix]
        public static void OnClientTickPostfix(BlockEntityResonator __instance, float dt)
        {
            if (__instance.Api.Side != EnumAppSide.Client) return;

            ILoadedSound sound = ResonatorReflection.GetSound(__instance);
            if (sound != null && sound.IsPlaying)
            {
                if (__instance.Api.World.ElapsedMilliseconds % 2000 < 50)
                {
                    savedPositions.Remove(__instance);
                    savedPositions.Add(__instance, new PlaybackPosition(sound.PlaybackPosition));

                    if (ServerHasMod && SoundPhysicsAdaptedModSystem.ClientChannel != null)
                    {
                        SoundPhysicsAdaptedModSystem.ClientChannel.SendPacket(new ResonatorSyncPacket()
                        {
                            Pos = __instance.Pos,
                            PlaybackPosition = sound.PlaybackPosition,
                            IsPlaying = true,
                            IsPaused = false
                        });
                    }
                }
            }
        }

        [HarmonyPatch(typeof(BlockEntityResonator), "Initialize")]
        [HarmonyPostfix]
        public static void InitializePostfix(BlockEntityResonator __instance, ICoreAPI api)
        {
            if (api.Side != EnumAppSide.Client) return;
            if (!__instance.IsPlaying) return;

            object trackObj = ResonatorReflection.TrackField?.GetValue(__instance);
            if (trackObj != null) return;

            if (__instance.Inventory[0] == null || __instance.Inventory[0].Empty) return;

            api.Logger.Debug($"[SoundPhysicsAdapted] Initialize: IsPlaying=true track=null, scheduling deferred StartMusic");

            // Use TryEnqueue which is safer during pause and won't crash
            try
            {
                var capi = api as ICoreClientAPI;
                
                // Schedule with a delay that's safer to call during pause
                capi?.World.RegisterCallback((dt) =>
                {
                    try
                    {
                        if (__instance.Api == null) return;
                        object trackAfterDefer = ResonatorReflection.TrackField?.GetValue(__instance);
                        if (trackAfterDefer != null) return;
                        if (!__instance.IsPlaying) return;

                        __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] Initialize (delayed): Calling StartMusic");
                        AccessTools.Method(typeof(BlockEntityResonator), "StartMusic")?.Invoke(__instance, null);
                        __instance.Api.World?.BlockAccessor?.MarkBlockDirty(__instance.Pos);
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Debug($"[SoundPhysicsAdapted] Initialize (delayed): Caught exception: {ex.Message}");
                    }
                }, 200);
            }
            catch
            {
                // Silently ignore if registration fails (e.g., game paused)
            }
        }

        [HarmonyPatch(typeof(BlockEntityResonator), "OnBlockUnloaded")]
        [HarmonyPrefix]
        public static void OnBlockUnloadedPrefix(BlockEntityResonator __instance)
        {
            if (__instance.Api?.Side != EnumAppSide.Client) return;
            if (!__instance.IsPlaying) return;

            ILoadedSound sound = ResonatorReflection.GetSound(__instance);
            if (sound == null) return;

            float currentPos = sound.PlaybackPosition;

            __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] OnBlockUnloaded: Capturing position {currentPos:F2}s");

            savedPositions.Remove(__instance);
            savedPositions.Add(__instance, new PlaybackPosition(currentPos));

            if (ServerHasMod && SoundPhysicsAdaptedModSystem.ClientChannel != null)
            {
                __instance.Api.Logger.Debug($"[SoundPhysicsAdapted] OnBlockUnloaded: Sending sync packet pos={__instance.Pos}");
                SoundPhysicsAdaptedModSystem.ClientChannel.SendPacket(new ResonatorSyncPacket()
                {
                    Pos = __instance.Pos,
                    PlaybackPosition = currentPos,
                    IsPlaying = true,
                    IsPaused = false
                });
            }
        }

        #endregion
    }
}
