using NOVR.Diagnostics;

namespace NOVR.VrTogglers;

public abstract class VrToggler
{
    public bool IsVrEnabled { get; private set; }

    private bool _isSetUp;

    protected abstract bool SetUp();
    protected abstract bool EnableVr();
    protected abstract bool DisableVr();

    public void SetVrEnabled(bool nextVrEnabled)
    {
        RenderDiagnostics.Info($"{GetType().Name}.SetVrEnabled requested: next={nextVrEnabled} setupComplete={_isSetUp} current={IsVrEnabled}");
        if (!_isSetUp)
        {
            _isSetUp = SetUp();
            RenderDiagnostics.Info($"{GetType().Name}.SetUp completed: setupComplete={_isSetUp}");
        }

        if (nextVrEnabled)
        {
            IsVrEnabled = EnableVr();
            RenderDiagnostics.Info($"{GetType().Name}.EnableVr completed: current={IsVrEnabled}");
        }
        else if (DisableVr())
        {
            IsVrEnabled = false;
            RenderDiagnostics.Info($"{GetType().Name}.DisableVr completed: current={IsVrEnabled}");
        }
    }
}
