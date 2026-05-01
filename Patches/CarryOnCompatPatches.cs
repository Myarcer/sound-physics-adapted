using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace soundphysicsadapted.Patches
{
    /// <summary>
    /// Carry On mod compatibility patches - Boombox feature.
    /// When player picks up a playing resonator with Carry On, the music continues
    /// playing from the player's position (like carrying a boombox).
    /// Seamless handoff in both directions - no audio gaps on pickup or placement.
    /// 
    /// KEY INSIGHT: Carry On removes blocks via SetBlock(0, pos) on the CLIENT.
    /// This does NOT call BlockEntityResonator.StopMusic() - the sound is disposed
    /// through block entity cleanup, not StopMusic. Therefore we must PRE-STEAL
    /// the sound during Carry On's ~800ms pickup animation, before the block is removed.
    /// 
    /// Sound positioning:
    /// - Hands slot: ~0.5 blocks in front of player at chest height (avoids L/R pan on camera turn)
    /// - Back slot: ~0.4 blocks behind player at back height
    /// 
    /// Only loaded when Carry On mod is detected.
    /// </summary>
    public static class CarryOnCompatPatches
    {
        #region State

        /// <summary>
        /// The "stolen" sound that we're keeping alive while carried.
        /// </summary>
        private static ILoadedSound activeBoomboxSound = null;

        /// <summary>
        /// Was the resonator playing when picked up? (vs paused)
        /// </summary>
        private static bool wasPlayingWhenPickedUp = false;

        /// <summary>
        /// Original position of the resonator before pickup (for logging/debug).
        /// </summary>
        private static BlockPos originalResonatorPos = null;

        /// <summary>
        /// Client API reference for tick registration.
        /// </summary>
        private static ICoreClientAPI capi = null;

        /// <summary>
        /// Tick listener ID for position updates.
        /// </summary>
        private static long tickListenerId = 0;

        /// <summary>
        /// Carry On's attribute path for carried blocks.
        /// </summary>
        private const string CARRYON_ATTRIBUTE_ID = "carryon:Carried";

        /// <summary>
        /// Which carry slot the resonator is currently in.
        /// </summary>
        private enum CarrySlotType { None, Hands, Back }
        private static CarrySlotType currentCarrySlot = CarrySlotType.None;

        // --- Pre-steal state ---

        /// <summary>
        /// Sound pre-stolen from a resonator during Carry On pickup animation.
        /// Waiting for carry confirmation before promoting to activeBoomboxSound.
        /// </summary>
        private static ILoadedSound pendingBoomboxSound = null;

        /// <summary>
        /// The resonator MusicTrack object captured during pre-steal.
        /// Used to suppress vanilla music while the boombox is being carried.
        /// </summary>
        private static object pendingBoomboxTrack = null;

        /// <summary>
        /// Active carried boombox MusicTrack placeholder.
        /// </summary>
        private static object activeBoomboxTrack = null;

        /// <summary>
        /// Position of the resonator we pre-stole from.
        /// </summary>
        private static BlockPos pendingPickupPos = null;

        /// <summary>
        /// Timestamp when we pre-stole the sound (for timeout).
        /// </summary>
        private static long pendingStolenTimeMs = 0;

        /// <summary>
        /// Block entity we stole from (for returning sound on cancel).
        /// </summary>
        private static BlockEntityResonator pendingSourceResonator = null;

        /// <summary>
        /// Throttle counter for "Has carriedAttr but no resonator in Hands/Back" log spam.
        /// </summary>
        private static int noSlotLogCount = 0;

        // --- Multiplayer sync state ---

        /// <summary>
        /// The music track asset path captured during pre-steal (e.g. "music/lament").
        /// Sent to server for relay to remote clients so they can create their own sound.
        /// </summary>
        private static string activeBoomboxTrackLocation = null;

        /// <summary>
        /// Timestamp of last sync packet sent to server.
        /// </summary>
        private static long lastSyncTimeMs = 0;

        /// <summary>
        /// How often to send boombox sync packets to the server (ms).
        /// </summary>
        private const long SYNC_INTERVAL_MS = 500;

        /// <summary>
        /// Last computed sound position (cached for sync packets).
        /// </summary>
        private static float lastSoundX, lastSoundY, lastSoundZ;

        /// <summary>
        /// Key used to register the carried boombox with resonator music suppression.
        /// </summary>
        private const string BOOMBOX_SUPPRESSION_KEY = "carryon-boombox";

        #endregion

        #region Public Queries

        /// <summary>
        /// Returns true if a pre-steal or active boombox is in progress for the given position.
        /// Used by OnClientTickPostfix to avoid false "track finished naturally" detection
        /// when the sound field was nulled by pre-steal rather than actual track completion.
        /// </summary>
        public static bool HasPendingOrActiveSteal(BlockPos pos)
        {
            // Active boombox — sound is being carried
            if (activeBoomboxSound != null) return true;

            // Pending pre-steal for this specific position
            if (pendingBoomboxSound != null && pendingPickupPos != null && pos.Equals(pendingPickupPos))
                return true;

            return false;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Apply Carry On compatibility patches. Only call when Carry On is detected.
        /// </summary>
        public static void ApplyPatches(Harmony harmony, ICoreClientAPI api)
        {
            capi = api;

            // Check if feature is enabled in config
            var config = SoundPhysicsAdaptedModSystem.Config;
            if (config?.EnableCarryOnCompat != true)
            {
                api.Logger.Notification("[SoundPhysicsAdapted] Carry On boombox feature DISABLED by config");
                return;
            }

            try
            {
                // Patch BlockEntityResonator.StopMusic - prevent disposal when we've pre-stolen
                var stopMusicMethod = AccessTools.Method(typeof(BlockEntityResonator), "StopMusic");
                if (stopMusicMethod != null)
                {
                    var prefix = typeof(CarryOnCompatPatches).GetMethod(nameof(StopMusicPrefix), BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(stopMusicMethod, prefix: new HarmonyMethod(prefix));
                    if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                        SoundPhysicsAdaptedModSystem.ResonatorDebugLog("CarryOn compat: Patched StopMusic");
                }

                // Patch BlockEntityResonator.StartMusic - intercept to inject existing sound
                var startMusicMethod = AccessTools.Method(typeof(BlockEntityResonator), "StartMusic");
                if (startMusicMethod != null)
                {
                    var prefix = typeof(CarryOnCompatPatches).GetMethod(nameof(StartMusicPrefix), BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(startMusicMethod, prefix: new HarmonyMethod(prefix));
                    if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                        SoundPhysicsAdaptedModSystem.ResonatorDebugLog("CarryOn compat: Patched StartMusic");
                }

                // Register tick listener to detect carry state changes and pre-steal sound
                api.Event.RegisterGameTickListener(OnCarryCheckTick, 100);
                api.Logger.Notification("[SoundPhysicsAdapted] Carry On boombox feature enabled");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[SoundPhysicsAdapted] Failed to apply CarryOn compat patches: {ex.Message}");
            }
        }

        #endregion

        #region Carry Detection & Pre-Steal
        
        /// <summary>
        /// Check if a resonator at this position is being paused or resumed by our system.
        /// </summary>
        private static bool IsPausingResonator(BlockPos pos)
        {
            return ResonatorPatches.IsPositionPausing(pos) || ResonatorPatches.IsPositionResuming(pos);
        }

        /// <summary>
        /// Track whether we were carrying a resonator last tick.
        /// </summary>
        private static bool wasCarryingResonator = false;

        /// <summary>
        /// Check for carry state changes every 100ms.
        /// Also handles pre-stealing sound during Carry On pickup animation.
        /// 
        /// Flow:
        /// 1. Carry On's pickup animation takes ~800ms (configurable)
        /// 2. During animation, "carryKeyHeld" attribute is true on entity
        /// 3. We detect this + player looking at playing resonator ÔåÆ pre-steal sound
        /// 4. Carry On completes: SetBlock(0,pos) ÔåÆ block entity removed (sound safe because we cleared the field)
        /// 5. WatchedAttributes updated ÔåÆ IsCarryingResonator detects carry ÔåÆ boombox activates
        /// </summary>
        private static void OnCarryCheckTick(float dt)
        {
            if (capi == null) return;

            var player = capi.World.Player;
            if (player?.Entity == null) return;

            bool isCarryingResonator = IsCarryingResonator(player.Entity, out var carriedBlockCode, out var slot);

            // Check if Carry On pickup key is being held (stored by Carry On in entity attributes)
            bool carryKeyHeld = false;
            try
            {
                carryKeyHeld = ((TreeAttribute)player.Entity.Attributes).GetBool("carryKeyHeld", false);
            }
            catch { }

            // --- Pre-steal logic ---
            // During Carry On's ~800ms pickup animation, the carry key is held.
            // We steal the sound from the resonator's track BEFORE the block is removed,
            // because Carry On's SetBlock(0, pos) disposes the block entity without
            // calling StopMusic - the sound would be lost otherwise.
            
            // Only pre-steal when carry key held AND not already doing a pause/resume 
            // (Ctrl+RMB on a resonator triggers pause, not carry)
            if (carryKeyHeld && pendingBoomboxSound == null && activeBoomboxSound == null
                && !ResonatorPatches.IsPausingOrResuming)
            {
                TryPreStealResonatorSound(player);
            }

            // Cancel pre-steal if carry key released without picking up
            if (!carryKeyHeld && pendingBoomboxSound != null && !isCarryingResonator)
            {
                if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                    SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox: Carry key released without pickup, canceling pre-steal");
                CancelPreSteal();
            }

            // Timeout pre-steal after 3 seconds (safety net for edge cases)
            if (pendingBoomboxSound != null && pendingStolenTimeMs > 0 && !isCarryingResonator)
            {
                long elapsed = capi.World.ElapsedMilliseconds - pendingStolenTimeMs;
                if (elapsed > 3000)
                {
                    if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                        SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox: Pre-steal timed out after 3s");
                    CancelPreSteal();
                }
            }

            // Transition: Started carrying resonator
            if (isCarryingResonator && !wasCarryingResonator)
            {
                currentCarrySlot = slot;
                OnResonatorPickedUp(player.Entity);
            }
            // Transition: Stopped carrying resonator
            else if (!isCarryingResonator && wasCarryingResonator)
            {
                OnResonatorPlacedOrDropped();
                currentCarrySlot = CarrySlotType.None;
            }
            // Slot change while carrying (Hands -> Back or vice versa)
            else if (isCarryingResonator && slot != currentCarrySlot)
            {
                if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                    SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"Boombox: Carry slot changed from {currentCarrySlot} to {slot}");
                currentCarrySlot = slot;
            }

            wasCarryingResonator = isCarryingResonator;
        }

        /// <summary>
        /// Attempt to pre-steal the sound from a resonator the player is looking at.
        /// Called when carry key is held and we don't already have a pending/active boombox sound.
        /// </summary>
        private static void TryPreStealResonatorSound(IClientPlayer player)
        {
            if (capi == null) return;

            // Cooldown after placement — don't immediately re-steal the block we just placed
            if (lastPlacementTimeMs > 0 && capi.World.ElapsedMilliseconds - lastPlacementTimeMs < 2000)
                return;

            var sel = player.CurrentBlockSelection;
            if (sel == null) return;

            var be = capi.World.BlockAccessor.GetBlockEntity(sel.Position) as BlockEntityResonator;
            if (be == null || !be.IsPlaying) return;

            var sound = ResonatorReflection.GetSound(be);
            if (sound == null || sound.IsDisposed) return;

            if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"Boombox: Pre-stealing sound from resonator at {sel.Position}, isPlaying={sound.IsPlaying}");

            // Clear the sound reference from the track so block entity cleanup can't dispose it.
            // When Carry On calls SetBlock(0, pos), the block entity will be removed.
            // Any cleanup code that tries to dispose the sound will find null and skip.
            var trackField = ResonatorReflection.TrackField;
            var soundField = ResonatorReflection.SoundField;
            if (trackField != null && soundField != null)
            {
                var track = trackField.GetValue(be);
                if (track != null)
                {
                    pendingBoomboxTrack = track;
                    soundField.SetValue(track, null);
                    if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                        SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox: Cleared sound field from track to prevent disposal");
                }
            }

            pendingBoomboxSound = sound;
            pendingPickupPos = sel.Position.Copy();
            pendingStolenTimeMs = capi.World.ElapsedMilliseconds;
            pendingSourceResonator = be;
            wasPlayingWhenPickedUp = sound.IsPlaying;

            // Register the captured track for vanilla-music suppression IMMEDIATELY at
            // pre-steal time. Carry On's SetBlock(0,pos) will unload the resonator block
            // entity, and vanilla cleanup can call StopMusic which removes the resonator's
            // entry from activeResonatorTracksByPos. If that empties the dict before our
            // boombox activation tick runs, ResonatorPatches releases MusicEngine.currentTrack
            // and vanilla music starts in the gap. Registering here keeps the dict non-empty
            // across the handoff. CancelPreSteal / OnResonatorPlacedOrDropped unregister it.
            if (pendingBoomboxTrack != null)
            {
                ResonatorPatches.RegisterExternalMusicTrack(BOOMBOX_SUPPRESSION_KEY, pendingBoomboxTrack);
            }

            // Capture the track asset path for multiplayer sync.
            // Remote clients need this to create their own ILoadedSound.
            activeBoomboxTrackLocation = null;
            try
            {
                if (be.Inventory?[0]?.Itemstack?.ItemAttributes != null)
                {
                    activeBoomboxTrackLocation = be.Inventory[0].Itemstack.ItemAttributes["musicTrack"].AsString(null);
                }
            }
            catch { }

            if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"Boombox: Sound pre-stolen successfully, wasPlaying={wasPlayingWhenPickedUp}, trackLocation={activeBoomboxTrackLocation ?? "NULL"}");
        }

        /// <summary>
        /// Cancel a pre-steal and return the sound to the resonator if possible.
        /// Called when carry key is released without a pickup completing, or on timeout.
        /// </summary>
        private static void CancelPreSteal()
        {
            if (pendingBoomboxSound == null) return;

            // Always release the suppression key registered at pre-steal time, otherwise
            // we'd leak a stale track reference and keep vanilla music suppressed.
            ResonatorPatches.UnregisterExternalMusicTrack(BOOMBOX_SUPPRESSION_KEY, capi);

            if (!pendingBoomboxSound.IsDisposed)
            {
                // Try to return the sound to the original resonator's track
                bool returned = false;
                if (pendingSourceResonator != null)
                {
                    try
                    {
                        var trackField = ResonatorReflection.TrackField;
                        var soundField = ResonatorReflection.SoundField;
                        if (trackField != null && soundField != null)
                        {
                            var track = trackField.GetValue(pendingSourceResonator);
                            if (track != null)
                            {
                                // Check if track already has a sound (e.g. pause/resume created a new one)
                                var existingSound = soundField.GetValue(track) as ILoadedSound;
                                if (existingSound == null || existingSound.IsDisposed)
                                {
                                    soundField.SetValue(track, pendingBoomboxSound);
                                    if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                                        SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox: Sound returned to resonator track");
                                    returned = true;
                                }
                                else
                                {
                                    if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                                        SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox: Track already has a sound, disposing our stolen copy");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                            SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"Boombox: Failed to return sound: {ex.Message}");
                    }
                }

                if (!returned)
                {
                    // Can't return to original resonator - dispose cleanly
                    if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                        SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox: Can't return sound to resonator, disposing");
                    try
                    {
                        pendingBoomboxSound.Stop();
                        pendingBoomboxSound.Dispose();
                    }
                    catch { }
                }
            }

            pendingBoomboxSound = null;
            pendingBoomboxTrack = null;
            pendingPickupPos = null;
            pendingStolenTimeMs = 0;
            pendingSourceResonator = null;
        }

        /// <summary>
        /// Check if an entity is carrying a resonator block via Carry On.
        /// Checks both Hands and Back slots.
        /// </summary>
        private static bool IsCarryingResonator(Entity entity, out string blockCode, out CarrySlotType slot)
        {
            blockCode = null;
            slot = CarrySlotType.None;
            if (entity == null) return false;

            try
            {
                var carriedAttr = entity.WatchedAttributes.GetTreeAttribute(CARRYON_ATTRIBUTE_ID);
                if (carriedAttr == null) return false;

                // Check Hands first (primary carry)
                if (CheckCarrySlot(entity, carriedAttr, "Hands", out blockCode))
                {
                    slot = CarrySlotType.Hands;
                    return true;
                }

                // Check Back (player swapped to back via Carry On)
                if (CheckCarrySlot(entity, carriedAttr, "Back", out blockCode))
                {
                    slot = CarrySlotType.Back;
                    return true;
                }

                // Neither slot has a resonator
                noSlotLogCount++;
                if (noSlotLogCount <= 1 || noSlotLogCount % 50 == 0)
                {
                    if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                        SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"IsCarryingResonator: Has carriedAttr but no resonator in Hands or Back (log #{noSlotLogCount})");
                }
                return false;
            }
            catch (Exception ex)
            {
                if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                    SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"IsCarryingResonator: Exception - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check a specific Carry On slot for a resonator block.
        /// </summary>
        private static bool CheckCarrySlot(Entity entity, ITreeAttribute carriedAttr, string slotName, out string blockCode)
        {
            blockCode = null;

            var slotAttr = carriedAttr.GetTreeAttribute(slotName);
            if (slotAttr == null) return false;

            var stack = slotAttr.GetItemstack("Stack");
            if (stack == null) return false;

            stack.ResolveBlockOrItem(entity.World);
            if (stack.Block == null) return false;

            blockCode = stack.Block.Code?.Path ?? "";
            if (blockCode.Contains("resonator"))
            {
                // Reset spam counter when we find a resonator
                noSlotLogCount = 0;
                return true;
            }

            return false;
        }

        #endregion

        #region Boombox Logic

        /// <summary>
        /// Called when player picks up a resonator with Carry On.
        /// </summary>
        private static void OnResonatorPickedUp(Entity playerEntity)
        {
            // Check if we have a pre-stolen sound from the tick-based detection
            if (pendingBoomboxSound != null && !pendingBoomboxSound.IsDisposed)
            {
                // Verify the block we stole from was the one Carry On actually picked up.
                // Carry On removes the carried block via SetBlock(0, pos), so the slot at
                // pendingPickupPos must be empty (or no longer a resonator) by now. If a
                // resonator block is STILL there, the player's crosshair drifted to a
                // different (playing) resonator during the carry animation while actually
                // picking up a SILENT one nearby — we stole from the wrong block. Cancel
                // the steal so the silent pickup stays silent and the playing resonator
                // keeps playing.
                bool stoleFromWrongBlock = false;
                if (capi != null && pendingPickupPos != null)
                {
                    try
                    {
                        var blockAtPickup = capi.World.BlockAccessor.GetBlock(pendingPickupPos);
                        if (blockAtPickup != null && blockAtPickup.Id != 0 &&
                            (blockAtPickup.Code?.Path?.Contains("resonator") ?? false))
                        {
                            stoleFromWrongBlock = true;
                        }
                    }
                    catch { }
                }

                if (stoleFromWrongBlock)
                {
                    if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                        SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"Boombox: Pre-stole from wrong resonator (block at {pendingPickupPos} still present) — returning sound, no boombox");
                    CancelPreSteal();
                    return;
                }

                activeBoomboxSound = pendingBoomboxSound;
                activeBoomboxTrack = pendingBoomboxTrack;
                originalResonatorPos = pendingPickupPos?.Copy();

                // Pre-steal clears track.Sound to protect the audio from block cleanup disposal.
                // Once the carried boombox is active, restore that link so MusicEngine.currentTrack
                // points at a live track+sound pair and can suppress vanilla music correctly.
                if (activeBoomboxTrack != null && activeBoomboxSound != null)
                {
                    try
                    {
                        ResonatorReflection.SoundField?.SetValue(activeBoomboxTrack, activeBoomboxSound);
                    }
                    catch (Exception ex)
                    {
                        if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                            SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"Boombox: Failed to restore sound onto carried track: {ex.Message}");
                    }
                }

                // Suppression key was already registered at pre-steal time.
                // Refresh in case the track reference changed.
                if (activeBoomboxTrack != null)
                {
                    ResonatorPatches.RegisterExternalMusicTrack(BOOMBOX_SUPPRESSION_KEY, activeBoomboxTrack);
                }

                // Clear pending state
                pendingBoomboxSound = null;
                pendingBoomboxTrack = null;
                pendingPickupPos = null;
                pendingStolenTimeMs = 0;
                pendingSourceResonator = null;

                StartBoomboxTick();
                if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                    SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"Boombox ACTIVATED from pre-steal! slot={currentCarrySlot}, from={originalResonatorPos}, wasPlaying={wasPlayingWhenPickedUp}");
            }
            else
            {
                if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                    SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox: Carrying resonator but no pre-stolen sound available");
            }
        }

        /// <summary>
        /// Timestamp of last placement — prevents immediate re-steal of freshly placed block.
        /// </summary>
        private static long lastPlacementTimeMs = 0;

        /// <summary>
        /// Called when player places or drops the resonator.
        /// Sound is disposed here; vanilla StartMusic will create a fresh sound for the placed block.
        /// </summary>
        private static void OnResonatorPlacedOrDropped()
        {
            if (capi != null)
                lastPlacementTimeMs = capi.World.ElapsedMilliseconds;

            StopBoomboxTick();

            // Notify remote clients to stop their local boombox sound
            SendBoomboxStopPacket();

            ResonatorPatches.UnregisterExternalMusicTrack(BOOMBOX_SUPPRESSION_KEY, capi);

            if (activeBoomboxSound != null && !activeBoomboxSound.IsDisposed)
            {
                if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                    SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox deactivated - disposing carried sound, vanilla will restart");

                if (activeBoomboxTrack != null)
                {
                    try
                    {
                        ResonatorReflection.SoundField?.SetValue(activeBoomboxTrack, null);
                    }
                    catch { }
                }

                activeBoomboxSound.Stop();
                activeBoomboxSound.Dispose();
            }

            activeBoomboxSound = null;
            activeBoomboxTrack = null;
            originalResonatorPos = null;
            wasPlayingWhenPickedUp = false;
            activeBoomboxTrackLocation = null;
        }

        /// <summary>
        /// Send a stop packet to server so remote clients dispose their boombox sound for us.
        /// </summary>
        private static void SendBoomboxStopPacket()
        {
            try
            {
                var player = capi?.World?.Player?.Entity;
                if (player == null) return;

                SoundPhysicsAdaptedModSystem.ClientChannel?.SendPacket(new BoomboxSyncPacket
                {
                    CarrierEntityId = player.EntityId,
                    IsPlaying = false
                });
            }
            catch { }
        }

        /// <summary>
        /// Start the tick listener that updates boombox position.
        /// </summary>
        private static void StartBoomboxTick()
        {
            if (tickListenerId != 0) return;
            tickListenerId = capi.Event.RegisterGameTickListener(OnBoomboxTick, 50);
        }

        /// <summary>
        /// Stop the boombox position tick listener.
        /// </summary>
        private static void StopBoomboxTick()
        {
            if (tickListenerId != 0)
            {
                capi.Event.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }
        }

        /// <summary>
        /// Update boombox sound position to follow player.
        /// Position is offset based on carry slot:
        /// - Hands: 0.5 blocks in front of player at chest height
        /// - Back: 0.4 blocks behind player at back height
        /// This prevents left/right ear shifting when turning the camera.
        /// </summary>
        private static void OnBoomboxTick(float dt)
        {
            if (activeBoomboxSound == null || activeBoomboxSound.IsDisposed)
            {
                ResonatorPatches.UnregisterExternalMusicTrack(BOOMBOX_SUPPRESSION_KEY, capi);
                activeBoomboxTrack = null;
                StopBoomboxTick();
                return;
            }

            var player = capi?.World?.Player?.Entity;
            if (player == null) return;

            // Base position at player's center
            var pos = player.Pos;
            double baseX = pos.X;
            double baseY = pos.Y + 0.8; // Chest height (not eye height)
            double baseZ = pos.Z;

            // Calculate forward/backward offset based on yaw
            float yaw = player.Pos.Yaw;
            // VS yaw: 0 = south, PI/2 = west, PI = north, 3PI/2 = east
            double forwardX = -Math.Sin(yaw);
            double forwardZ = -Math.Cos(yaw);

            double offsetDistance;
            double offsetSign; // +1 = forward, -1 = backward

            if (currentCarrySlot == CarrySlotType.Back)
            {
                // Behind player
                offsetDistance = 0.4;
                offsetSign = -1.0;
                baseY = pos.Y + 0.7; // Slightly lower for back
            }
            else
            {
                // In front of player (Hands or default)
                offsetDistance = 0.5;
                offsetSign = 1.0;
            }

            float x = (float)(baseX + forwardX * offsetDistance * offsetSign);
            float y = (float)baseY;
            float z = (float)(baseZ + forwardZ * offsetDistance * offsetSign);

            activeBoomboxSound.SetPosition(x, y, z);
            lastSoundX = x;
            lastSoundY = y;
            lastSoundZ = z;

            // Carried boombox is attached to the player, so it is always fully audible while active.
            // Feed that into the same MusicEngine suppression path as placed resonators.
            ResonatorPatches.ReportExternalMusicSuppression(capi, 1f);

            // Update AudioRenderer tracking for occlusion system
            var soundPos = new Vec3d(x, y, z);
            AudioRenderer.UpdateStoredPosition(activeBoomboxSound, soundPos);

            // Apply pitch glitch effect
            float pitch = GameMath.Clamp(1 - capi.Render.ShaderUniforms.GlitchStrength, 0.1f, 1);
            activeBoomboxSound.SetPitch(pitch);

            // Send sync packet to server for relay to nearby players (every 500ms)
            long now = capi.World.ElapsedMilliseconds;
            if (now - lastSyncTimeMs >= SYNC_INTERVAL_MS)
            {
                lastSyncTimeMs = now;

                if (activeBoomboxTrackLocation == null)
                {
                    capi.Logger.Debug("[SoundPhysicsAdapted] [Boombox] SYNC SKIPPED: activeBoomboxTrackLocation is NULL — remote players won't hear this");
                }
                else if (SoundPhysicsAdaptedModSystem.ClientChannel == null)
                {
                    capi.Logger.Debug("[SoundPhysicsAdapted] [Boombox] SYNC SKIPPED: ClientChannel is NULL");
                }
                else
                {
                    try
                    {
                        SoundPhysicsAdaptedModSystem.ClientChannel.SendPacket(new BoomboxSyncPacket
                        {
                            CarrierEntityId = player.EntityId,
                            TrackLocation = activeBoomboxTrackLocation,
                            PlaybackPosition = activeBoomboxSound.PlaybackPosition,
                            IsPlaying = true,
                            PosX = x,
                            PosY = y,
                            PosZ = z
                        });
                    }
                    catch (Exception ex)
                    {
                        capi.Logger.Debug($"[SoundPhysicsAdapted] [Boombox] SYNC FAILED: {ex.Message}");
                    }
                }
            }
        }

        #endregion

        #region Harmony Patches

        /// <summary>
        /// Prefix for BlockEntityResonator.StopMusic.
        /// Handles two cases:
        /// 1. Pause/resume: passes through to vanilla (normal behavior)
        /// 2. Pre-stolen sound: lets vanilla run but sound field is already null (no-op disposal)
        /// No longer attempts to steal sound here - that's handled by the tick-based pre-steal.
        /// </summary>
        public static bool StopMusicPrefix(BlockEntityResonator __instance)
        {
            if (__instance.Api?.Side != EnumAppSide.Client) return true;
            
            // Check if feature is enabled
            if (SoundPhysicsAdaptedModSystem.Config?.EnableCarryOnCompat != true) return true;
            
            if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"Boombox StopMusicPrefix: ENTERED for pos={__instance.Pos}");
            
            // Skip if we're doing a pause/resume action
            if (ResonatorPatches.IsPausingOrResuming)
            {
                if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                    SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox StopMusicPrefix: Pause/resume in progress, passing through");
                return true;
            }
            
            // Check position-based pausing list
            bool inPausingList = IsPausingResonator(__instance.Pos);
            if (inPausingList)
            {
                if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                    SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox StopMusicPrefix: Position in pausing/resuming list, passing through");
                return true;
            }

            // If we've pre-stolen the sound for this position, log it.
            // The sound field on the track is already null, so vanilla StopMusic
            // will just clean up the track object without disposing our sound.
            if (pendingBoomboxSound != null && pendingPickupPos != null && 
                __instance.Pos.Equals(pendingPickupPos))
            {
                if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                    SoundPhysicsAdaptedModSystem.ResonatorDebugLog("Boombox StopMusicPrefix: Sound was pre-stolen, vanilla will no-op on null sound field");
            }

            return true; // Always let vanilla StopMusic proceed
        }

        /// <summary>
        /// Prefix for BlockEntityResonator.StartMusic.
        /// Just logs for debugging - we no longer try to inject sound during placement.
        /// Vanilla will create a fresh sound for the placed block.
        /// </summary>
        public static bool StartMusicPrefix(BlockEntityResonator __instance)
        {
            if (__instance.Api?.Side != EnumAppSide.Client) return true;
            if (SoundPhysicsAdaptedModSystem.Config?.EnableCarryOnCompat != true) return true;

            if (SoundPhysicsAdaptedModSystem.IsResonatorDebugEnabled)
                SoundPhysicsAdaptedModSystem.ResonatorDebugLog($"Boombox StartMusicPrefix: ENTERED for pos={__instance.Pos}");
            return true; // Always let vanilla handle it
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Dispose any active boombox sound. Called on mod unload.
        /// </summary>
        public static void Cleanup()
        {
            StopBoomboxTick();

            if (activeBoomboxSound != null && !activeBoomboxSound.IsDisposed)
            {
                if (activeBoomboxTrack != null)
                {
                    try
                    {
                        ResonatorReflection.SoundField?.SetValue(activeBoomboxTrack, null);
                    }
                    catch { }
                }

                activeBoomboxSound.Stop();
                activeBoomboxSound.Dispose();
            }

            if (pendingBoomboxSound != null && !pendingBoomboxSound.IsDisposed)
            {
                pendingBoomboxSound.Stop();
                pendingBoomboxSound.Dispose();
            }

            activeBoomboxSound = null;
            pendingBoomboxSound = null;
            activeBoomboxTrack = null;
            pendingBoomboxTrack = null;
            pendingPickupPos = null;
            pendingStolenTimeMs = 0;
            pendingSourceResonator = null;
            originalResonatorPos = null;
            currentCarrySlot = CarrySlotType.None;
            activeBoomboxTrackLocation = null;
            lastSyncTimeMs = 0;
            lastPlacementTimeMs = 0;
            ResonatorPatches.UnregisterExternalMusicTrack(BOOMBOX_SUPPRESSION_KEY, capi);
            capi = null;
        }

        #endregion
    }
}
