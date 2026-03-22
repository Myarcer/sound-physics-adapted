# VS Startup Hang Investigation — March 22, 2026

## Test Plan
User will launch the game **without Sound Physics Adapted** and compare logs against this baseline (with SPA enabled). Goal: determine if the hang/freeze is caused by SPA interaction with other mods, or is independent.

---

## Baseline (WITH SPA v0.1.8) — Timeline

| Time | Delta | Event |
|------|-------|-------|
| 21:01:09 | +0s | Client started |
| 21:02:07 | +58s | Connected to server (play.vscivilizations.com) |
| 21:02:10 | +61s | 129 mods loaded (2 disabled), 301 mod systems |
| 21:02:21 | +72s | SPA patches applied, reverb ready |
| 21:02:22 | +73s | All mod systems starting |
| 21:02:42 | +93s | 64,528 block types populated |
| 21:02:56 | +107s | All clientside asset loading complete |
| 21:02:59 | +110s | SPA LevelFinalize → "World ready, warmup started" |
| 21:03:00 | +111s | 242 startup warnings captured |
| 21:03:13→21:04:26 | +124-197s | **STALLED** — SPA occlusion calcs show NaN positions, player at (-2147483648,-2147483648,-2147483648) |
| **21:04:26** | **+197s** | **SPA HEARTBEAT: fps=0.0, avgFrame=86,024ms (86 SECONDS per frame!)** |
| 21:04:50 | +221s | SPA warmup complete (4 ticks took 1m51s due to stalled game loop) |
| 21:04:51-54 | +222s | Viconomy BEVinconLiquidContainer texture atlas crashes begin |
| 21:06:29 | +320s | Client daytime drifted 15.9 minutes from server |

## Key Errors Observed

### 1. Viconomy — Thread-unsafe texture insertion (CRITICAL, REPEATED)
```
Exception: Attempting to insert a texture into the atlas outside of the main thread.
  at Viconomy.BlockEntities.BEVinconLiquidContainer.GenMesh()
  at Viconomy.BlockEntities.BEVinconLiquidContainer.TesselateDisplayedItems()
  at Viconomy.BlockEntities.BEVinconContainer.OnTesselation()
```
Coordinates: 23466/166/34805 and 23468/166/34805 — fires on every retesselation attempt, multiple times.

### 2. VanillaVariants — Hundreds of missing texture/lang warnings
- Missing textures: shingles, planks, barrel, chest variants (aged, golden, owl)
- Missing lang keys: hundreds of block names (palisadewall, clutch, pulverizerframe, chair, angledwindow)
- These flood the log during AssetsFinalize (21:02:58-21:03:00)

### 3. SPA Occlusion — NaN player positions during loading
From 21:03:13 to 21:04:26, SPA occlusion calcs run but player position is invalid:
```
DDA=(-2147483648,-2147483648,-2147483648)->(23488,178,34647)
dist=NaN center=0,00 (clear, skip offset)
```
These are harmless (SPA skips the computation) but indicate the player entity isn't positioned yet.

### 4. Immersive Fibercraft — Scanning 79,555 recipes at 21:02:58
Not an error but a notable CPU cost during the already-stalled loading period.

---

## SPA Internal Diagnostics (Proves SPA is NOT doing heavy work)

```
HEARTBEAT #2 | fps=0.0 avgFrame=86024.3ms maxFrame=86024.3ms
  smooth: 1 ticks avg=0.01ms max=0.01ms
  occlusion: 0 ticks avg=0.00ms max=0.00ms
  cleanup: 0 ticks
  filters=8 hooks=0 untracked=0
```

- **0 occlusion ticks** — SPA's heavy raycasting is fully gated by `IsWorldReady`
- **1 smoothing tick** in the entire 86-second frame — negligible
- **8 filters** registered — minimal overhead

---

## Test Result — WITHOUT SPA (March 22, 2026 21:17)

### Timeline (WITHOUT SPA)

| Time | Delta | Event |
|------|-------|-------|
| 21:17:42 | +0s | Client started |
| 21:17:47 | +5s | Connected to server (UDP) |
| 21:17:50 | +8s | 126 mods loaded, 300 mod systems |
| 21:18:01 | +19s | Shaders + sounds reloaded with mod assets |
| 21:18:11 | +29s | 64,528 block types loaded |
| 21:18:17 | +35s | Server assets loaded |
| 21:18:26 | +44s | Received level finalize |
| 21:18:29 | +47s | Handling LevelFinalize packet |
| 21:18:31 | +49s | **Done level finalize** (handbook, liquid containers, wearables, traders) |
| 21:18:32-39 | +50-57s | Sound files loading (culinaryartillery, hydrateordiedrate, spinningwheel, nprpeditor) — smooth 1-5s intervals |
| 21:18:33+ | +51s+ | Chunks tessellating (Cabinet meshes, Viconomy errors) — **continuous, no freeze** |
| 21:18:54 | +72s | Entity pool cleanup — game running normally |
| 21:21:14 | +212s | User exits to main menu voluntarily |

### Comparison: SPA vs No-SPA

| Metric | WITH SPA (v0.1.8) | WITHOUT SPA | Delta |
|--------|-------------------|-------------|-------|
| Client start → LevelFinalize | 110s | 49s | **-61s** |
| LevelFinalize → playable | **86+ seconds (FROZEN)** | **<3 seconds** | **HANG ELIMINATED** |
| Max frame time | **86,024ms (86s!)** | Normal (continuous logging) | **86s → ~0ms** |
| FPS during loading | **0.0** | Normal | Fixed |
| Viconomy texture crash | Yes (21:04:51) | Yes (21:18:41) | Still present (not SPA) |
| VanillaVariants warnings | Yes (243) | Yes (243) | Same (not SPA) |
| Time drift | 15.9 minutes | None | **Fixed** |

### Verdict: **SPA IS the cause of the 86-second freeze**

The Viconomy and VanillaVariants errors exist in both sessions (they're bugs in those mods), but without SPA:
- No freeze at all after LevelFinalize
- Logging is continuous with ~1ms between entries
- Sounds load smoothly over 5-7 seconds
- Chunks tessellate in the background without blocking

---

## Root Cause Analysis

### Why SPA freezes the game during startup

The freeze occurs in the **~73 second window between LevelFinalize and "world ready"** when hundreds of sounds are triggered by chunk tessellation (ambient loops, block entity sounds, etc.). SPA intercepts ALL of these through its Harmony patches:

#### Hot Path Chain (per sound start):
1. **`StartPlayingAudioMonoPrefix`** — Runs on `ClientMain.StartPlaying()`. Calls `MonoDownmixManager.EnsureMono()` which does stereo→mono audio buffer conversion. _Cached after first call per asset, so only expensive on first encounter._

2. **`CreateSoundSourcePostfix`** — Runs on `LoadedSoundNative.createSoundSource()`. Calls `AudioRenderer.DetachGlobalFilter()` via OpenAL. _Cheap but adds syscall overhead._

3. **`SoundStartPrefix`** — **THE MAIN SUSPECT**. Runs on `LoadedSoundNative.Start()` BEFORE `AL.SourcePlay()`:
   - Calls `AudioRenderer.GetSourceId()` (reflection)
   - Calls `AudioRenderer.DetachGlobalFilter()` (OpenAL API call)
   - **After LevelFinalize** (`IsWorldDataLoaded=true`): calls **`ApplyOcclusion()`** which runs:
     - `SoundSourceAdjuster.Adjust()` — block lookups + multiblock resolution
     - **`OcclusionCalculator.Calculate()`** — DDA raycast through world blocks (up to 9 rays per sound!)
     - `ApplyLowPassFilter()` — another OpenAL API call via reflection
   - **Before LevelFinalize**: just applies a flat LPF (cheap)

4. **`ALSourcePlayPrefix_*`** (4-5 overloads) — Runs on every `AL.SourcePlay()` call:
   - `lock (sourceTrackLock)` — **thread contention** if tessellation fires sounds from worker threads
   - `AudioRenderer.IsSourceTracked()` — dictionary lookup
   - **`alSourceMethod_Hook.Invoke()`** — **REFLECTION CALL on hot path** to reattach filter

5. **`SoundStartPostfix`** — Runs AFTER `AL.SourcePlay()`:
   - `AudioRenderer.ReattachFilter()` — another reflection + OpenAL call
   - If world ready: **full reverb calculation** including player position, block accessor lookups

6. **`StartPlayingFinalPostfix`** — Runs on `ClientMain.StartPlaying()` return:
   - `AudioRenderer.GetSourceId()` + `AudioRenderer.ReattachFilter()` — more reflection

#### Per-sound cost estimate:
- ~3 reflection invocations (`GetSourceId`, `alSourceMethod_Hook.Invoke`, `ReattachFilter`)
- ~2 OpenAL native calls (DetachGlobalFilter, filter attachment)
- 1 DDA raycast with up to 9 rays (after LevelFinalize)
- 2 `lock` acquisitions (sourceTrackLock)
- Multiple block accessor lookups

#### With 100+ sounds starting during chunk tessellation, this means:
- **300+ reflection calls** (slow — boxing, security checks, invoke overhead)
- **200+ OpenAL API calls** (context switches)
- **100+ DDA raycasts** with invalid player positions (NaN → wasted work)
- **200+ lock acquisitions** (threading contention)

All of this runs **on the main thread** (or worse, on tessellation worker threads via `AL.SourcePlay` hook), blocking the game loop completely.

### The critical insight:
SPA's HEARTBEAT showed `filters=8 hooks=0` and `0 occlusion ticks` — meaning SPA's OWN tick system wasn't doing work. But the **Harmony patch PREFIX/POSTFIX hooks are outside the tick system**. They fire synchronously inline with every game sound operation, completely invisible to SPA's own performance monitoring.

---

## Fix Plan

### Priority 1: Startup grace period (IMMEDIATE FIX)
Add an `IsStartupGracePeriod` flag that's true from mod load until N seconds after first rendered frame (not LevelFinalize). During grace period:
- `SoundStartPrefix`: Skip occlusion entirely, just apply flat LPF or no-op
- `ALSourcePlayPrefix_*`: Skip lock + reflection entirely (return immediately)
- `SoundStartPostfix`: Skip reverb calculation
- `StartPlayingFinalPostfix`: Skip re-attachment

This costs nothing in audio quality (player can't hear during loading anyway) and eliminates the freeze.

### Priority 2: Eliminate reflection from hot path (MEDIUM TERM)
- Cache `alSourceMethod_Hook.Invoke()` as a proper delegate (`Action<int, object, int>`) instead of using `MethodInfo.Invoke()`
- Pre-resolve `AudioRenderer.GetSourceId()` to avoid reflection per-call
- Replace lock-based `sourceTrackLock` with `ConcurrentDictionary` to eliminate contention

### Priority 3: Defer sound processing to tick system (LONG TERM)
Instead of processing every sound synchronously in PREFIX hooks:
- Queue sound start events in a lock-free collection
- Process them in the tick system with budget limits (N per tick)
- Apply filters retroactively (small delay, inaudible during loading)
