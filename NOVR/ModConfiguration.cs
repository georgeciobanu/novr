using System.ComponentModel;
using BepInEx.Configuration;
using UnityEngine;

namespace NOVR;

public class ModConfiguration
{
    public static ModConfiguration Instance;

    private const string OpenXrRenderModeEnvironmentVariable = "NOVR_OPENXR_RENDER_MODE";

    public readonly ConfigFile Config;
    public readonly ConfigEntry<float> TargetDesignatorOvershoot;
    public readonly ConfigEntry<OpenXrStereoRenderMode> OpenXrRenderMode;
    public readonly ConfigEntry<bool> RenderDiagnosticsEnabled;
    public readonly ConfigEntry<bool> DynamicMapVrCursorFixEnabled;
    public readonly ConfigEntry<bool> DynamicMapVrCursorDiagnosticsEnabled;

    public OpenXrStereoRenderMode EffectiveOpenXrRenderMode =>
        TryParseOpenXrRenderMode(
            System.Environment.GetEnvironmentVariable(OpenXrRenderModeEnvironmentVariable),
            out var environmentRenderMode)
            ? environmentRenderMode
            : OpenXrRenderMode.Value;

    public ModConfiguration(ConfigFile config)
    {
        Instance = this;

        Config = config;
        TargetDesignatorOvershoot = config.Bind(
            "General",
            "Target Designator Overshoot",
            1.2f,
            "How much the target designator should multiply rotation to make for easier high off boresight target designation. Set to 1.0 to disable");

        OpenXrRenderMode = config.Bind(
            "OpenXR",
            "Render Mode",
            OpenXrStereoRenderMode.SinglePassInstanced,
            "OpenXR stereo render mode. SinglePassInstanced is the performance-oriented default. MultiPass is a compatibility fallback. Default leaves Unity OpenXR's built-in setting unpatched.");

        RenderDiagnosticsEnabled = config.Bind(
            "Diagnostics",
            "Render Diagnostics Enabled",
            true,
            "Log key OpenXR, XR loader, render pipeline, URP camera, and mission launch diagnostics.");

        DynamicMapVrCursorFixEnabled = config.Bind(
            "UI",
            "Dynamic Map VR Cursor Fix Enabled",
            true,
            "Use NOVR's VR cursor position for DynamicMap hit tests that normally read UnityEngine.Input.mousePosition.");

        DynamicMapVrCursorDiagnosticsEnabled = config.Bind(
            "Diagnostics",
            "Dynamic Map VR Cursor Diagnostics Enabled",
            true,
            "Log DynamicMap VR cursor hit-test details when map icons are clicked or selected.");
    }

    public static bool TryParseOpenXrRenderMode(string? value, out OpenXrStereoRenderMode renderMode)
    {
        renderMode = OpenXrStereoRenderMode.MultiPass;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value!
            .Trim()
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "default":
            case "unitydefault":
                renderMode = OpenXrStereoRenderMode.Default;
                return true;
            case "multipass":
            case "multi":
                renderMode = OpenXrStereoRenderMode.MultiPass;
                return true;
            case "singlepassinstanced":
            case "singlepass":
            case "spi":
                renderMode = OpenXrStereoRenderMode.SinglePassInstanced;
                return true;
            default:
                return false;
        }
    }
}

public enum OpenXrStereoRenderMode
{
    Default,
    MultiPass,
    SinglePassInstanced
}
