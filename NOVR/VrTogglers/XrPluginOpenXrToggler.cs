
using System.Linq;
using NOVR.Diagnostics;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace NOVR.VrTogglers;

public class XrPluginOpenXrToggler : XrPluginToggler
{
    protected override XRLoader CreateLoader()
    {
        var xrLoader = ScriptableObject.CreateInstance<OpenXRLoader>();
        RenderDiagnostics.Info($"Created OpenXR loader instance: {xrLoader.GetType().FullName}");
        return xrLoader;
    }

    protected override void ConfigureBeforeInitialize()
    {
        var renderMode = ModConfiguration.Instance?.EffectiveOpenXrRenderMode ?? OpenXrStereoRenderMode.SinglePassInstanced;
        RenderDiagnostics.Info($"OpenXR before initialize: requestedConfig={ModConfiguration.Instance?.OpenXrRenderMode.Value.ToString() ?? "<no config>"} effective={renderMode} settingsBefore={DescribeOpenXrSettings()}");
        if (renderMode == OpenXrStereoRenderMode.Default)
        {
            RenderDiagnostics.Info("Leaving OpenXR render mode at Unity default.");
            return;
        }

        OpenXRSettings.Instance.renderMode = renderMode == OpenXrStereoRenderMode.SinglePassInstanced
            ? OpenXRSettings.RenderMode.SinglePassInstanced
            : OpenXRSettings.RenderMode.MultiPass;
        OpenXRSettings.Instance.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None;

        RenderDiagnostics.Info($"OpenXR settings after request: {DescribeOpenXrSettings()}");
    }

    protected override void ConfigureAfterInitialize()
    {
        RenderDiagnostics.Info($"OpenXR after initialize: {DescribeOpenXrSettings()}");
        RenderDiagnostics.Info($"OpenXR enabled features: {DescribeOpenXrFeatures()}");
    }

    private static string DescribeOpenXrSettings()
    {
        var settings = OpenXRSettings.Instance;
        return $"renderMode={settings.renderMode} depthSubmissionMode={settings.depthSubmissionMode} featureCount={settings.GetFeatures().Length}";
    }

    private static string DescribeOpenXrFeatures()
    {
        var features = OpenXRSettings.Instance.GetFeatures();
        if (features.Length == 0)
        {
            return "none";
        }

        return string.Join(", ", features
            .Where(feature => feature != null)
            .Select(feature => $"{feature.GetType().Name}:enabled={feature.enabled}"));
    }
}
