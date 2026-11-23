using System;
using Avalonia;
using Avalonia.Threading;
using RandWall.Services;

namespace RandWall;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new SingleInstance();
        if (!instance.TryAcquire())
        {
            instance.SignalExistingInstance();
            return;
        }

        instance.ListenForActivation(() =>
        {
            try
            {
                Dispatcher.UIThread.Post(App.ActivateMainWindow);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"激活主窗口失败：{ex.Message}");
            }
        });

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
