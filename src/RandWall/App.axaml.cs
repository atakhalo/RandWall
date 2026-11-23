using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using RandWall.Services;
using RandWall.ViewModels;
using RandWall.Views;

namespace RandWall;

public partial class App : Application
{
    private SettingsService? _settingsService;
    private DesktopWallpaperService? _wallpaperService;
    private PlaybackEngine? _engine;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _pauseMenuItem;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SetupApplication(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>第二个实例启动时调用：激活已有窗口。</summary>
    public static void ActivateMainWindow()
    {
        if (Current is App app)
        {
            Dispatcher.UIThread.Post(app.ShowMainWindow);
        }
    }

    private void SetupApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settingsService = new SettingsService();
        var settings = _settingsService.Load();
        var library = new WallpaperLibrary();
        _wallpaperService = new DesktopWallpaperService();
        _engine = new PlaybackEngine(_wallpaperService, library, settings);
        var thumbnails = new ThumbnailService();
        var autoStart = new AutoStartService();

        _viewModel = new MainViewModel(_settingsService, settings, _engine, thumbnails, autoStart);

        _window = new MainWindow { DataContext = _viewModel };
        _window.Closed += (_, _) => desktop.Shutdown();
        desktop.MainWindow = _window;

        CreateTrayIcon();
        UpdateTray();

        desktop.Exit += (_, _) => Teardown();

        var startHidden = settings.StartMinimized
                          || Environment.GetCommandLineArgs()
                              .Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        if (startHidden)
        {
            // 框架会在 Start 时显示主窗口，这里在消息循环开始后立即隐藏
            Dispatcher.UIThread.Post(() =>
            {
                if (_window is { IsVisible: true })
                {
                    _window.Hide();
                }
            }, DispatcherPriority.Background);
        }

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.InitializeAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("启动初始化失败", ex);
        }

        UpdateTray();
    }

    private void CreateTrayIcon()
    {
        if (_viewModel is null || _engine is null)
        {
            return;
        }

        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("显示主界面");
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(showItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var previousItem = new NativeMenuItem("上一组");
        previousItem.Click += (_, _) => _viewModel.PreviousCommand.Execute(null);
        menu.Items.Add(previousItem);

        _pauseMenuItem = new NativeMenuItem("暂停轮换");
        _pauseMenuItem.Click += (_, _) => _viewModel.TogglePauseCommand.Execute(null);
        menu.Items.Add(_pauseMenuItem);

        var nextItem = new NativeMenuItem("下一组");
        nextItem.Click += (_, _) => _viewModel.NextCommand.Execute(null);
        menu.Items.Add(nextItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var settingsItem = new NativeMenuItem("设置");
        settingsItem.Click += (_, _) =>
        {
            ShowMainWindow();
            _viewModel.SelectedSection = 2;
        };
        menu.Items.Add(settingsItem);

        var exitItem = new NativeMenuItem("退出 RandWall");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://RandWall/Assets/AppIcon.ico"))),
            ToolTipText = "RandWall 园林",
            Menu = menu,
            IsVisible = true,
        };

        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });

        _engine.Changed += (_, _) => UpdateTray();
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void UpdateTray()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = _viewModel.TrayToolTip;
        }

        if (_pauseMenuItem is not null)
        {
            _pauseMenuItem.Header = _viewModel.IsPaused ? "继续轮换" : "暂停轮换";
        }
    }

    private void ExitApplication()
    {
        if (_window is not null)
        {
            _window.CloseForExit();
            return;
        }

        Teardown();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void Teardown()
    {
        try
        {
            _viewModel?.Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Error("退出时保存配置失败", ex);
        }

        _trayIcon?.Dispose();
        _engine?.Dispose();
        _wallpaperService?.Dispose();
    }
}
