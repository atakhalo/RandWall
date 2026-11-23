using System;

namespace RandWall.Models;

/// <summary>一块显示器（来自 IDesktopWallpaper 的枚举结果）。</summary>
public sealed class MonitorInfo
{
    public MonitorInfo(int index, string deviceId, int left, int top, int width, int height)
    {
        Index = index;
        DeviceId = deviceId;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    /// <summary>从 0 开始的序号。</summary>
    public int Index { get; }

    /// <summary>设备路径，作为 IDesktopWallpaper 的监视器标识。</summary>
    public string DeviceId { get; }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => Left + Width;

    public int Bottom => Top + Height;

    /// <summary>显示用名称：显示器 1</summary>
    public string DisplayName => $"显示器 {Index + 1}";

    public string ResolutionText => $"{Width} × {Height}";

    public override string ToString() => $"{DisplayName} ({ResolutionText})";
}

/// <summary>一次轮换产生的一组壁纸：每个显示器对应一张图片。</summary>
public sealed class WallpaperGroup
{
    public WallpaperGroup(string[] paths, int startIndex, int cursorAfter, DateTimeOffset appliedAt)
    {
        Paths = paths;
        StartIndex = startIndex;
        CursorAfter = cursorAfter;
        AppliedAt = appliedAt;
    }

    /// <summary>按显示器顺序排列的图片路径；同一张图时长度为 1。</summary>
    public string[] Paths { get; }

    /// <summary>生成该组时的池起始索引（用于“上一组”回溯）。</summary>
    public int StartIndex { get; }

    /// <summary>生成该组后的池游标。</summary>
    public int CursorAfter { get; }

    public DateTimeOffset AppliedAt { get; }
}
