#if MODERN
using System;
using System.Collections.Generic;
using NOVR.Diagnostics;
using UnityEngine;
using UnityEngine.XR.Management;

namespace NOVR.VrTogglers;

public abstract class XrPluginToggler: VrToggler
{
    protected XRGeneralSettings _generalSettings;
    protected XRManagerSettings _managerSetings;
    
    protected override bool SetUp()
    {
        RenderDiagnostics.Info($"{GetType().Name}.SetUp: creating XR general/manager settings.");
        _generalSettings = ScriptableObject.CreateInstance<XRGeneralSettings>();
        _managerSetings = ScriptableObject.CreateInstance<XRManagerSettings>();
        _generalSettings.Manager = _managerSetings;
        
        #pragma warning disable CS0618
        /*
         * ManagerSettings.loaders is deprecated but very useful, allows me to add the xr loader without reflection.
         * Should be fine unless the game's Unity version gets majorly updated, in which case the whole mod will be
         * broken, so I'll have to update it anyway.
         */
        var loader = CreateLoader();
        _managerSetings.loaders.Add(loader);
        RenderDiagnostics.Info($"{GetType().Name}.SetUp: added loader={loader.GetType().FullName}; loaders={DescribeLoaders()}");
        #pragma warning restore CS0618

        ConfigureBeforeInitialize();
        
        RenderDiagnostics.Info($"{GetType().Name}.SetUp: InitializeLoaderSync starting. activeLoader={_managerSetings.activeLoader?.GetType().FullName ?? "<null>"}");
        _managerSetings.InitializeLoaderSync();
        RenderDiagnostics.Info($"{GetType().Name}.SetUp: InitializeLoaderSync finished. activeLoader={_managerSetings.activeLoader?.GetType().FullName ?? "<null>"}");
        if (_managerSetings.activeLoader == null) throw new Exception("Cannot initialize OpenXR Loader. Maybe The VR headset wasn't ready?");

        ConfigureAfterInitialize();

        return true;
    }

    protected override bool EnableVr()
    {
        RenderDiagnostics.Info($"{GetType().Name}.EnableVr: StartSubsystems starting. activeLoader={_managerSetings.activeLoader?.GetType().FullName ?? "<null>"}");
        _managerSetings.StartSubsystems();
        RenderDiagnostics.Info($"{GetType().Name}.EnableVr: StartSubsystems finished. {RenderDiagnostics.DescribeXrState()}");
        RenderDiagnostics.Info(RenderDiagnostics.DescribeXrDisplays());
        return _managerSetings.activeLoader != null;
    }

    protected override bool DisableVr()
    {
        if (_managerSetings.activeLoader == null) return true;

        RenderDiagnostics.Info($"{GetType().Name}.DisableVr: stopping/deinitializing XR loader={_managerSetings.activeLoader.GetType().FullName}");
        _managerSetings.StopSubsystems();
        _managerSetings.DeinitializeLoader();
        RenderDiagnostics.Info($"{GetType().Name}.DisableVr: deinitialized. activeLoader={_managerSetings.activeLoader?.GetType().FullName ?? "<null>"}");
        return _managerSetings.activeLoader == null;
    }

    protected abstract XRLoader CreateLoader();
    protected virtual void ConfigureBeforeInitialize() {}
    protected virtual void ConfigureAfterInitialize() {}

    private string DescribeLoaders()
    {
        #pragma warning disable CS0618
        var loaderNames = new List<string>();
        foreach (var loader in _managerSetings.loaders)
        {
            loaderNames.Add(loader.GetType().FullName);
        }
        #pragma warning restore CS0618

        return string.Join(", ", loaderNames);
    }
}
#endif
