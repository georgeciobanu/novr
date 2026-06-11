using System.Linq;
using NOVR.Diagnostics;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRMainMenuBehavior : UIRenderedCanvasBehavior
{
    private const string QuickLaunchButtonName = "NOVR Furball Quick Launch";
    private static readonly Color ButtonColor = new(0.12f, 0.34f, 0.20f, 0.96f);
    private static readonly Color ButtonTextColor = Color.white;

    private void Start()
    {
        transform.localScale = new Vector3(0.003f, 0.003f, 0.003f);
        transform.localPosition = new Vector3(0f, 0f, 3f);
        CreateFurballQuickLaunchButton();
    }

    private void CreateFurballQuickLaunchButton()
    {
        if (transform.Find(QuickLaunchButtonName) != null)
        {
            return;
        }

        var buttonObject = new GameObject(QuickLaunchButtonName);
        buttonObject.transform.SetParent(transform, false);
        LayerHelper.SetLayerRecursive(buttonObject.transform, LayerHelper.GetVrUiLayer());

        var rectTransform = buttonObject.AddComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.anchoredPosition = new Vector2(0f, -390f);
        rectTransform.sizeDelta = new Vector2(430f, 68f);

        var image = buttonObject.AddComponent<Image>();
        image.color = ButtonColor;

        var button = buttonObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(LaunchFurballMission);
        RenderDiagnostics.Info($"Created Furball quick-launch button on {name}. position={rectTransform.anchoredPosition} size={rectTransform.sizeDelta}");

        var textObject = new GameObject("Text");
        textObject.transform.SetParent(buttonObject.transform, false);
        LayerHelper.SetLayerRecursive(textObject.transform, LayerHelper.GetVrUiLayer());

        var textTransform = textObject.AddComponent<RectTransform>();
        textTransform.anchorMin = Vector2.zero;
        textTransform.anchorMax = Vector2.one;
        textTransform.offsetMin = Vector2.zero;
        textTransform.offsetMax = Vector2.zero;

        var text = textObject.AddComponent<Text>();
        text.text = "START FURBALL";
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.fontSize = 24;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = ButtonTextColor;
        text.raycastTarget = false;
    }

    private static void LaunchFurballMission()
    {
        try
        {
            MissionGroup.Init();
            var missionEntries = MissionSaveLoad.QuickLoadMany(MissionGroup.All.GetMissions()).ToList();
            RenderDiagnostics.Info($"Furball quick launch requested. missionEntries={missionEntries.Count}");
            foreach (var missionEntry in missionEntries)
            {
                if (!HasTag(missionEntry.mission, MissionTag.SinglePlayer)) continue;
                if (!MissionNameMatches(missionEntry.key.Name, "furball")) continue;

                RenderDiagnostics.Info($"Furball quick launch selected mission: key={missionEntry.key} name={missionEntry.key.Name}");
                StartMission(missionEntry.key);
                return;
            }

            RenderDiagnostics.Warning("Could not find a single-player Furball mission.");
        }
        catch (System.Exception exception)
        {
            RenderDiagnostics.Error($"Failed to start Furball mission: {exception}");
        }
    }

    private static void StartMission(MissionKey missionKey)
    {
        if (!missionKey.TryLoad(out var mission, out var error))
        {
            RenderDiagnostics.Warning($"Failed to load Furball mission '{missionKey}': {error}");
            return;
        }

        RenderDiagnostics.Info($"Starting Furball mission '{missionKey.Name}' map={mission.MapKey}.");
        MissionManager.SetMission(mission, checkIfSame: false);
        NetworkManagerNuclearOption.i.StartHost(new HostOptions(SocketType.Offline, GameState.SinglePlayer, mission.MapKey));
    }

    private static bool HasTag(MissionQuickLoad mission, MissionTag tag)
    {
        var tags = mission.missionSettings.Tags;
        return tags != null && tags.Any(existing => existing.Equals(tag));
    }

    private static bool MissionNameMatches(string? missionName, string searchTerm)
    {
        return !string.IsNullOrWhiteSpace(missionName) &&
               missionName.ToLowerInvariant().Contains(searchTerm);
    }
}
