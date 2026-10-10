namespace GeoNex;

public partial class App : Application
{
    private readonly GeoNex.Services.WindowCloseCoordinator _windowCloseCoordinator;
    
    public App(MainPage mainPage, GeoNex.Services.WindowCloseCoordinator windowCloseCoordinator)
    {
        _windowCloseCoordinator = windowCloseCoordinator;
        System.IO.File.AppendAllText("debug_boot.txt", "4. App Constructor Started\n");
        InitializeComponent();
        System.IO.File.AppendAllText("debug_boot.txt", "5. InitializeComponent Finished\n");
        MainPage = mainPage;
        System.IO.File.AppendAllText("debug_boot.txt", "6. App Constructor Finished\n");
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(MainPage);
#if WINDOWS
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return;
            IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Closing += (_, args) =>
            {
                if (_windowCloseCoordinator.ConsumeClosePermit()) return;
                args.Cancel = true;
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (_windowCloseCoordinator.RequestClose()) return;
                    _windowCloseCoordinator.PermitNextClose();
                    Quit();
                });
            };
        };
#endif
        return window;
    }
}
