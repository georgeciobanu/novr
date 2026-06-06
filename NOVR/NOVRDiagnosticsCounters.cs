namespace NOVR;

public static class NOVRDiagnosticsCounters
{
    public static int ComponentPatchQueued;
    public static int NamePatchQueued;
    public static int UiComponentsAdded;
    public static int UiSetActiveBouncesSkipped;
    public static int UiSetActiveBouncesPerformed;
    public static int UiSetActiveReactivations;
    public static int UiCameraStackAdds;
    public static int UiCameraStackMissing;
    public static int UiCameraStackDuplicateEntriesRemoved;
    public static int LastUiCameraStackSize;

    public static string ConsumeSummary()
    {
        var summary =
            $"uiComponentQueued={ComponentPatchQueued}, " +
            $"uiNameQueued={NamePatchQueued}, " +
            $"uiComponentsAdded={UiComponentsAdded}, " +
            $"uiSetActiveSkipped={UiSetActiveBouncesSkipped}, " +
            $"uiSetActivePerformed={UiSetActiveBouncesPerformed}, " +
            $"uiSetActiveReactivated={UiSetActiveReactivations}, " +
            $"uiCameraStackAdds={UiCameraStackAdds}, " +
            $"uiCameraStackMissing={UiCameraStackMissing}, " +
            $"uiCameraStackDuplicateEntriesRemoved={UiCameraStackDuplicateEntriesRemoved}, " +
            $"lastUiCameraStackSize={LastUiCameraStackSize}";

        ComponentPatchQueued = 0;
        NamePatchQueued = 0;
        UiComponentsAdded = 0;
        UiSetActiveBouncesSkipped = 0;
        UiSetActiveBouncesPerformed = 0;
        UiSetActiveReactivations = 0;
        UiCameraStackAdds = 0;
        UiCameraStackMissing = 0;
        UiCameraStackDuplicateEntriesRemoved = 0;

        return summary;
    }
}
