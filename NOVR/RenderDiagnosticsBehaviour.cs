using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using Debug = UnityEngine.Debug;

namespace NOVR;

internal sealed class RenderDiagnosticsBehaviour : MonoBehaviour
{
    private const int FrameTimingBufferSize = 16;
    private static readonly List<XRDisplaySubsystem> DisplaySubsystems = new();
    private readonly Dictionary<Camera, long> _activeCameraStartTicks = new();
    private readonly Dictionary<string, CameraTimingStats> _cameraStats = new();
    private readonly Dictionary<string, TimingStats> _systemStats = new();
    private readonly Dictionary<string, long> _playerLoopStartTicks = new();
    private readonly Dictionary<string, TimingStats> _playerLoopStats = new();
    private readonly Dictionary<string, long> _playerLoopStartAlloc = new();
    private readonly Dictionary<string, AllocStats> _playerLoopAllocStats = new();
    private readonly Dictionary<Camera, long> _activeCameraStartAlloc = new();
    private readonly Dictionary<string, AllocStats> _cameraAllocStats = new();
    private static bool _allocProbed;
    private static bool _allocSupported;
    private readonly FrameTiming[] _frameTimings = new FrameTiming[FrameTimingBufferSize];
    private readonly List<MarkerRecorder> _markerRecorders = new();
    private readonly List<MarkerRecorder> _counterRecorders = new();
    private readonly TimingStats _updateDeltaStats = new("Update.delta");
    private readonly TimingStats _timeDeltaStats = new("Time.deltaTime");
    private readonly TimingStats _unscaledDeltaStats = new("Time.unscaledDeltaTime");
    private readonly TimingStats _smoothDeltaStats = new("Time.smoothDeltaTime");
    private readonly TimingStats _updateToLateStats = new("Update->LateUpdate");
    private readonly TimingStats _updateToBeforeRenderStats = new("Update->onBeforeRender");
    private readonly TimingStats _lateToBeforeRenderStats = new("LateUpdate->onBeforeRender");
    private readonly TimingStats _lateToBeginFrameStats = new("LateUpdate->beginFrameRendering");
    private readonly TimingStats _beforeRenderToBeginFrameStats = new("onBeforeRender->beginFrameRendering");
    private readonly TimingStats _beginToEndFrameStats = new("beginFrameRendering->endFrameRendering");
    private readonly TimingStats _endFrameToNextUpdateStats = new("endFrameRendering->next Update");
    private readonly TimingStats _endOfFrameToNextUpdateStats = new("EndOfFrame->next Update");
    private readonly TimingStats _updateToEndOfFrameStats = new("Update->EndOfFrame");

    private const int FramePartsCapacity = 4096;
    private readonly FramePartsSample[] _frameParts = new FramePartsSample[FramePartsCapacity];
    private int _framePartsCount;
    private FramePartsSample _pending;
    private float _lastUpdateDeltaMs;
    private int _lastGc0;
    private int _lastGc1;
    private int _lastGc2;
    private long _lastTotalMemory;

    private float _nextLogTime;
    private int _beginFrameCameraCount;
    private long _beginFrameTicks;
    private long _currentUpdateStartTicks;
    private long _lastUpdateTicks;
    private long _lastLateUpdateTicks;
    private long _lastBeforeRenderTicks;
    private long _lastEndFrameTicks;
    private long _lastEndOfFrameTicks;
    private long _lastFrameElapsedTicks;
    private uint _latestFrameTimingCount;
    private bool _warnedProfilerRecorderSetup;
    private Coroutine? _endOfFrameCoroutine;
    private long? _lastXrDroppedFrames;
    private long? _lastXrPresentedFrames;
    private static RenderDiagnosticsBehaviour? _current;
    private static bool _playerLoopMarkersInstalled;
    private double _lastEncodeMsP50;

    private static double TickToMs => 1000.0 / Stopwatch.Frequency;

    private static bool AllocationBreakdownEnabled =>
        ModConfiguration.Instance != null &&
        ModConfiguration.Instance.LogAllocationBreakdown.Value;

    // Managed-heap size sampler used to attribute allocations to bracketed phases.
    //
    // We intentionally do NOT use GC.GetAllocatedBytesForCurrentThread(): on the game's IL2CPP
    // Boehm GC that call does not throw but returns a frozen value, so every begin/end delta is 0
    // and the whole per-phase breakdown gets filtered out ("no per-phase allocations recorded").
    // GC.GetTotalMemory(false) reports the live managed heap and is proven to move here (the
    // FrameParts gc line reports ~260KB/frame from the same source). All our brackets run on the
    // main thread and are effectively non-overlapping, so end-start approximates the allocation
    // done inside the bracket; windows where a collection shrinks the heap are dropped by the
    // end >= start guards at the call sites.
    private static long CurrentHeapBytes()
    {
        if (!_allocProbed)
        {
            _allocProbed = true;
            try
            {
                _ = GC.GetTotalMemory(false);
                _allocSupported = true;
            }
            catch
            {
                _allocSupported = false;
            }
        }

        if (!_allocSupported) return -1;
        try
        {
            return GC.GetTotalMemory(false);
        }
        catch
        {
            _allocSupported = false;
            return -1;
        }
    }

    public static bool IsEnabled =>
        ModConfiguration.Instance != null &&
        ModConfiguration.Instance.LogRenderDiagnostics.Value;

    private static bool Verbose =>
        ModConfiguration.Instance != null &&
        ModConfiguration.Instance.LogRenderDiagnosticsVerbose.Value;

    private void OnEnable()
    {
        _current = this;
#if MODERN
        RenderPipelineManager.beginFrameRendering += OnBeginFrameRendering;
        RenderPipelineManager.endFrameRendering += OnEndFrameRendering;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
#endif
        Application.onBeforeRender += OnBeforeRender;
        TryInstallPlayerLoopMarkers();
        TryCreateMarkerRecorders();
        _endOfFrameCoroutine = StartCoroutine(EndOfFrameLoop());
        _nextLogTime = Time.realtimeSinceStartup + 2f;
    }

    private void OnDisable()
    {
        if (_current == this)
        {
            _current = null;
        }

#if MODERN
        RenderPipelineManager.beginFrameRendering -= OnBeginFrameRendering;
        RenderPipelineManager.endFrameRendering -= OnEndFrameRendering;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
#endif
        Application.onBeforeRender -= OnBeforeRender;
        if (_endOfFrameCoroutine != null)
        {
            StopCoroutine(_endOfFrameCoroutine);
            _endOfFrameCoroutine = null;
        }

        foreach (var recorder in _markerRecorders)
        {
            recorder.Dispose();
        }

        _markerRecorders.Clear();
        foreach (var recorder in _counterRecorders)
        {
            recorder.Dispose();
        }

        _counterRecorders.Clear();
    }

    private void Update()
    {
        if (!IsEnabled) return;

        RecordUpdateStart();

        try
        {
            FrameTimingManager.CaptureFrameTimings();
            _latestFrameTimingCount = FrameTimingManager.GetLatestTimings((uint)_frameTimings.Length, _frameTimings);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[NOVR] Render diagnostics failed to capture frame timings: {ex.GetType().Name}: {ex.Message}");
        }

        RecordFramePartsSample();

        var interval = Math.Max(1f, ModConfiguration.Instance.RenderDiagnosticsIntervalSeconds.Value);
        if (Time.realtimeSinceStartup < _nextLogTime) return;

        _nextLogTime = Time.realtimeSinceStartup + interval;
        LogSummary();
        ResetIntervalStats();
    }

    private void LateUpdate()
    {
        if (!IsEnabled) return;

        var now = Stopwatch.GetTimestamp();
        if (_currentUpdateStartTicks != 0)
        {
            _updateToLateStats.Record((now - _currentUpdateStartTicks) * TickToMs);
        }

        _lastLateUpdateTicks = now;
    }

    private void OnBeforeRender()
    {
        if (!IsEnabled) return;

        var now = Stopwatch.GetTimestamp();
        if (_currentUpdateStartTicks != 0)
        {
            _updateToBeforeRenderStats.Record((now - _currentUpdateStartTicks) * TickToMs);
        }

        if (_lastLateUpdateTicks != 0)
        {
            _lateToBeforeRenderStats.Record((now - _lastLateUpdateTicks) * TickToMs);
        }

        _lastBeforeRenderTicks = now;
    }

    private IEnumerator EndOfFrameLoop()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();

            if (!IsEnabled) continue;

            var now = Stopwatch.GetTimestamp();
            if (_currentUpdateStartTicks != 0)
            {
                _updateToEndOfFrameStats.Record((now - _currentUpdateStartTicks) * TickToMs);
            }

            _lastEndOfFrameTicks = now;
        }
    }

    private static void TryInstallPlayerLoopMarkers()
    {
        if (_playerLoopMarkersInstalled) return;

        try
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!InsertPlayerLoopMarkers(ref loop, null, 0))
            {
                return;
            }

            PlayerLoop.SetPlayerLoop(loop);
            _playerLoopMarkersInstalled = true;
            Debug.Log("[NOVR] Render diagnostics installed PlayerLoop phase probes.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[NOVR] Render diagnostics could not install PlayerLoop probes: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool InsertPlayerLoopMarkers(ref PlayerLoopSystem system, string? parentName, int depth)
    {
        var changed = false;
        var children = system.subSystemList;
        if (children == null || children.Length == 0) return false;

        var rewritten = new List<PlayerLoopSystem>(children.Length * 3);
        foreach (var child in children)
        {
            var mutableChild = child;
            var childName = GetPlayerLoopName(mutableChild);
            changed |= InsertPlayerLoopMarkers(ref mutableChild, childName, depth + 1);

            if (ShouldProbePlayerLoopNode(mutableChild, parentName, depth + 1))
            {
                var key = FormatPlayerLoopKey(mutableChild, parentName, depth + 1);
                rewritten.Add(MakePlayerLoopMarker(key, true));
                rewritten.Add(mutableChild);
                rewritten.Add(MakePlayerLoopMarker(key, false));
                changed = true;
            }
            else
            {
                rewritten.Add(mutableChild);
            }
        }

        if (changed)
        {
            system.subSystemList = rewritten.ToArray();
        }

        return changed;
    }

    private static bool ShouldProbePlayerLoopNode(PlayerLoopSystem system, string? parentName, int depth)
    {
        if (system.type == null) return false;
        if (system.type == typeof(RenderDiagnosticsBehaviour)) return false;

        var name = GetPlayerLoopName(system);
        if (name.Contains("NOVR")) return false;

        if (depth == 1)
        {
            return name is "Initialization" or "EarlyUpdate" or "FixedUpdate" or "PreUpdate" or "Update" or "PreLateUpdate" or "PostLateUpdate";
        }

        if (depth == 2 && parentName != null)
        {
            return parentName is "EarlyUpdate" or "FixedUpdate" or "PreUpdate" or "Update" or "PreLateUpdate" or "PostLateUpdate";
        }

        return false;
    }

    private static PlayerLoopSystem MakePlayerLoopMarker(string key, bool begin)
    {
        return new PlayerLoopSystem
        {
            type = typeof(RenderDiagnosticsBehaviour),
            updateDelegate = () => RecordPlayerLoopMarker(key, begin)
        };
    }

    private static void RecordPlayerLoopMarker(string key, bool begin)
    {
        var current = _current;
        if (current == null || !IsEnabled) return;

        current.RecordPlayerLoopMarkerInstance(key, begin);
    }

    private void RecordPlayerLoopMarkerInstance(string key, bool begin)
    {
        var now = Stopwatch.GetTimestamp();
        var trackAlloc = AllocationBreakdownEnabled;
        if (begin)
        {
            _playerLoopStartTicks[key] = now;
            if (trackAlloc)
            {
                var alloc = CurrentHeapBytes();
                if (alloc >= 0) _playerLoopStartAlloc[key] = alloc;
            }

            return;
        }

        if (trackAlloc && _playerLoopStartAlloc.TryGetValue(key, out var startAlloc))
        {
            _playerLoopStartAlloc.Remove(key);
            var endAlloc = CurrentHeapBytes();
            if (endAlloc >= startAlloc)
            {
                if (!_playerLoopAllocStats.TryGetValue(key, out var allocStats))
                {
                    allocStats = new AllocStats();
                    _playerLoopAllocStats.Add(key, allocStats);
                }

                allocStats.Record(endAlloc - startAlloc);
            }
        }

        if (!_playerLoopStartTicks.TryGetValue(key, out var startTicks)) return;

        _playerLoopStartTicks.Remove(key);
        if (!_playerLoopStats.TryGetValue(key, out var stats))
        {
            stats = new TimingStats(key);
            _playerLoopStats.Add(key, stats);
        }

        var elapsedMs = (now - startTicks) * TickToMs;
        stats.Record(elapsedMs);
        AccumulateFramePart(key, (float)elapsedMs);
    }

    private void AccumulateFramePart(string key, float ms)
    {
        if (key.EndsWith("PlayerSendFrameStarted", StringComparison.Ordinal)) _pending.Send = ms;
        else if (key.EndsWith("FinishFrameRendering", StringComparison.Ordinal)) _pending.Finish = ms;
        else if (key.EndsWith("PlayerUpdateCanvases", StringComparison.Ordinal)) _pending.Canvases += ms;
        else if (key.EndsWith("PlayerEmitCanvasGeometry", StringComparison.Ordinal)) _pending.EmitCanvas += ms;
        else if (key.EndsWith("UpdateAllRenderers", StringComparison.Ordinal)) _pending.UpdateRenderers += ms;
        else if (key.EndsWith("/ScriptRunBehaviourUpdate", StringComparison.Ordinal)) _pending.ScriptsUpdate += ms;
        else if (key.EndsWith("ScriptRunBehaviourFixedUpdate", StringComparison.Ordinal)) _pending.ScriptsFixed += ms;
        else if (key.EndsWith("ScriptRunBehaviourLateUpdate", StringComparison.Ordinal)) _pending.ScriptsLate += ms;
        else if (key.EndsWith("PhysicsFixedUpdate", StringComparison.Ordinal)) _pending.Physics += ms;
        else if (key.EndsWith("UpdatePreloading", StringComparison.Ordinal)) _pending.Preloading += ms;
        else if (key.EndsWith("XRUpdate", StringComparison.Ordinal)) _pending.XrUpdate += ms;
    }

    private static string FormatPlayerLoopKey(PlayerLoopSystem system, string? parentName, int depth)
    {
        var name = GetPlayerLoopName(system);
        return depth == 1 || string.IsNullOrEmpty(parentName)
            ? $"L{depth}/{name}"
            : $"L{depth}/{parentName}/{name}";
    }

    private static string GetPlayerLoopName(PlayerLoopSystem system)
    {
        if (system.type == null) return "null";

        var fullName = system.type.FullName ?? system.type.Name;
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
    }

    private void RecordUpdateStart()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastUpdateTicks != 0)
        {
            var deltaMs = (now - _lastUpdateTicks) * TickToMs;
            _updateDeltaStats.Record(deltaMs);
            _lastUpdateDeltaMs = (float)deltaMs;
        }

        if (_lastEndFrameTicks != 0)
        {
            _endFrameToNextUpdateStats.Record((now - _lastEndFrameTicks) * TickToMs);
        }

        if (_lastEndOfFrameTicks != 0)
        {
            _endOfFrameToNextUpdateStats.Record((now - _lastEndOfFrameTicks) * TickToMs);
        }

        _timeDeltaStats.Record(Time.deltaTime * 1000.0);
        _unscaledDeltaStats.Record(Time.unscaledDeltaTime * 1000.0);
        _smoothDeltaStats.Record(Time.smoothDeltaTime * 1000.0);
        _currentUpdateStartTicks = now;
        _lastUpdateTicks = now;
    }

    private struct FramePartsSample
    {
        public float FrameDelta;
        public float Send;
        public float Finish;
        public float UrpSubmit;
        public float MainThread;
        public float RenderThread;
        public float Gpu;
        public float PresentWait;
        public float Canvases;
        public float EmitCanvas;
        public float UpdateRenderers;
        public float ScriptsUpdate;
        public float ScriptsFixed;
        public float ScriptsLate;
        public float Physics;
        public float Preloading;
        public float XrUpdate;
        public float CamWorld;
        public float CamPost;
        public float CamHud;
        public float CamCockpit;
        public float CamOther;
        public int Gc0;
        public int Gc1;
        public int Gc2;
        public float AllocKb;
        public double FrameStartTs;
        public double SubmitDelayTs;
        public double PresentDelayTs;
        public double CompleteDelayTs;
        public bool HasTimings;
    }

    // Captures the PREVIOUS frame's phase durations (its PostLateUpdate markers fired before this Update).
    private void RecordFramePartsSample()
    {
        if (_framePartsCount >= FramePartsCapacity)
        {
            _pending = default;
            return;
        }

        var sample = _pending;
        _pending = default;
        sample.FrameDelta = _lastUpdateDeltaMs;
        sample.UrpSubmit = (float)(_lastFrameElapsedTicks * TickToMs);

        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var totalMemory = GC.GetTotalMemory(false);
        sample.Gc0 = gc0 - _lastGc0;
        sample.Gc1 = gc1 - _lastGc1;
        sample.Gc2 = gc2 - _lastGc2;
        sample.AllocKb = totalMemory > _lastTotalMemory ? (totalMemory - _lastTotalMemory) / 1024f : 0f;
        _lastGc0 = gc0;
        _lastGc1 = gc1;
        _lastGc2 = gc2;
        _lastTotalMemory = totalMemory;

        if (_latestFrameTimingCount > 0)
        {
            var timing = _frameTimings[0];
            sample.MainThread = (float)timing.cpuMainThreadFrameTime;
            sample.RenderThread = (float)timing.cpuRenderThreadFrameTime;
            sample.Gpu = (float)timing.gpuFrameTime;
            sample.PresentWait = (float)timing.cpuMainThreadPresentWaitTime;
            sample.FrameStartTs = timing.frameStartTimestamp;
            sample.SubmitDelayTs = timing.firstSubmitTimestamp - (double)timing.frameStartTimestamp;
            sample.PresentDelayTs = timing.cpuTimePresentCalled - (double)timing.firstSubmitTimestamp;
            sample.CompleteDelayTs = timing.cpuTimeFrameComplete - (double)timing.cpuTimePresentCalled;
            sample.HasTimings = true;
        }

        _frameParts[_framePartsCount++] = sample;
        MaybeLogSpike(in sample);
    }

    private float _lastSpikeLogRealtime = -100f;

    // Emit a full per-frame attribution line the instant a frame blows past the spike threshold, so the
    // rare 17-83ms judder frames are caught with their cause inline (not just aggregated into the window's
    // worst-3). Throttled so an asset-streaming burst can't flood the log.
    private void MaybeLogSpike(in FramePartsSample s)
    {
        var thresholdMs = ModConfiguration.Instance?.RenderDiagnosticsSpikeMs.Value ?? 0f;
        if (thresholdMs <= 0f || s.FrameDelta < thresholdMs) return;

        var now = Time.realtimeSinceStartup;
        if (now - _lastSpikeLogRealtime < 0.1f) return;
        _lastSpikeLogRealtime = now;

        // Name the dominant main-thread contributor so the line reads at a glance.
        var worstName = "?";
        var worstMs = 0f;
        void Rank(string name, float ms) { if (ms > worstMs) { worstMs = ms; worstName = name; } }
        Rank("preload", s.Preloading);
        Rank("scriptsUpdate", s.ScriptsUpdate);
        Rank("scriptsFixed", s.ScriptsFixed);
        Rank("scriptsLate", s.ScriptsLate);
        Rank("physics", s.Physics);
        Rank("canvases", s.Canvases);
        Rank("camWorld", s.CamWorld);
        Rank("camPost", s.CamPost);
        Rank("camHud", s.CamHud);
        Rank("camCockpit", s.CamCockpit);
        Rank("camOther", s.CamOther);
        var gcThisFrame = s.Gc0 + s.Gc1 + s.Gc2;
        if (gcThisFrame > 0 && s.MainThread - worstMs > worstMs) worstName = $"GC(gen{(s.Gc2 > 0 ? 2 : s.Gc1 > 0 ? 1 : 0)})";

        Debug.LogWarning(
            $"[NOVR] SPIKE frame={s.FrameDelta:0.0}ms main={s.MainThread:0.0} rt={s.RenderThread:0.0} gpu={s.Gpu:0.0} likely={worstName} " +
            $"| preload={s.Preloading:0.0} scrU={s.ScriptsUpdate:0.0} scrF={s.ScriptsFixed:0.0} scrL={s.ScriptsLate:0.0} " +
            $"physics={s.Physics:0.0} canvases={s.Canvases:0.0} xr={s.XrUpdate:0.0} " +
            $"| camW={s.CamWorld:0.0} camP={s.CamPost:0.0} camH={s.CamHud:0.0} camCk={s.CamCockpit:0.0} camO={s.CamOther:0.0} " +
            $"| gc={s.Gc0}/{s.Gc1}/{s.Gc2} alloc={s.AllocKb:0}KB");
    }

    private void AppendFramePartBreakdown(StringBuilder builder)
    {
        var n = _framePartsCount;
        if (n < 8)
        {
            builder.AppendLine($"[NOVR]   FrameParts: insufficient samples (n={n})");
            return;
        }

        double Percentile(double[] sorted, double p) => sorted[(int)Math.Min(sorted.Length - 1, Math.Round(p * (sorted.Length - 1)))];

        double[] Column(Func<FramePartsSample, double> selector)
        {
            var values = new double[n];
            for (var i = 0; i < n; i++) values[i] = selector(_frameParts[i]);
            Array.Sort(values);
            return values;
        }

        string Dist(Func<FramePartsSample, double> selector)
        {
            var sorted = Column(selector);
            return $"{Percentile(sorted, 0.5):0.00}/{Percentile(sorted, 0.9):0.00}/{Percentile(sorted, 0.99):0.00}/{sorted[n - 1]:0.00}";
        }

        builder.AppendLine(
            $"[NOVR]   FrameParts p50/p90/p99/max ms (n={n}): frame={Dist(s => s.FrameDelta)}, send={Dist(s => s.Send)}, " +
            $"finish={Dist(s => s.Finish)}, urpSubmit={Dist(s => s.UrpSubmit)}, mainThread={Dist(s => s.MainThread)}, " +
            $"renderThread={Dist(s => s.RenderThread)}, gpu={Dist(s => s.Gpu)}, presentWait={Dist(s => s.PresentWait)}");
        builder.AppendLine(
            $"[NOVR]   FrameParts sub p50/p90/p99/max ms: canvases={Dist(s => s.Canvases)}, emitCanvas={Dist(s => s.EmitCanvas)}, " +
            $"updateRenderers={Dist(s => s.UpdateRenderers)}, scriptsU={Dist(s => s.ScriptsUpdate)}, scriptsF={Dist(s => s.ScriptsFixed)}, " +
            $"scriptsL={Dist(s => s.ScriptsLate)}, physics={Dist(s => s.Physics)}, preload={Dist(s => s.Preloading)}, xrUpdate={Dist(s => s.XrUpdate)}");
        builder.AppendLine(
            $"[NOVR]   FrameParts cams p50/p90/p99/max ms (both eyes): world={Dist(s => s.CamWorld)}, post={Dist(s => s.CamPost)}, " +
            $"hud={Dist(s => s.CamHud)}, cockpit={Dist(s => s.CamCockpit)}, other={Dist(s => s.CamOther)}");

        var gc0Total = 0; var gc1Total = 0; var gc2Total = 0;
        var gcFrameDeltas = new List<double>(n);
        var nonGcFrameDeltas = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            gc0Total += _frameParts[i].Gc0;
            gc1Total += _frameParts[i].Gc1;
            gc2Total += _frameParts[i].Gc2;
            if (_frameParts[i].Gc0 > 0) gcFrameDeltas.Add(_frameParts[i].FrameDelta);
            else nonGcFrameDeltas.Add(_frameParts[i].FrameDelta);
        }

        gcFrameDeltas.Sort();
        nonGcFrameDeltas.Sort();
        var allocSorted = Column(s => s.AllocKb);
        builder.AppendLine(
            $"[NOVR]   FrameParts gc: gen0={gc0Total} gen1={gc1Total} gen2={gc2Total} in window, alloc p50/p99={Percentile(allocSorted, 0.5):0.0}/{Percentile(allocSorted, 0.99):0.0}KB/frame, " +
            $"gc0Frames={gcFrameDeltas.Count} medianFrame={(gcFrameDeltas.Count > 0 ? gcFrameDeltas[gcFrameDeltas.Count / 2] : 0):0.00}ms vs nonGc={(nonGcFrameDeltas.Count > 0 ? nonGcFrameDeltas[nonGcFrameDeltas.Count / 2] : 0):0.00}ms");

        // Calibrate FrameTimingManager timestamp units against measured frame deltas.
        var timestampScaleMsPerUnit = 0.0;
        var frameStartDeltas = new List<double>(n);
        var frameDeltas = new List<double>(n);
        for (var i = 1; i < n; i++)
        {
            if (!_frameParts[i].HasTimings || !_frameParts[i - 1].HasTimings) continue;
            var tsDelta = _frameParts[i].FrameStartTs - _frameParts[i - 1].FrameStartTs;
            if (tsDelta <= 0 || _frameParts[i].FrameDelta <= 0f) continue;
            frameStartDeltas.Add(tsDelta);
            frameDeltas.Add(_frameParts[i].FrameDelta);
        }

        if (frameStartDeltas.Count >= 8)
        {
            frameStartDeltas.Sort();
            frameDeltas.Sort();
            var medianTs = frameStartDeltas[frameStartDeltas.Count / 2];
            var medianMs = frameDeltas[frameDeltas.Count / 2];
            if (medianTs > 0) timestampScaleMsPerUnit = medianMs / medianTs;
        }

        if (timestampScaleMsPerUnit > 0)
        {
            string TsDist(Func<FramePartsSample, double> selector)
            {
                var values = new double[n];
                for (var i = 0; i < n; i++) values[i] = Math.Max(0, selector(_frameParts[i])) * timestampScaleMsPerUnit;
                Array.Sort(values);
                return $"{Percentile(values, 0.5):0.00}/{Percentile(values, 0.9):0.00}/{Percentile(values, 0.99):0.00}/{values[n - 1]:0.00}";
            }

            // Stash the present->complete (encode) median so the Bound verdict line can reference it next window.
            {
                var complete = new double[n];
                for (var i = 0; i < n; i++) complete[i] = Math.Max(0, _frameParts[i].CompleteDelayTs) * timestampScaleMsPerUnit;
                Array.Sort(complete);
                _lastEncodeMsP50 = Percentile(complete, 0.5);
            }

            builder.AppendLine(
                $"[NOVR]   FrameParts delivery p50/p90/p99/max ms (tsScale={timestampScaleMsPerUnit:E2}): " +
                $"frameStart->firstSubmit={TsDist(s => s.SubmitDelayTs)}, firstSubmit->presentCalled={TsDist(s => s.PresentDelayTs)}, " +
                // present->complete is the GPU draining the submitted frame after Present(); on a streaming
                // HMD (Quest Link / Virtual Desktop / etc.) the runtime's composite+encode lands in this window,
                // so this is the closest app-side proxy for "encode" cost.
                $"present->complete(gpuDrain+encode)={TsDist(s => s.CompleteDelayTs)}");
        }
        else
        {
            builder.AppendLine("[NOVR]   FrameParts delivery: timestamp scale unavailable");
        }

        // Correlation: what does the render pipeline look like when send is long vs short?
        var sendSorted = Column(s => s.Send);
        var sendP90 = Percentile(sendSorted, 0.9);
        double MedianWhere(Func<FramePartsSample, bool> filter, Func<FramePartsSample, double> selector)
        {
            var values = new List<double>(n);
            for (var i = 0; i < n; i++) if (filter(_frameParts[i])) values.Add(selector(_frameParts[i]));
            if (values.Count == 0) return 0;
            values.Sort();
            return values[values.Count / 2];
        }

        builder.AppendLine(
            $"[NOVR]   FrameParts longSend (send>={sendP90:0.00}ms): " +
            $"medianRenderThread={MedianWhere(s => s.Send >= sendP90, s => s.RenderThread):0.00}ms vs {MedianWhere(s => s.Send < sendP90, s => s.RenderThread):0.00}ms, " +
            $"medianGpu={MedianWhere(s => s.Send >= sendP90, s => s.Gpu):0.00}ms vs {MedianWhere(s => s.Send < sendP90, s => s.Gpu):0.00}ms, " +
            $"medianUrpSubmit={MedianWhere(s => s.Send >= sendP90, s => s.UrpSubmit):0.00}ms vs {MedianWhere(s => s.Send < sendP90, s => s.UrpSubmit):0.00}ms");

        void AppendWorst(string label, Func<FramePartsSample, float> rank)
        {
            builder.Append($"[NOVR]   FrameParts {label}:");
            foreach (var i in Enumerable.Range(0, n).OrderByDescending(i => rank(_frameParts[i])).Take(3))
            {
                var s = _frameParts[i];
                builder.Append(
                    $" [frame={s.FrameDelta:0.0} send={s.Send:0.0} finish={s.Finish:0.0} urp={s.UrpSubmit:0.0} " +
                    $"rt={s.RenderThread:0.0} gpu={s.Gpu:0.0} scrU={s.ScriptsUpdate:0.0} scrF={s.ScriptsFixed:0.0} " +
                    $"preload={s.Preloading:0.0} camW={s.CamWorld:0.0} camP={s.CamPost:0.0} camH={s.CamHud:0.0} " +
                    $"gc={s.Gc0}/{s.Gc1}/{s.Gc2} alloc={s.AllocKb:0}KB" +
                    (timestampScaleMsPerUnit > 0 && s.HasTimings
                        ? $" submitD={Math.Max(0, s.SubmitDelayTs) * timestampScaleMsPerUnit:0.0} presentD={Math.Max(0, s.PresentDelayTs) * timestampScaleMsPerUnit:0.0} completeD={Math.Max(0, s.CompleteDelayTs) * timestampScaleMsPerUnit:0.0}]"
                        : "]"));
            }

            builder.AppendLine();
        }

        AppendWorst("worstSend", s => s.Send);
        AppendWorst("worstFrame", s => s.FrameDelta);
        AppendWorst("worstEncode(present->complete)", s => (float)s.CompleteDelayTs);
    }

#if MODERN
    private void OnBeginFrameRendering(ScriptableRenderContext context, Camera[] cameras)
    {
        if (!IsEnabled) return;

        _beginFrameTicks = Stopwatch.GetTimestamp();
        if (_lastLateUpdateTicks != 0)
        {
            _lateToBeginFrameStats.Record((_beginFrameTicks - _lastLateUpdateTicks) * TickToMs);
        }

        if (_lastBeforeRenderTicks != 0)
        {
            _beforeRenderToBeginFrameStats.Record((_beginFrameTicks - _lastBeforeRenderTicks) * TickToMs);
        }

        _beginFrameCameraCount = cameras?.Length ?? 0;
    }

    private void OnEndFrameRendering(ScriptableRenderContext context, Camera[] cameras)
    {
        if (!IsEnabled || _beginFrameTicks == 0) return;

        var now = Stopwatch.GetTimestamp();
        _lastFrameElapsedTicks = now - _beginFrameTicks;
        _beginToEndFrameStats.Record(_lastFrameElapsedTicks * TickToMs);
        _lastEndFrameTicks = now;
        _beginFrameTicks = 0;
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!IsEnabled || camera == null) return;

        _activeCameraStartTicks[camera] = Stopwatch.GetTimestamp();
        if (AllocationBreakdownEnabled)
        {
            var alloc = CurrentHeapBytes();
            if (alloc >= 0) _activeCameraStartAlloc[camera] = alloc;
        }
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!IsEnabled || camera == null) return;
        if (!_activeCameraStartTicks.TryGetValue(camera, out var startTicks)) return;

        _activeCameraStartTicks.Remove(camera);
        var elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * TickToMs;
        var key = GetCameraKey(camera);
        if (!_cameraStats.TryGetValue(key, out var stats))
        {
            stats = new CameraTimingStats(key);
            _cameraStats.Add(key, stats);
        }

        stats.Record(elapsedMs, camera);
        if (_activeCameraStartAlloc.TryGetValue(camera, out var startAlloc))
        {
            _activeCameraStartAlloc.Remove(camera);
            var endAlloc = CurrentHeapBytes();
            if (endAlloc >= startAlloc)
            {
                var allocKey = ClassifyCamera(camera);
                if (!_cameraAllocStats.TryGetValue(allocKey, out var camAlloc))
                {
                    camAlloc = new AllocStats();
                    _cameraAllocStats.Add(allocKey, camAlloc);
                }

                camAlloc.Record(endAlloc - startAlloc);
            }
        }

        var category = ClassifyCamera(camera);
        if (category.StartsWith("world", StringComparison.Ordinal)) _pending.CamWorld += (float)elapsedMs;
        else if (category == "postProcessingOverlay") _pending.CamPost += (float)elapsedMs;
        else if (category == "vrHud") _pending.CamHud += (float)elapsedMs;
        else if (category == "cockpitOverlay") _pending.CamCockpit += (float)elapsedMs;
        else _pending.CamOther += (float)elapsedMs;

        var slowThresholdMs = ModConfiguration.Instance.RenderDiagnosticsSlowCameraMs.Value;
        if (slowThresholdMs > 0f && elapsedMs >= slowThresholdMs)
        {
            Debug.LogWarning($"[NOVR] Slow camera render: {ClassifyCamera(camera)} {key} elapsed={elapsedMs:0.00}ms {FormatCamera(camera)}");
        }
    }
#endif

    private void LogSummary()
    {
        var builder = new StringBuilder(4096);
        builder.AppendLine("[NOVR] Render diagnostics");
        AppendFrameTimings(builder);
        AppendFramePhases(builder);
        AppendFramePartBreakdown(builder);
        AppendAllocationBreakdown(builder);
        AppendCpuBreakdown(builder);
        AppendXrState(builder);
        AppendXrRuntimeTimings(builder);
        AppendXrPassSummary(builder);
        AppendPostProcessingCameraDiagnostics(builder);

        if (Verbose)
        {
            AppendCameraTimings(builder);
            AppendCameraBuckets(builder);
            AppendSystemTimings(builder);
            AppendProfilerCounters(builder);
            AppendProfilerMarkers(builder);
            AppendRenderSettings(builder);
            AppendFlightState(builder);
            AppendCameraInventory(builder);
            AppendXrRenderPassDetails(builder);
        }

        Debug.Log(builder.ToString());
    }

    private static void AppendPostProcessingCameraDiagnostics(StringBuilder builder)
    {
        var cameras = GetAllCurrentCameras()
            .Where(camera => GetPath(camera.transform).Contains("postProcessingRenderer"))
            .OrderBy(camera => GetPath(camera.transform))
            .ThenBy(camera => camera.GetInstanceID())
            .ToArray();

        if (cameras.Length == 0)
        {
            builder.AppendLine("[NOVR]   PostProcessing cameras: count=0");
            return;
        }

        builder.AppendLine($"[NOVR]   PostProcessing cameras: count={cameras.Length}");
        foreach (var camera in cameras)
        {
            builder.AppendLine(
                $"[NOVR]     postProcessingCamera id={camera.GetInstanceID()}, {FormatCamera(camera)}, stackOwners=[{FormatCameraStackOwners(camera)}]");
        }
    }

    private void AppendAllocationBreakdown(StringBuilder builder)
    {
        if (!AllocationBreakdownEnabled) return;

        if (!_allocSupported && _allocProbed)
        {
            builder.AppendLine("[NOVR]   Alloc: GC.GetTotalMemory unsupported on this runtime; rely on 'GC Allocated In Frame' counter.");
            return;
        }

        string Kb(long bytes) => $"{bytes / 1024.0:0.#}KB";

        var loopTop = _playerLoopAllocStats
            .Where(kvp => kvp.Value.TotalBytes > 0)
            .OrderByDescending(kvp => kvp.Value.TotalBytes)
            .Take(8)
            .ToArray();

        if (loopTop.Length == 0)
        {
            builder.AppendLine("[NOVR]   Alloc: no per-phase allocations recorded this window.");
        }
        else
        {
            builder.AppendLine("[NOVR]   Alloc by player-loop phase (window total | per-call avg | max single):");
            foreach (var kvp in loopTop)
            {
                var s = kvp.Value;
                var avg = s.Samples > 0 ? s.TotalBytes / s.Samples : 0;
                builder.AppendLine($"[NOVR]     {kvp.Key}: total={Kb(s.TotalBytes)} n={s.Samples} avg={Kb(avg)} max={Kb(s.MaxBytes)}");
            }
        }

        var camTop = _cameraAllocStats
            .Where(kvp => kvp.Value.TotalBytes > 0)
            .OrderByDescending(kvp => kvp.Value.TotalBytes)
            .Take(6)
            .ToArray();

        if (camTop.Length > 0)
        {
            builder.Append("[NOVR]   Alloc by camera category:");
            foreach (var kvp in camTop)
            {
                var s = kvp.Value;
                var avg = s.Samples > 0 ? s.TotalBytes / s.Samples : 0;
                builder.Append($" {kvp.Key}=total {Kb(s.TotalBytes)}/avg {Kb(avg)}/max {Kb(s.MaxBytes)};");
            }

            builder.AppendLine();
        }
    }

    private void AppendFrameTimings(StringBuilder builder)
    {
        builder.Append("[NOVR]   Frame: ");
        builder.Append($"srpFrame={_lastFrameElapsedTicks * TickToMs:0.00}ms, ");
        builder.Append($"beginFrameCameras={_beginFrameCameraCount}, ");

        if (_latestFrameTimingCount == 0)
        {
            builder.AppendLine("FrameTimingManager=unavailable");
            return;
        }

        var count = Math.Min(_latestFrameTimingCount, FrameTimingBufferSize);
        var cpuFrame = AverageTiming(count, timing => timing.cpuFrameTime);
        var cpuMain = AverageTiming(count, timing => timing.cpuMainThreadFrameTime);
        var cpuRender = AverageTiming(count, timing => timing.cpuRenderThreadFrameTime);
        var gpuFrame = AverageTiming(count, timing => timing.gpuFrameTime);
        var displayRefresh = GetDisplayRefreshRate();
        var refreshBudgetMs = displayRefresh > 0f ? 1000.0 / displayRefresh : 0.0;
        builder.AppendLine(
            $"samples={count}, cpuFrame={cpuFrame:0.00}ms, cpuMain={cpuMain:0.00}ms, " +
            $"cpuRender={cpuRender:0.00}ms, gpuFrame={gpuFrame:0.00}ms");
        builder.Append("[NOVR]   Effective: ");
        builder.Append($"cpuFrameFps={MsToFps(cpuFrame):0.0}, gpuFrameFps={MsToFps(gpuFrame):0.0}");
        if (refreshBudgetMs > 0.0)
        {
            builder.Append(
                $", refresh={displayRefresh:0.#}Hz, budget={refreshBudgetMs:0.00}ms, " +
                $"cpuVsBudget={FormatSignedMs(cpuFrame - refreshBudgetMs)}, " +
                $"gpuVsBudget={FormatSignedMs(gpuFrame - refreshBudgetMs)}");
        }

        builder.AppendLine();

        if (refreshBudgetMs > 0.0)
        {
            AppendBoundVerdict(builder, cpuMain, gpuFrame, refreshBudgetMs);
        }
    }

    // Plain-language "what is limiting the frame" verdict, entirely from data we already read:
    // FrameTimingManager cpu/gpu vs the display budget, plus the present->complete (encode) window.
    // No native interop. gpuFrame is the app's GPU time; compositor/app GPU are also logged separately
    // in the XR runtime line, and the true encode cost lives in the Oculus PerfLog.
    private void AppendBoundVerdict(StringBuilder builder, double cpuMain, double gpuFrame, double budgetMs)
    {
        const double slack = 1.10;
        string verdict;
        if (cpuMain > budgetMs * slack && cpuMain >= gpuFrame)
        {
            verdict = $"CPU-bound (cpuMain {cpuMain:0.0}ms > budget {budgetMs:0.0}ms; gpu {gpuFrame:0.0}ms has headroom)";
        }
        else if (gpuFrame > budgetMs * slack)
        {
            verdict = $"GPU-bound (gpuFrame {gpuFrame:0.0}ms > budget {budgetMs:0.0}ms; cpuMain {cpuMain:0.0}ms)";
        }
        else
        {
            var idle = Math.Max(0.0, budgetMs - Math.Max(cpuMain, gpuFrame));
            verdict =
                $"at-refresh/paced (cpuMain {cpuMain:0.0} & gpu {gpuFrame:0.0} both under budget {budgetMs:0.0}ms; " +
                $"~{idle:0.0}ms idle/compositor wait — not compute-limited)";
        }

        var encodeNote = _lastEncodeMsP50 > 0.01
            ? $" | encode(present->complete) p50~{_lastEncodeMsP50:0.0}ms = motion-to-photon LATENCY, not framerate"
            : "";
        builder.AppendLine($"[NOVR]   Bound: {verdict}{encodeNote}");
    }

    private void AppendFramePhases(StringBuilder builder)
    {
        builder.Append("[NOVR]   Frame phases: ");
        builder.Append($"updateDelta={FormatStats(_updateDeltaStats)}, ");
        builder.Append($"timeDelta={FormatStats(_timeDeltaStats)}, ");
        builder.Append($"updateLate={FormatStats(_updateToLateStats)}, ");
        builder.Append($"lateBeginFrame={FormatStats(_lateToBeginFrameStats)}, ");
        builder.Append($"beginEndFrame={FormatStats(_beginToEndFrameStats)}, ");
        builder.Append($"endFrameNextUpdate={FormatStats(_endFrameToNextUpdateStats)}, ");
        builder.Append($"endOfFrameNextUpdate={FormatStats(_endOfFrameToNextUpdateStats)}");
        builder.AppendLine();

        builder.Append("[NOVR]   Frame phase extras: ");
        builder.Append($"unscaledDelta={FormatStats(_unscaledDeltaStats)}, ");
        builder.Append($"smoothDelta={FormatStats(_smoothDeltaStats)}, ");
        builder.Append($"updateBeforeRender={FormatStats(_updateToBeforeRenderStats)}, ");
        builder.Append($"lateBeforeRender={FormatStats(_lateToBeforeRenderStats)}, ");
        builder.Append($"beforeRenderBeginFrame={FormatStats(_beforeRenderToBeginFrameStats)}, ");
        builder.Append($"updateEndOfFrame={FormatStats(_updateToEndOfFrameStats)}");
        builder.AppendLine();
    }

    private void AppendCpuBreakdown(StringBuilder builder)
    {
        builder.AppendLine("[NOVR]   CPU breakdown:");
        builder.AppendLine(
            $"[NOVR]     cadence: updateDelta={FormatStats(_updateDeltaStats)}, " +
            $"endFrame->nextUpdate={FormatStats(_endFrameToNextUpdateStats)}, " +
            $"endOfFrame->nextUpdate={FormatStats(_endOfFrameToNextUpdateStats)}");
        builder.AppendLine(
            $"[NOVR]     script/render phases: update->late={FormatStats(_updateToLateStats)}, " +
            $"late->beginFrame={FormatStats(_lateToBeginFrameStats)}, " +
            $"begin->endFrame={FormatStats(_beginToEndFrameStats)}, " +
            $"update->endOfFrame={FormatStats(_updateToEndOfFrameStats)}");
        builder.AppendLine($"[NOVR]     playerLoop L1: {FormatPlayerLoopSummary(1, 8)}");
        builder.AppendLine($"[NOVR]     playerLoop L2: {FormatPlayerLoopSummary(2, 12)}");
        builder.AppendLine(
            $"[NOVR]     frameTiming waits: mainPresentWait={FormatFrameTimingMember("cpuMainThreadPresentWaitTime")}, " +
            $"renderPresentWait={FormatFrameTimingMember("cpuRenderThreadPresentWaitTime")}, " +
            $"mainFrame={FormatFrameTimingMember("cpuMainThreadFrameTime")}, " +
            $"renderFrame={FormatFrameTimingMember("cpuRenderThreadFrameTime")}, " +
            $"gpuFrame={FormatFrameTimingMember("gpuFrameTime")}");

        var topMarkers = _markerRecorders
            .Select(recorder => recorder.GetSnapshot())
            .Where(snapshot => snapshot != null && snapshot.SampleCount > 0 && (snapshot.AverageMs > 0.005 || snapshot.MaxMs > 0.05))
            .Cast<MarkerSnapshot>()
            .OrderByDescending(snapshot => Math.Max(snapshot.AverageMs, snapshot.LastMs))
            .ThenByDescending(snapshot => snapshot.MaxMs)
            .Take(10)
            .ToArray();

        if (topMarkers.Length == 0)
        {
            builder.AppendLine("[NOVR]     profilerTop: no non-zero Unity profiler markers exposed in this player");
            return;
        }

        builder.Append("[NOVR]     profilerTop: ");
        builder.AppendLine(string.Join("; ", topMarkers.Select(snapshot =>
            $"{snapshot.Category}/{snapshot.Name} avg={snapshot.AverageMs:0.000}ms last={snapshot.LastMs:0.000}ms max={snapshot.MaxMs:0.000}ms n={snapshot.SampleCount}")));
    }

    private void AppendXrState(StringBuilder builder)
    {
        builder.Append("[NOVR]   XR: ");
        builder.Append($"enabled={SafeValue(() => XRSettings.enabled.ToString())}, ");
        builder.Append($"active={SafeValue(() => XRSettings.isDeviceActive.ToString())}, ");
        builder.Append($"device='{SafeValue(() => XRSettings.loadedDeviceName)}', ");
        builder.Append($"eyeTexture={SafeValue(() => $"{XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}")}, ");
        builder.Append($"viewportScale={SafeValue(() => XRSettings.renderViewportScale.ToString("0.###"))}, ");
        builder.Append($"openxrRenderMode={SafeValue(() => OpenXRSettings.Instance.renderMode.ToString())}, ");
        builder.Append($"display={FormatDisplaySubsystem(GetRunningDisplaySubsystem())}");
        builder.AppendLine();
    }

    private void AppendXrRuntimeTimings(StringBuilder builder)
    {
        builder.Append("[NOVR]   XR runtime: ");
        var display = GetRunningDisplaySubsystem();
        if (display == null)
        {
            builder.AppendLine("no running display subsystem");
            return;
        }

        var appGpu = TryInvokeOutRaw(display, "TryGetAppGPUTimeLastFrame");
        var compositorGpu = TryInvokeOutRaw(display, "TryGetCompositorGPUTimeLastFrame");
        var dropped = TryInvokeOutRaw(display, "TryGetDroppedFrameCount");
        var presented = TryInvokeOutRaw(display, "TryGetFramePresentCount");
        var droppedLong = TryToLong(dropped);
        var presentedLong = TryToLong(presented);
        var droppedDelta = droppedLong.HasValue && _lastXrDroppedFrames.HasValue
            ? (droppedLong.Value - _lastXrDroppedFrames.Value).ToString()
            : "n/a";
        var presentedDelta = presentedLong.HasValue && _lastXrPresentedFrames.HasValue
            ? (presentedLong.Value - _lastXrPresentedFrames.Value).ToString()
            : "n/a";

        if (droppedLong.HasValue) _lastXrDroppedFrames = droppedLong.Value;
        if (presentedLong.HasValue) _lastXrPresentedFrames = presentedLong.Value;

        builder.Append($"appGpuLast={FormatMaybeMs(appGpu)}, ");
        builder.Append($"compositorGpuLast={FormatMaybeMs(compositorGpu)}, ");
        builder.Append($"dropped={FormatValue(dropped)}, droppedDelta={droppedDelta}, ");
        builder.Append($"presented={FormatValue(presented)}, presentedDelta={presentedDelta}");
        builder.AppendLine();
    }

    private static void AppendXrPassSummary(StringBuilder builder)
    {
        builder.Append("[NOVR]   XR passes: ");
        var display = GetRunningDisplaySubsystem();
        if (display == null)
        {
            builder.AppendLine("no running display subsystem");
            return;
        }

        builder.AppendLine(FormatXrPassSummary(display));
    }

    private static string FormatXrPassSummary(XRDisplaySubsystem display)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var countMethod = display.GetType().GetMethod("GetRenderPassCount", flags, null, Type.EmptyTypes, null);
            if (countMethod?.Invoke(display, null) is not int passCount)
            {
                return "renderPassCount unavailable";
            }

            var getRenderPass = display.GetType().GetMethods(flags)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "GetRenderPass") return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(int) &&
                           parameters[1].ParameterType.IsByRef;
                });
            if (getRenderPass == null) return $"renderPassCount={passCount}, GetRenderPass unavailable";

            var passType = getRenderPass.GetParameters()[1].ParameterType.GetElementType();
            if (passType == null) return $"renderPassCount={passCount}, pass type unavailable";

            var passParts = new List<string>();
            var totalParams = 0;
            for (var i = 0; i < passCount; i++)
            {
                var args = new[] { (object)i, Activator.CreateInstance(passType) };
                getRenderPass.Invoke(display, args);
                var pass = args[1];
                var paramSummary = GetRenderParameterSummary(pass, out var paramCount);
                totalParams += paramCount;
                passParts.Add(
                    $"pass[{i}]: params={paramCount}, cull={FormatValue(ReadMember(pass, "cullingPassIndex"))}, " +
                    $"target={FormatRenderTargetDesc(ReadMember(pass, "renderTargetDesc"))}, {paramSummary}");
            }

            var mode = SafeValue(() => OpenXRSettings.Instance.renderMode.ToString());
            var shape = passCount >= 2
                ? "MultiPass-shaped"
                : passCount == 1 && totalParams >= 2
                    ? "SPI-shaped"
                    : "unknown-shape";
            return $"mode={mode}, shape={shape}, passes={passCount}, totalParams={totalParams}; {string.Join("; ", passParts)}";
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string GetRenderParameterSummary(object? pass, out int paramCount)
    {
        paramCount = 0;
        if (pass == null) return "params unavailable";

        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var passType = pass.GetType();
            var countMethod = passType.GetMethod("GetRenderParameterCount", flags, null, Type.EmptyTypes, null);
            if (countMethod?.Invoke(pass, null) is not int count)
            {
                return "params unavailable";
            }

            paramCount = count;
            var getParameter = passType.GetMethods(flags)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "GetRenderParameter") return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(int) &&
                           parameters[1].ParameterType.IsByRef;
                });
            if (getParameter == null) return "GetRenderParameter unavailable";

            var parameterType = getParameter.GetParameters()[1].ParameterType.GetElementType();
            if (parameterType == null) return "parameter type unavailable";

            var slices = new List<string>();
            var viewports = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var args = new[] { (object)i, Activator.CreateInstance(parameterType) };
                getParameter.Invoke(pass, args);
                slices.Add(FormatValue(ReadMember(args[1], "textureArraySlice")));
                viewports.Add(FormatValue(ReadMember(args[1], "viewport")));
            }

            return $"slices=[{string.Join(",", slices)}], viewports=[{string.Join(" | ", viewports)}]";
        }
        catch (Exception ex)
        {
            return $"params unavailable:{ex.GetType().Name}";
        }
    }

    private void AppendBottleneckLadder(StringBuilder builder)
    {
        builder.AppendLine("[NOVR]   Bottleneck ladder:");

        if (_latestFrameTimingCount == 0)
        {
            builder.AppendLine("[NOVR]     1. Frame: unavailable from FrameTimingManager");
            return;
        }

        var count = Math.Min(_latestFrameTimingCount, FrameTimingBufferSize);
        var cpuFrame = AverageTiming(count, timing => timing.cpuFrameTime);
        var cpuMain = AverageTiming(count, timing => timing.cpuMainThreadFrameTime);
        var cpuRender = AverageTiming(count, timing => timing.cpuRenderThreadFrameTime);
        var gpuFrame = AverageTiming(count, timing => timing.gpuFrameTime);
        var srpFrame = _lastFrameElapsedTicks * TickToMs;
        var refresh = GetDisplayRefreshRate();
        var budget = refresh > 0f ? 1000.0 / refresh : 0.0;
        var cameraMsPerSecond = _cameraStats.Values.Sum(stats => stats.TotalMs) /
                                Math.Max(1f, ModConfiguration.Instance.RenderDiagnosticsIntervalSeconds.Value);
        var systemMsPerSecond = _systemStats.Values.Sum(stats => stats.TotalMs) /
                                Math.Max(1f, ModConfiguration.Instance.RenderDiagnosticsIntervalSeconds.Value);
        var topCamera = _cameraStats.Values.OrderByDescending(stats => stats.TotalMs).FirstOrDefault();
        var topSystem = _systemStats.Values.OrderByDescending(stats => stats.TotalMs).FirstOrDefault();

        var slowestFrameSide = cpuFrame >= gpuFrame ? "CPU/main pacing" : "GPU/rendering";
        if (budget > 0.0)
        {
            builder.AppendLine(
                $"[NOVR]     1. Frame budget: slowest={slowestFrameSide}, cpu={cpuFrame:0.00}ms ({FormatSignedMs(cpuFrame - budget)}), " +
                $"gpu={gpuFrame:0.00}ms ({FormatSignedMs(gpuFrame - budget)}), budget={budget:0.00}ms");
        }
        else
        {
            builder.AppendLine($"[NOVR]     1. Frame budget: slowest={slowestFrameSide}, cpu={cpuFrame:0.00}ms, gpu={gpuFrame:0.00}ms");
        }

        builder.AppendLine(
            $"[NOVR]     2. Engine render window: srpFrame={srpFrame:0.00}ms, renderThread={cpuRender:0.00}ms, mainThread={cpuMain:0.00}ms");
        if (Verbose)
        {
            builder.AppendLine(
                $"[NOVR]     3. Camera callbacks: total~{cameraMsPerSecond:0.00}ms/s, top={FormatTopTiming(topCamera)}");
            builder.AppendLine(
                $"[NOVR]     4. Patched systems: total~{systemMsPerSecond:0.00}ms/s, top={FormatTopTiming(topSystem)}");
        }

        if (_lateToBeginFrameStats.AverageMs > _beginToEndFrameStats.AverageMs * 2.0 && _lateToBeginFrameStats.AverageMs > 5.0)
        {
            builder.AppendLine("[NOVR]     Next drilldown: pre-SRP gap; inspect playerLoop L1/L2, frameTiming waits, and XR runtime counters.");
        }
        else if (Verbose && topSystem != null && topSystem.AverageMs > 1.0)
        {
            builder.AppendLine($"[NOVR]     Next drilldown: patched system '{topSystem.Key}' is high enough to break down further.");
        }
        else if (Verbose && topCamera != null && topCamera.AverageMs > 1.0)
        {
            builder.AppendLine($"[NOVR]     Next drilldown: camera bucket '{topCamera.LastCategory}' is high enough to break down further.");
        }
    }

    private void AppendCameraTimings(StringBuilder builder)
    {
        builder.AppendLine("[NOVR]   Cameras:");

        var rows = _cameraStats.Values
            .OrderByDescending(stats => stats.TotalMs)
            .Take(12)
            .ToArray();

        if (rows.Length == 0)
        {
            builder.AppendLine("[NOVR]     no camera render callbacks captured");
            return;
        }

        foreach (var stats in rows)
        {
            builder.AppendLine(
                $"[NOVR]     [{stats.LastCategory}] {stats.Key}: calls={stats.Calls}, avg={stats.AverageMs:0.00}ms, " +
                $"max={stats.MaxMs:0.00}ms, total={stats.TotalMs:0.00}ms, last={stats.LastCameraSummary}");
        }
    }

    private void AppendCameraBuckets(StringBuilder builder)
    {
        builder.AppendLine("[NOVR]   Camera buckets:");

        var rows = _cameraStats.Values
            .GroupBy(stats => stats.LastCategory)
            .Select(group =>
            {
                var calls = group.Sum(stats => stats.Calls);
                var total = group.Sum(stats => stats.TotalMs);
                return new
                {
                    Category = group.Key,
                    Cameras = group.Count(),
                    Calls = calls,
                    TotalMs = total,
                    AverageMs = calls == 0 ? 0.0 : total / calls,
                    MaxMs = group.Max(stats => stats.MaxMs)
                };
            })
            .OrderByDescending(row => row.TotalMs)
            .ToArray();

        if (rows.Length == 0)
        {
            builder.AppendLine("[NOVR]     no camera buckets captured");
            return;
        }

        foreach (var row in rows)
        {
            builder.AppendLine(
                $"[NOVR]     {row.Category}: cameras={row.Cameras}, calls={row.Calls}, " +
                $"avg={row.AverageMs:0.00}ms, max={row.MaxMs:0.00}ms, total={row.TotalMs:0.00}ms");
        }
    }

    private void AppendSystemTimings(StringBuilder builder)
    {
        builder.AppendLine("[NOVR]   Systems:");

        var rows = _systemStats.Values
            .OrderByDescending(stats => stats.TotalMs)
            .Take(18)
            .ToArray();

        if (rows.Length == 0)
        {
            builder.AppendLine("[NOVR]     no direct system timings captured");
            return;
        }

        foreach (var stats in rows)
        {
            builder.AppendLine(
                $"[NOVR]     {stats.Key}: calls={stats.Calls}, avg={stats.AverageMs:0.000}ms, " +
                $"max={stats.MaxMs:0.000}ms, total={stats.TotalMs:0.000}ms");
        }
    }

    private void AppendProfilerMarkers(StringBuilder builder)
    {
        if (_markerRecorders.Count == 0)
        {
            builder.AppendLine("[NOVR]   ProfilerMarkers: unavailable");
            return;
        }

        builder.AppendLine("[NOVR]   ProfilerMarkers:");
        foreach (var marker in _markerRecorders)
        {
            builder.AppendLine(marker.Format());
        }
    }

    private void AppendProfilerCounters(StringBuilder builder)
    {
        if (_counterRecorders.Count == 0)
        {
            builder.AppendLine("[NOVR]   ProfilerCounters: unavailable");
            return;
        }

        builder.AppendLine("[NOVR]   ProfilerCounters:");
        foreach (var recorder in _counterRecorders)
        {
            builder.AppendLine(recorder.Format());
        }
    }

    private static void AppendRenderSettings(StringBuilder builder)
    {
        builder.Append("[NOVR]   Quality: ");
        builder.Append($"level={QualitySettings.GetQualityLevel()} '{QualitySettings.names.ElementAtOrDefault(QualitySettings.GetQualityLevel())}', ");
        builder.Append($"vSync={QualitySettings.vSyncCount}, ");
        builder.Append($"antiAliasing={QualitySettings.antiAliasing}, ");
        builder.Append($"pixelLightCount={QualitySettings.pixelLightCount}, ");
        builder.Append($"shadowDistance={QualitySettings.shadowDistance:0.##}, ");
        builder.Append($"shadowResolution={QualitySettings.shadowResolution}, ");
        builder.Append($"shadowCascades={QualitySettings.shadowCascades}, ");
        builder.Append($"lodBias={QualitySettings.lodBias:0.###}, ");
        builder.Append($"fixedDelta={Time.fixedDeltaTime:0.0000}, ");
        builder.Append($"targetFrameRate={Application.targetFrameRate}");
        builder.AppendLine();

        var asset = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.renderPipelineAsset;
        builder.Append("[NOVR]   RenderPipeline: ");
        builder.Append(asset == null ? "builtin/null" : $"{asset.GetType().FullName} '{asset.name}'");
        if (asset != null)
        {
            builder.Append(", ");
            builder.Append(FormatObjectMembers(
                asset,
                "renderScale",
                "msaaSampleCount",
                "supportsHDR",
                "supportsCameraDepthTexture",
                "supportsCameraOpaqueTexture",
                "shadowDistance",
                "mainLightShadowmapResolution",
                "additionalLightsShadowmapResolution",
                "cascadeCount",
                "supportsSoftShadows",
                "maxAdditionalLightsCount"));
        }

        builder.AppendLine();
        builder.Append("[NOVR]   Screen: ");
        builder.Append($"screen={Screen.width}x{Screen.height}@{Screen.currentResolution.refreshRate}Hz, ");
        builder.Append($"fullScreen={Screen.fullScreenMode}, dpi={Screen.dpi:0.##}");
        builder.AppendLine();
    }

    private void AppendFlightState(StringBuilder builder)
    {
        builder.Append("[NOVR]   Flight: ");
        builder.Append($"state={SafeValue(() => SceneSingleton<CameraStateManager>.i.currentState.GetType().Name)}, ");
        builder.Append($"cameraMode={SafeValue(() => CameraStateManager.cameraMode.ToString())}, ");
        builder.Append($"aircraft='{SafeValue(() => Core.CurrentAircraftId ?? "null")}', ");
        builder.Append($"localAircraft={SafeValue(() => GameManager.GetLocalAircraft(out Aircraft aircraft) && aircraft != null ? aircraft.name : "null")}, ");
        builder.Append($"dynamicMapMaximized={SafeValue(() => DynamicMap.mapMaximized.ToString())}, ");
        builder.Append($"paused={SafeValue(() => GameplayUI.GameIsPaused.ToString())}");
        builder.AppendLine();

        builder.Append("[NOVR]   Cockpit/HUD: ");
        builder.Append($"gameMainCamera={SafeValue(() => FormatCamera(SceneSingleton<CameraStateManager>.i.mainCamera))}, ");
        builder.Append($"cockpitCamRender={SafeValue(() => FormatCamera(SceneSingleton<CameraStateManager>.i.cockpitCamRender))}, ");
        builder.Append($"novrMain={SafeValue(() => FormatCamera(APIBus.MainCamera))}, ");
        builder.Append($"vrHud={SafeValue(() => FormatCamera(APIBus.CockpitHudCamera))}");
        builder.AppendLine();
    }

    private void AppendHypotheses(StringBuilder builder)
    {
        builder.AppendLine("[NOVR]   Hypotheses:");
        if (_latestFrameTimingCount == 0)
        {
            builder.AppendLine("[NOVR]     FrameTimingManager unavailable; rely on Oculus PerfLog plus camera/system totals.");
            return;
        }

        var count = Math.Min(_latestFrameTimingCount, FrameTimingBufferSize);
        var cpuFrame = AverageTiming(count, timing => timing.cpuFrameTime);
        var cpuMain = AverageTiming(count, timing => timing.cpuMainThreadFrameTime);
        var cpuRender = AverageTiming(count, timing => timing.cpuRenderThreadFrameTime);
        var gpuFrame = AverageTiming(count, timing => timing.gpuFrameTime);
        var refresh = GetDisplayRefreshRate();
        var budget = refresh > 0f ? 1000.0 / refresh : 0.0;
        var renderPassCount = SafeDouble(() => Convert.ToDouble(TryInvokeNoArg(GetRunningDisplaySubsystem()!, "GetRenderPassCount")));

        if (budget > 0.0 && cpuFrame > budget * 1.5 && gpuFrame > budget * 1.2)
        {
            builder.AppendLine("[NOVR]     CPU and GPU are both over refresh budget; likely resolution plus MultiPass world rendering, not just one script path.");
        }
        else if (budget > 0.0 && cpuFrame > budget * 1.5 && gpuFrame <= budget * 1.1)
        {
            builder.AppendLine("[NOVR]     CPU is over budget while GPU is near budget; suspect frame pacing, main-thread wait, culling, scripts, or render-thread sync.");
        }
        else if (budget > 0.0 && gpuFrame > budget * 1.2 && cpuFrame <= budget * 1.2)
        {
            builder.AppendLine("[NOVR]     GPU is the clearer over-budget side; lower resolution/MSAA/shadows/post effects should move this fastest.");
        }

        if (renderPassCount >= 2.0)
        {
            builder.AppendLine("[NOVR]     XR renderPassCount >= 2 confirms MultiPass; world/URP work is expected to be repeated per eye.");
        }

        if (_endOfFrameToNextUpdateStats.AverageMs > 5.0 && _beginToEndFrameStats.AverageMs < 5.0)
        {
            builder.AppendLine(
                $"[NOVR]     Large post-render gap: EndOfFrame->nextUpdate avg={_endOfFrameToNextUpdateStats.AverageMs:0.00}ms " +
                $"while begin->endFrame avg={_beginToEndFrameStats.AverageMs:0.00}ms; suspect wait/pacing/runtime cadence.");
        }

        if (_lateToBeginFrameStats.AverageMs > 6.0 && _beginToEndFrameStats.AverageMs < _lateToBeginFrameStats.AverageMs * 0.5)
        {
            builder.AppendLine(
                $"[NOVR]     Large pre-SRP gap: LateUpdate->beginFrame avg={_lateToBeginFrameStats.AverageMs:0.00}ms " +
                $"vs begin->endFrame avg={_beginToEndFrameStats.AverageMs:0.00}ms; suspect XR wait, render scheduling, or previous-frame pacing before SRP starts.");
        }

        if (cpuFrame > 15.5 && cpuFrame < 18.5 && refresh >= 100f)
        {
            builder.AppendLine("[NOVR]     CPU frame near 16.7ms at 120Hz suggests a 60fps/reprojection cadence or wait/pacing boundary.");
        }

        var eyePixels = XRSettings.eyeTextureWidth * XRSettings.eyeTextureHeight;
        if (eyePixels >= 7_000_000)
        {
            builder.AppendLine(
                $"[NOVR]     Eye texture is very large ({XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, {eyePixels / 1_000_000.0:0.0}MP per eye); " +
                "pixel cost should scale strongly with Meta resolution.");
        }

        builder.AppendLine(
            $"[NOVR]     Diagnostic totals: cpuMain={cpuMain:0.00}ms, cpuRender={cpuRender:0.00}ms, gpu={gpuFrame:0.00}ms, " +
            $"late->beginFrame={_lateToBeginFrameStats.AverageMs:0.00}ms, begin->endFrame={_beginToEndFrameStats.AverageMs:0.00}ms.");
    }

    private void AppendCameraInventory(StringBuilder builder)
    {
        var cameras = GetAllCurrentCameras();

        builder.AppendLine($"[NOVR]   Camera inventory ({cameras.Length}):");
        foreach (var camera in cameras.Where(camera => camera != null).OrderByDescending(camera => camera.depth))
        {
            builder.AppendLine($"[NOVR]     {FormatCamera(camera)} stack=[{FormatCameraStack(camera)}]");
        }
    }

    private static Camera[] GetAllCurrentCameras()
    {
        var cameras = new Camera[Camera.allCamerasCount];
        Camera.GetAllCameras(cameras);
        return cameras.Where(camera => camera != null).ToArray();
    }

    private void AppendXrRenderPassDetails(StringBuilder builder)
    {
        var display = GetRunningDisplaySubsystem();
        if (display == null)
        {
            builder.AppendLine("[NOVR]   XR render passes: no running display subsystem");
            return;
        }

        builder.AppendLine("[NOVR]   XR render passes:");
        AppendRenderPassDetails(builder, display);
    }

    private static void AppendRenderPassDetails(StringBuilder builder, XRDisplaySubsystem display)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var countMethod = display.GetType().GetMethod("GetRenderPassCount", flags, null, Type.EmptyTypes, null);
            if (countMethod?.Invoke(display, null) is not int count)
            {
                builder.AppendLine("[NOVR]     renderPassCount unavailable");
                return;
            }

            builder.AppendLine($"[NOVR]     renderPassCount={count}");
            var getRenderPass = display.GetType().GetMethods(flags)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "GetRenderPass") return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(int) &&
                           parameters[1].ParameterType.IsByRef;
                });

            if (getRenderPass == null)
            {
                builder.AppendLine("[NOVR]     GetRenderPass unavailable");
                return;
            }

            var passType = getRenderPass.GetParameters()[1].ParameterType.GetElementType();
            if (passType == null)
            {
                builder.AppendLine("[NOVR]     GetRenderPass pass type unavailable");
                return;
            }

            for (var i = 0; i < count; i++)
            {
                var args = new[] { (object)i, Activator.CreateInstance(passType) };
                getRenderPass.Invoke(display, args);
                var pass = args[1];
                builder.AppendLine($"[NOVR]     pass[{i}] {FormatObjectMembers(pass, "renderTarget", "renderTargetDesc", "cullingPassIndex")}");
                AppendRenderPassParameters(builder, pass);
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"[NOVR]     unavailable:{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AppendRenderPassParameters(StringBuilder builder, object? pass)
    {
        if (pass == null) return;

        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var passType = pass.GetType();
            var countMethod = passType.GetMethod("GetRenderParameterCount", flags, null, Type.EmptyTypes, null);
            if (countMethod?.Invoke(pass, null) is not int count)
            {
                return;
            }

            builder.AppendLine($"[NOVR]       renderParameterCount={count}");
            var getParameter = passType.GetMethods(flags)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "GetRenderParameter") return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(int) &&
                           parameters[1].ParameterType.IsByRef;
                });

            if (getParameter == null) return;

            var parameterType = getParameter.GetParameters()[1].ParameterType.GetElementType();
            if (parameterType == null) return;

            for (var i = 0; i < count; i++)
            {
                var args = new[] { (object)i, Activator.CreateInstance(parameterType) };
                getParameter.Invoke(pass, args);
                builder.AppendLine($"[NOVR]       param[{i}] {FormatObjectMembers(args[1], "viewport", "textureArraySlice", "projection", "view", "previousView")}");
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"[NOVR]       render parameters unavailable:{ex.GetType().Name}");
        }
    }

    private void TryCreateMarkerRecorders()
    {
        var markerNames = new[]
        {
            "DetailRenderer.LateUpdate",
            "DetailRenderer.Compute",
            "DetailRenderer.Compute.Queue",
            "DetailRenderer.Compute.Dispatch",
            "DetailRenderer.Render",
            "GrassRenderer.Compute",
            "GrassRenderer.Render",
            "TreeRenderer.Compute",
            "TreeRenderer.Render",
            "PlayerLoop",
            "Camera.Render",
            "RenderLoop.Draw",
            "CullResults.Cull",
            "Culling",
            "Shadows.RenderShadowMap",
            "ScriptRunBehaviourUpdate",
            "ScriptRunBehaviourLateUpdate",
            "ScriptRunDelayedDynamicFrameRate",
            "Gfx.WaitForPresentOnGfxThread",
            "Gfx.PresentFrame",
            "WaitForTargetFPS",
            "XR.WaitForGPU",
            "XR.SubmitFrame"
        };

        foreach (var markerName in markerNames)
        {
            TryAddRecorder(_markerRecorders, ProfilerCategory.Scripts, markerName);
            TryAddRecorder(_markerRecorders, ProfilerCategory.Render, markerName);
            TryAddRecorder(_markerRecorders, ProfilerCategory.Internal, markerName);
        }

        var counters = new[]
        {
            (ProfilerCategory.Render, "Draw Calls Count"),
            (ProfilerCategory.Render, "Batches Count"),
            (ProfilerCategory.Render, "SetPass Calls Count"),
            (ProfilerCategory.Render, "Triangles Count"),
            (ProfilerCategory.Render, "Vertices Count"),
            (ProfilerCategory.Render, "Render Textures Count"),
            (ProfilerCategory.Memory, "GC Allocated In Frame"),
            (ProfilerCategory.Memory, "GC Used Memory"),
            (ProfilerCategory.Memory, "GC Reserved Memory"),
            (ProfilerCategory.Memory, "Total Used Memory"),
            (ProfilerCategory.Memory, "Texture Memory"),
            (ProfilerCategory.Memory, "Render Texture Memory"),
            (ProfilerCategory.Memory, "Mesh Memory")
        };

        foreach (var (category, counterName) in counters)
        {
            TryAddRecorder(_counterRecorders, category, counterName);
        }
    }

    private void TryAddRecorder(List<MarkerRecorder> recorders, ProfilerCategory category, string markerName)
    {
        try
        {
            var recorder = new MarkerRecorder(category, markerName);
            if (recorder.Valid)
            {
                recorders.Add(recorder);
            }
            else
            {
                recorder.Dispose();
            }
        }
        catch (Exception ex)
        {
            if (_warnedProfilerRecorderSetup) return;

            _warnedProfilerRecorderSetup = true;
            Debug.LogWarning($"[NOVR] Render diagnostics could not create profiler recorders: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ResetIntervalStats()
    {
        _cameraStats.Clear();
        _systemStats.Clear();
        _playerLoopStats.Clear();
        _playerLoopStartTicks.Clear();
        _playerLoopAllocStats.Clear();
        _cameraAllocStats.Clear();
        _framePartsCount = 0;
        foreach (var marker in _markerRecorders)
        {
            marker.Reset();
        }

        foreach (var recorder in _counterRecorders)
        {
            recorder.Reset();
        }

        ResetPhaseStats();
    }

    private void ResetPhaseStats()
    {
        _updateDeltaStats.Reset();
        _timeDeltaStats.Reset();
        _unscaledDeltaStats.Reset();
        _smoothDeltaStats.Reset();
        _updateToLateStats.Reset();
        _updateToBeforeRenderStats.Reset();
        _lateToBeforeRenderStats.Reset();
        _lateToBeginFrameStats.Reset();
        _beforeRenderToBeginFrameStats.Reset();
        _beginToEndFrameStats.Reset();
        _endFrameToNextUpdateStats.Reset();
        _endOfFrameToNextUpdateStats.Reset();
        _updateToEndOfFrameStats.Reset();
    }

    public static void RecordSystemTiming(string key, long elapsedTicks)
    {
        if (!IsEnabled || _current == null) return;

        _current.RecordSystemTimingInstance(key, elapsedTicks * TickToMs);
    }

    private void RecordSystemTimingInstance(string key, double elapsedMs)
    {
        if (!_systemStats.TryGetValue(key, out var stats))
        {
            stats = new TimingStats(key);
            _systemStats.Add(key, stats);
        }

        stats.Record(elapsedMs);
    }

    private double AverageTiming(uint count, Func<FrameTiming, double> selector)
    {
        if (count == 0) return 0.0;

        double total = 0.0;
        for (var i = 0; i < count; i++)
        {
            total += selector(_frameTimings[i]);
        }

        return total / count;
    }

    private static XRDisplaySubsystem? GetRunningDisplaySubsystem()
    {
        DisplaySubsystems.Clear();
        SubsystemManager.GetInstances(DisplaySubsystems);
        return DisplaySubsystems.FirstOrDefault(display => display != null && display.running);
    }

    private static string FormatDisplaySubsystem(XRDisplaySubsystem? display)
    {
        if (display == null) return "none";

        return $"type='{display.GetType().FullName}', running={display.running}, " +
               $"refresh={TryInvokeOutValue(display, "TryGetDisplayRefreshRate")}, " +
               $"renderPassCount={TryInvokeNoArg(display, "GetRenderPassCount")}";
    }

    private static float GetDisplayRefreshRate()
    {
        var display = GetRunningDisplaySubsystem();
        if (display == null) return 0f;

        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = display.GetType().GetMethod(
                "TryGetDisplayRefreshRate",
                flags,
                null,
                new[] { typeof(float).MakeByRefType() },
                null);
            if (method == null) return 0f;

            var args = new object[] { 0f };
            return method.Invoke(display, args) is bool success && success && args[0] is float refresh
                ? refresh
                : 0f;
        }
        catch
        {
            return 0f;
        }
    }

    private static double MsToFps(double frameMs)
    {
        return frameMs > 0.0 ? 1000.0 / frameMs : 0.0;
    }

    private static string FormatSignedMs(double value)
    {
        return $"{value:+0.00;-0.00;0.00}ms";
    }

    private static string FormatStats(TimingStats stats)
    {
        return stats.Calls == 0
            ? "n=0"
            : $"avg={stats.AverageMs:0.00}ms,min={stats.MinMs:0.00},max={stats.MaxMs:0.00},n={stats.Calls}";
    }

    private string FormatFrameTimingMember(string memberName)
    {
        var value = AverageFrameTimingMember(memberName);
        return value.HasValue ? $"avg={value.Value:0.00}ms" : "n/a";
    }

    private double? AverageFrameTimingMember(string memberName)
    {
        if (_latestFrameTimingCount == 0) return null;

        var count = Math.Min(_latestFrameTimingCount, FrameTimingBufferSize);
        var seen = 0;
        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            var value = ReadMember(_frameTimings[i], memberName);
            if (!TryToDouble(value, out var numeric)) continue;

            total += numeric;
            seen++;
        }

        return seen == 0 ? null : total / seen;
    }

    private string FormatPacingSuspects()
    {
        var rows = new List<(string Name, double Value)>
        {
            ("late->beginFrame", _lateToBeginFrameStats.AverageMs),
            ("begin->endFrame", _beginToEndFrameStats.AverageMs),
            ("endFrame->nextUpdate", _endFrameToNextUpdateStats.AverageMs),
            ("endOfFrame->nextUpdate", _endOfFrameToNextUpdateStats.AverageMs),
            ("update->late", _updateToLateStats.AverageMs)
        };

        var mainPresentWait = AverageFrameTimingMember("cpuMainThreadPresentWaitTime");
        if (mainPresentWait.HasValue) rows.Add(("FrameTiming.mainPresentWait", mainPresentWait.Value));

        var renderPresentWait = AverageFrameTimingMember("cpuRenderThreadPresentWaitTime");
        if (renderPresentWait.HasValue) rows.Add(("FrameTiming.renderPresentWait", renderPresentWait.Value));

        return string.Join(", ", rows
            .OrderByDescending(row => row.Value)
            .Take(5)
            .Select(row => $"{row.Name}={row.Value:0.00}ms"));
    }

    private string FormatPlayerLoopSummary(int level, int take)
    {
        var prefix = $"L{level}/";
        var rows = _playerLoopStats.Values
            .Where(stats => stats.Key.StartsWith(prefix))
            .OrderByDescending(stats => stats.TotalMs)
            .Take(take)
            .ToArray();

        if (rows.Length == 0) return "n=0";

        return string.Join("; ", rows.Select(stats =>
            $"{stats.Key.Substring(prefix.Length)} avg={stats.AverageMs:0.00}ms max={stats.MaxMs:0.00}ms total={stats.TotalMs:0.1}ms n={stats.Calls}"));
    }

    private static string FormatTopTiming(CameraTimingStats? stats)
    {
        return stats == null
            ? "none"
            : $"{stats.LastCategory}/{stats.Key}: avg={stats.AverageMs:0.00}ms, max={stats.MaxMs:0.00}ms";
    }

    private static string FormatTopTiming(TimingStats? stats)
    {
        return stats == null
            ? "none"
            : $"{stats.Key}: avg={stats.AverageMs:0.000}ms, max={stats.MaxMs:0.000}ms";
    }

    private static string GetCameraKey(Camera camera)
    {
        return $"{GetPath(camera.transform)}#{camera.GetInstanceID()}";
    }

    private static string FormatCamera(Camera? camera)
    {
        if (camera == null) return "null";

        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        var renderType = data != null ? data.renderType.ToString() : "n/a";
        // cameraStack's getter logs a warning every access on non-Base cameras.
        var stackCount = data != null && data.renderType == CameraRenderType.Base
            ? data.cameraStack?.Count ?? 0
            : 0;
        return $"category={ClassifyCamera(camera)}, path='{GetPath(camera.transform)}', enabled={camera.enabled}, active={camera.gameObject.activeInHierarchy}, " +
               $"depth={camera.depth:0.##}, stereo={camera.stereoTargetEye}, target='{FormatObjectName(camera.targetTexture)}', " +
               $"targetSize={FormatRenderTargetSize(camera)}, rect={camera.rect}, clear={camera.clearFlags}, " +
               $"fov={camera.fieldOfView:0.##}, near={camera.nearClipPlane:0.###}, far={camera.farClipPlane:0.#}, " +
               $"cullingMask=0x{camera.cullingMask:X8}, urp={renderType}, " +
               $"post={SafeValue(() => data == null ? "n/a" : data.renderPostProcessing.ToString())}, " +
               $"depthTex={SafeValue(() => data == null ? "n/a" : data.requiresDepthTexture.ToString())}, " +
               $"opaqueTex={SafeValue(() => data == null ? "n/a" : data.requiresColorTexture.ToString())}, " +
               $"stack={stackCount}";
    }

    private static string ClassifyCamera(Camera? camera)
    {
        if (camera == null) return "null";

        var path = GetPath(camera.transform);
        if (path.Contains("VrCockpitHudCamera")) return "vrHud";
        if (path.Contains("cockpitRenderer")) return "cockpitOverlay";
        if (path.Contains("postProcessingRenderer")) return "postProcessingOverlay";
        if (path.Contains("screenCam")) return "mfdScreen";
        if (path.Contains("targetCam") || path.Contains("TargetCamera")) return "targetCamera";
        if (path.Contains("Reflection Probes Camera")) return "reflectionProbe";
        if (path.Contains("Menu Camera")) return "menu";
        if (path.Contains("NOVR Main Camera"))
        {
            if (path.Contains("cockpit_")) return "worldCockpit";
            if (path.Contains("Datum")) return "worldTv";
            if (path.Contains("trainer")) return "worldTrainer";
            if (path.Contains("pilotDismounted")) return "worldDismounted";
            return "world";
        }

        return camera.targetTexture == null ? "screenOther" : "rtOther";
    }

    private static string FormatRenderTargetSize(Camera camera)
    {
        if (camera.targetTexture != null)
        {
            return $"{camera.targetTexture.width}x{camera.targetTexture.height}x{camera.targetTexture.volumeDepth}, msaa={camera.targetTexture.antiAliasing}";
        }

        return $"XR/Screen {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}";
    }

    private static string FormatCameraStack(Camera? camera)
    {
        if (camera == null) return "";

        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        // cameraStack's getter logs a warning every access on non-Base cameras.
        if (data == null || data.renderType != CameraRenderType.Base) return "";
        if (data.cameraStack == null || data.cameraStack.Count == 0) return "";

        return string.Join(", ", data.cameraStack.Select(stackedCamera =>
            stackedCamera == null ? "null" : $"{GetPath(stackedCamera.transform)}#{stackedCamera.GetInstanceID()}"));
    }

    private static string FormatCameraStackOwners(Camera camera)
    {
        var owners = GetAllCurrentCameras()
            .Where(owner => owner != null && owner != camera)
            .Select(owner => new { Camera = owner, Data = owner.GetComponent<UniversalAdditionalCameraData>() })
            .Where(owner => owner.Data != null && owner.Data.renderType == CameraRenderType.Base)
            .Where(owner => owner.Data.cameraStack != null && owner.Data.cameraStack.Contains(camera))
            .Select(owner => $"{GetPath(owner.Camera.transform)}#{owner.Camera.GetInstanceID()}")
            .ToArray();

        return owners.Length == 0 ? "none" : string.Join(", ", owners);
    }

    private static string GetPath(Transform? transform)
    {
        if (transform == null) return "null";

        var path = transform.name;
        var parent = transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }

        return path;
    }

    private static string FormatObjectName(UnityEngine.Object? value)
    {
        return value == null ? "null" : $"{value.name}#{value.GetInstanceID()}";
    }

    private static object? ReadMember(object? value, string memberName)
    {
        if (value == null) return null;

        try
        {
            var type = value.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = type.GetProperty(memberName, flags);
            if (property != null) return property.GetValue(value, null);

            var field = type.GetField(memberName, flags);
            return field?.GetValue(value);
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string FormatRenderTargetDesc(object? desc)
    {
        if (desc == null) return "null";

        var width = FormatValue(ReadMember(desc, "width"));
        var height = FormatValue(ReadMember(desc, "height"));
        var volumeDepth = FormatValue(ReadMember(desc, "volumeDepth"));
        var msaa = FormatValue(ReadMember(desc, "msaaSamples"));
        var dimension = FormatValue(ReadMember(desc, "dimension"));
        var colorFormat = FormatValue(ReadMember(desc, "colorFormat"));
        return $"{width}x{height}x{volumeDepth}, msaa={msaa}, dim={dimension}, format={colorFormat}";
    }

    private static string FormatObjectMembers(object? value, params string[] memberNames)
    {
        if (value == null) return "null";

        var type = value.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var parts = new List<string>();

        foreach (var memberName in memberNames)
        {
            try
            {
                var property = type.GetProperty(memberName, flags);
                if (property != null)
                {
                    parts.Add($"{memberName}={FormatValue(property.GetValue(value, null))}");
                    continue;
                }

                var field = type.GetField(memberName, flags);
                if (field != null)
                {
                    parts.Add($"{memberName}={FormatValue(field.GetValue(value))}");
                }
            }
            catch (Exception ex)
            {
                parts.Add($"{memberName}=unavailable:{ex.GetType().Name}");
            }
        }

        return parts.Count == 0 ? value.ToString() ?? "" : string.Join(", ", parts);
    }

    private static string TryInvokeNoArg(object target, string methodName)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = target.GetType().GetMethod(methodName, flags, null, Type.EmptyTypes, null);
            return method == null ? "n/a" : FormatValue(method.Invoke(target, null));
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string TryInvokeOutValue(object target, string methodName)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = target.GetType().GetMethod(methodName, flags);
            if (method == null) return "n/a";

            var parameters = method.GetParameters();
            if (parameters.Length != 1 || !parameters[0].ParameterType.IsByRef) return "n/a";

            var elementType = parameters[0].ParameterType.GetElementType();
            var outValue = elementType?.IsValueType == true ? Activator.CreateInstance(elementType) : null;
            var args = new[] { outValue };
            var result = method.Invoke(target, args);
            return result is bool ok && ok ? FormatValue(args[0]) : "unavailable";
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static object? TryInvokeOutRaw(object target, string methodName)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = target.GetType().GetMethod(methodName, flags);
            if (method == null) return "n/a";

            var parameters = method.GetParameters();
            if (parameters.Length != 1 || !parameters[0].ParameterType.IsByRef) return "n/a";

            var elementType = parameters[0].ParameterType.GetElementType();
            var outValue = elementType?.IsValueType == true ? Activator.CreateInstance(elementType) : null;
            var args = new[] { outValue };
            var result = method.Invoke(target, args);
            return result is bool ok && ok ? args[0] : "unavailable";
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static string SafeValue(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static double SafeDouble(Func<double> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0.0;
        }
    }

    private static bool TryToDouble(object? value, out double numeric)
    {
        numeric = 0.0;
        if (value == null) return false;

        try
        {
            numeric = Convert.ToDouble(value);
            return !double.IsNaN(numeric) && !double.IsInfinity(numeric);
        }
        catch
        {
            return false;
        }
    }

    private static long? TryToLong(object? value)
    {
        if (value == null) return null;

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatMaybeMs(object? value)
    {
        return TryToDouble(value, out var numeric) ? $"{numeric:0.00}ms" : FormatValue(value);
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "null",
            Array array => $"[{string.Join(", ", array.Cast<object>().Select(FormatValue))}]",
            _ => value.ToString() ?? ""
        };
    }

    private sealed class CameraTimingStats
    {
        public CameraTimingStats(string key)
        {
            Key = key;
        }

        public string Key { get; }
        public int Calls { get; private set; }
        public double TotalMs { get; private set; }
        public double MaxMs { get; private set; }
        public string LastCategory { get; private set; } = "";
        public string LastCameraSummary { get; private set; } = "";
        public double AverageMs => Calls == 0 ? 0.0 : TotalMs / Calls;

        public void Record(double elapsedMs, Camera camera)
        {
            Calls++;
            TotalMs += elapsedMs;
            MaxMs = Math.Max(MaxMs, elapsedMs);
            LastCategory = ClassifyCamera(camera);
            LastCameraSummary = FormatCamera(camera);
        }
    }

    private sealed class AllocStats
    {
        public int Samples { get; private set; }
        public long TotalBytes { get; private set; }
        public long MaxBytes { get; private set; }

        public void Record(long bytes)
        {
            Samples++;
            TotalBytes += bytes;
            if (bytes > MaxBytes) MaxBytes = bytes;
        }
    }

    private sealed class TimingStats
    {
        public TimingStats(string key)
        {
            Key = key;
        }

        public string Key { get; }
        public int Calls { get; private set; }
        public double TotalMs { get; private set; }
        public double MinMs { get; private set; } = double.MaxValue;
        public double MaxMs { get; private set; }
        public double AverageMs => Calls == 0 ? 0.0 : TotalMs / Calls;

        public void Record(double elapsedMs)
        {
            Calls++;
            TotalMs += elapsedMs;
            MinMs = Math.Min(MinMs, elapsedMs);
            MaxMs = Math.Max(MaxMs, elapsedMs);
        }

        public void Reset()
        {
            Calls = 0;
            TotalMs = 0.0;
            MinMs = double.MaxValue;
            MaxMs = 0.0;
        }
    }

    private sealed class MarkerRecorder : IDisposable
    {
        private readonly string _markerName;
        private readonly ProfilerCategory _category;
        private readonly ProfilerRecorder _recorder;
        private readonly List<ProfilerRecorderSample> _samples = new();

        public MarkerRecorder(ProfilerCategory category, string markerName)
        {
            _markerName = markerName;
            _category = category;
            _recorder = ProfilerRecorder.StartNew(category, markerName, 128);
        }

        public bool Valid => _recorder.Valid;

        public MarkerSnapshot? GetSnapshot()
        {
            if (!_recorder.Valid) return null;

            _samples.Clear();
            _recorder.CopyTo(_samples);
            var sampleCount = _samples.Count;
            var avg = 0.0;
            var max = 0.0;

            if (sampleCount > 0)
            {
                long total = 0;
                long maxValue = 0;
                foreach (var sample in _samples)
                {
                    total += sample.Value;
                    maxValue = Math.Max(maxValue, sample.Value);
                }

                avg = NsToMs(total / (double)sampleCount);
                max = NsToMs(maxValue);
            }

            return new MarkerSnapshot(
                _category.Name,
                _markerName,
                sampleCount,
                NsToMs(_recorder.LastValue),
                avg,
                max);
        }

        public string Format()
        {
            var snapshot = GetSnapshot();
            if (snapshot == null)
            {
                return $"[NOVR]     {_category.Name}/{_markerName}: unavailable";
            }

            return $"[NOVR]     {snapshot.Category}/{snapshot.Name}: samples={snapshot.SampleCount}, last={snapshot.LastMs:0.000}ms, " +
                   $"avg={snapshot.AverageMs:0.000}ms, max={snapshot.MaxMs:0.000}ms";
        }

        public void Reset()
        {
            if (_recorder.Valid)
            {
                _recorder.Reset();
            }
        }

        public void Dispose()
        {
            _recorder.Dispose();
        }

        private static double NsToMs(long value)
        {
            return value / 1_000_000.0;
        }

        private static double NsToMs(double value)
        {
            return value / 1_000_000.0;
        }
    }

    private sealed class MarkerSnapshot
    {
        public MarkerSnapshot(string category, string name, int sampleCount, double lastMs, double averageMs, double maxMs)
        {
            Category = category;
            Name = name;
            SampleCount = sampleCount;
            LastMs = lastMs;
            AverageMs = averageMs;
            MaxMs = maxMs;
        }

        public string Category { get; }
        public string Name { get; }
        public int SampleCount { get; }
        public double LastMs { get; }
        public double AverageMs { get; }
        public double MaxMs { get; }
    }
}
