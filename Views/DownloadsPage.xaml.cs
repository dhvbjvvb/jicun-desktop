using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Jicun.Desktop.Models;
using Jicun.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jicun.Desktop.Views;

public sealed partial class DownloadsPage : Page
{
    public ObservableCollection<DownloadTask> Tasks => AppServices.Downloads.Tasks;

    /// <summary>「选择 / 全选 / 删除」那套跟历史页共用，见 <see cref="SelectableList{T}"/>。</summary>
    private readonly SelectableList<DownloadTask> _picking;

    public DownloadsPage()
    {
        InitializeComponent();

        _picking = new SelectableList<DownloadTask>(
            TaskList, SelectButton, SelectAllButton, DeleteButton,
            count: () => Tasks.Count,
            delete: AppServices.Downloads.Remove);

        UpdateEmptyHint();

        // 页面没设 NavigationCacheMode，每次导航都是新实例，而集合是 AppServices 里的单例：
        // 只在 Loaded 订阅、Unloaded 退订 —— 否则每进一次下载页就把上一页钉在订阅表里，
        // 泄页，而且列表一变死页也跟着回调。
        Loaded += (_, _) => Tasks.CollectionChanged += OnTasksChanged;
        Unloaded += (_, _) => Tasks.CollectionChanged -= OnTasksChanged;
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyHint();
        _picking.Refresh();
    }

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadTask task) AppServices.Downloads.Cancel(task);
    }

    private void OnCancelAllClick(object sender, RoutedEventArgs e) => AppServices.Downloads.CancelAll();

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadTask task) return;
        if (task.SavedPath is not { Length: > 0 } path) return;
        Reveal(path);
    }

    /// <summary>三档目录各有各的地方，所以打开最近一条任务的那一档；列表空的时候给视频目录。</summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e) =>
        Reveal(Tasks.FirstOrDefault()?.Folder ?? AppServices.FolderFor(MediaKind.Video));

    // ---- 选择 / 全选 / 删除：逻辑都在 SelectableList 里，这里只转发 ----

    private void OnSelectClick(object sender, RoutedEventArgs e) => _picking.Toggle();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => _picking.SelectAllOrClear();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => _picking.Refresh();

    private void OnDeleteClick(object sender, RoutedEventArgs e) => _picking.DeleteSelected();

    private static void Reveal(string path)
    {
        try
        {
            // 文件就选中它，文件夹就直接打开
            var isFile = File.Exists(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = isFile ? "/select,\"" + path + "\"" : path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 资源管理器起不来不值得弹窗
        }
    }
}
