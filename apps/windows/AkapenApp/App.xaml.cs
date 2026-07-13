// Akapen WinUI 3 shell App entry (spec §7.4-4 / §9 M2).
//
// Deliberately minimal: OnLaunched creates a single MainWindow and shows it.
// Every M2 acceptance-bar behavior (open image, wire SwapChainPanel to the
// wgpu surface via ISwapChainPanelNative, feed WM_POINTER-equivalent samples
// through akapen_pointer, save the 3-file _review/ set) lives on the window
// itself so it can share Loaded/SizeChanged/Closed lifecycle with the panel.

using Microsoft.UI.Xaml;

namespace AkapenApp;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // Registers the default XAML controls resources declared in App.xaml.
        try
        {
            this.InitializeComponent();
        }
        catch (Exception ex)
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Akapen",
                "startup-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, "App.InitializeComponent failed\n" + ex);
            throw;
        }
    }

    /// <summary>
    /// Invoked by the WinUI runtime when the app is launched. Bringing up the
    /// engine, attaching the GPU surface, and wiring the toolbar accelerators
    /// all belong to <see cref="MainWindow"/>; App.xaml.cs's only job is to
    /// create it and keep it alive (WinUI 3 windows are not activated in
    /// Application.Resources like UWP — the code-behind holds the reference).
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
