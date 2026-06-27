# vr-cursor-map-mfd-fix

This branch is based on `InfernoSuperNova/novr` `dev` at commit `f6b37bbb1914c3381ced7a9eaed960e584719bb2`.

It keeps the current dev-branch VR work, including the native VR menu, cockpit offset/recenter work, and the upstream white-rectangle fix in `NOVR/Patches/HUD/MFDScreenPatch.cs`.

## Changes

- Improves VR cursor visibility with a slightly larger cursor and a black outline.
- Keeps the cursor just in front of the UI surface it is pointing at, so it is easier to see and less likely to disappear into map elements.
- Uses a stable fallback cursor distance while the tactical map is maximized.
- Prevents non-interactive tactical map, radar, jamming, threat, and cockpit tac-screen visuals from receiving UI raycasts, so they do not block map clicks.

## Deliberately not included

- The experimental joystick select-to-mouse-click bridge. That change caused the cursor to become constrained to a small center region and was left out.
- No noisy cursor, map-raycast, or white-rectangle diagnostic logging is enabled by this branch.

## Build and install

Build the package with:

```powershell
dotnet build .\NOVR.Build\NOVR.Build.csproj -c Release
```

Install the generated package by copying both generated NOVR folders into the game BepInEx folder:

```text
build-output/game/BepInEx/plugins/NOVR
build-output/game/BepInEx/patchers/NOVR
```

Both folders are required. `plugins/NOVR` contains the runtime mod, while `patchers/NOVR` contains the BepInEx patcher and XR support payload used before the game assemblies load.
