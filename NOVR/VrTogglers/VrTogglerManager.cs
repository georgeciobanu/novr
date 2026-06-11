using NOVR.Diagnostics;

namespace NOVR.VrTogglers;

public class VrTogglerManager
{
    private VrToggler _toggler;
    
    public VrTogglerManager()
    {
        RenderDiagnostics.Info("VR toggler manager setup starting.");
        SetUpToggler();
        _toggler?.SetVrEnabled(true);
        RenderDiagnostics.Info($"VR toggler manager setup complete. enabled={_toggler?.IsVrEnabled}");
    }

    private void SetUpToggler()
    {
        if (_toggler != null)
        {
            RenderDiagnostics.Info($"Disabling previous VR toggler: {_toggler.GetType().Name}");
            _toggler.SetVrEnabled(false);
        }
        _toggler = new XrPluginOpenXrToggler();
        RenderDiagnostics.Info($"Using VR toggler: {_toggler.GetType().Name}");
    }

    public void ToggleVr()
    {
        RenderDiagnostics.Info($"ToggleVr requested. current={_toggler.IsVrEnabled} next={!_toggler.IsVrEnabled}");
        _toggler.SetVrEnabled(!_toggler.IsVrEnabled);
        RenderDiagnostics.Info($"ToggleVr completed. current={_toggler.IsVrEnabled}");
    }
}
