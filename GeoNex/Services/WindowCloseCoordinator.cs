namespace GeoNex.Services;

/// <summary>Bridges the native window close gesture to the project's in-app close workflow.</summary>
public sealed class WindowCloseCoordinator
{
    private int _closePermit;

    public event Action? CloseRequested;

    public bool RequestClose()
    {
        Action? handlers = CloseRequested;
        if (handlers is null) return false;
        handlers.Invoke();
        return true;
    }

    public void PermitNextClose() => Interlocked.Exchange(ref _closePermit, 1);

    public bool ConsumeClosePermit() => Interlocked.Exchange(ref _closePermit, 0) == 1;
}
