using System.Collections.ObjectModel;
using System.Diagnostics;
using Jicun.Desktop.Models;
using Jicun.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jicun.Desktop.Views;

public sealed partial class DownloadsPage : Page
{
    public ObservableCollection<DownloadTask> Tasks => AppServices.Downloads.Tasks;

    /// <summary>是不是在「勾选 / 删除」模式。默认关着，这时候只有「选择」能点。</summary>
    private bool _picking;

    public DownloadsPage()
    {
        InitializeComponent();
        Tasks.CollectionChanged += (_, _) =>
        {
            UpdateEmptyHint();
            UpdateSelectAllButton();
        };
        UpdateEmptyHint();
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

    // ---- 选择 / 全选 / 删除 ----

    /// <summary>「选择」是个开关：点开进勾选模式（按钮变「完成」），再点一下就退出。</summary>
    private void OnSelectClick(object sender, RoutedEventArgs e) => SetPicking(!_picking);

    private void SetPicking(bool on)
    {
        // 注意顺序：退出勾选模式时必须在把 SelectionMode 切回 None *之前* 清空选中项。
        // SelectionMode 已经是 None 之后再碰 SelectedItems，WinUI 会抛 COMException(0x8000FFFF)，
        // 整个进程 fail-fast 直接消失。
        if (!on && TaskList.SelectedItems.Count > 0) TaskList.SelectedItems.Clear();

        _picking = on;
        // Multiple 模式下 ListView 自己会给每一行画勾选框，点整行也能勾
        TaskList.SelectionMode = on ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        SelectButton.Content = on ? "完成" : "选择";
        UpdateSelectAllButton();
        UpdateDeleteButton();
    }

    /// <summary>全选和取消全选是同一个按钮：已经全勾上了就全清掉。</summary>
    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (Tasks.Count > 0 && TaskList.SelectedItems.Count == Tasks.Count) TaskList.SelectedItems.Clear();
        else TaskList.SelectAll();
        UpdateDeleteButton();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDeleteButton();

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var picked = TaskList.SelectedItems.Cast<DownloadTask>().ToList();
        if (picked.Count == 0) return;

        TaskList.SelectedItems.Clear();
        AppServices.Downloads.Remove(picked);
        if (Tasks.Count == 0) SetPicking(false);
        UpdateDeleteButton();
    }

    private void UpdateSelectAllButton() => SelectAllButton.IsEnabled = _picking && Tasks.Count > 0;

    private void UpdateDeleteButton() => DeleteButton.IsEnabled = _picking && TaskList.SelectedItems.Count > 0;

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
