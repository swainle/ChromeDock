using Avalonia;

namespace ChromeDock;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args) => AppBuilder.Configure<App>()
        .UsePlatformDetect().WithInterFont().LogToTrace()
        .StartWithClassicDesktopLifetime(args);
}
