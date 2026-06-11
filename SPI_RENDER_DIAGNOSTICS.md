# SPI Render Diagnostics

This branch adds key-point diagnostics for troubleshooting Single Pass Instanced
rendering in NOVR. The goal is to show what Unity/OpenXR/URP is loading and
when, without logging per texture, draw call, or frame.

## Branch

`spi-render-diagnostics`

## Runtime Controls

OpenXR render mode:

- Config: `BepInEx/config/deltawing.novr.cfg`
- Key: `[OpenXR] Render Mode`
- Values: `SinglePassInstanced`, `MultiPass`, `Default`
- Environment override: `NOVR_OPENXR_RENDER_MODE=SinglePassInstanced|MultiPass|Default`

Diagnostics:

- Config key: `[Diagnostics] Render Diagnostics Enabled`
- Default: `true`

The preloader runs before the plugin config exists on a first launch. If an
immediate fallback is needed before the config file has been generated, set:

```text
NOVR_OPENXR_RENDER_MODE=MultiPass
```

## What Gets Logged

### Patcher / Preloader

Purpose: verify what render mode is forced before `OpenXRSettings.ApplySettings`
and `Awake` run.

Logs include:

- Whether `UnityEngine.XR.OpenXR.OpenXRSettings` and `m_renderMode` were found.
- Which source selected the render mode: env var, legacy env var, config, or
  default.
- Whether the patcher left OpenXR unpatched via `Default`.
- Which OpenXR methods had render-mode assignments injected.

### Plugin Startup

Purpose: verify config and Harmony setup.

Logs include:

- Mod folder path.
- Configured and effective OpenXR render mode.
- Whether render diagnostics are enabled.
- Harmony patch application.

### XR Loader Lifecycle

Purpose: verify the order of OpenXR setup and whether the loader/subsystems
actually start.

Logs include:

- XR manager/general settings creation.
- Loader instance type.
- Loader list before initialization.
- `InitializeLoaderSync` start and finish.
- Active loader before/after initialization.
- `StartSubsystems` and `StopSubsystems`.
- `XRSettings` state and loaded XR display subsystems.

### OpenXR Settings

Purpose: verify the runtime render mode and feature set.

Logs include:

- OpenXR settings before render-mode configuration.
- Effective render mode requested by NOVR.
- OpenXR settings after NOVR applies the render mode.
- OpenXR settings after loader initialization.
- Enabled OpenXR feature summary.

### Render Pipeline

Purpose: identify URP/SRP state and first-frame camera participation.

Logs include:

- Current and default render pipeline assets.
- Scene load events.
- XR state on scene load.
- First SRP frame camera count and camera summary.
- Render pipeline asset changes.

### Cameras / URP Additional Camera Data

Purpose: troubleshoot grey/black-eye or left-eye-only issues caused by camera
stacking, `targetTexture`, stereo target eye, or URP XR flags.

Logs include:

- Candidate Nuclear Option main/menu cameras found by NOVR.
- Root camera copied into the tracked VR camera.
- Tracked camera `stereoTargetEye`, `targetTexture`, clear flags, depth, and
  culling mask.
- URP `UniversalAdditionalCameraData` availability.
- URP `renderType`, `allowXRRendering`, and camera stack count.
- Camera stack copy results.
- NOVR UI camera creation and UI stack insertion.

### Furball Quick Launch

Purpose: distinguish UI click issues from mission discovery or mission host
startup issues.

Logs include:

- Quick-launch button creation.
- Mission entry count.
- Selected Furball mission key/name.
- Mission load failure.
- Offline single-player host startup map key.

## Interpreting SPI Failures

If the logs show `renderMode=SinglePassInstanced`, XR displays are running, and
NOVR cameras have `stereoEye=Both`, `targetTexture=null`, and
`allowXRRendering=True`, but one eye is grey/black, the likely remaining cause is
a URP pass, post-processing pass, UI shader, or game shader that was not built
with SPI-compatible stereo variants.

In Unity terms, generating stereo shader variants means building shaders with
Single Pass Instanced support so the compiled shader variants include stereo
instancing. Runtime DLLs can set OpenXR render mode and provide XR behaviours,
but they cannot regenerate baked shader variants from the shipped game build.
