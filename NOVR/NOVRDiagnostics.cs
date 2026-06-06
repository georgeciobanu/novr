using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;

namespace NOVR;

public class NOVRDiagnostics : MonoBehaviour
{
    private readonly List<XRDisplaySubsystem> _displaySubsystems = new();
    private float _elapsed;
    private float _deltaSum;
    private float _deltaMax;
    private int _frameCount;
    private int _framesOver11Ms;
    private int _framesOver14Ms;
    private int _framesOver22Ms;
    private bool _loggedStartup;

    private void Update()
    {
        var config = ModConfiguration.Instance;
        if (config == null || !config.DiagnosticsEnabled.Value)
        {
            ResetFrameStats();
            return;
        }

        if (!_loggedStartup)
        {
            _loggedStartup = true;
            Debug.Log($"NOVR diagnostics startup: {BuildRuntimeSummary()}");
        }

        var delta = Time.unscaledDeltaTime;
        _elapsed += delta;
        _deltaSum += delta;
        _deltaMax = Mathf.Max(_deltaMax, delta);
        _frameCount++;

        if (delta > 0.0111f) _framesOver11Ms++;
        if (delta > 0.0140f) _framesOver14Ms++;
        if (delta > 0.0222f) _framesOver22Ms++;

        var interval = Mathf.Clamp(config.DiagnosticsIntervalSeconds.Value, 1.0f, 60.0f);
        if (_elapsed < interval)
        {
            return;
        }

        LogSummary();
        ResetFrameStats();
    }

    private void LogSummary()
    {
        var averageDelta = _frameCount > 0 ? _deltaSum / _frameCount : 0.0f;
        var averageFps = averageDelta > 0.0f ? 1.0f / averageDelta : 0.0f;

        Debug.Log(
            "NOVR diagnostics: " +
            $"frames={_frameCount}, " +
            $"avgMs={averageDelta * 1000.0f:0.00}, " +
            $"maxMs={_deltaMax * 1000.0f:0.00}, " +
            $"avgFps={averageFps:0.0}, " +
            $"over11ms={_framesOver11Ms}, " +
            $"over14ms={_framesOver14Ms}, " +
            $"over22ms={_framesOver22Ms}, " +
            $"{BuildRuntimeSummary()}, " +
            $"{NOVRDiagnosticsCounters.ConsumeSummary()}");
    }

    private void ResetFrameStats()
    {
        _elapsed = 0.0f;
        _deltaSum = 0.0f;
        _deltaMax = 0.0f;
        _frameCount = 0;
        _framesOver11Ms = 0;
        _framesOver14Ms = 0;
        _framesOver22Ms = 0;
    }

    private string BuildRuntimeSummary()
    {
        return
            $"openXR={GetOpenXrSummary()}, " +
            $"xrDisplay={GetXrDisplaySummary()}, " +
            $"urp={GetUrpSummary()}, " +
            $"renderScale=requested:{RenderScaleManager.LastRequestedScale:0.00}/xr:{RenderScaleManager.LastAppliedXrScale:0.00}/urp:{RenderScaleManager.LastAppliedUrpScale:0.00}/status:{RenderScaleManager.LastApplyStatus}";
    }

    private static string GetOpenXrSummary()
    {
        try
        {
            var settings = OpenXRSettings.Instance;
            var enabledExtensions = OpenXRRuntime.GetEnabledExtensions();
            var hasFoveationExtension =
                HasExtension(enabledExtensions, "XR_FB_foveation") ||
                HasExtension(enabledExtensions, "XR_UNITY_foveation") ||
                HasExtension(enabledExtensions, "XR_META_foveation_eye_tracked");

            return
                $"name:{SafeString(OpenXRRuntime.name)}, " +
                $"version:{SafeString(OpenXRRuntime.version)}, " +
                $"api:{SafeString(OpenXRRuntime.apiVersion)}, " +
                $"renderMode:{settings.renderMode}, " +
                $"extensions:{enabledExtensions.Length}, " +
                $"foveationExt:{hasFoveationExtension}";
        }
        catch (Exception exception)
        {
            return $"unavailable:{exception.GetType().Name}";
        }
    }

    private string GetXrDisplaySummary()
    {
        try
        {
            _displaySubsystems.Clear();
            SubsystemManager.GetInstances(_displaySubsystems);
            if (_displaySubsystems.Count == 0)
            {
                return "count:0";
            }

            var runningCount = 0;
            var scale = -1.0f;
            foreach (var displaySubsystem in _displaySubsystems)
            {
                if (displaySubsystem == null)
                {
                    continue;
                }

                if (displaySubsystem.running)
                {
                    runningCount++;
                }

                scale = displaySubsystem.scaleOfAllRenderTargets;
            }

            return $"count:{_displaySubsystems.Count}, running:{runningCount}, scale:{scale:0.00}";
        }
        catch (Exception exception)
        {
            return $"unavailable:{exception.GetType().Name}";
        }
    }

    private static string GetUrpSummary()
    {
        try
        {
            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urpAsset)
            {
                return "none";
            }

            return $"renderScale:{urpAsset.renderScale:0.00}";
        }
        catch (Exception exception)
        {
            return $"unavailable:{exception.GetType().Name}";
        }
    }

    private static bool HasExtension(IEnumerable<string> extensions, string extensionName)
    {
        foreach (var extension in extensions)
        {
            if (extension == extensionName)
            {
                return true;
            }
        }

        return false;
    }

    private static string SafeString(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
}
