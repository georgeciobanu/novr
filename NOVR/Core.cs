using System;
using System.Reflection;
using NOVR.Diagnostics;
using NOVR.VrCamera;
using NOVR.VrTogglers;
using NOVR.VrUi;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace NOVR;

public class Core : MonoBehaviour
{
    
    private float _originalFixedDeltaTime;

    private NOVRHeadsetData? _headsetData;
    private NOUIManager? _vrUi;
    private PropertyInfo? _refreshRateProperty;
    private VrTogglerManager? _vrTogglerManager;
    
    private Aircraft _aircraft;
    private Aircraft _oldAircraft;

    public static void Create()
    {
        new GameObject("NOVR").AddComponent<Core>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        RenderDiagnostics.Info("Core Awake: creating persistent NOVR services.");
        gameObject.AddComponent<RenderPipelineDiagnostics>();
        gameObject.AddComponent<VrCameraManager>();
        gameObject.AddComponent<APIBus>();
    }

    private void OnDestroy()
    {
        Debug.Log("NOVR has been destroyed. This shouldn't have happened. Recreating...");
        
        Create();
    }

    private void Start()
    {
        RenderDiagnostics.Info("Core Start: resolving XR refresh-rate API and creating runtime behaviours.");
        var xrDeviceType = Type.GetType("UnityEngine.XR.XRDevice, UnityEngine.XRModule") ??
                           Type.GetType("UnityEngine.XR.XRDevice, UnityEngine.VRModule") ??
                           Type.GetType("UnityEngine.VR.VRDevice, UnityEngine.VRModule") ??
                           Type.GetType("UnityEngine.VR.VRDevice, UnityEngine");

        _refreshRateProperty = xrDeviceType?.GetProperty("refreshRate");
        RenderDiagnostics.Info($"XR refresh-rate source type={xrDeviceType?.FullName ?? "<not found>"} propertyFound={_refreshRateProperty != null}");
        
        _headsetData = NOVRBehaviour.Create<NOVRHeadsetData>(transform);
        _vrUi = NOVRBehaviour.Create<NOUIManager>(transform);
        RenderDiagnostics.Info("Core Start: headset data and VR UI behaviours created.");
        
        _vrTogglerManager = new VrTogglerManager();
        RenderDiagnostics.Info("Core Start: VR toggler manager created.");
    }



    private void Update()
    {
        UpdatePhysicsRate();
    }

    private void UpdatePhysicsRate()
    {
        if (_originalFixedDeltaTime == 0)
        {
            _originalFixedDeltaTime = Time.fixedDeltaTime;
        }

        if (_refreshRateProperty == null) return;

        var headsetRefreshRate = (float)_refreshRateProperty.GetValue(null, null);
        if (headsetRefreshRate <= 0) return;


        Time.fixedDeltaTime = _originalFixedDeltaTime;
        
    }
    private void FixedUpdate()
    {
        _oldAircraft = _aircraft;
        GameManager.GetLocalAircraft(out _aircraft);
        if (_aircraft != _oldAircraft) NOVRHeadsetData.CalibrateTranslation();
        CameraStateManager.enableMouseLook = false;
    }

}
