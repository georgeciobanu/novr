using System;
using NOVR.Diagnostics;
using NOVR.VrCamera;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NOVR.VrUi;

public class NOUIManager : NOVRBehaviour
{
    private const float SmoothingFactor = 10f;
    private Camera? _cockpitHudCamera;
    private GameObject? _smoothedForwardReference;

    public static NOUIManager I { get; private set; }

    public Camera CockpitHudCamera => _cockpitHudCamera ??= CreateUiCamera("VrCockpitHudCamera", 100);
    public GameObject CockpitHudReference => _smoothedForwardReference ??= CreateSmoothedForwardReference();

    private GameObject CreateSmoothedForwardReference()
    {
        var go = new GameObject("SmoothedForwardReference");
        go.transform.SetParent(transform);
        
        var cam = CockpitHudCamera;
        go.transform.position = cam.transform.position;
        go.transform.localRotation = cam.transform.localRotation;
        return go;
    }

    private new void Awake()
    {
        base.Awake();
        APIBus.OnMainCameraChanged += OnMainCameraChanged;
    }

    private void OnDestroy()
    {
        APIBus.OnMainCameraChanged -= OnMainCameraChanged;
    }

    private void Start()
    {
        I = this;
        Create<UIBehaviorPatcher>(transform);
        UIBehaviorPatcher.DoPatching();
        Create<VrUiCursor>(transform);
        ConfigureUiCameras();
    }

    protected override void OnSettingChanged()
    {
        base.OnSettingChanged();
        ConfigureUiCameras();
    }

    private void Update()
    {
        ConfigureUiCameras();
        UpdateSmoothedPosition();
    }
    
    private void UpdateSmoothedPosition()
    {
        var smoothedForwardReference = CockpitHudReference;
        var cam = CockpitHudCamera;
        smoothedForwardReference.transform.position = cam.transform.position;
        smoothedForwardReference.transform.localRotation = Quaternion.Lerp(smoothedForwardReference.transform.localRotation, cam.transform.localRotation, Mathf.Clamp(Time.deltaTime * SmoothingFactor, 0, 1));
    }

    

    private Camera CreateUiCamera(string cameraName, float depth)
    {
        RenderDiagnostics.Info($"Creating NOVR UI camera: name={cameraName} depth={depth}");
        var poseDriver = Create<NOVRPoseDriver>(transform);
        poseDriver.name = cameraName;

        var camera = poseDriver.gameObject.AddComponent<Camera>();
        var additionalCameraData = poseDriver.gameObject.AddComponent<UniversalAdditionalCameraData>();
        VrCameraManager.IgnoredCameras.Add(camera);
        

        camera.stereoTargetEye = StereoTargetEyeMask.Both;
        camera.targetTexture = null;
        camera.clearFlags = CameraClearFlags.Depth;
        camera.backgroundColor = Color.clear;
        camera.depth = depth;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.cullingMask = 1 << LayerHelper.GetVrUiLayer();
        additionalCameraData.renderType = CameraRenderType.Overlay;
        additionalCameraData.allowXRRendering = true;

        RenderDiagnostics.Info($"Created NOVR UI camera: {RenderDiagnostics.DescribeCamera(camera)}; {RenderDiagnostics.DescribeUniversalAdditionalCameraData(camera.gameObject)}");
        return camera;
    }

    private void OnMainCameraChanged(Camera? previous, Camera? newCam)
    {
        RenderDiagnostics.Info($"Main camera changed for UI stack. previous={RenderDiagnostics.DescribeCamera(previous)} new={RenderDiagnostics.DescribeCamera(newCam)} cockpitHud={RenderDiagnostics.DescribeCamera(_cockpitHudCamera)}");
        newCam?.gameObject?.GetComponent<UniversalAdditionalCameraData>()?.cameraStack?.Add(_cockpitHudCamera);
        if (newCam != null)
        {
            RenderDiagnostics.Info($"Main camera URP data after UI stack add: {RenderDiagnostics.DescribeUniversalAdditionalCameraData(newCam.gameObject)}");
        }
    }

    private void ConfigureUiCameras()
    {
        ConfigureUiCamera(CockpitHudCamera);
    }

    private static void ConfigureUiCamera(Camera camera)
    {
        
        camera.clearFlags = CameraClearFlags.Depth;
        camera.backgroundColor = Color.clear;
        camera.targetTexture = null;
        camera.stereoTargetEye = StereoTargetEyeMask.Both;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 10000f;
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        var additionalCameraData = camera.GetComponent<UniversalAdditionalCameraData>();
        if (additionalCameraData != null)
        {
            additionalCameraData.allowXRRendering = true;
        }
        RenderDiagnostics.InfoOnce(
            $"configured-ui-camera-{camera.GetInstanceID()}",
            $"Configured NOVR UI camera: {RenderDiagnostics.DescribeCamera(camera)}; {RenderDiagnostics.DescribeUniversalAdditionalCameraData(camera.gameObject)}");
    }
}
