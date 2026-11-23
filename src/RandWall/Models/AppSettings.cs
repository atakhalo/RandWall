using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RandWall.Models;

/// <summary>持久化到 %AppData%\RandWall\settings.json 的配置。</summary>
public sealed class AppSettings
{
    /// <summary>壁纸池文件夹。</summary>
    public List<string> Folders { get; set; } = new();

    /// <summary>是否递归扫描子文件夹。</summary>
    public bool IncludeSubfolders { get; set; } = true;

    public PlaybackMode Mode { get; set; } = PlaybackMode.Random;

    public MultiMonitorMode MonitorMode { get; set; } = MultiMonitorMode.PerMonitor;

    public WallpaperScale Scale { get; set; } = WallpaperScale.Fill;

    /// <summary>自动切换间隔（秒）。范围 60 秒 ~ 30 天。</summary>
    public long IntervalSeconds { get; set; } = 30 * 60;

    /// <summary>切换间隔的显示单位；null 表示旧配置没有记录，按秒数推断。</summary>
    public IntervalUnit? IntervalUnit { get; set; }

    /// <summary>整点基准：间隔从整点起算，而不是从当前时间起算。</summary>
    public bool AlignToClock { get; set; }

    /// <summary>旧版字段：间隔（分钟）。只用于读取旧配置，写入时为 null 不落盘。</summary>
    [JsonInclude]
    public int? IntervalMinutes { get; set; }

    public bool AutoStart { get; set; }

    /// <summary>关闭窗口时最小化到托盘，而不是退出。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>启动后不显示主窗口。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>启动后处于暂停状态，不自动切换。</summary>
    public bool StartPaused { get; set; }

    /// <summary>顺序模式下的播放位置。</summary>
    public int Cursor { get; set; }

    /// <summary>上次应用的壁纸组（用于启动时恢复）。</summary>
    public List<string> LastGroupPaths { get; set; } = new();

    /// <summary>上次停留的页面索引。</summary>
    public int SelectedSection { get; set; }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            Folders = new List<string>(Folders),
            IncludeSubfolders = IncludeSubfolders,
            Mode = Mode,
            MonitorMode = MonitorMode,
            Scale = Scale,
            IntervalSeconds = IntervalSeconds,
            IntervalUnit = IntervalUnit,
            AlignToClock = AlignToClock,
            AutoStart = AutoStart,
            MinimizeToTray = MinimizeToTray,
            StartMinimized = StartMinimized,
            StartPaused = StartPaused,
            Cursor = Cursor,
            LastGroupPaths = new List<string>(LastGroupPaths),
            SelectedSection = SelectedSection,
        };
    }
}
