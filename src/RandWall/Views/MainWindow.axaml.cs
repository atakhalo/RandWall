using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using RandWall.ViewModels;

namespace RandWall.Views;

public partial class MainWindow : Window
{
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>托盘“退出”使用：跳过最小化到托盘逻辑直接关闭。</summary>
    public void CloseForExit()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            UpdateChromeIcon();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose && ViewModel?.MinimizeToTray == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void UpdateChromeIcon()
    {
        if (MaxIconPath is null)
        {
            return;
        }

        MaxIconPath.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M1,3 H7 V9 H1 Z M3,1 H9 V7 H3 Z"
            : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.MinimizeToTray == true)
        {
            Hide();
            return;
        }

        Close();
    }
}
