using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace RpfExplorer.Desktop;

public sealed class Program : Application
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<Program>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.MainWindow = new ExplorerWindow(lifetime.Args ?? []);
        base.OnFrameworkInitializationCompleted();
    }
}
