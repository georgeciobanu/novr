using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Mono.Cecil;
using Mono.Cecil.Cil;


public class Patcher
{
    private const string PluginConfigFileName = "deltawing.novr.cfg";
    private const string OpenXrConfigSection = "OpenXR";
    private const string OpenXrRenderModeConfigKey = "Render Mode";
    private const string OpenXrRenderModeEnvironmentVariable = "NOVR_OPENXR_RENDER_MODE";
    private const string LegacySinglePassEnvironmentVariable = "NOVR_EXPERIMENTAL_SINGLE_PASS_INSTANCED";
    private const string LegacySinglePassConfigKey = "Experimental Single Pass Instanced";

    private static readonly List<string> GlobalSettingsFileNames =
        new()
        {
            "globalgamemanagers", "mainData", "data.unity3d"
        };

    private static readonly List<string> PluginsToDeleteBeforePatch =
        new()
        {
            "openvr_api", "openxr_loader", "UnityOpenXR", "ucrtbased.dll", "XRSDKOpenVR"
        };

    public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll", "Unity.XR.OpenXR.dll" };
    
    public static void Patch(AssemblyDefinition assembly)
    {
        if (assembly.Name.Name == "Unity.XR.OpenXR")
        {
            PatchOpenXrSettings(assembly);
        }
    }

#if MONO
    private static void PatchOpenXrSettings(AssemblyDefinition assembly)
    {
        Console.WriteLine("[NOVR.Patcher] Inspecting Unity.XR.OpenXR settings for render-mode patching.");
        var openXrSettingsType = assembly.MainModule.GetType("UnityEngine.XR.OpenXR.OpenXRSettings");
        if (openXrSettingsType == null)
        {
            Console.WriteLine("[NOVR.Patcher] Failed to find UnityEngine.XR.OpenXR.OpenXRSettings.");
            return;
        }

        var renderModeField = openXrSettingsType.Fields.FirstOrDefault(field => field.Name == "m_renderMode");
        if (renderModeField == null)
        {
            Console.WriteLine("[NOVR.Patcher] Failed to find OpenXRSettings.m_renderMode.");
            return;
        }

        var renderModeName = GetConfiguredOpenXrRenderModeName();
        if (renderModeName == null)
        {
            Console.WriteLine("[NOVR.Patcher] Leaving OpenXRSettings render mode unpatched.");
            return;
        }

        var applySettingsMethod = openXrSettingsType.Methods.FirstOrDefault(method => method.Name == "ApplySettings");
        if (applySettingsMethod != null)
        {
            ForceRenderMode(applySettingsMethod, renderModeField, openXrSettingsType, renderModeName);
        }

        var awakeMethod = openXrSettingsType.Methods.FirstOrDefault(method => method.Name == "Awake");
        if (awakeMethod != null)
        {
            ForceRenderMode(awakeMethod, renderModeField, openXrSettingsType, renderModeName);
        }

        Console.WriteLine($"[NOVR.Patcher] Patched OpenXRSettings to force {renderModeName}.");
    }

    private static void ForceRenderMode(
        MethodDefinition method,
        FieldDefinition renderModeField,
        TypeDefinition openXrSettingsType,
        string renderModeName)
    {
        if (!method.HasBody)
        {
            Console.WriteLine($"[NOVR.Patcher] Skipping {method.FullName} because it has no body.");
            return;
        }

        var renderModeValue = openXrSettingsType.NestedTypes
            .First(type => type.Name == "RenderMode")
            .Fields
            .First(field => field.Name == renderModeName);

        var il = method.Body.GetILProcessor();
        var firstInstruction = method.Body.Instructions.First();

        il.InsertBefore(firstInstruction, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(firstInstruction, il.Create(OpCodes.Ldc_I4, renderModeValue.Constant is int value ? value : 1));
        il.InsertBefore(firstInstruction, il.Create(OpCodes.Stfld, renderModeField));
        Console.WriteLine($"[NOVR.Patcher] Injected {method.Name} render-mode assignment: {renderModeName}={renderModeValue.Constant}.");
    }

    private static string? GetConfiguredOpenXrRenderModeName()
    {
        if (TryReadConfiguredRenderMode(out var renderModeName))
        {
            Console.WriteLine($"[NOVR.Patcher] Configured OpenXR render mode: {renderModeName ?? "Default/unpatched"}.");
            return renderModeName;
        }

        Console.WriteLine("[NOVR.Patcher] No OpenXR render mode override found; defaulting to SinglePassInstanced.");
        return "SinglePassInstanced";
    }

    private static bool TryReadConfiguredRenderMode(out string? renderModeName)
    {
        renderModeName = null;

        var renderModeEnvironmentValue = Environment.GetEnvironmentVariable(OpenXrRenderModeEnvironmentVariable);
        if (TryParseRenderMode(renderModeEnvironmentValue, out renderModeName))
        {
            Console.WriteLine($"[NOVR.Patcher] Using {OpenXrRenderModeEnvironmentVariable}={renderModeEnvironmentValue}.");
            return true;
        }

        var legacyEnvironmentValue = Environment.GetEnvironmentVariable(LegacySinglePassEnvironmentVariable);
        if (TryParseBoolean(legacyEnvironmentValue, out var legacyEnvEnabled))
        {
            renderModeName = legacyEnvEnabled ? "SinglePassInstanced" : "MultiPass";
            Console.WriteLine($"[NOVR.Patcher] Using legacy {LegacySinglePassEnvironmentVariable}={legacyEnvironmentValue}; mappedRenderMode={renderModeName}.");
            return true;
        }

        var configPath = GetPluginConfigPath();
        Console.WriteLine($"[NOVR.Patcher] Looking for plugin config at: {configPath ?? "<unknown>"}.");
        if (configPath == null || !File.Exists(configPath))
        {
            Console.WriteLine("[NOVR.Patcher] Plugin config not found yet.");
            return false;
        }

        if (TryReadBepInExConfigValue(configPath, OpenXrConfigSection, OpenXrRenderModeConfigKey, out var configValue) &&
            TryParseRenderMode(configValue, out renderModeName))
        {
            Console.WriteLine($"[NOVR.Patcher] Using config [{OpenXrConfigSection}] {OpenXrRenderModeConfigKey}={configValue}.");
            return true;
        }

        if (TryReadBepInExConfigValue(configPath, OpenXrConfigSection, LegacySinglePassConfigKey, out var legacyConfigValue) &&
            TryParseBoolean(legacyConfigValue, out var legacyConfigEnabled))
        {
            renderModeName = legacyConfigEnabled ? "SinglePassInstanced" : "MultiPass";
            Console.WriteLine($"[NOVR.Patcher] Using legacy config [{OpenXrConfigSection}] {LegacySinglePassConfigKey}={legacyConfigValue}; mappedRenderMode={renderModeName}.");
            return true;
        }

        Console.WriteLine("[NOVR.Patcher] No render mode value found in plugin config.");
        return false;
    }

    private static bool TryParseRenderMode(string? value, out string? renderModeName)
    {
        renderModeName = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value!
            .Trim()
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "default":
            case "unitydefault":
                renderModeName = null;
                return true;
            case "multipass":
            case "multi":
                renderModeName = "MultiPass";
                return true;
            case "singlepassinstanced":
            case "singlepass":
            case "spi":
                renderModeName = "SinglePassInstanced";
                return true;
            default:
                Console.WriteLine($"[NOVR.Patcher] Ignoring invalid OpenXR render mode '{value}'.");
                return false;
        }
    }

    private static string? GetPluginConfigPath()
    {
        try
        {
            var gameExePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(gameExePath))
            {
                return null;
            }

            var gamePath = Path.GetDirectoryName(gameExePath);
            return gamePath == null
                ? null
                : Path.Combine(gamePath, "BepInEx", "config", PluginConfigFileName);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadBepInExConfigValue(string configPath, string section, string key, out string value)
    {
        value = string.Empty;
        var inRequestedSection = false;

        foreach (var rawLine in File.ReadAllLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
            {
                continue;
            }

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                var sectionName = line.Substring(1, line.Length - 2).Trim();
                inRequestedSection = string.Equals(sectionName, section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inRequestedSection)
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var currentKey = line.Substring(0, separatorIndex).Trim();
            if (!string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = line.Substring(separatorIndex + 1).Trim();
            return true;
        }

        return false;
    }

    private static bool TryParseBoolean(string? value, out bool result)
    {
        result = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value!.Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "on":
                result = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                return false;
        }
    }
#endif


    public static void Initialize()
    {
        
        
        
        
        
        Console.WriteLine("Patching NOVR...");
        
        
        
        

        var installerPath = Assembly.GetExecutingAssembly().Location;

        var gameExePath = Process.GetCurrentProcess().MainModule.FileName;

        var gamePath = Path.GetDirectoryName(gameExePath);
        var gameName = Path.GetFileNameWithoutExtension(gameExePath);
        var dataPath = Path.Combine(gamePath, $"{gameName}_Data/");
        var patcherPath = Path.GetDirectoryName(installerPath);
        
        CopyFilesToGame(patcherPath, dataPath);
        

        Console.WriteLine("");
        Console.WriteLine("Installed successfully, probably.");
    }

    private static string GetGlobalSettingsFilePath(string dataPath)
    {
        foreach (var globalSettingsFielName in GlobalSettingsFileNames)
        {
            var path = Path.Combine(dataPath, globalSettingsFielName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new Exception("Failed to find global settings file path");
    }

    private static string CreateGlobalSettingsBackup(string globalSettingsFilePath)
    {
        Console.WriteLine($"Backing up '{globalSettingsFilePath}'...");
        var backupPath = globalSettingsFilePath + ".bak";
        if (File.Exists(backupPath))
        {
            Console.WriteLine($"Backup already exists.");
            return backupPath;
        }

        File.Copy(globalSettingsFilePath, backupPath);
        Console.WriteLine($"Created backup in '{backupPath}'");
        return backupPath;
    }

    private static void PatchVR(string globalSettingsBackupPath, string globalSettingsFilePath, string classDataPath)
    {
        Console.WriteLine($"Using classData file from path '{classDataPath}'");

        AssetsManager am = new();
        am.LoadClassPackage(classDataPath);
        var ggm = am.LoadAssetsFile(globalSettingsBackupPath, false);
        var ggmFile = ggm.file;
        var ggmTable = ggm.table;
        am.LoadClassDatabaseFromPackage(ggmFile.typeTree.unityVersion);

        List<AssetsReplacer> replacers = new();
        
        // TODO: Read inputs from globalgamemanagers, store map somewhere, patch in-game?
        // AssetFileInfoEx inputManager = ggmTable.GetAssetInfo(2);
        // AssetTypeValueField inputManagerBase = am.GetATI(ggmFile, inputManager).GetBaseField();
        // AssetTypeValueField axes = inputManagerBase.Get("m_Axes").Get("Array");
        // Console.WriteLine($"#### Found axes: {axes.children.Length}, looping...");
        //
        // foreach (AssetTypeValueField? child in axes.children)
        // {
        //     int axis = child.Get("axis").value.AsInt();
        //     int type = child.Get("type").value.AsInt();
        //     int joyNum = child.Get("joyNum").value.AsInt();
        //     string? name = child.Get("m_Name").value.AsString();
        //     string? positiveButton = child.Get("positiveButton").value.AsString();
        //     string? negativeButton = child.Get("negativeButton").value.AsString();
        //     string? altNegativeButton = child.Get("altNegativeButton").value.AsString();
        //     string? altPositiveButton = child.Get("altPositiveButton").value.AsString();
        //     float gravity = child.Get("gravity").value.AsFloat();
        //     float dead = child.Get("dead").value.AsFloat();
        //     float sensitivity = child.Get("sensitivity").value.AsFloat();
        //     bool snap = child.Get("snap").value.AsBool();
        //     bool invert = child.Get("invert").value.AsBool();
        //
        //     if (string.IsNullOrEmpty(positiveButton)) continue;
        //
        //     if (!positiveButton.StartsWith("joystick")) continue;
        //
        //     Console.WriteLine($"name:{name} | positiveButton:{positiveButton} ");
        // }
        
        var buildSettings = ggmTable.GetAssetInfo(11);
        #pragma warning disable CS0618 // Type or member is obsolete
        var buildSettingsBase = am.GetATI(ggmFile, buildSettings).GetBaseField();
        #pragma warning restore CS0618 // Type or member is obsolete
        var enabledVRDevices = buildSettingsBase.Get("enabledVRDevices").Get("Array");
        var stringTemplate = enabledVRDevices.templateField.children[1];
        
        // AssetTypeValueField[] vrDevicesList = { StringField("None", stringTemplate), StringField("OpenVR", stringTemplate), StringField("Oculus", stringTemplate) };
        AssetTypeValueField[] vrDevicesList = { StringField("OpenVR", stringTemplate), StringField("Oculus", stringTemplate) };
        enabledVRDevices.SetChildrenList(vrDevicesList);

        replacers.Add(new AssetsReplacerFromMemory(0, buildSettings.index, (int)buildSettings.curFileType, 0xffff,
            buildSettingsBase.WriteToByteArray()));

        using AssetsFileWriter writer = new(File.OpenWrite(globalSettingsFilePath));
        ggmFile.Write(writer, 0, replacers, 0);
    }

    private static AssetTypeValueField StringField(string str, AssetTypeTemplateField template)
    {
        return new AssetTypeValueField()
        {
            children = null,
            childrenCount = 0,
            templateField = template,
            value = new AssetTypeValue(EnumValueTypes.ValueType_String, str)
        };
    }

    private static void CopyFilesToGame(string patcherPath, string dataPath)
    {
        var copyToGameFolderPath = Path.Combine(patcherPath, "CopyToGame");

        Console.WriteLine($"Copying mod files to game... These files get overwritten every time the game starts. If you want to change them manually, replace them in the mod folder instead: {copyToGameFolderPath}");

        CopyDirectory(Path.Combine(copyToGameFolderPath, "Data"), dataPath);

        var gamePluginsPath = Path.Combine(dataPath, "Plugins");
        Directory.CreateDirectory(gamePluginsPath);

        var uuvrPluginsPath = Path.Combine(copyToGameFolderPath, "Plugins");

        DeleteExistingVrPlugins(gamePluginsPath);

        // IntPtr size is 4 on x86, 8 on x64.
        var is64Bit = IntPtr.Size == 8;
        Console.WriteLine($"Detected game as being {(is64Bit ? "x64" : "x86")}");

        // Unity plugins are often in a subfolder of the Plugins folder, but they also get detected from the root folder,
        // so we don't need to worry about the subfolders.
        CopyDirectory(is64Bit ? Path.Combine(uuvrPluginsPath, "x64") : Path.Combine(uuvrPluginsPath, "x86"), gamePluginsPath);
    }

    // There might be leftover stuff from previous UUVR versions, or from other filthy VR mods,
    // and they might be in different subfolders, which could cause conflicts.
    // So we should make sure to nuke them all before replacing with our own.
    private static void DeleteExistingVrPlugins(string gamePluginsPath)
    {
        var pluginPaths = Directory
            .GetFiles(gamePluginsPath, "*.dll", SearchOption.AllDirectories)
            .Where(pluginPath => PluginsToDeleteBeforePatch
                .Select(pluginToDelete => $"{pluginToDelete.ToLower()}.dll")
                .Contains(Path.GetFileName(pluginPath).ToLower()));

        Console.WriteLine($"### Found {pluginPaths.Count()} plugins");

        foreach (var pluginPath in pluginPaths)
        {
            try
            {
                Console.WriteLine($"Deleting plugin `{pluginPath}`");
                File.Delete(pluginPath);
            } catch (Exception exception)
            {
                Console.WriteLine($"Failed to delete plugin before patching. Path: `{pluginPath}`. Exception: `{exception}`");
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        DirectoryInfo dir = new(sourceDir);

        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

        var dirs = dir.GetDirectories();

        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (var subDir in dirs)
        {
            var newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
        }

        Console.WriteLine($"Copied files from:\n> {sourceDir}\nto:\n> {destinationDir}");
    }

#if CPP
    public override void Finalizer() { }
#endif
}
