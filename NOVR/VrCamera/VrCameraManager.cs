#if CPP
using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif
using System.Collections.Generic;
using NOVR.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NOVR.VrCamera;

public class VrCameraManager: MonoBehaviour
{
    private const string NuclearOptionMainCameraName = "Main Camera";
    private const string NuclearOptionMenuCameraName = "Menu Camera";
    private const string VrCameraChildName = "NOVR Main Camera";
    private static readonly string[] TrackedChildNames = {"cockpitRenderer", "postProcessingRenderer"};

    public static HashSet<Camera> IgnoredCameras = new();
    
    private void Update() // Todo: Make me behave on events if possible
    {
        Camera[] cameras = new Camera[Camera.allCamerasCount];
        Camera.GetAllCameras(cameras);

        foreach (var camera in cameras)
        {
            var gameObject = camera.gameObject;
            if (gameObject.name is not (NuclearOptionMainCameraName or NuclearOptionMenuCameraName) || IgnoredCameras.Contains(camera)) continue;

            RenderDiagnostics.Info($"Camera candidate found: {RenderDiagnostics.DescribeCamera(camera)}; {RenderDiagnostics.DescribeUniversalAdditionalCameraData(gameObject)}");
            if (gameObject.name == NuclearOptionMainCameraName)
            {
                SetUpMainCameraRig(camera);
            }
            else
            {
                HandleChildCameras(camera);
                gameObject.AddComponent<VrCamera>();
                IgnoredCameras.Add(camera);
            }
        }
    }

    public static Camera? GetTrackedMainCamera(GameObject gameCameraRoot)
    {
        var trackedCameraTransform = gameCameraRoot.transform.Find(VrCameraChildName);
        return trackedCameraTransform != null ? trackedCameraTransform.GetComponent<Camera>() : null;
    }

    private void SetUpMainCameraRig(Camera rootCamera)
    {
        RenderDiagnostics.Info($"Setting up main camera rig from root: {RenderDiagnostics.DescribeCamera(rootCamera)}; {RenderDiagnostics.DescribeUniversalAdditionalCameraData(rootCamera.gameObject)}");
        HandleChildCameras(rootCamera);

        var existingTrackedCamera = GetTrackedMainCamera(rootCamera.gameObject);
        if (existingTrackedCamera != null)
        {
            RenderDiagnostics.Info($"Tracked main camera already exists: {RenderDiagnostics.DescribeCamera(existingTrackedCamera)}");
            IgnoredCameras.Add(rootCamera);
            IgnoredCameras.Add(existingTrackedCamera);
            return;
        }

        var trackedCameraObject = new GameObject(VrCameraChildName);
        trackedCameraObject.transform.SetParent(rootCamera.transform, false);
        trackedCameraObject.tag = rootCamera.tag;

        var trackedCamera = trackedCameraObject.AddComponent<Camera>();
        trackedCamera.CopyFrom(rootCamera);
        trackedCamera.stereoTargetEye = StereoTargetEyeMask.Both;
        trackedCamera.targetTexture = null;
        var additionalCameraData = AdditionalCameraData.Create(trackedCamera);
        additionalCameraData?.SetRenderTypeBase();
        additionalCameraData?.SetAllowXrRendering(true);
        //additionalCameraData.GetCameraStack().AddRange(rootCamera.GetComponent<AdditionalCameraData>().GetCameraStack());

        var universalAdditionalCameraData = trackedCameraObject.GetComponent<UniversalAdditionalCameraData>();
        var rootUniversalAdditionalCameraData = rootCamera.GetComponent<UniversalAdditionalCameraData>();
        
        if (universalAdditionalCameraData != null && rootUniversalAdditionalCameraData != null)
        {
            universalAdditionalCameraData.cameraStack.AddRange(rootUniversalAdditionalCameraData.cameraStack);
            RenderDiagnostics.Info($"Copied URP camera stack from root to tracked camera. rootStack={rootUniversalAdditionalCameraData.cameraStack.Count} trackedStack={universalAdditionalCameraData.cameraStack.Count}");
        }
        else
        {
            RenderDiagnostics.Warning($"Could not copy URP camera stack. trackedData={universalAdditionalCameraData != null} rootData={rootUniversalAdditionalCameraData != null}");
        }
        
        
        var rootAudioListener = rootCamera.GetComponent<AudioListener>();
        if (rootAudioListener != null)
        {
            var trackedAudioListener = trackedCameraObject.AddComponent<AudioListener>();
            trackedAudioListener.enabled = rootAudioListener.enabled;
            rootAudioListener.enabled = false;
        }

        rootCamera.tag = "Untagged";
        rootCamera.enabled = false;
        ReparentTrackedChildren(rootCamera.transform, trackedCameraObject.transform);

        trackedCameraObject.AddComponent<VrCamera>();

        IgnoredCameras.Add(rootCamera);
        IgnoredCameras.Add(trackedCamera);
        RenderDiagnostics.Info($"Main camera rig ready. rootIgnored={RenderDiagnostics.DescribeCamera(rootCamera)} tracked={RenderDiagnostics.DescribeCamera(trackedCamera)}; {RenderDiagnostics.DescribeUniversalAdditionalCameraData(trackedCameraObject)}");
    }

    private void HandleChildCameras(Camera parentCamera)
    {
        foreach (var child in parentCamera.GetComponentsInChildren<Camera>())
        {
            if (child != parentCamera && !IgnoredCameras.Contains(child))
            {
                RenderDiagnostics.Info($"Adding StereoCamera behaviour to child camera under {parentCamera.name}: {RenderDiagnostics.DescribeCamera(child)}");
                child.gameObject.AddComponent<StereoCamera>();
            }
        }
    }

    private static void ReparentTrackedChildren(Transform rootCameraTransform, Transform trackedCameraTransform)
    {
        foreach (var childName in TrackedChildNames)
        {
            var child = rootCameraTransform.Find(childName);
            if (child != null)
            {
                child.SetParent(trackedCameraTransform, false);
            }
        }
    }
}
