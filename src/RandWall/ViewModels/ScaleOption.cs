using RandWall.Models;

namespace RandWall.ViewModels;

/// <summary>缩放方式下拉项。</summary>
public sealed class ScaleOption
{
    public ScaleOption(WallpaperScale value, string label, string hint)
    {
        Value = value;
        Label = label;
        Hint = hint;
    }

    public WallpaperScale Value { get; }

    public string Label { get; }

    public string Hint { get; }

    public override string ToString() => Label;
}
