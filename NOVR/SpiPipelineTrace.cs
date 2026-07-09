#if MODERN
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace NOVR;

// Deep, sampled trace of the stereo render pipeline. Walks every render context and camera once per
// interval to show which cameras render stereo (single-pass) vs mono, what targets they use, whether
// per-eye projection is distinct, and whether scene shaders run the instancing variant. Also reads back
// the centre of each eye slice to objectively catch a black/failed eye. Nothing runs per frame except a
// cheap interval check; the heavy inspection happens on one sampled frame per interval.
internal sealed class SpiPipelineTrace : MonoBehaviour
{
    private const int MaxCamerasPerSnapshot = 24;
    private const int ShaderSampleRenderers = 200;
    private const int ReadbackRegion = 32;

    private static readonly Type? UrpCameraDataType =
        Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");

    // For a blind, short-lived SPI run, capture the first frames unconditionally and sample fast early,
    // because the scene renders the moment the mission loads and the player may die within a minute.
    private const int FirstContextsAlwaysCaptured = 4;
    private const float BurstWindowSeconds = 30f;
    private const float BurstIntervalSeconds = 1.5f;

    private double _nextSnapshotTime;
    private double _enableTime;
    private bool _captureRequested;
    private bool _capturingFrame;
    private readonly List<string> _cameraRecords = new();
    private int _contextCameraCount;
    private int _contextsCaptured;
    private bool _readbackInFlight;
    private bool _loggedCapabilities;

    private static bool Enabled =>
        ModConfiguration.Instance != null &&
        ModConfiguration.Instance.LogSpiPipelineTrace.Value;

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        _enableTime = Time.realtimeSinceStartupAsDouble;
        RenderPipelineManager.beginContextRendering += OnBeginContext;
        RenderPipelineManager.endContextRendering += OnEndContext;
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginContextRendering -= OnBeginContext;
        RenderPipelineManager.endContextRendering -= OnEndContext;
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
    }

    private void Update()
    {
        if (!Enabled) return;

        var now = Time.realtimeSinceStartupAsDouble;

        // Sample fast during an initial burst window, then fall back to the configured cadence.
        // The scene renders the instant the mission loads and a blind SPI run can end within a
        // minute, so we want several dense snapshots up front rather than waiting for the first
        // configured interval to elapse.
        var inBurst = now - _enableTime <= BurstWindowSeconds;
        var interval = inBurst
            ? BurstIntervalSeconds
            : Math.Max(1f, ModConfiguration.Instance!.SpiPipelineTraceIntervalSeconds.Value);

        if (now >= _nextSnapshotTime)
        {
            _nextSnapshotTime = now + interval;
            _captureRequested = true;
        }
    }

    private void OnBeginContext(ScriptableRenderContext context, List<Camera> cameras)
    {
        if (!Enabled) return;

        // Always capture the first few contexts unconditionally: they show the very first stereo
        // setup the game builds, which we may otherwise miss if the run ends before an interval fires.
        var forceEarly = _contextsCaptured < FirstContextsAlwaysCaptured;
        if (!_captureRequested && !forceEarly) return;

        _captureRequested = false;
        _capturingFrame = true;
        _contextsCaptured++;
        _cameraRecords.Clear();
        _contextCameraCount = cameras?.Count ?? 0;
    }

    private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
    {
        if (!_capturingFrame || camera == null || _cameraRecords.Count >= MaxCamerasPerSnapshot) return;

        _cameraRecords.Add(DescribeCameraStereo(camera));
    }

    private void OnEndCamera(ScriptableRenderContext context, Camera camera)
    {
    }

    private void OnEndContext(ScriptableRenderContext context, List<Camera> cameras)
    {
        if (!_capturingFrame) return;

        _capturingFrame = false;
        EmitSnapshot();
    }

    private void EmitSnapshot()
    {
        var builder = new StringBuilder(2048);
        builder.AppendLine("[NOVR] SPI pipeline trace");
        if (!_loggedCapabilities)
        {
            _loggedCapabilities = true;
            builder.AppendLine($"[NOVR]   Device caps: {DescribeDeviceCapabilities()}");
            builder.AppendLine($"[NOVR]   Global stereo keywords: {DescribeGlobalStereoKeywords()}");
        }

        builder.AppendLine($"[NOVR]   Mode: openxrRenderMode={SafeXr(() => OpenXrRenderModeName())}, XRSettings.stereoRenderingMode={XRSettings.stereoRenderingMode}, eyeTex={DescribeEyeTexture()}");
        builder.AppendLine($"[NOVR]   XR passes: {DescribeXrPasses()}");
        builder.AppendLine($"[NOVR]   Stereo RT inventory: {DescribeStereoRenderTextures()}");
        builder.AppendLine($"[NOVR]   Shader instancing sample: {DescribeShaderInstancing()}");

        builder.AppendLine($"[NOVR]   Cameras this context ({_contextCameraCount}, captured {_cameraRecords.Count}):");
        foreach (var record in _cameraRecords)
        {
            builder.AppendLine($"[NOVR]     {record}");
        }

        Debug.Log(builder.ToString());

        if (ModConfiguration.Instance!.SpiPipelineTraceEyeReadback.Value)
        {
            TryReadbackEyes();
        }
    }

    private static string DescribeCameraStereo(Camera camera)
    {
        var name = camera.name;
        var stereoEnabled = camera.stereoEnabled;
        var targetEye = camera.stereoTargetEye;
        var activeEye = camera.stereoActiveEye;
        var target = camera.targetTexture;
        var activeRt = camera.activeTexture;

        var projDiffer = "n/a";
        if (stereoEnabled)
        {
            var left = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
            var right = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
            projDiffer = (left != right).ToString();
        }

        var renderType = "n/a";
        var post = "n/a";
        var xr = "n/a";
        if (UrpCameraDataType != null && camera.GetComponent(UrpCameraDataType) is UniversalAdditionalCameraData data)
        {
            renderType = data.renderType.ToString();
            post = data.renderPostProcessing.ToString();
            xr = data.allowXRRendering.ToString();
        }

        return
            $"{name}: stereoEnabled={stereoEnabled} targetEye={targetEye} activeEye={activeEye} projPerEyeDiffers={projDiffer} " +
            $"cameraType={camera.cameraType} urpRenderType={renderType} post={post} allowXR={xr} " +
            $"targetTex={DescribeTargetShape(target)} activeTex={DescribeTargetShape(activeRt)} verdict={StereoVerdict(camera, activeRt ?? target)}";
    }

    // Best-effort read of what the camera is actually drawing into, and whether that path can carry two eyes.
    private static string StereoVerdict(Camera camera, RenderTexture? currentTarget)
    {
        if (!camera.stereoEnabled)
        {
            return "MONO(stereoDisabled: overlay/RT camera paints one view; suspect under SPI)";
        }

        if (currentTarget == null)
        {
            // XR-driven camera with no explicit RT: relies on the XR eye texture (the array target).
            var desc = XRSettings.eyeTextureDesc;
            return desc.dimension == TextureDimension.Tex2DArray && desc.volumeDepth >= 2
                ? "STEREO->XR array target (single-pass OK)"
                : $"STEREO->XR target dim={desc.dimension} slices={desc.volumeDepth} (not an array: MultiPass or fallback)";
        }

        return currentTarget.dimension == TextureDimension.Tex2DArray && currentTarget.volumeDepth >= 2
            ? "STEREO->explicit array RT (single-pass OK)"
            : $"STEREO->explicit RT dim={currentTarget.dimension} slices={currentTarget.volumeDepth} (single 2D: only one eye lands here)";
    }

    private static string DescribeTargetShape(RenderTexture? rt)
    {
        if (rt == null) return "<null/backbuffer>";
        return $"{rt.name}[{rt.width}x{rt.height} dim={rt.dimension} slices={rt.volumeDepth} vrUsage={rt.vrUsage} fmt={rt.format} msaa={rt.antiAliasing}]";
    }

    private static string DescribeEyeTexture()
    {
        var desc = XRSettings.eyeTextureDesc;
        return $"{XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} dim={desc.dimension} slices={desc.volumeDepth} vrUsage={desc.vrUsage} msaa={desc.msaaSamples}";
    }

    // Whether the running GPU/graphics API supports the primitives single-pass-instanced stereo needs.
    // If any of these are false, SPI cannot possibly light both eyes regardless of shader variants.
    private static string DescribeDeviceCapabilities()
    {
        return
            $"gfxApi={SystemInfo.graphicsDeviceType} gpu=\"{SystemInfo.graphicsDeviceName}\" " +
            $"supportsMultiview={SystemInfo.supportsMultiview} supports2DArrayTextures={SystemInfo.supports2DArrayTextures} " +
            $"supportsInstancing={SystemInfo.supportsInstancing} supportsAsyncGPUReadback={SystemInfo.supportsAsyncGPUReadback} " +
            $"renderTargetCount={SystemInfo.supportedRenderTargetCount}";
    }

    // Single-pass instancing is driven by a global shader keyword. If STEREO_INSTANCING_ON is not
    // globally enabled while SPI is the active mode, the vertex stage never emits the second eye.
    private static string DescribeGlobalStereoKeywords()
    {
        string[] candidates =
        {
            "STEREO_INSTANCING_ON", "STEREO_MULTIVIEW_ON", "UNITY_SINGLE_PASS_STEREO", "STEREO_CUBEMAP_RENDER_ON"
        };

        var states = candidates.Select(keyword => $"{keyword}={Shader.IsKeywordEnabled(keyword)}");
        return string.Join(" ", states);
    }

    private static string DescribeXrPasses()
    {
        var displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetInstances(displays);
        var display = displays.FirstOrDefault(d => d.running);
        if (display == null) return "no running XR display";

        try
        {
            var passCount = display.GetRenderPassCount();
            var parts = new List<string>();
            for (var i = 0; i < passCount && i < 4; i++)
            {
                display.GetRenderPass(i, out var pass);
                var rt = pass.renderTarget;
                parts.Add($"pass{i}[params={pass.GetRenderParameterCount()} target={rt}]");
            }

            var shape = passCount == 1 ? "single-pass" : passCount >= 2 ? "multi-pass" : "unknown";
            return $"{shape} passCount={passCount} {string.Join(" ", parts)}";
        }
        catch (Exception ex)
        {
            return $"failed {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string DescribeStereoRenderTextures()
    {
        var all = Resources.FindObjectsOfTypeAll<RenderTexture>();
        var arrays = 0;
        var vrTagged = 0;
        var notable = new List<string>();
        foreach (var rt in all)
        {
            if (rt == null) continue;
            var isArray = rt.dimension == TextureDimension.Tex2DArray && rt.volumeDepth > 1;
            if (isArray) arrays++;
            if (rt.vrUsage != VRTextureUsage.None) vrTagged++;
            if (isArray && notable.Count < 8) notable.Add(DescribeTargetShape(rt));
        }

        return $"total={all.Length} stereoArrays={arrays} vrTagged={vrTagged} notable=[{string.Join(" ; ", notable)}]";
    }

    // Whether scene shaders are actually running the stereo-instancing variant. If SPI is active but the
    // instancing keyword is absent on most materials, those materials only fill one eye -> black eye.
    private static string DescribeShaderInstancing()
    {
        var instanced = 0;
        var notInstanced = 0;
        var multiview = 0;
        var scanned = 0;
        var shaderNoInstancing = new Dictionary<string, int>();

        foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
        {
            if (scanned >= ShaderSampleRenderers) break;
            if (renderer == null || !renderer.gameObject.scene.IsValid()) continue;

            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || material.shader == null) continue;
                scanned++;
                var keywords = material.shaderKeywords;
                var hasInstancing = keywords.Any(k => k.Contains("STEREO_INSTANCING"));
                var hasMultiview = keywords.Any(k => k.Contains("STEREO_MULTIVIEW"));
                if (hasInstancing) instanced++;
                else notInstanced++;
                if (hasMultiview) multiview++;

                if (!hasInstancing && !hasMultiview)
                {
                    shaderNoInstancing.TryGetValue(material.shader.name, out var count);
                    shaderNoInstancing[material.shader.name] = count + 1;
                }

                if (scanned >= ShaderSampleRenderers) break;
            }
        }

        var topMissing = shaderNoInstancing
            .OrderByDescending(kvp => kvp.Value)
            .Take(6)
            .Select(kvp => $"{kvp.Key}={kvp.Value}");

        return
            $"scannedMaterials={scanned} withStereoInstancing={instanced} withMultiview={multiview} withoutStereoVariant={notInstanced}; " +
            $"topShadersMissingStereoVariant=[{string.Join(" ; ", topMissing)}]";
    }

    private void TryReadbackEyes()
    {
        if (_readbackInFlight || !SystemInfo.supportsAsyncGPUReadback) return;

        var targets = FindEyeArrayTargets();
        if (targets.Count == 0)
        {
            Debug.Log("[NOVR]   SPI eye readback: no readable stereo array target found.");
            return;
        }

        // Read back the centre of eye0 vs eye1 for EACH distinct stereo array so we can see the
        // pipeline stage where the second eye first goes black (e.g. opaque pass vs colour attachment).
        _readbackInFlight = true;
        var results = new string[targets.Count];
        var targetsPending = targets.Count;

        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            var targetIndex = t;
            var regionW = Mathf.Min(ReadbackRegion, target.width);
            var regionH = Mathf.Min(ReadbackRegion, target.height);
            var originX = Mathf.Clamp(target.width / 2 - regionW / 2, 0, target.width - regionW);
            var originY = Mathf.Clamp(target.height / 2 - regionH / 2, 0, target.height - regionH);
            var sliceCount = Mathf.Min(2, target.volumeDepth);
            var colors = new Color[2];
            var slicesPending = sliceCount;
            var targetName = target.name;
            var targetShape = $"{target.width}x{target.height}";

            for (var slice = 0; slice < sliceCount; slice++)
            {
                var captured = slice;
                AsyncGPUReadback.Request(
                    target, 0, originX, regionW, originY, regionH, slice, 1, TextureFormat.RGBA32,
                    request =>
                    {
                        colors[captured] = request.hasError ? new Color(-1f, -1f, -1f, -1f) : Average(request.GetData<Color32>());
                        if (--slicesPending > 0) return;

                        var verdict = EyeSuspect(colors[0], colors[1])
                            ? "SUSPECT: eyes differ hugely or one is black"
                            : "eyes look similar";
                        results[targetIndex] =
                            $"{targetName}[{targetShape}] eye0={Fmt(colors[0])} eye1={Fmt(colors[1])} ({verdict})";

                        if (--targetsPending > 0) return;

                        _readbackInFlight = false;
                        var builder = new StringBuilder(512);
                        builder.AppendLine($"[NOVR]   SPI eye readback centre {ReadbackRegion}px across {results.Length} stereo array(s):");
                        foreach (var line in results)
                        {
                            builder.AppendLine($"[NOVR]     {line}");
                        }

                        Debug.Log(builder.ToString());
                    });
            }
        }
    }

    private static bool EyeSuspect(Color a, Color b)
    {
        if (a.r < 0f || b.r < 0f) return true;
        var aLum = a.r + a.g + a.b;
        var bLum = b.r + b.g + b.b;
        if (aLum < 0.02f || bLum < 0.02f) return true;
        var diff = Mathf.Abs(aLum - bLum);
        return diff > 0.5f * Mathf.Max(aLum, bLum);
    }

    private const int MaxReadbackTargets = 6;

    private static List<RenderTexture> FindEyeArrayTargets()
    {
        var candidates = new List<RenderTexture>();
        var seenNames = new HashSet<string>();
        foreach (var rt in Resources.FindObjectsOfTypeAll<RenderTexture>())
        {
            if (rt == null ||
                rt.dimension != TextureDimension.Tex2DArray ||
                rt.volumeDepth < 2 ||
                !rt.IsCreated() ||
                rt.antiAliasing > 1 ||
                rt.width < 1000 ||
                rt.format == RenderTextureFormat.Depth ||
                rt.format == RenderTextureFormat.Shadowmap)
            {
                continue;
            }

            // One per distinct name is enough; URP keeps stable names per pipeline stage.
            if (seenNames.Add(rt.name))
            {
                candidates.Add(rt);
            }
        }

        // Surface the stages the black-eye investigation cares about first: the camera colour
        // attachment and the opaque copy (where the second eye was found empty), then by size.
        return candidates
            .OrderByDescending(rt => rt.name.Contains("Opaque") || rt.name.Contains("Color") || rt.name.Contains("XR"))
            .ThenByDescending(rt => rt.width * rt.height)
            .Take(MaxReadbackTargets)
            .ToList();
    }

    private static Color Average(NativeArray<Color32> pixels)
    {
        if (pixels.Length == 0) return Color.black;
        double r = 0, g = 0, b = 0, a = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            r += pixels[i].r;
            g += pixels[i].g;
            b += pixels[i].b;
            a += pixels[i].a;
        }

        var scale = 1.0 / (pixels.Length * 255.0);
        return new Color((float)(r * scale), (float)(g * scale), (float)(b * scale), (float)(a * scale));
    }

    private static string Fmt(Color c) =>
        c.r < 0f ? "<readback-error>" : $"rgba({c.r:0.00},{c.g:0.00},{c.b:0.00},{c.a:0.00})";

    private static string OpenXrRenderModeName()
    {
        var settingsType = Type.GetType("UnityEngine.XR.OpenXR.OpenXRSettings, Unity.XR.OpenXR");
        var activeInstance = settingsType?.GetProperty("ActiveBuildTargetInstance")?.GetValue(null)
                             ?? settingsType?.GetProperty("Instance")?.GetValue(null);
        var renderMode = activeInstance == null ? null : settingsType!.GetProperty("renderMode")?.GetValue(activeInstance);
        return renderMode?.ToString() ?? "unknown";
    }

    private static string SafeXr(Func<string> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}>";
        }
    }
}
#endif
