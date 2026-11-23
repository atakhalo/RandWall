using System.Collections.Generic;
using RandWall.Models;

namespace RandWall.ViewModels;

/// <summary>整点基准模式下可选的分钟间隔（必须能整除 60）。</summary>
public sealed class AlignedMinuteOption
{
    public AlignedMinuteOption(long minutes)
    {
        Minutes = minutes;
        Label = $"{minutes} 分钟";
    }

    public long Minutes { get; }

    public string Label { get; }

    public override string ToString() => Label;

    /// <summary>1、2、3、4、5、6、10、12、15、20、30、60 分钟。</summary>
    public static IReadOnlyList<AlignedMinuteOption> CreateAll()
    {
        var list = new List<AlignedMinuteOption>();
        for (var minutes = 1L; minutes <= 60; minutes++)
        {
            if (ClockAlignedSchedule.IsWholeHourDivisor(minutes))
            {
                list.Add(new AlignedMinuteOption(minutes));
            }
        }

        return list;
    }
}
