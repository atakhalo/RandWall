namespace RandWall.Models;

/// <summary>播放顺序。</summary>
public enum PlaybackMode
{
    /// <summary>按文件顺序依次轮换。</summary>
    Sequential,

    /// <summary>随机打乱轮换（一个周期内不重复）。</summary>
    Random,
}

/// <summary>多显示器时的分配方式。</summary>
public enum MultiMonitorMode
{
    /// <summary>每块屏幕各自显示不同壁纸。</summary>
    PerMonitor,

    /// <summary>所有屏幕显示同一张壁纸。</summary>
    SameOnAll,
}

/// <summary>壁纸缩放方式（对应系统 DESKTOP_WALLPAPER_POSITION）。</summary>
public enum WallpaperScale
{
    Fill = 0,
    Fit = 1,
    Stretch = 2,
    Tile = 3,
    Center = 4,
    Span = 5,
}
