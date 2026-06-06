using System.ComponentModel;
using BepInEx.Configuration;
using UnityEngine;

namespace NOVR;

public class ModConfiguration
{
    public static ModConfiguration Instance;
    

    public readonly ConfigFile Config;
    public readonly ConfigEntry<float> TargetDesignatorOvershoot;
    public readonly ConfigEntry<bool> UiSetActiveBounceEnabled;
    public readonly ConfigEntry<bool> UiCameraStackDedupEnabled;
    public readonly ConfigEntry<bool> UiConfigureCameraEveryFrame;
    public readonly ConfigEntry<bool> RenderScaleEnabled;
    public readonly ConfigEntry<float> RenderScale;
    public readonly ConfigEntry<bool> DiagnosticsEnabled;
    public readonly ConfigEntry<float> DiagnosticsIntervalSeconds;
    public readonly ConfigEntry<bool> ExperimentalSinglePassInstanced;

    public ModConfiguration(ConfigFile config)
    {
        Instance = this;

        Config = config;
        TargetDesignatorOvershoot = config.Bind(
            "General",
            "Target Designator Overshoot",
            1.2f,
            "How much the target designator should multiply rotation to make for easier high off boresight target designation. Set to 1.0 to disable");

        UiSetActiveBounceEnabled = config.Bind(
            "UI",
            "Set Active Bounce Enabled",
            false,
            "Legacy fallback for forcing UI lifecycle callbacks by disabling then re-enabling patched UI objects. Leave off unless a menu or HUD stops initializing.");

        UiCameraStackDedupEnabled = config.Bind(
            "UI",
            "Camera Stack Dedup Enabled",
            true,
            "Removes duplicate NOVR overlay camera entries before adding the UI camera to the active URP camera stack.");

        UiConfigureCameraEveryFrame = config.Bind(
            "UI",
            "Configure Camera Every Frame",
            false,
            "Legacy fallback. Rewrites NOVR UI camera settings every frame instead of only at startup/config changes.");

        RenderScaleEnabled = config.Bind(
            "Performance",
            "Render Scale Enabled",
            false,
            "Applies the Performance.Render Scale value to XR and URP render targets. Leave off to preserve game/runtime defaults.");

        RenderScale = config.Bind(
            "Performance",
            "Render Scale",
            1.0f,
            new ConfigDescription(
                "Applies a fixed XR render-target scale. Lower values improve performance at the cost of clarity.",
                new AcceptableValueRange<float>(0.5f, 1.5f)));

        DiagnosticsEnabled = config.Bind(
            "Diagnostics",
            "Enabled",
            true,
            "Logs periodic NOVR runtime diagnostics to help verify fixes without visual inspection.");

        DiagnosticsIntervalSeconds = config.Bind(
            "Diagnostics",
            "Interval Seconds",
            5.0f,
            new ConfigDescription(
                "Seconds between periodic diagnostics summaries.",
                new AcceptableValueRange<float>(1.0f, 60.0f)));

        ExperimentalSinglePassInstanced = config.Bind(
            "OpenXR",
            "Experimental Single Pass Instanced",
            false,
            "Experimental. If true before game startup, the preloader patcher asks OpenXR to use SinglePassInstanced instead of MultiPass.");
    }
}
