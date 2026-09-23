using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace BmwebFlasher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Close when the main window closes, rather than when the last
            // window does. A dialog that has been dismissed but not yet
            // collected -- or a native file picker still holding a handle --
            // otherwise leaves the process running with nothing on screen,
            // which reads as "the flasher will not close".
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}