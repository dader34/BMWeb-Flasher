using Avalonia;
using System;
using System.Text;

namespace BmwebFlasher;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // EdiabasNet has a static field initialised with
        // Encoding.GetEncoding(1252). Windows-1252 is built into .NET
        // Framework but NOT into .NET 8, where it throws
        // NotSupportedException unless this provider is registered. Because
        // it is a static initialiser, the failure surfaces as a
        // TypeInitializationException on the FIRST `new EdiabasNet()` --
        // before the port is opened and before any job runs -- so on macOS
        // every action looked like it silently did nothing.
        //
        // This must run before any EdiabasLib type is touched.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
