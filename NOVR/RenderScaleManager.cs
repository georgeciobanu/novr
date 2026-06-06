using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace NOVR;

public class RenderScaleManager : NOVRBehaviour
{
    private readonly List<XRDisplaySubsystem> _displaySubsystems = new();
    private float _lastRequestedScale = -1.0f;
    private float _nextRetryTime;

    public static float LastRequestedScale { get; private set; } = 1.0f;
    public static float LastAppliedXrScale { get; private set; } = -1.0f;
    public static float LastAppliedUrpScale { get; private set; } = -1.0f;
    public static int LastApplyFrame { get; private set; }
    public static string LastApplyStatus { get; private set; } = "not-applied";

    private void Start()
    {
        ApplyRenderScale(forceLog: true);
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextRetryTime)
        {
            return;
        }

        _nextRetryTime = Time.unscaledTime + 1.0f;
        ApplyRenderScale(forceLog: false);
    }

    protected override void OnSettingChanged()
    {
        base.OnSettingChanged();
        ApplyRenderScale(forceLog: true);
    }

    private void ApplyRenderScale(bool forceLog)
    {
        var config = ModConfiguration.Instance;
        if (config == null)
        {
            LastApplyStatus = "missing-config";
            return;
        }

        if (!config.RenderScaleEnabled.Value)
        {
            LastRequestedScale = config.RenderScale.Value;
            LastAppliedXrScale = -1.0f;
            LastAppliedUrpScale = -1.0f;
            LastApplyStatus = "disabled";
            _lastRequestedScale = -1.0f;
            return;
        }

        var requestedScale = Mathf.Clamp(config.RenderScale.Value, 0.5f, 1.5f);
        LastRequestedScale = requestedScale;
        var scaleChanged = Math.Abs(requestedScale - _lastRequestedScale) > 0.0001f;
        if (!scaleChanged && !forceLog)
        {
            return;
        }

        _lastRequestedScale = requestedScale;
        LastApplyFrame = Time.frameCount;

        var appliedXr = ApplyXrRenderScale(requestedScale);
        var appliedUrp = ApplyUrpRenderScale(requestedScale);
        LastApplyStatus = $"xr={appliedXr}, urp={appliedUrp}";

        if (forceLog || scaleChanged)
        {
            Debug.Log($"NOVR render scale: requested={requestedScale:0.00}, {LastApplyStatus}");
        }
    }

    private bool ApplyXrRenderScale(float requestedScale)
    {
        _displaySubsystems.Clear();
        SubsystemManager.GetInstances(_displaySubsystems);
        if (_displaySubsystems.Count == 0)
        {
            LastAppliedXrScale = -1.0f;
            return false;
        }

        var applied = false;
        foreach (var displaySubsystem in _displaySubsystems)
        {
            if (displaySubsystem == null)
            {
                continue;
            }

            displaySubsystem.scaleOfAllRenderTargets = requestedScale;
            applied = true;
        }

        LastAppliedXrScale = applied ? requestedScale : -1.0f;
        return applied;
    }

    private static bool ApplyUrpRenderScale(float requestedScale)
    {
        if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urpAsset)
        {
            LastAppliedUrpScale = -1.0f;
            return false;
        }

        urpAsset.renderScale = requestedScale;
        LastAppliedUrpScale = requestedScale;
        return true;
    }
}
