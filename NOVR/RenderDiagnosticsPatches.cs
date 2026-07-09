using System.Diagnostics;
using HarmonyLib;
using NuclearOption.Effects;
using NOVR.VrUi;

namespace NOVR;

internal static class RenderDiagnosticsPatches
{
    private static void Begin(out long startTicks)
    {
        startTicks = RenderDiagnosticsBehaviour.IsEnabled ? Stopwatch.GetTimestamp() : 0;
    }

    private static void End(string key, long startTicks)
    {
        if (startTicks == 0) return;

        RenderDiagnosticsBehaviour.RecordSystemTiming(key, Stopwatch.GetTimestamp() - startTicks);
    }

    [HarmonyPatch(typeof(CameraStateManager), "Update")]
    private static class CameraStateManagerUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("CameraStateManager.Update", __state);
    }

    [HarmonyPatch(typeof(CameraStateManager), "LateUpdate")]
    private static class CameraStateManagerLateUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("CameraStateManager.LateUpdate", __state);
    }

    [HarmonyPatch(typeof(CameraStateManager), "FixedUpdate")]
    private static class CameraStateManagerFixedUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("CameraStateManager.FixedUpdate", __state);
    }

    [HarmonyPatch(typeof(CameraCockpitState), "UpdateState")]
    private static class CameraCockpitStateUpdateStatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("CameraCockpitState.UpdateState", __state);
    }

    [HarmonyPatch(typeof(CameraCockpitState), "FixedUpdateState")]
    private static class CameraCockpitStateFixedUpdateStatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("CameraCockpitState.FixedUpdateState", __state);
    }

    [HarmonyPatch(typeof(FlightHud), "Update")]
    private static class FlightHudUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("FlightHud.Update", __state);
    }

    [HarmonyPatch(typeof(CombatHUD), "LateUpdate")]
    private static class CombatHudLateUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("CombatHUD.LateUpdate", __state);
    }

    [HarmonyPatch(typeof(CombatHUD), "FixedUpdate")]
    private static class CombatHudFixedUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("CombatHUD.FixedUpdate", __state);
    }

    [HarmonyPatch(typeof(MFDAppManager), "Update")]
    private static class MfdAppManagerUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("MFDAppManager.Update", __state);
    }

    [HarmonyPatch(typeof(DynamicMap), "Update")]
    private static class DynamicMapUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("DynamicMap.Update", __state);
    }

    [HarmonyPatch(typeof(UIBehaviorPatcher), "FixedUpdate")]
    private static class UiBehaviorPatcherFixedUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("UIBehaviorPatcher.FixedUpdate", __state);
    }

    [HarmonyPatch(typeof(DetailRenderer), "LateUpdate")]
    private static class DetailRendererLateUpdatePatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("DetailRenderer.LateUpdate", __state);
    }

    [HarmonyPatch(typeof(GrassRenderer), "UpdatePositions")]
    private static class GrassRendererUpdatePositionsPatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("GrassRenderer.UpdatePositions", __state);
    }

    [HarmonyPatch(typeof(GrassRenderer), "Render")]
    private static class GrassRendererRenderPatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("GrassRenderer.Render", __state);
    }

    [HarmonyPatch(typeof(TreeRenderer), "UpdatePositions")]
    private static class TreeRendererUpdatePositionsPatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("TreeRenderer.UpdatePositions", __state);
    }

    [HarmonyPatch(typeof(TreeRenderer), "Render")]
    private static class TreeRendererRenderPatch
    {
        private static void Prefix(out long __state) => Begin(out __state);
        private static void Postfix(long __state) => End("TreeRenderer.Render", __state);
    }
}
