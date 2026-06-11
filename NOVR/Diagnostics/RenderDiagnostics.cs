using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace NOVR.Diagnostics;

internal static class RenderDiagnostics
{
    private const string Prefix = "[NOVR.Diagnostics]";
    private static readonly HashSet<string> LoggedOnceKeys = new();

    public static bool Enabled => ModConfiguration.Instance?.RenderDiagnosticsEnabled.Value ?? true;

    public static void Info(string message)
    {
        if (!Enabled) return;
        Debug.Log($"{Prefix} {message}");
    }

    public static void Warning(string message)
    {
        if (!Enabled) return;
        Debug.LogWarning($"{Prefix} {message}");
    }

    public static void Error(string message)
    {
        if (!Enabled) return;
        Debug.LogError($"{Prefix} {message}");
    }

    public static void InfoOnce(string key, string message)
    {
        if (!Enabled || !LoggedOnceKeys.Add(key)) return;
        Info(message);
    }

    public static string DescribeObject(UnityEngine.Object? value)
    {
        return value == null
            ? "<null>"
            : $"{value.name} ({value.GetType().FullName})";
    }

    public static string DescribeCamera(Camera? camera)
    {
        if (camera == null)
        {
            return "<null camera>";
        }

        var targetTexture = camera.targetTexture == null
            ? "null"
            : $"{camera.targetTexture.name}:{camera.targetTexture.width}x{camera.targetTexture.height}";
        return
            $"{camera.name} enabled={camera.enabled} depth={camera.depth} clear={camera.clearFlags} " +
            $"stereoEye={camera.stereoTargetEye} targetTexture={targetTexture} cullingMask=0x{camera.cullingMask:X}";
    }

    public static string DescribeRenderPipeline()
    {
        return
            $"current={DescribeObject(GraphicsSettings.currentRenderPipeline)}; " +
            $"default={DescribeObject(GraphicsSettings.defaultRenderPipeline)}";
    }

    public static string DescribeUniversalAdditionalCameraData(GameObject gameObject)
    {
        var type = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
        if (type == null)
        {
            return "URP additional camera data type not loaded";
        }

        var data = gameObject.GetComponent(type);
        if (data == null)
        {
            return "URP additional camera data missing";
        }

        return
            $"URP data renderType={ReadProperty(data, "renderType")} " +
            $"allowXRRendering={ReadProperty(data, "allowXRRendering")} " +
            $"cameraStackCount={ReadCameraStackCount(data)}";
    }

    public static string DescribeXrState()
    {
        return
            $"XRSettings.enabled={XRSettings.enabled} loadedDevice={XRSettings.loadedDeviceName} " +
            $"stereoMode={ReadStaticProperty(typeof(XRSettings), "stereoRenderingMode")}";
    }

    public static string DescribeXrDisplays()
    {
        var displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetInstances(displays);
        if (displays.Count == 0)
        {
            return "XR displays: none";
        }

        return "XR displays: " + string.Join("; ", displays.Select(display =>
            $"{GetSubsystemId(display)} running={display.running}"));
    }

    private static object? ReadProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance);
    }

    private static object? ReadStaticProperty(Type type, string propertyName)
    {
        return type.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
    }

    private static int ReadCameraStackCount(object additionalCameraData)
    {
        if (ReadProperty(additionalCameraData, "cameraStack") is ICollection<Camera> stack)
        {
            return stack.Count;
        }

        return -1;
    }

    private static string GetSubsystemId(XRDisplaySubsystem display)
    {
        var descriptor = ReadProperty(display, "subsystemDescriptor") ?? ReadProperty(display, "SubsystemDescriptor");
        var id = descriptor == null ? null : ReadProperty(descriptor, "id");
        return id?.ToString() ?? display.GetType().FullName ?? "<unknown>";
    }
}
