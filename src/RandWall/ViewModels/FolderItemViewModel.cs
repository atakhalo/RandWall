using System;
using System.IO;
using System.Windows.Input;
using RandWall.Helpers;

namespace RandWall.ViewModels;

/// <summary>壁纸库中的一个文件夹。</summary>
public sealed class FolderItemViewModel : ObservableObject
{
    private int _count;

    public FolderItemViewModel(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(HasImages));
            }
        }
    }

    public string CountText => Count > 0 ? $"{Count} 张图片" : "没有找到图片";

    public bool HasImages => Count > 0;

    /// <summary>由 MainViewModel 注入的移除命令。</summary>
    public ICommand? RemoveCommand { get; set; }

    /// <summary>由 MainViewModel 注入的「在资源管理器中打开」命令。</summary>
    public ICommand? OpenCommand { get; set; }
}
