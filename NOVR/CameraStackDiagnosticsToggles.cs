using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NOVR;

internal sealed class CameraStackDiagnosticsToggles : MonoBehaviour
{
    private readonly Dictionary<Camera, bool> _originalEnabled = new();
    private readonly List<RemovedStackCamera> _removedStackCameras = new();

    private void LateUpdate()
    {
        RestoreDisabledCamerasIfNeeded();

        var cameras = new Camera[Camera.allCamerasCount];
        Camera.GetAllCameras(cameras);

        foreach (var camera in cameras)
        {
            if (camera == null) continue;

            var disabled = ShouldDisable(camera);
            ApplyCameraEnabled(camera, disabled);
        }

        UpdateCameraStacks(cameras);
    }

    private void RestoreDisabledCamerasIfNeeded()
    {
        var trackedCameras = new List<Camera>(_originalEnabled.Keys);
        var destroyedCameras = new List<Camera>();

        foreach (var camera in trackedCameras)
        {
            if (camera == null)
            {
                destroyedCameras.Add(camera!);
                continue;
            }

            if (!ShouldDisable(camera))
            {
                ApplyCameraEnabled(camera, disabled: false);
            }
        }

        foreach (var camera in destroyedCameras)
        {
            _originalEnabled.Remove(camera);
        }
    }

    private static bool ShouldDisable(Camera camera)
    {
        var config = ModConfiguration.Instance;
        var path = GetPath(camera.transform);

        if (config.DisablePostProcessingRenderer.Value && path.Contains("postProcessingRenderer")) return true;
        if (config.DisableCockpitRenderer.Value && path.Contains("cockpitRenderer")) return true;
        if (config.DisableVrHudCamera.Value && path.Contains("VrCockpitHudCamera")) return true;
        if (config.DisableMfdScreenCameras.Value && IsMfdScreenCamera(camera, path)) return true;
        if (config.DisableReflectionProbeCameras.Value && IsReflectionProbeCamera(camera, path)) return true;

        return false;
    }

    private void ApplyCameraEnabled(Camera camera, bool disabled)
    {
        if (disabled)
        {
            if (!_originalEnabled.ContainsKey(camera))
            {
                _originalEnabled[camera] = camera.enabled;
            }

            if (camera.enabled)
            {
                camera.enabled = false;
                Debug.Log($"[NOVR] Camera diagnostic toggle disabled camera '{GetPath(camera.transform)}'.");
            }

            return;
        }

        if (!_originalEnabled.TryGetValue(camera, out var originalEnabled)) return;

        if (camera.enabled != originalEnabled)
        {
            camera.enabled = originalEnabled;
            Debug.Log($"[NOVR] Camera diagnostic toggle restored camera '{GetPath(camera.transform)}' enabled={originalEnabled}.");
        }

        _originalEnabled.Remove(camera);
    }

    private void UpdateCameraStacks(Camera[] cameras)
    {
        RestoreStackCameras();

        foreach (var owner in cameras)
        {
            if (owner == null) continue;

            var ownerData = owner.GetComponent<UniversalAdditionalCameraData>();
            if (ownerData == null || ownerData.renderType != CameraRenderType.Base) continue;

            var stack = ownerData.cameraStack;
            if (stack == null || stack.Count == 0) continue;

            for (var index = stack.Count - 1; index >= 0; index--)
            {
                var stackedCamera = stack[index];
                if (stackedCamera == null || !ShouldDisable(stackedCamera)) continue;

                stack.RemoveAt(index);
                _removedStackCameras.Add(new RemovedStackCamera(ownerData, stackedCamera));
                Debug.Log($"[NOVR] Camera diagnostic toggle removed '{GetPath(stackedCamera.transform)}' from stack on '{GetPath(owner.transform)}'.");
            }
        }
    }

    private void RestoreStackCameras()
    {
        for (var index = _removedStackCameras.Count - 1; index >= 0; index--)
        {
            var removed = _removedStackCameras[index];
            if (removed.OwnerData == null || removed.Camera == null || ShouldDisable(removed.Camera)) continue;

            var stack = removed.OwnerData.cameraStack;
            if (stack != null && !stack.Contains(removed.Camera))
            {
                stack.Add(removed.Camera);
                Debug.Log($"[NOVR] Camera diagnostic toggle restored '{GetPath(removed.Camera.transform)}' to a camera stack.");
            }

            _removedStackCameras.RemoveAt(index);
        }
    }

    private static bool IsMfdScreenCamera(Camera camera, string path)
    {
        return camera.targetTexture != null &&
               (path.Contains("screenCam") ||
                path.Contains("tacScreen") ||
                path.Contains("MFD") ||
                path.Contains("mfd"));
    }

    private static bool IsReflectionProbeCamera(Camera camera, string path)
    {
        return path.Contains("Reflection Probes Camera") ||
               path.Contains("ReflectionProbe") ||
               (camera.name.Contains("Reflection") && camera.name.Contains("Probe"));
    }

    private static string GetPath(Transform transform)
    {
        var parts = new Stack<string>();
        var current = transform;

        while (current != null)
        {
            parts.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", parts);
    }

    private readonly struct RemovedStackCamera
    {
        public RemovedStackCamera(UniversalAdditionalCameraData ownerData, Camera camera)
        {
            OwnerData = ownerData;
            Camera = camera;
        }

        public UniversalAdditionalCameraData OwnerData { get; }
        public Camera Camera { get; }
    }
}
