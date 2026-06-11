using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace NOVR.Diagnostics;

public class RenderPipelineDiagnostics : MonoBehaviour
{
    private bool _loggedFirstFrame;
    private RenderPipelineAsset? _lastCurrentPipeline;
    private RenderPipelineAsset? _lastDefaultPipeline;

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        _lastCurrentPipeline = GraphicsSettings.currentRenderPipeline;
        _lastDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
        RenderDiagnostics.Info($"Render diagnostics initialized. {RenderDiagnostics.DescribeRenderPipeline()}");
        RenderDiagnostics.Info(RenderDiagnostics.DescribeXrState());
        RenderDiagnostics.Info(RenderDiagnostics.DescribeXrDisplays());
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
#if MODERN
        RenderPipelineManager.beginFrameRendering += OnBeginFrameRendering;
#endif
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
#if MODERN
        RenderPipelineManager.beginFrameRendering -= OnBeginFrameRendering;
#endif
    }

    private void Update()
    {
        var current = GraphicsSettings.currentRenderPipeline;
        var defaultPipeline = GraphicsSettings.defaultRenderPipeline;
        if (current == _lastCurrentPipeline && defaultPipeline == _lastDefaultPipeline)
        {
            return;
        }

        _lastCurrentPipeline = current;
        _lastDefaultPipeline = defaultPipeline;
        RenderDiagnostics.Info($"Render pipeline asset changed. {RenderDiagnostics.DescribeRenderPipeline()}");
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RenderDiagnostics.Info($"Scene loaded: name={scene.name} buildIndex={scene.buildIndex} mode={mode}");
        RenderDiagnostics.Info(RenderDiagnostics.DescribeRenderPipeline());
        RenderDiagnostics.Info(RenderDiagnostics.DescribeXrState());
    }

#if MODERN
    private void OnBeginFrameRendering(ScriptableRenderContext context, Camera[] cameras)
    {
        if (_loggedFirstFrame)
        {
            return;
        }

        _loggedFirstFrame = true;
        RenderDiagnostics.Info($"First SRP frame: cameraCount={cameras.Length}; {RenderDiagnostics.DescribeRenderPipeline()}");

        var cameraSummaries = new List<string>();
        for (var i = 0; i < cameras.Length && i < 8; i++)
        {
            cameraSummaries.Add(RenderDiagnostics.DescribeCamera(cameras[i]));
        }

        if (cameras.Length > 8)
        {
            cameraSummaries.Add($"... {cameras.Length - 8} more cameras");
        }

        RenderDiagnostics.Info("First SRP frame cameras: " + string.Join(" | ", cameraSummaries));
        RenderDiagnostics.Info(RenderDiagnostics.DescribeXrDisplays());
    }
#endif
}
