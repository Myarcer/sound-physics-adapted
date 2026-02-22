# Resonator Patch Analysis

## 1. Architectural Overview

The current Resonator implementation is split across three patch files, tackling different aspects of the block's behavior:

*   **`ResonatorPatch.cs` (Audio/Help):**
    *   **Scope:** Spatial audio positioning (Centering sound) and visual Interaction Help.
    *   **Technique:** Manual Harmony patching of `OnClientTick` and `GetPlacedBlockInteractionHelp`.
    *   **Key Logic:** Forces sound position to block center, updates `AudioRenderer`, and suppresses vanilla volume/distance logic.

*   **`ResonatorLogicPatch.cs` (State/Logic):**
    *   **Scope:** Pause/Resume functionality, Persistence, Network Sync, and Animation Control.
    *   **Technique:** mix of Manual and Attribute-based Harmony patches (`OnInteract`, `Stop/StartMusic`, `TreeAttributes`).
    *   **Key Logic:**
        *   Intercepts Shift+RightClick for Pause/Resume.
        *   Syncs playback position and paused state between Client/Server.
        *   **Animation Hacking:** Freezes the vanilla `animUtil` "running" animation by setting speed to 0 instead of stopping it (crucial for visual state persistence).
        *   Handles chunk save/load persistence via `TreeAttributes` and fallback Dictionaries.

*   **`ResonatorRendererPatch.cs` (Visuals):**
    *   **Scope:** Disc rotation freezing.
    *   **Technique:** Manual Harmony patching of internal `ResonatorRenderer`.
    *   **Key Logic:** Manually calculates and "freezes" the disc rotation by manipulating `updatedTotalMs`, as the vanilla renderer derives rotation from elapsed time.

## 2. Architectural Analysis

### ✅ What is Good
1.  **Robust Reflection:** The code uses `AccessTools` and type searching (scanning all assemblies) to locate `Vintagestory.GameContent.BlockEntityResonator`. This is critical because `VSSurvivalMod.dll` is not a guaranteed compile-time dependency.
2.  **Lifecycle Management:** The use of `ConditionalWeakTable` for runtime instances backed by `Dictionary<BlockPos, ...>` for persistence (surviving chunk unloads) is a sophisticated and correct solution to the problem of VS BlockEntity lifecycle (where objects are destroyed/recreated frequently).
3.  **Visual Fidelity:** The dual-patching of Animation (LogicPatch) and Disc Rotation (RendererPatch) correctly addresses the fact that the Resonator has two moving parts:
    *   **The Arm:** Controlled by `animUtil` (patched in `LogicPatch`).
    *   **The Disc:** Controlled by `ResonatorRenderer` (patched in `RendererPatch`).
    *   *Verification:* Decompiled vanilla code confirms `animUtil` is used for a "running" animation (likely arm movement), while the renderer handles the disc.

### ⚠️ What Could Be Optimized (Shortening)

While functional, the code suffers from fragmentation and repetition.

1.  **Duplicate Reflection Logic:**
    *   `ResonatorPatch` and `ResonatorLogicPatch` both implement their own logic to find `BlockEntityResonator`, `track`, and `sound` fields.
    *   **Optimization:** Centralize reflection into a shared helper or single class.

2.  **Scattered Patch Registration:**
    *   `SoundPhysicsAdaptedModSystem.cs` calls `ResonatorPatch.Patch`, `ResonatorPatch.PatchBlockInteractionHelp`, `ResonatorLogicPatch.ApplyManualPatches`, and `ResonatorRendererPatch.ApplyPatches`.
    *   **Optimization:** Consolidate into a single `ResonatorSystem.ApplyPatches(harmony, api)`.

3.  **File Fragmentation:**
    *   `ResonatorPatch.cs` is relatively small and logically belongs with `ResonatorLogicPatch.cs`.
    *   **Optimization:** Merge `ResonatorPatch.cs` into `ResonatorLogicPatch.cs` (renaming to `ResonatorSystem.cs` or `ResonatorPatches.cs`). Keep `RendererPatch` separate as it deals with a distinct internal system (Rendering).

4.  **Redundant Logic:**
    *   `ResonatorPatch.OnClientTickPrefix` overrides sound position.
    *   `ResonatorLogicPatch` *also* hooks `OnClientTick` (Postfix) for networking.
    *   These could be combined or cleaner if in the same file.

## 3. Proposal: Refactoring Plan (Refined)

To shorten code while strictly maintaining the **Client/Server architectural separation**:

1.  **Consolidate Reflection (The "Glue"):**
    *   Create a simple internal helper `ResonatorReflection.cs` (or inside the main patch file).
    *   **Goal:** Run `AccessTools` once. Both Client and Server patches need to access private fields (`track`, `sound`, `renderer`).
    *   **Benefit:** Removes ~40 lines of duplicate "Try/Catch/Find Type" boilerplate from each file.

2.  **Refactor `ResonatorLogicPatch.cs` -> `ResonatorPatches.cs`:**
    *   **Action:** This becomes the primary file.
    *   **Integration:** Move the `OnClientTick` (Audio positioning) logic from `ResonatorPatch.cs` into this file, specifically into a `ClientAudio` region.
    *   **Separation Strategy:** Use strict C# `#region` blocks or internal static classes to separate usage:
        *   `#region Shared_Logic` (OnInteract - needs both for prediction prevention)
        *   `#region Server_Persistence` (ToTreeAttributes/Sync)
        *   `#region Client_Audio` (The moved Logic from ResonatorPatch.cs)
        *   `#region Client_Animation` (The existing animation hacks)

3.  **Keep `ResonatorRendererPatch.cs` Separate:**
    *   **Reason:** This touches the *Rendering* thread/system, which is distinct from the *Logic/Audio* system. Merging this would blur the architectural lines too much.

4.  **Registration Cleanup:**
    *   ModSystem checks `api.Side`.
    *   Calls `ResonatorPatches.ApplyShared(harmony)` (Both)
    *   Calls `ResonatorPatches.ApplyClient(harmony)` (Client Only)
    *   Calls `ResonatorPatches.ApplyServer(harmony)` (Server Only)

### Resulting Structure
*   `Patches/ResonatorPatches.cs` (Logic + Audio + Networking) - *Heavily optimized*::
    *   Handles "What the resonator **IS DOING**" (Playing, Pausing, Syncing, Emitting Sound).
*   `Patches/ResonatorRendererPatch.cs` (Visuals) - *Kept separate*:
    *   Handles "What the resonator **LOOKS LIKE**" (Spinning disc).

### Estimated Gains
*   **Lines of Code:** ~ -100 lines (deduplication).
*   **Clarity:** Better. All "Behavior" is in one place, all "Rendering" in another.
*   **Safety:** Explicit Apply methods (`ApplyShared`, `ApplyClient`) prevent accidental server-side execution of client code.
