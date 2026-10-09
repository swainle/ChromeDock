using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChromeDock.Repositories;

namespace ChromeDock;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        LogRepository.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogRepository.For<App>().Fatal(e.ExceptionObject as Exception, "未处理的进程异常");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += (_, e) =>
            {
                LogRepository.For<App>().Info("程序退出，退出码 {0}", e.ApplicationExitCode);
                LogRepository.Shutdown();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
