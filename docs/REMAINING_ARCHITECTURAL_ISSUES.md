# Remaining Architectural Issues

Identified 2025-02-21. Issues that require logic changes or deeper investigation before fixing.

## Logic Changes Required (will change runtime behavior)

### 1. `IsSoundRepositioned()` returns wrong result
**File:** `Core/AudioPhysicsSystem.cs` ~line 757
**Severity:** Medium — feeds wrong data to weather system

`IsSoundRepositioned()` uses `cache.HasSmoothedOcc` as its indicator. But `HasSmoothedOcc` is set to `true` even on the clear-LOS path (line ~504) where repositioning is explicitly skipped:
```csharp
// skipRepositioning = true path:
cache.SmoothedBlendedOcc = occlusion;
cache.HasSmoothedOcc = true;  // ← also true when NOT repositioned
```

The weather system uses this to decide persistence behavior. Sounds with clear LOS are incorrectly reported as "repositioned."

**Fix:** Track repositioning state separately (e.g. `cache.IsCurrentlyRepositioned` bool set only in the `else if (pathResult.HasValue)` branch).

---

### 2. ReverbCellCache TTL uses creation-time player position
**File:** `Core/ReverbCellCache.cs` — `Cleanup()` method
**Severity:** Low — affects cache efficiency, not correctness

TTL distance buckets are calculated using `entry.PlayerPosX/Y/Z` (player position at cache creation time), not current player position. A far entry created when the player was nearby gets a short TTL (1s) even though the player has since moved 60 blocks away. Conversely, close entries may persist too long if the player moved away.

**Fix:** Pass `currentPlayerPos` into `Cleanup()` and use it for distance calculation instead of stored player position.

---

### 3. Sky probe fallback is binary all-or-nothing
**File:** `Core/AudioPhysicsSystem.cs` ~line 657
**Severity:** Low — only affects fallback when weather system is inactive

```csharp
isOutdoors = (skyHits == SKY_PROBE_RAY_COUNT); // must be ALL 5
```

One blocked diagonal (tree branch, eave) flips to "indoors," reducing reverb ray count. The weather system's smoothed `OcclusionFactor < 0.1` is much more robust. Fallback could use `>= 4` for better edge behavior at cave entrances/overhangs.

**Fix:** Change threshold to `skyHits >= SKY_PROBE_RAY_COUNT - 1` (4 of 5).

---

## Investigation Required

### 4. `SoundPlaybackThrottle` may be disconnected
**File:** `Core/SoundPlaybackThrottle.cs`
**Severity:** Unknown — either dead code or wired elsewhere

`SoundPlaybackThrottle` has `EvaluateThrottle()` and `IsThrottled()` but `AudioPhysicsSystem.UpdateAllSounds()` never calls either. The tick loop has its own `MaxSoundsPerTick` budget system. Either:
- The throttle is wired in from a file not yet inspected (check `SoundPhysicsAdaptedModSystem` main class)
- It's dead code from a planned feature that was never integrated
- The two systems are meant to coexist (budget = per-tick raycast cap, throttle = total concurrent sound cap)

**Action:** Search for `SoundPlaybackThrottle` usage across all files. If unused, remove. If used, document the relationship with the budget system.

---

### 5. Thread safety inconsistency
**File:** `Patches/LoadSoundPatch.cs` — `HandleSourcePlay()` + `AcousticRaytracer.cs` statics
**Severity:** Unknown — potential latent bug

`AcousticRaytracer` has mutable static state (`_probeSearchQueue`, `_probeVisited`, `_cacheableBouncePoints`, etc.) justified with "VS mod ticks are single-threaded." But `HandleSourcePlay` in `LoadSoundPatch` uses `lock (sourceTrackLock)` — implying `AL.SourcePlay` can fire from non-game threads.

If `AL.SourcePlay` truly comes from another thread, and that thread triggers any code path that touches the raytracer statics, there's a data race. If it's always the game thread, the lock is dead weight.

**Action:** Add logging to `HandleSourcePlay` to check `Thread.CurrentThread.ManagedThreadId` vs game thread ID. If always same thread, remove the lock. If different, audit all paths from `HandleSourcePlay` for static state access.

---

### 6. `LoadSoundPatch` god class (1296 lines, 8+ responsibilities)
**File:** `Patches/LoadSoundPatch.cs`
**Severity:** Low — maintenance burden, no runtime impact

Handles: assembly discovery, 8 patch registrations, mono downmix buffer swap, occlusion application, reverb application, underwater filtering, AL.SourcePlay diagnostic hooks, source ID tracking with its own lock.

**Potential split:**
- `PatchRegistration.cs` — assembly discovery + Harmony patch wiring
- `SoundProcessing.cs` — occlusion, reverb, underwater filter application
- `ALSourcePlayHook.cs` — native OpenAL interception + source tracking
- `MonoDownmixSwap.cs` — stereo→mono cache swap for positional weather

**Risk:** Harmony patches are order-sensitive and reference static state across methods. Splitting requires careful analysis of shared state dependencies. Low priority.
