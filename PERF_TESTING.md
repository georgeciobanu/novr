# NOVR Performance Test Build Notes

This document describes the experimental performance and flicker-mitigation changes in this branch, how to enable or disable each one, and what to test before submitting anything upstream.

## What Changed

### UI Flicker Mitigations

`UIBehaviorPatcher` previously added a VR UI behaviour to active UI objects, then forced a one-frame `SetActive(false)` / reactivation cycle to trigger Unity lifecycle callbacks. That can visibly blink patched canvases. The bounce is now configurable and defaults off.

`NOUIManager` also now has an optional dedupe path for the URP overlay camera stack. Without dedupe, repeated main-camera changes can add the same NOVR UI camera more than once, which can cause double-compositing or unstable ordering.

The old per-frame UI camera reconfiguration is also configurable. Rewriting camera settings every frame is unnecessary if startup and config-change setup is enough.

### Render Scale Control

`RenderScaleManager` adds an opt-in fixed render-scale control. When enabled, it applies the configured scale to:

- `XRDisplaySubsystem.scaleOfAllRenderTargets`
- URP `UniversalRenderPipelineAsset.renderScale`, when URP is active

This is not foveated rendering. It is a simple, reliable perf lever for testing resolution/performance tradeoffs.

### Diagnostics Harness

`NOVRDiagnostics` logs periodic runtime summaries. It is enabled by default for this test branch and reports:

- frame count, average frame time, max frame time, and rough over-budget frame counts
- OpenXR runtime name/version/API, render mode, extension count, and foveation extension presence when available
- XR display subsystem count/running state/render scale
- URP render scale
- UI patch counters and camera-stack counters

Look for lines starting with:

```text
NOVR diagnostics startup:
NOVR diagnostics:
NOVR render scale:
[NOVR.Patcher] Patched OpenXRSettings to force ...
```

### SinglePassInstanced Experiment

The patcher still defaults to `MultiPass`, matching upstream behavior. A disabled-by-default config flag can force `SinglePassInstanced` at startup for experiments. This must be set before launching the game because the BepInEx preloader patcher reads it before the plugin runs.

## Config Flags

Edit:

```text
BepInEx/config/deltawing.novr.cfg
```

If a key is missing, launch once with this build so BepInEx creates it.

```ini
[UI]
## Flicker fix 1. false = new behavior, no SetActive blink. true = legacy fallback.
Set Active Bounce Enabled = false

## Flicker fix 2. true = remove duplicate NOVR overlay camera stack entries.
Camera Stack Dedup Enabled = true

## Camera config churn. false = configure on startup/config changes only. true = legacy every-frame behavior.
Configure Camera Every Frame = false

[Performance]
## Master switch for fixed render-scale control.
Render Scale Enabled = false

## Used only when Render Scale Enabled = true. Valid range: 0.5 to 1.5.
Render Scale = 1

[Diagnostics]
Enabled = true
Interval Seconds = 5

[OpenXR]
## Experimental. Must be set before game startup.
Experimental Single Pass Instanced = false
```

## Test Matrix

Use one change at a time first. Keep diagnostics enabled for every run.

### Baseline

This approximates previous behavior while keeping diagnostics active.

```ini
[UI]
Set Active Bounce Enabled = true
Camera Stack Dedup Enabled = false
Configure Camera Every Frame = true

[Performance]
Render Scale Enabled = false

[OpenXR]
Experimental Single Pass Instanced = false
```

Record a log and note any visible flicker.

### Flicker Fix A: No SetActive Bounce

```ini
[UI]
Set Active Bounce Enabled = false
Camera Stack Dedup Enabled = false
Configure Camera Every Frame = true
```

Expected log signal:

- `uiSetActiveSkipped` increases
- `uiSetActivePerformed` stays at `0`

If menus or HUD elements stop initializing, turn `Set Active Bounce Enabled = true` back on.

### Flicker Fix B: Camera Stack Dedupe

```ini
[UI]
Set Active Bounce Enabled = true
Camera Stack Dedup Enabled = true
Configure Camera Every Frame = true
```

Expected log signal:

- `lastUiCameraStackSize` should stay stable
- `uiCameraStackDuplicateEntriesRemoved` should report duplicates if they happen

### Camera Config Churn Fix

```ini
[UI]
Set Active Bounce Enabled = true
Camera Stack Dedup Enabled = false
Configure Camera Every Frame = false
```

Expected result:

- UI should behave the same as baseline
- no obvious render-target or UI-camera regressions

### Render Scale

```ini
[Performance]
Render Scale Enabled = true
Render Scale = 0.85
```

Expected log signal:

```text
NOVR render scale: requested=0.85, xr=True/False, urp=True/False
```

Use several values:

- `1.0`: control
- `0.9`: mild performance test
- `0.8`: stronger performance test
- `0.7`: stress/clarity check

Watch both clarity and frame-time logs.

### SinglePassInstanced

```ini
[OpenXR]
Experimental Single Pass Instanced = true
```

Fully restart the game after changing this.

Expected patcher log:

```text
[NOVR.Patcher] Patched OpenXRSettings to force SinglePassInstanced.
```

If rendering breaks, revert to:

```ini
[OpenXR]
Experimental Single Pass Instanced = false
```

Then restart the game.

## What To Capture

For each run, save:

- the exact config values used
- `BepInEx/LogOutput.log`
- headset/runtime used
- whether the flicker is gone, reduced, unchanged, or worse
- whether menus/HUD still initialize correctly
- any visible rendering issues
- frame-time changes from diagnostics or runtime tools

## Build Notes

On macOS, `dotnet restore` for the C# projects succeeds, and `git diff --check` passes. Full compile/runtime verification still needs the Windows/game environment. Local macOS builds hit existing project/reference issues before validating this branch completely.

## Windows Desktop Handoff

Use this section when continuing from a fresh Codex session on the Windows desktop with Nuclear Option and the headset installed.

### Current Branch State

The worktree should contain these intentional changes:

```text
NOVR.Patcher/UuvrPatcher.cs
NOVR/Core.cs
NOVR/ModConfiguration.cs
NOVR/VrUi/NOUIManager.cs
NOVR/VrUi/UIBehaviorPatcher.cs
NOVR/NOVRDiagnostics.cs
NOVR/NOVRDiagnosticsCounters.cs
NOVR/RenderScaleManager.cs
PERF_TESTING.md
```

No generated build output should be committed.

Recommended branch name:

```text
perf-diagnostics-render-scale
```

### Goal On Windows

1. Build the solution successfully.
2. Deploy the build into the Nuclear Option BepInEx install.
3. Launch the game normally with the headset/runtime active.
4. Verify that the plugin starts and writes diagnostics.
5. Run the test matrix above one config group at a time.
6. Save logs and note visible headset behavior.

### Environment Setup

Install or verify:

- Visual Studio or Build Tools with MSBuild and C++ targets, needed for `Uuvr.XInput`.
- .NET SDK capable of building the solution.
- .NET Framework 4.8 developer/targeting pack.
- Git.
- Nuclear Option installed and runnable.
- BepInEx installed for Nuclear Option.
- Headset runtime active before launching the game.

If dependencies are missing on the Windows desktop, install them there. It is safe to modify that machine.

### Build Commands

From the repo root:

```powershell
dotnet restore NuclearOptionVirtualRealityMod.sln
dotnet build NuclearOptionVirtualRealityMod.sln
```

If `dotnet build` cannot handle the C++ project, use Visual Studio/MSBuild:

```powershell
msbuild NuclearOptionVirtualRealityMod.sln /t:Restore
msbuild NuclearOptionVirtualRealityMod.sln /p:Configuration=Debug
```

If the repo auto-detects the Nuclear Option install, build output may deploy automatically through the existing MSBuild targets. If not, inspect:

```text
build-output/
```

and copy the plugin/patcher files into the matching BepInEx folders for the game.

### Game Paths To Check

Likely paths:

```text
<Nuclear Option>\BepInEx\plugins\NOVR\
<Nuclear Option>\BepInEx\patchers\NOVR\
<Nuclear Option>\BepInEx\config\deltawing.novr.cfg
<Nuclear Option>\BepInEx\LogOutput.log
```

Confirm the loaded files in the game install are the freshly built ones, not an older NOVR release.

### First Launch Checklist

Before changing config values, launch once with defaults.

Confirm these log lines or equivalents:

```text
[NOVR.Patcher] Patched OpenXRSettings to force MultiPass.
NOVR diagnostics startup:
NOVR diagnostics:
```

Confirm the config file contains the new keys:

```ini
[UI]
Set Active Bounce Enabled = false
Camera Stack Dedup Enabled = true
Configure Camera Every Frame = false

[Performance]
Render Scale Enabled = false
Render Scale = 1

[Diagnostics]
Enabled = true
Interval Seconds = 5

[OpenXR]
Experimental Single Pass Instanced = false
```

### Runtime Verification Priorities

Start with diagnostics only, then isolate changes:

1. Baseline legacy-style UI behavior with diagnostics enabled.
2. Disable `Set Active Bounce Enabled` only.
3. Enable `Camera Stack Dedup Enabled` only.
4. Disable `Configure Camera Every Frame` only.
5. Enable `Render Scale Enabled` with `Render Scale = 0.85`.
6. Try `Experimental Single Pass Instanced = true` only after the previous tests are understood.

Restart the game after changing `Experimental Single Pass Instanced`. Other settings should generally apply through BepInEx config change notifications, but restarting between test cases is cleaner.

### What The Next Session Should Report Back

For each test case:

- config values used
- whether the game launched
- headset runtime name from diagnostics
- OpenXR render mode from diagnostics
- whether flicker is gone/reduced/unchanged/worse
- whether any UI failed to initialize
- whether render scale changed clarity/performance
- any SinglePass visual breakage
- relevant `NOVR diagnostics:` lines
- relevant `[NOVR.Patcher]` lines

### Known Mac-Side Limitation

The current Mac session could not fully compile or run the game. It verified restore and whitespace only. Windows/headset testing is the source of truth.

## GitHub Workflow

Create a branch and commit:

```bash
git switch -c perf-diagnostics-render-scale
git add NOVR.Patcher/UuvrPatcher.cs \
  NOVR/Core.cs \
  NOVR/ModConfiguration.cs \
  NOVR/VrUi/NOUIManager.cs \
  NOVR/VrUi/UIBehaviorPatcher.cs \
  NOVR/NOVRDiagnostics.cs \
  NOVR/NOVRDiagnosticsCounters.cs \
  NOVR/RenderScaleManager.cs \
  PERF_TESTING.md

git commit -m "Add diagnostics and experimental performance controls"
git push -u origin perf-diagnostics-render-scale
```

That makes the branch available from any computer through GitHub.

For an upstream PR, wait until Windows/headset testing confirms the behavior. In the PR description, present the work as:

1. configurable UI flicker mitigations
2. default-on diagnostic logging for test builds
3. opt-in render scale control
4. disabled-by-default SinglePassInstanced experiment

Call out that `SinglePassInstanced` remains off by default and should be treated as experimental.
