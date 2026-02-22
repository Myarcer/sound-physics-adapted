# Sound Physics Adapted - Intermittent Filter Issue

## STATUS: RESOLVED (2026-02-05)

**Root Cause:** `CleanupEntry()` called `DetachFilter(entry.SourceId)` on stale entries, but the sourceId had already been recycled for a new sound, removing that sound's filter.

**Fix:** Removed `DetachFilter()` from cleanup - only delete the filter object, let OpenAL handle source cleanup.

**Commit:** `5137f2c` - fix: don't detach filter from recycled sources in CleanupEntry

---

## Problem Summary

Some sounds play **unmuffled** even when our occlusion system correctly calculates they should be heavily muffled. This happens **intermittently** - the same sound effect that plays muffled one time may play completely unmuffled the next time.

**Key observation**: The issue is **more prevalent when many sounds play simultaneously** (overlapping sounds).

## What We Know Works

1. **Occlusion calculation is correct** - DDA raytracing properly detects blocks between player and sound
2. **Filter creation works** - `EFX.GenFilter()` returns valid filter IDs
3. **Filter configuration works** - `EFX.Filter(filterId, FilterType, LOWPASS)` succeeds
4. **Filter parameter setting works** - `EFX.Filter(filterId, LowpassGainHF, value)` returns success
5. **Filter attachment works** - `AL.Source(sourceId, EfxDirectFilter, filterId)` returns no errors
6. **GetSourceId works** - We correctly retrieve the sourceId field from LoadedSoundNative
7. **Patches are applied** - All Harmony patches on LoadSound, Start, SetPosition, etc. are working

## What We Observed

### Logs show everything "succeeds":
```
PRE-START: sounds/environment/mediumsplash.ogg pos=(512042,71,512032) sourceId=39
NEW REGISTRATION: sounds/environment/mediumsplash.ogg filter=18 src=39
APPLYING TO NEW: filter=18 value=0,050
SetLowpassGainHF returned True
ATTACHED filter=18 to source=39
FILTER APPLIED: sounds/environment/mediumsplash.ogg src=39 filter=18 value=0,050 [INSTANT]
FILTER-REATTACH: sounds/environment/mediumsplash.ogg sourceId=39
```

Yet the sound plays **completely unmuffled**.

### Tests performed:
1. **Setting AL_GAIN to 0** - Sound still played at full volume
2. **Reading back AL_GAIN after setting** - Could not verify (method lookup issues)
3. **Blocking vanilla SetLowPassfiltering** - Patch works, but issue persists
4. **Detaching global filter in createSoundSource** - Implemented, issue persists
5. **Reattaching filter on every position update** - Implemented, issue persists
6. **Reattaching filter in StartPlayingFinalPostfix** - Implemented, issue persists

## Hypotheses

### 1. Source ID Race Condition (Most Likely)
OpenAL recycles source IDs. When many sounds play simultaneously:
- Sound A finishes on sourceId=39
- VS recycles sourceId=39 for Sound B
- Our code modifies sourceId=39 thinking it's Sound A
- Sound B plays on a DIFFERENT internal source

**Evidence**: Issue is worse with overlapping sounds.

### 2. VS Uses Different Audio Path
VS might route some sounds through:
- Auxiliary sends (reverb path) instead of direct path
- A separate audio mixer layer
- Buffered/streaming playback that doesn't use the source we're modifying

**Evidence**: Setting AL_GAIN to 0 had no effect on some sounds.

### 3. OpenAL Internal Source Pool
OpenAL Soft has 255 mono sources. When pool is stressed:
- Sources may be virtualized (not actually playing)
- Filter state may not persist across virtualization
- Some sources may be in invalid states

**Evidence**: Issue correlates with many simultaneous sounds.

### 4. Timing/Threading Issue
VS's audio system runs on a separate thread. Our patches run on main thread:
- We modify source state
- VS's audio thread immediately overwrites
- Race condition determines which sounds work

### 5. ILoadedSound Instance Reuse
VS might pool ILoadedSound objects:
- Same object reference used for different sounds
- Our dictionary keyed by object finds stale entries
- Filter attached to wrong source

## Code Architecture

### Current Flow:
1. `createSoundSource` postfix → Detach global filter
2. `Start` prefix → Calculate occlusion, create filter, attach filter
3. `StartPlaying` postfix → Reattach filter
4. `SetPosition` postfix → Recalculate occlusion, update filter, reattach

### Key Files:
- `src/Patches/LoadSoundPatch.cs` - Harmony patches
- `src/Core/SoundFilterManager.cs` - Per-sound filter management
- `src/Core/EfxHelper.cs` - OpenAL EFX reflection wrappers
- `src/Core/OcclusionCalculator.cs` - Raycast occlusion

## Things to Try

### 1. Verify sourceId is actually the playing source
- Hook into AL.SourcePlay directly to see which source VS calls
- Compare with the sourceId we read from LoadedSoundNative

### 2. Use VS's global filter instead of per-sound
- Instead of creating our own filters, modify VS's existing global filter
- This is what VS does natively for underwater

### 3. Hook lower in the stack
- Patch AL.SourcePlay directly
- Patch AL.Source calls to intercept VS's filter changes

### 4. Check source state before modifying
- Query AL_SOURCE_STATE to verify source is valid
- Skip modification if source is in unexpected state

### 5. Investigate auxiliary sends
- Check if VS uses AL_AUXILIARY_SEND_FILTER
- May need to filter both direct AND auxiliary paths

### 6. Add source validation
- Before attaching filter, verify sourceId is valid with AL.IsSource
- Log when sourceId becomes invalid

## Config Options

Current config in `soundphysicsadapted.json`:
```json
{
  "Enabled": true,
  "DebugMode": true,
  "ReplaceVanillaLowpass": true,
  ...
}
```

## Related Resources

- OpenAL EFX Guide: https://openal-soft.org/misc-downloads/Effects%20Extension%20Guide.pdf
- VS Modding Discord for decompiled source insights
- Sound Physics Remastered (Minecraft) for reference implementation

## Session Notes (2026-02-05)

### Confirmed Facts
- All sounds are mono (channels=1)
- EFX initialization succeeds
- 255 mono sources available
- No OpenAL errors reported
- Issue is definitely intermittent and correlates with sound overlap

### Investigation: Filter ID Recycling (Session 2)
**Observation**: OpenAL recycles filter IDs when they're deleted.

**Original Bug Found**:
```
Set filter 6 type to LOWPASS
Set filter 6 gainHF to 1         <-- PROBLEM: Attached with 1.0 first!
ATTACHED filter=6 to source=26
...
SetLowpassGainHF filter=6 to 0,129  <-- Then lowered, but too late?
ATTACHED filter=6 to source=26
```

**Fix Applied**: Moved `ConfigureLowpass(1.0f)` from `RegisterSound` to `SetOcclusion`, so filter is configured with the CORRECT occlusion value BEFORE first attachment.

**Result**: Fix confirmed in logs - now directly sets correct value:
```
FILTER-NEW: mediumsplash.ogg src=26 filter=1 value=0,129
Set filter 1 type to LOWPASS
Set filter 1 gainHF to 0,12873492  <-- Correct value from start!
ATTACHED filter=1 to source=26
```

**BUT**: Issue still persists! Sounds still play unmuffled despite correct filter setup.

### Verification System Issue
Added `GetAttachedFilter()` to read back attached filter ID from OpenAL source.
- Returns -1 for ALL sounds (verification unavailable)
- Likely issue with reflection lookup for `AL.GetSource(int, ALSourcei, out int)`
- Added diagnostic logging to debug this

### Current Hypothesis

Since the timing fix didn't help and everything logs as "successful", the issue is likely:

1. **OpenAL source virtualization**: When 255 sources are stressed, OpenAL virtualizes some sources. The sourceId we have might be virtualized and not actually playing audio.

2. **VS creating multiple sources per sound**: VS might internally create additional sources we don't know about.

3. **Auxiliary send path**: Sound might be routed through auxiliary send (reverb) which bypasses EfxDirectFilter entirely.

4. **EfxDirectFilter only affects mono positional sources**: Stereo or non-positional sources ignore EfxDirectFilter.

### Next Steps

1. ~~Fix GetAttachedFilter diagnostic~~ ✓ DONE
2. **Check AL_AUXILIARY_SEND_FILTER** - May need to filter that path too
3. **Query AL_SOURCE_STATE** - Verify source is actually AL_PLAYING
4. **Patch AL.SourcePlay directly** - Catch the exact moment VS plays each source

---

## Session 3: Critical Discovery (2026-02-05 19:15)

### GetAttachedFilter Fixed
- Changed from `ALSourcei` to `ALGetSourcei` enum for reading
- OpenTK uses different enums for setters vs getters
- Now successfully reads back filter from sources

### EfxDirectFilter Value Verified
```
EfxDirectFilter: EfxDirectFilter (numeric=0x20005 = 131077, expected=0x20005 = 131077)
```
Value matches VS's hardcoded `(ALSourcei)131077` ✓

### CRITICAL FINDING: Filters NOT Actually Attaching!
**ALL sounds show `actual=0` on readback:**
```
FILTER-VERIFY: mediumsplash.ogg src=30 expected=3 actual=0 [MISMATCH! got=0]
FILTER-POST: mediumsplash.ogg src=30 expected=3 [was-wrong:0] -> [STILL-WRONG:0]
```

We call `AL.Source(sourceId, EfxDirectFilter, filterId)`:
- No OpenAL errors reported
- Returns "success"
- But `AL.GetSource` shows filter=0 (no filter attached!)

### Paradox: Most Sounds ARE Muffled!
User reports most sounds work correctly. Pattern observed:
- **Looping sounds** (beehive, waterwaves) - updated via POSITION-UPDATE, work correctly
- **One-shot sounds** (splashes, creature sounds) - caught in FILTER-VERIFY, show no filter

### New Hypothesis: Source Not Yet Playing

The `AL.Source` call to attach EfxDirectFilter may FAIL SILENTLY when:
1. The source is not yet in AL_PLAYING state
2. The source buffer hasn't been fully set up
3. The source is in a transitional state (just created, about to play)

**Evidence**: We patch in `Start` PREFIX, which runs BEFORE `AL.SourcePlay()` is called. The source isn't playing yet!

OpenAL EFX filters may require the source to be in a valid playing/paused state.

### Why Looping Sounds Work - HYPOTHESIS WAS WRONG

We assumed looping sounds worked because they got filter attached after starting. But LOOP-VERIFY shows they ALSO have filter=0.

**Since muffled sounds ALSO show filter=0, the GetAttachedFilter verification is BROKEN, not the filters.**

---

## Session 4: Verification Method is Broken (2026-02-05 19:44)

### Critical Realization
User observation: "Our mod does muffle the majority of sounds, but some leak through."

If most sounds ARE muffled by our mod:
- **ALL sounds showing filter=0 in verification is IMPOSSIBLE**
- The `GetAttachedFilter()` method itself is broken
- It's returning 0 regardless of actual filter state

### Why GetAttachedFilter Returns 0 For Everything

The issue is likely in our reflection. We're using:
- `ALGetSourcei.EfxDirectFilter` as the parameter
- `AL.GetSource(int, ALGetSourcei, out int)` call

But OpenAL EFX might require:
1. Different getter method signature
2. Different enum entirely for reading filter attachment
3. The value 0x20005 might not be valid for `ALGetSourcei`

**Note**: 0x20005 (EfxDirectFilter) is defined for `ALSourcei` (setter), NOT `ALGetSourcei` (getter). They may be different enums with different values!

### Conclusion About Verification

The `GetAttachedFilter` verification is unreliable and should be REMOVED to stop generating misleading logs.

### Actual Issue Pattern

Based on user observation:
- **Most sounds**: Correctly muffled (filter works)
- **Some sounds**: Play unmuffled then sometimes become muffled
- **Pattern**: May be related to sound overlapping/many sounds at once

### Next Investigation Steps

1. **Remove GetAttachedFilter verification** - It's generating false negatives
2. **Focus on the actual pattern** - When do sounds leak through?
3. **Check if issue correlates with sound count** - Source pool exhaustion?
4. **Investigate source ID recycling** - Are we attaching to wrong sources?

---

## Session 5: Deep Analysis (2026-02-05 20:00)

### Why Looping Sounds Work But One-Shots Fail

**Looping sounds** get continuous `SetPosition()` updates every frame. Each update reattaches the filter. So even if filter gets "lost", it's reattached within milliseconds.

**One-shot sounds** only get:
1. Initial setup in `SoundStartPrefix`
2. One reattach in `SoundStartPostfix`
3. One reattach in `StartPlayingFinalPostfix`

After that, no more reattachments. If something detaches the filter after these patches run, the sound plays unmuffled for its entire remaining duration.

### Critical Evidence: AL_GAIN=0 Had No Effect

From earlier testing:
> Setting AL_GAIN to 0 - Sound still played at full volume

If setting `AL_GAIN = 0` on our `sourceId` doesn't silence the sound, then **the audio is coming from a DIFFERENT source** than the one we're modifying.

VS may internally create:
- A "primary" source (stored in `sourceId` field)
- A "backup" or "crossfade" source
- Or use a completely different source for actual playback

### The "Unmute in Middle of Playback" Clue

If a sound starts muffled and becomes unmuffled mid-playback, something is **actively removing** the filter:
- VS's audio thread running parallel to our patches
- A delayed effect application that triggers after our patches complete
- Source virtualization/devirtualization in OpenAL Soft

### Potential Tests to Try

#### Test A: Filter Every Frame (Brute Force)
Instead of only reattaching on position updates, reattach ALL sound filters every game tick.
- **Pro**: Would confirm if constant reattachment solves the leak
- **Con**: Performance cost of iterating all sounds every frame
- **Status**: Marked for potential test if AL.SourcePlay hook doesn't reveal cause

#### Test B: Hook AL.SourcePlay (Diagnostic)
Patch OpenTK's `AL.SourcePlay()` to intercept every actual OpenAL play call.
- Compare the sourceId VS passes to `SourcePlay` against our tracked sourceId
- If they differ, we've found the bug (VS uses different source)
- Can attach filter right before native play call (most reliable timing)
- **Status**: IMPLEMENTING NOW

### Hypothesis Summary

| Hypothesis | Evidence For | Evidence Against |
|-----------|--------------|------------------|
| VS uses multiple sources | AL_GAIN=0 didn't work | Would affect all sounds equally |
| Source ID recycling | Worse with overlapping sounds | Code already checks for this |
| VS overwrites after patches | Explains mid-play unmute | We already patch final postfix |
| Filter ID invalidation | Random behavior | Would cause errors, not silent fails |

---

## Session 6: ROOT CAUSE FOUND (2026-02-05 20:25)

### AL.SourcePlay Hook Results

Patched ALL variants:
- `AL.SourcePlay(int)`
- `AL.SourcePlay(int ns, int[] sids)`
- `AL.SourcePlay(int ns, ref int sids)`
- `AL.SourcePlay(ReadOnlySpan<int>)`

**Result: NONE of them triggered!** VS calls native OpenAL directly via P/Invoke, bypassing OpenTK's managed wrappers entirely. The hook approach is a dead end.

### THE ACTUAL BUG: CleanupEntry Detaches Wrong Source

In `CleanupEntry()`, we had:
```csharp
if (entry.IsAttached && entry.SourceId != 0)
{
    DetachFilter(entry.SourceId);  // BUG!
}
```

**The Problem:**
When `RecalculateAll` detects "sourceId changed 39->0":
1. `entry.SourceId` still holds OLD value (39)
2. VS already recycled source 39 for a NEW sound
3. `CleanupEntry` calls `DetachFilter(39)`
4. This **removes the filter from the NEW sound**!

**Timeline:**
```
T=0ms:   Sound A starts on src=39, filter=5 attached
T=100ms: Sound A finishes, VS sets A.sourceId = 0
T=100ms: Sound B starts on src=39 (recycled), filter=6 attached
T=100ms: RecalculateAll finds A's stale entry
T=100ms: CleanupEntry(A) calls DetachFilter(39)
         → Sound B's filter is now DETACHED!
T=100ms: Sound B plays UNMUFFLED!
```

**Why looping sounds remuffled:** Next `SetPosition` update reattaches filter.
**Why one-shots stayed unmuffled:** No more updates after startup.

### The Fix

Removed `DetachFilter` call from `CleanupEntry`:
```csharp
// DO NOT detach filter from source!
// The sourceId may have been recycled for a new sound.
// Only delete the filter object - OpenAL handles cleanup.
if (entry.FilterId != 0)
{
    EfxHelper.DeleteFilter(entry.FilterId);
}
```

### Status: TESTING FIX

