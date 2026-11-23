using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using RandWall.Helpers;
using RandWall.Services;

namespace RandWall.ViewModels;

/// <summary>缩略图墙中的一张图片。</summary>
public sealed class ThumbnailItemViewModel : ObservableObject
{
    private Bitmap? _image;

    public ThumbnailItemViewModel(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public string FolderName
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            return dir is null ? string.Empty : System.IO.Path.GetFileName(dir);
        }
    }

    public Bitmap? Image
    {
        get => _image;
        private set
        {
            if (SetProperty(ref _image, value))
            {
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    public bool HasImage => _image is not null;

    public async Task LoadAsync(ThumbnailService thumbnails)
    {
        if (_image is not null)
        {
            return;
        }

        var bitmap = await thumbnails.LoadAsync(Path, 320).ConfigureAwait(true);
        if (bitmap is not null)
        {
            Image = bitmap;
        }
    }
}

/// <summary>“当前壁纸”里的一块显示器卡片。</summary>
public sealed class MonitorCardViewModel : ObservableObject
{
    private Bitmap? _preview;
    private string _fileName;
    private string? _path;

    public MonitorCardViewModel(string title, string subtitle, string? path)
    {
        Title = title;
        Subtitle = subtitle;
        _path = path;
        _fileName = path is null ? "未设置" : System.IO.Path.GetFileName(path);
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string? Path
    {
        get => _path;
        private set => SetProperty(ref _path, value);
    }

    public string FileName
    {
        get => _fileName;
        private set => SetProperty(ref _fileName, value);
    }

    public Bitmap? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(HasPreview));
            }
        }
    }

    public bool HasPreview => _preview is not null;

    public bool HasPath => !string.IsNullOrEmpty(_path);

    /// <summary>由 MainViewModel 注入的「在资源管理器中打开」命令。</summary>
    public ICommand? OpenCommand { get; set; }

    /// <summary>分帧切换时逐屏更新图片。</summary>
    public void UpdatePath(string? path)
    {
        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Path = path;
        _fileName = path is null ? "未设置" : System.IO.Path.GetFileName(path);
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(HasPath));
        Preview = null;
        OnPropertyChanged(nameof(Preview));
    }

    public async Task LoadPreviewAsync(ThumbnailService thumbnails)
    {
        if (string.IsNullOrEmpty(_path) || _preview is not null)
        {
            return;
        }

        var bitmap = await thumbnails.LoadAsync(_path, 640).ConfigureAwait(true);
        if (bitmap is not null)
        {
            Preview = bitmap;
        }
    }
}
