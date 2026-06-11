using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using NOVR.Diagnostics;
using NOVR.VrCamera;
using NOVR.VrUi;
using NOVR.VrUi.SpecialBehavior;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

#if CPP
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
#endif

namespace NOVR;

[BepInPlugin(
    "deltawing.novr",
    "NOVR",
    "0.3.1")]
public class NOVRPlugin : BaseUnityPlugin
{
    
    private static NOVRPlugin _instance;
    public static string ModFolderPath { get; private set; }

    public NOVRPlugin()
    {
        InputTracking.trackingAcquired += TrackingAcquired;
        _instance = this;
        ModFolderPath = Path.GetDirectoryName(Assembly.GetAssembly(typeof(NOVRPlugin)).Location);
        
        new ModConfiguration(Config);
        RenderDiagnostics.Info($"Plugin constructed. modFolder={ModFolderPath} renderMode={ModConfiguration.Instance.OpenXrRenderMode.Value} effectiveRenderMode={ModConfiguration.Instance.EffectiveOpenXrRenderMode}");
        RenderDiagnostics.Info($"Diagnostics enabled={ModConfiguration.Instance.RenderDiagnosticsEnabled.Value}");
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
        RenderDiagnostics.Info("Harmony patches applied.");
        Core.Create();
    }

    private void TrackingAcquired(XRNodeState obj)
    {
        NOVRHeadsetData.CalibrateTranslation();
    }
     
    private void Awake()
    {

    }
    
    
}
