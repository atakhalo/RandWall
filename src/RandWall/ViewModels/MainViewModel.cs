using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using RandWall.Helpers;
using RandWall.Models;
using RandWall.Services;

namespace RandWall.ViewModels;

/// <summary>主窗口 ViewModel：串起设置、壁纸池、轮换引擎与界面状态。</summary>
public sealed class MainViewModel : ObservableObject
{
    private const int ThumbnailSampleCount = 24;

    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly PlaybackEngine _engine;
    private readonly ThumbnailService _thumbnails;
    private readonly AutoStartService _autoStart;
    private readonly DispatcherTimer _saveTimer;

    private int _selectedSection;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private bool _isLoadingThumbnails;
    private bool _autoStartEnabled;
    private bool _suppressSettingsSync;
    private string _statusMessage = string.Empty;
    private CancellationTokenSource? _thumbnailCts;

    private static readonly IntervalUnitOption UnitMinutes = new(60, "分钟", IntervalUnit.Minutes);
    private static readonly IntervalUnitOption UnitHours = new(3600, "小时", IntervalUnit.Hours);
    private static readonly IntervalUnitOption UnitDays = new(86400, "天", IntervalUnit.Days);

    private IntervalUnitOption _intervalUnit;
    private decimal? _intervalValue;
    private bool _syncingInterval;
    private int _selectedPreviewTab;

    public MainViewModel(
        SettingsService settingsService,
        AppSettings settings,
        PlaybackEngine engine,
        ThumbnailService thumbnails,
        AutoStartService autoStart)
    {
        _settings = settings;
        _settingsService = settingsService;
        _engine = engine;
        _thumbnails = thumbnails;
        _autoStart = autoStart;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettingsNow();
        };

        ScaleOptions = new List<ScaleOption>
        {
            new(WallpaperScale.Fill, "填充", "铺满屏幕，多余部分裁掉"),
            new(WallpaperScale.Fit, "适应", "完整显示，可能留黑边"),
            new(WallpaperScale.Stretch, "拉伸", "拉伸铺满，可能变形"),
            new(WallpaperScale.Center, "居中", "原始大小居中"),
            new(WallpaperScale.Tile, "平铺", "按原始大小重复排列"),
            new(WallpaperScale.Span, "跨屏", "一张图横跨所有屏幕"),
        };

        _selectedScale = ScaleOptions.First(o => o.Value == _settings.Scale);

        IntervalUnits = new[] { UnitMinutes, UnitHours, UnitDays };
        _intervalUnit = _settings.IntervalUnit is { } savedUnit
            ? OptionFor(savedUnit)
            : PickBestUnit(_settings.IntervalSeconds);
        _intervalValue = ToDisplay(_settings.IntervalSeconds, _intervalUnit);
        _settings.IntervalUnit ??= _intervalUnit.Unit;

        // 配置里可能存有与「整点基准」不兼容的分钟值，启动时先校正
        EnsureAlignedMinutes();

        NextCommand = new RelayCommand(() => _ = _engine.ApplyNextAsync(), () => HasWallpapers && !IsBusy);
        PreviousCommand = new RelayCommand(() => _ = _engine.ApplyPreviousAsync(), () => _engine.History.Count > 1);
        TogglePauseCommand = new RelayCommand(() => _engine.TogglePause(), () => HasWallpapers);
        RefreshCommand = new RelayCommand(() => _ = RescanAsync());
        RemoveFolderCommand = new RelayCommand<FolderItemViewModel>(item => _ = RemoveFolderAsync(item));
        SelectSectionCommand = new RelayCommand<string>(SelectSection);
        SetModeCommand = new RelayCommand<string>(SetMode);
        SetLayoutCommand = new RelayCommand<string>(SetLayout);
        SetIntervalCommand = new RelayCommand<string>(SetInterval);
        SelectPreviewTabCommand = new RelayCommand<string>(SelectPreviewTab);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);

        _engine.Changed += OnEngineChanged;
        _engine.Tick += OnEngineTick;
        _engine.GroupApplied += OnGroupApplied;
    }

    // ---------------- 导航 ----------------

    public int SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!SetProperty(ref _selectedSection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsPlaybackPage));
            OnPropertyChanged(nameof(IsLibraryPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            _settings.SelectedSection = value;
            ScheduleSave();
        }
    }

    public bool IsPlaybackPage => _selectedSection == 0;

    public bool IsLibraryPage => _selectedSection == 1;

    public bool IsSettingsPage => _selectedSection == 2;

    public RelayCommand<string> SelectSectionCommand { get; }

    // ---------------- 播放状态 ----------------

    public bool HasWallpapers => _engine.HasContent;

    public bool IsPaused => _engine.IsPaused;

    public string PlayStateText => _engine.IsPaused
        ? "已暂停"
        : HasWallpapers ? "自动轮换中" : "等待添加壁纸";

    public string PauseButtonText => _engine.IsPaused ? "继续" : "暂停";

    public string PoolSummary => _engine.PoolCount == 0
        ? "壁纸池为空"
        : $"共 {_engine.PoolCount} 张图片";

    public string MonitorSummary
    {
        get
        {
            var count = _engine.Monitors.Count;
            var mode = _settings.MonitorMode == MultiMonitorMode.PerMonitor ? "每屏不同" : "全部相同";
            return count == 0 ? "未检测到显示器" : $"{count} 块屏幕 · {mode}";
        }
    }

    public string NextSwitchText
    {
        get
        {
            if (!HasWallpapers)
            {
                return "添加壁纸文件夹后开始";
            }

            if (_engine.IsPaused)
            {
                return "已暂停 · 不会自动切换";
            }

            return $"下次切换 · {FormatSpan(_engine.Remaining)}";
        }
    }

    public string NextSwitchCaption
    {
        get
        {
            if (!HasWallpapers)
            {
                return "添加壁纸文件夹后开始";
            }

            return _engine.IsPaused ? "已暂停 · 不会自动切换" : "距离下一次自动切换";
        }
    }

    public double CountdownProgress => HasWallpapers ? _engine.Progress : 0;

    public string CountdownText => !HasWallpapers
        ? "--:--"
        : _engine.IsPaused ? "暂停" : FormatSpan(_engine.Remaining);

    public string ErrorMessage => _engine.LastError ?? string.Empty;

    public bool HasError => !string.IsNullOrEmpty(_engine.LastError);

    public string Hint => _engine.Hint ?? string.Empty;

    public bool HasHint => !string.IsNullOrEmpty(_engine.Hint);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public RelayCommand NextCommand { get; }

    public RelayCommand PreviousCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

    public RelayCommand RefreshCommand { get; }

    // ---------------- 播放设置 ----------------

    public bool IsSequential => _settings.Mode == PlaybackMode.Sequential;

    public bool IsRandom => _settings.Mode == PlaybackMode.Random;

    public bool IsPerMonitor => _settings.MonitorMode == MultiMonitorMode.PerMonitor;

    public bool IsSameOnAll => _settings.MonitorMode == MultiMonitorMode.SameOnAll;

    public IReadOnlyList<IntervalUnitOption> IntervalUnits { get; }

    /// <summary>间隔数值（配合 <see cref="SelectedIntervalUnit"/> 使用）。</summary>
    public decimal? IntervalValue
    {
        get => _intervalValue;
        set
        {
            if (_intervalValue == value)
            {
                return;
            }

            _intervalValue = value;
            OnPropertyChanged();
            ApplyIntervalFromUi();
        }
    }

    public IntervalUnitOption SelectedIntervalUnit
    {
        get => _intervalUnit;
        set
        {
            if (value is null || ReferenceEquals(value, _intervalUnit))
            {
                return;
            }

            _intervalUnit = value;
            OnPropertyChanged();
            _settings.IntervalUnit = value.Unit;

            // 切换单位时保持总时长不变，只换算显示值
            _syncingInterval = true;
            _intervalValue = ToDisplay(_settings.IntervalSeconds, value);
            OnPropertyChanged(nameof(IntervalValue));
            _syncingInterval = false;
            OnPropertyChanged(nameof(IntervalText));

            // 整点基准下分钟只能取 60 的约数，切到分钟时把旧值吸附过去
            if (_settings.AlignToClock && value == UnitMinutes)
            {
                EnsureAlignedMinutes();
            }

            OnPropertyChanged(nameof(UseAlignedMinutePicker));
            OnPropertyChanged(nameof(SelectedAlignedMinute));
            OnPropertyChanged(nameof(AlignToClockHint));

            ScheduleSave();
            _engine.OnSettingsChanged();
        }
    }

    public string IntervalText => FormatInterval(_settings.IntervalSeconds);

    private void ApplyIntervalFromUi()
    {
        if (_syncingInterval)
        {
            return;
        }

        var value = (double)(_intervalValue ?? 0);
        if (value <= 0)
        {
            _syncingInterval = true;
            _intervalValue = ToDisplay(_settings.IntervalSeconds, _intervalUnit);
            OnPropertyChanged(nameof(IntervalValue));
            _syncingInterval = false;
            return;
        }

        var seconds = (long)Math.Round(value * _intervalUnit.Seconds);
        seconds = Math.Clamp(seconds, SettingsService.MinIntervalSeconds, SettingsService.MaxIntervalSeconds);
        if (seconds == _settings.IntervalSeconds)
        {
            return;
        }

        _settings.IntervalSeconds = seconds;
        OnPropertyChanged(nameof(IntervalText));
        _engine.OnSettingsChanged();
        ScheduleSave();
    }

    /// <summary>整点基准：开启后切换时间对齐到整点，而不是从当前时刻起算。</summary>
    public bool AlignToClock
    {
        get => _settings.AlignToClock;
        set
        {
            if (value == _settings.AlignToClock)
            {
                return;
            }

            _settings.AlignToClock = value;

            if (value && _intervalUnit == UnitMinutes)
            {
                EnsureAlignedMinutes();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(UseAlignedMinutePicker));
            OnPropertyChanged(nameof(SelectedAlignedMinute));
            OnPropertyChanged(nameof(AlignToClockHint));
            ScheduleSave();
            _engine.OnSettingsChanged();
        }
    }

    public string AlignToClockHint
    {
        get
        {
            if (!_settings.AlignToClock)
            {
                return "关闭时从当前时刻开始计时";
            }

            if (_intervalUnit == UnitHours)
            {
                return "以整点起算：例如间隔 2 小时，会在 14:00、16:00 切换";
            }

            if (_intervalUnit == UnitDays)
            {
                return "以 0 点起算：例如间隔 1 天，会在每天 00:00 切换";
            }

            return "以整点起算：例如 24 分、间隔 10 分钟，下次在 30 分";
        }
    }

    /// <summary>整点基准 + 分钟单位时，输入框换成「能整除 60」的下拉。</summary>
    public bool UseAlignedMinutePicker => _settings.AlignToClock && _intervalUnit == UnitMinutes;

    public IReadOnlyList<AlignedMinuteOption> AlignedMinuteOptions { get; } = AlignedMinuteOption.CreateAll();

    public AlignedMinuteOption? SelectedAlignedMinute
    {
        get
        {
            var minutes = Math.Max(1, _settings.IntervalSeconds / 60);
            foreach (var option in AlignedMinuteOptions)
            {
                if (option.Minutes == minutes)
                {
                    return option;
                }
            }

            return null;
        }
        set
        {
            if (value is null || value.Minutes <= 0)
            {
                return;
            }

            var seconds = value.Minutes * 60;
            if (seconds == _settings.IntervalSeconds)
            {
                return;
            }

            ApplyIntervalSeconds(seconds, keepUnit: true);
        }
    }

    /// <summary>把当前分钟间隔吸附到最近的「60 的约数」。</summary>
    private void EnsureAlignedMinutes()
    {
        if (!_settings.AlignToClock || _intervalUnit != UnitMinutes)
        {
            return;
        }

        var seconds = _settings.IntervalSeconds;
        var minutes = (long)Math.Round(seconds / 60.0);
        var target = AlignedMinuteOptions[0];

        foreach (var option in AlignedMinuteOptions)
        {
            var distance = Math.Abs(option.Minutes - minutes);
            var bestDistance = Math.Abs(target.Minutes - minutes);
            if (distance < bestDistance
                || (distance == bestDistance && option.Minutes < target.Minutes))
            {
                target = option;
            }
        }

        if (target.Minutes * 60 == seconds)
        {
            return;
        }

        _settings.IntervalSeconds = target.Minutes * 60;

        _syncingInterval = true;
        _intervalValue = ToDisplay(_settings.IntervalSeconds, _intervalUnit);
        OnPropertyChanged(nameof(IntervalValue));
        _syncingInterval = false;

        OnPropertyChanged(nameof(IntervalText));
        OnPropertyChanged(nameof(SelectedAlignedMinute));
        StatusMessage = $"整点基准下分钟间隔需能整除 60，已从 {minutes} 分钟调整为 {target.Minutes} 分钟";
        ScheduleSave();
        _engine.OnSettingsChanged();
    }

    private void SetInterval(string? value)
    {
        if (!long.TryParse(value, out var seconds))
        {
            return;
        }

        ApplyIntervalSeconds(seconds);
    }

    private void ApplyIntervalSeconds(long seconds, bool keepUnit = false)
    {
        seconds = Math.Clamp(seconds, SettingsService.MinIntervalSeconds, SettingsService.MaxIntervalSeconds);
        _settings.IntervalSeconds = seconds;

        var unit = keepUnit ? _intervalUnit : PickBestUnit(seconds);
        _syncingInterval = true;
        _intervalUnit = unit;
        _intervalValue = ToDisplay(seconds, unit);
        OnPropertyChanged(nameof(IntervalValue));
        OnPropertyChanged(nameof(SelectedIntervalUnit));
        _syncingInterval = false;
        _settings.IntervalUnit = unit.Unit;

        OnPropertyChanged(nameof(IntervalText));
        OnPropertyChanged(nameof(UseAlignedMinutePicker));
        OnPropertyChanged(nameof(SelectedAlignedMinute));
        OnPropertyChanged(nameof(AlignToClockHint));
        ScheduleSave();
        _engine.OnSettingsChanged();
    }

    private static IntervalUnitOption PickBestUnit(long seconds)
    {
        return ClockAlignedSchedule.InferUnit(seconds) switch
        {
            IntervalUnit.Days => UnitDays,
            IntervalUnit.Hours => UnitHours,
            _ => UnitMinutes,
        };
    }

    private static IntervalUnitOption OptionFor(IntervalUnit unit)
    {
        return unit switch
        {
            IntervalUnit.Days => UnitDays,
            IntervalUnit.Hours => UnitHours,
            _ => UnitMinutes,
        };
    }

    private static decimal? ToDisplay(long seconds, IntervalUnitOption unit)
        => Math.Round((decimal)seconds / unit.Seconds, 2);

    private static string FormatInterval(long seconds)
    {
        if (seconds < 3600)
        {
            return $"{seconds / 60} 分钟";
        }

        if (seconds < 86400)
        {
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;
            return minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes} 分";
        }

        var days = seconds / 86400;
        var rest = (seconds % 86400) / 3600;
        return rest == 0 ? $"{days} 天" : $"{days} 天 {rest} 小时";
    }

    public IReadOnlyList<ScaleOption> ScaleOptions { get; }

    private ScaleOption _selectedScale;

    public ScaleOption SelectedScale
    {
        get => _selectedScale;
        set
        {
            if (value is null || ReferenceEquals(_selectedScale, value))
            {
                return;
            }

            _selectedScale = value;
            _settings.Scale = value.Value;
            OnPropertyChanged();

            if (value.Value == WallpaperScale.Span && _settings.MonitorMode != MultiMonitorMode.SameOnAll)
            {
                _settings.MonitorMode = MultiMonitorMode.SameOnAll;
                OnPropertyChanged(nameof(IsPerMonitor));
                OnPropertyChanged(nameof(IsSameOnAll));
                OnPropertyChanged(nameof(MonitorSummary));
            }

            ScheduleSave();
            _ = _engine.ReapplyCurrentAsync();
        }
    }

    public RelayCommand<string> SetModeCommand { get; }

    public RelayCommand<string> SetLayoutCommand { get; }

    public RelayCommand<string> SetIntervalCommand { get; }

    // ---------------- 壁纸库 ----------------

    public ObservableCollection<FolderItemViewModel> Folders { get; } = new();

    public ObservableCollection<ThumbnailItemViewModel> Thumbnails { get; } = new();

    public ObservableCollection<MonitorCardViewModel> MonitorCards { get; } = new();

    /// <summary>预览页签：0 = 当前，1 = 上一组，2 = 下一组。</summary>
    public int SelectedPreviewTab
    {
        get => _selectedPreviewTab;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (_selectedPreviewTab == clamped)
            {
                return;
            }

            _selectedPreviewTab = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCurrentTab));
            OnPropertyChanged(nameof(IsPreviousTab));
            OnPropertyChanged(nameof(IsNextTab));
            OnPropertyChanged(nameof(PreviewCaption));
            UpdateMonitorCards();
        }
    }

    public bool IsCurrentTab => _selectedPreviewTab == 0;

    public bool IsPreviousTab => _selectedPreviewTab == 1;

    public bool IsNextTab => _selectedPreviewTab == 2;

    public string PreviewCaption => _selectedPreviewTab switch
    {
        1 => "上一组",
        2 => "下一组",
        _ => "当前壁纸",
    };

    public bool HasPreviewEmpty => MonitorCards.Count == 0 || MonitorCards.All(c => !c.HasPath);

    public string PreviewEmptyText => _selectedPreviewTab switch
    {
        1 => "还没有上一组，切换过一次后可以在这里回看",
        2 => "还没有可预览的下一组",
        _ => "等待壁纸",
    };

    public RelayCommand<string> SelectPreviewTabCommand { get; }

    public RelayCommand<FolderItemViewModel> RemoveFolderCommand { get; }

    public string FolderSummary => Folders.Count == 0
        ? "还没有添加文件夹"
        : $"{Folders.Count} 个文件夹 · {_engine.PoolCount} 张图片";

    public bool HasFolders => Folders.Count > 0;

    public bool IncludeSubfolders
    {
        get => _settings.IncludeSubfolders;
        set
        {
            if (value == _settings.IncludeSubfolders)
            {
                return;
            }

            _settings.IncludeSubfolders = value;
            OnPropertyChanged();
            ScheduleSave();
            _ = RescanAsync();
        }
    }

    public bool IsLoadingThumbnails
    {
        get => _isLoadingThumbnails;
        private set => SetProperty(ref _isLoadingThumbnails, value);
    }

    public bool HasThumbnails => Thumbnails.Count > 0;

    /// <summary>缩略图墙的说明：从壁纸池里均匀抽取了多少张。</summary>
    public string ThumbnailSummary => Thumbnails.Count == 0
        ? "暂无图片"
        : $"均匀抽取 {Thumbnails.Count} / {_engine.PoolCount} 张";

    // ---------------- 应用设置 ----------------

    public bool AutoStart
    {
        get => _autoStartEnabled;
        set
        {
            if (value == _autoStartEnabled)
            {
                return;
            }

            if (!_autoStart.SetEnabled(value))
            {
                StatusMessage = "无法修改开机自启：注册表写入失败";
                OnPropertyChanged();
                return;
            }

            _autoStartEnabled = value;
            _settings.AutoStart = value;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (value == _settings.MinimizeToTray)
            {
                return;
            }

            _settings.MinimizeToTray = value;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set
        {
            if (value == _settings.StartMinimized)
            {
                return;
            }

            _settings.StartMinimized = value;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public bool StartPaused
    {
        get => _settings.StartPaused;
        set
        {
            if (value == _settings.StartPaused)
            {
                return;
            }

            _settings.StartPaused = value;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public string DataFolderPath => _settingsService.DataDirectory;

    public string VersionText
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null ? "v1.0.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public RelayCommand OpenDataFolderCommand { get; }

    // ---------------- 生命周期 ----------------

    public async Task InitializeAsync()
    {
        _suppressSettingsSync = true;
        _autoStartEnabled = _autoStart.IsEnabled;
        if (_autoStartEnabled != _settings.AutoStart)
        {
            _settings.AutoStart = _autoStartEnabled;
        }

        _suppressSettingsSync = false;
        OnPropertyChanged(nameof(AutoStart));

        SelectedSection = Math.Clamp(_settings.SelectedSection, 0, 2);

        IsBusy = true;
        BusyText = "正在扫描壁纸…";
        try
        {
            await _engine.StartAsync(restoreLastGroup: true).ConfigureAwait(true);
            RefreshAll(syncFolders: true);
            await LoadThumbnailsAsync().ConfigureAwait(true);
            UpdateMonitorCards();
            await UpdateMonitorPreviewsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化失败", ex);
            StatusMessage = $"初始化失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SaveSettingsNow()
    {
        var group = _engine.CurrentGroup;
        if (group is not null)
        {
            _settings.LastGroupPaths = group.Paths.ToList();
        }

        _settings.Cursor = _engine.Cursor;
        _settingsService.Save(_settings);
    }

    public void Shutdown()
    {
        _saveTimer.Stop();
        SaveSettingsNow();
        _thumbnailCts?.Cancel();
        _engine.Changed -= OnEngineChanged;
        _engine.Tick -= OnEngineTick;
        _engine.GroupApplied -= OnGroupApplied;
    }

    /// <summary>最小化到托盘时用于托盘提示文本。</summary>
    public string TrayToolTip
    {
        get
        {
            if (!HasWallpapers)
            {
                return "RandWall 园林 · 未添加壁纸";
            }

            return _engine.IsPaused
                ? "RandWall 园林 · 已暂停"
                : $"RandWall 园林 · 每 {IntervalText}轮换";
        }
    }

    // ---------------- 命令实现 ----------------

    public async Task AddFoldersAsync(IEnumerable<string> paths)
    {
        var changed = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = WallpaperLibrary.NormalizeFolder(path);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"无效文件夹 {path}：{ex.Message}");
                continue;
            }

            if (!Directory.Exists(normalized))
            {
                continue;
            }

            if (!_settings.Folders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                _settings.Folders.Add(normalized);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        StatusMessage = string.Empty;
        ScheduleSave();
        await RescanAsync().ConfigureAwait(true);
    }

    private async Task RemoveFolderAsync(FolderItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _settings.Folders.RemoveAll(f => string.Equals(f, item.Path, StringComparison.OrdinalIgnoreCase));
        ScheduleSave();
        await RescanAsync().ConfigureAwait(true);
    }

    public async Task RescanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = "正在扫描壁纸…";
        try
        {
            await _engine.RefreshLibraryAsync(force: true).ConfigureAwait(true);
            _engine.ResetPlaybackState();
            _thumbnails.Clear();
            RefreshAll(syncFolders: true);
            await LoadThumbnailsAsync().ConfigureAwait(true);

            if (_engine.CurrentGroup is null && _engine.HasContent && !_engine.IsPaused)
            {
                await _engine.ApplyNextAsync().ConfigureAwait(true);
            }

            UpdateMonitorCards();
            await UpdateMonitorPreviewsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("扫描壁纸失败", ex);
            StatusMessage = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetMode(string? value)
    {
        var mode = string.Equals(value, "sequential", StringComparison.OrdinalIgnoreCase)
            ? PlaybackMode.Sequential
            : PlaybackMode.Random;

        if (_settings.Mode == mode)
        {
            return;
        }

        _settings.Mode = mode;
        OnPropertyChanged(nameof(IsSequential));
        OnPropertyChanged(nameof(IsRandom));
        ScheduleSave();
        _engine.OnSettingsChanged();
    }

    private void SetLayout(string? value)
    {
        var mode = string.Equals(value, "same", StringComparison.OrdinalIgnoreCase)
            ? MultiMonitorMode.SameOnAll
            : MultiMonitorMode.PerMonitor;

        if (_settings.MonitorMode == mode)
        {
            return;
        }

        _settings.MonitorMode = mode;

        if (mode == MultiMonitorMode.PerMonitor && _settings.Scale == WallpaperScale.Span)
        {
            _settings.Scale = WallpaperScale.Fill;
            _selectedScale = ScaleOptions.First(o => o.Value == WallpaperScale.Fill);
            OnPropertyChanged(nameof(SelectedScale));
        }

        OnPropertyChanged(nameof(IsPerMonitor));
        OnPropertyChanged(nameof(IsSameOnAll));
        OnPropertyChanged(nameof(MonitorSummary));
        ScheduleSave();
        _engine.OnSettingsChanged();
        _ = _engine.ReapplyCurrentAsync();
    }

    private void SelectPreviewTab(string? value)
    {
        if (int.TryParse(value, out var index))
        {
            SelectedPreviewTab = index;
        }
    }

    private void SelectSection(string? value)
    {
        if (int.TryParse(value, out var index))
        {
            SelectedSection = Math.Clamp(index, 0, 2);
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_settingsService.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _settingsService.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法打开配置目录：{ex.Message}";
        }
    }

    /// <summary>在资源管理器中打开并选中图片。</summary>
    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusMessage = "图片文件不存在，可能已被移动或删除";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法打开资源管理器：{ex.Message}";
        }
    }

    /// <summary>在资源管理器中打开文件夹。</summary>
    private void OpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusMessage = "文件夹不存在或无法访问";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法打开文件夹：{ex.Message}";
        }
    }

    private void ScheduleSave()
    {
        if (_suppressSettingsSync)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ---------------- 引擎事件 ----------------

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        RefreshAll(syncFolders: false);
        UpdateMonitorCards();
    }

    private void OnEngineTick(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(NextSwitchText));
        OnPropertyChanged(nameof(CountdownProgress));
        OnPropertyChanged(nameof(CountdownText));
        OnPropertyChanged(nameof(TrayToolTip));
    }

    private void OnGroupApplied(object? sender, WallpaperGroup group)
    {
        UpdateMonitorCards();
    }

    private void RefreshAll(bool syncFolders)
    {
        OnPropertyChanged(nameof(HasWallpapers));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PlayStateText));
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(PoolSummary));
        OnPropertyChanged(nameof(MonitorSummary));
        OnPropertyChanged(nameof(NextSwitchText));
        OnPropertyChanged(nameof(NextSwitchCaption));
        OnPropertyChanged(nameof(CountdownProgress));
        OnPropertyChanged(nameof(CountdownText));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(HasHint));
        OnPropertyChanged(nameof(TrayToolTip));

        if (syncFolders)
        {
            SyncFolders();
        }

        OnPropertyChanged(nameof(FolderSummary));
        OnPropertyChanged(nameof(HasFolders));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        NextCommand.RaiseCanExecuteChanged();
        PreviousCommand.RaiseCanExecuteChanged();
        TogglePauseCommand.RaiseCanExecuteChanged();
    }

    private void SyncFolders()
    {
        var counts = _engine.Library.LastScan.FolderCounts;

        for (var i = Folders.Count - 1; i >= 0; i--)
        {
            if (!_settings.Folders.Contains(Folders[i].Path, StringComparer.OrdinalIgnoreCase))
            {
                Folders.RemoveAt(i);
            }
        }

        foreach (var folder in _settings.Folders)
        {
            var item = Folders.FirstOrDefault(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                var newItem = new FolderItemViewModel(folder);
                newItem.RemoveCommand = new RelayCommand(() => _ = RemoveFolderAsync(newItem));
                newItem.OpenCommand = new RelayCommand(() => OpenFolder(newItem.Path));
                item = newItem;
                Folders.Add(item);
            }

            item.Count = counts.TryGetValue(WallpaperLibrary.NormalizeFolder(folder), out var count) ? count : 0;
        }

        OnPropertyChanged(nameof(HasFolders));
    }

    private void UpdateMonitorCards()
    {
        var monitors = _engine.Monitors;
        var raw = GetPreviewPaths();
        var paths = ExpandPaths(raw, Math.Max(1, monitors.Count));

        if (monitors.Count == 0)
        {
            var fallback = paths.Length > 0 ? paths[0] : null;
            if (MonitorCards.Count != 1 || MonitorCards[0].Title != "桌面")
            {
                MonitorCards.Clear();
                MonitorCards.Add(CreateCard("桌面", "默认显示器", fallback));
            }
            else
            {
                MonitorCards[0].UpdatePath(fallback);
            }

            NotifyPreviewState();
            _ = UpdateMonitorPreviewsAsync();
            return;
        }

        if (MonitorCards.Count != monitors.Count)
        {
            MonitorCards.Clear();
            for (var i = 0; i < monitors.Count; i++)
            {
                MonitorCards.Add(CreateCard(
                    monitors[i].DisplayName,
                    monitors[i].ResolutionText,
                    PathAt(paths, i)));
            }
        }
        else
        {
            for (var i = 0; i < monitors.Count; i++)
            {
                MonitorCards[i].UpdatePath(PathAt(paths, i));
            }
        }

        NotifyPreviewState();
        _ = UpdateMonitorPreviewsAsync();
    }

    private void NotifyPreviewState()
    {
        OnPropertyChanged(nameof(HasMonitors));
        OnPropertyChanged(nameof(HasPreviewEmpty));
        OnPropertyChanged(nameof(PreviewEmptyText));
    }

    private MonitorCardViewModel CreateCard(string title, string subtitle, string? path)
    {
        var card = new MonitorCardViewModel(title, subtitle, path);
        card.OpenCommand = new RelayCommand(() => OpenInExplorer(card.Path));
        return card;
    }

    private IReadOnlyList<string> GetPreviewPaths()
    {
        return _selectedPreviewTab switch
        {
            1 => _engine.PreviousGroup?.Paths ?? Array.Empty<string>(),
            2 => _engine.PeekNextGroup()?.Paths ?? Array.Empty<string>(),
            _ => _engine.DisplayedPaths,
        };
    }

    private string[] ExpandPaths(IReadOnlyList<string> raw, int count)
    {
        if (raw.Count == 0)
        {
            return Array.Empty<string>();
        }

        var same = _settings.MonitorMode == MultiMonitorMode.SameOnAll || raw.Count == 1;
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = same ? raw[0] : raw[i % raw.Count];
        }

        return result;
    }

    private static string? PathAt(IReadOnlyList<string> paths, int index)
        => index >= 0 && index < paths.Count ? paths[index] : null;

    public bool HasMonitors => MonitorCards.Count > 0;

    private async Task UpdateMonitorPreviewsAsync()
    {
        try
        {
            var cards = MonitorCards.ToArray();
            using var semaphore = new SemaphoreSlim(3);
            var tasks = cards.Select(async card =>
            {
                await semaphore.WaitAsync().ConfigureAwait(true);
                try
                {
                    await card.LoadPreviewAsync(_thumbnails).ConfigureAwait(true);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载当前壁纸预览失败：{ex.Message}");
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var token = _thumbnailCts.Token;

        var files = _engine.Library.Files;
        var sample = Sample(files, ThumbnailSampleCount);

        var sameAsBefore = Thumbnails.Count == sample.Count
                           && Thumbnails.Select(t => t.Path).SequenceEqual(sample, StringComparer.OrdinalIgnoreCase);

        if (sameAsBefore)
        {
            return;
        }

        Thumbnails.Clear();
        foreach (var path in sample)
        {
            Thumbnails.Add(new ThumbnailItemViewModel(path));
        }

        OnPropertyChanged(nameof(HasThumbnails));
        OnPropertyChanged(nameof(ThumbnailSummary));

        if (Thumbnails.Count == 0)
        {
            return;
        }

        IsLoadingThumbnails = true;
        try
        {
            using var semaphore = new SemaphoreSlim(3);
            var tasks = Thumbnails.Select(async item =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await semaphore.WaitAsync(token).ConfigureAwait(true);
                try
                {
                    if (!token.IsCancellationRequested)
                    {
                        await item.LoadAsync(_thumbnails).ConfigureAwait(true);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 被新的扫描取消
        }
        finally
        {
            IsLoadingThumbnails = false;
        }
    }

    private static List<string> Sample(IReadOnlyList<string> files, int count)
    {
        if (files.Count == 0)
        {
            return new List<string>();
        }

        if (files.Count <= count)
        {
            return files.ToList();
        }

        var step = (double)files.Count / count;
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(files[(int)(i * step)]);
        }

        return result;
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays} 天 {span.Hours:00}:{span.Minutes:00}";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
        }

        return $"{span.Minutes:00}:{span.Seconds:00}";
    }
}
