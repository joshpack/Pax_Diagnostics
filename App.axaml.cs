using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PaxDiagnostics.Views;

namespace PaxDiagnostics;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            if (SelfTest.Dir is not null)
            {
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.UIThread.Post(async () =>
                {
                    await SelfTest.RunAsync();
                    lifetime.Shutdown(SelfTest.ExitCode);
                });
            }
            else
            {
                lifetime.MainWindow = new MainWindow(Program.StartTool);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
