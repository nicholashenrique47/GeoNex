namespace GeoNex;

public partial class App : Application
{
    
    public App(MainPage mainPage)
    {
        System.IO.File.AppendAllText("debug_boot.txt", "4. App Constructor Started\n");
        InitializeComponent();
        System.IO.File.AppendAllText("debug_boot.txt", "5. InitializeComponent Finished\n");
        MainPage = mainPage;
        System.IO.File.AppendAllText("debug_boot.txt", "6. App Constructor Finished\n");
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(MainPage);
    }
}