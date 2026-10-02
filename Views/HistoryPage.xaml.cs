using System.Collections.ObjectModel;
using Jicun.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jicun.Desktop.Views;

public sealed partial class HistoryPage : Page
{
    public ObservableCollection<HistoryEntry> Entries => AppServices.History.Items;

    /// <summary>是不是在「勾选 / 删除」模式。默认关着，这时候只有「选择」能点。</summary>
    private bool _picking;

    public HistoryPage()
    {
        InitializeComponent();
        Entries.CollectionChanged += (_, _) =>
        {
            UpdateEmptyHint();
            UpdateSelectAllButton();
        };
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>点一条就带着链接回解析页重跑一遍，省得再粘一次。</summary>
    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (_picking) return; // 勾选模式下点整行是「选中」，不是「重新解析」
        if (e.ClickedItem is not HistoryEntry entry) return;
        AppServices.PendingInput = entry.Url;
        AppServices.Navigate("parse");
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        AppServices.History.Clear();
        UpdateEmptyHint();
    }

    // ---- 选择 / 全选 / 删除 ----

    /// <summary>「选择」是个开关：点开进勾选模式（按钮变「完成」），再点一下就退出。</summary>
    private void OnSelectClick(object sender, RoutedEventArgs e) => SetPicking(!_picking);

    private void SetPicking(bool on)
    {
        // 注意顺序：退出勾选模式时必须在把 SelectionMode 切回 None *之前* 清空选中项。
        // SelectionMode 已经是 None 之后再碰 SelectedItems，WinUI 会抛 COMException(0x8000FFFF)，
        // 整个进程 fail-fast 直接消失。
        if (!on && HistoryList.SelectedItems.Count > 0) HistoryList.SelectedItems.Clear();

        _picking = on;
        // Multiple 模式下 ListView 自己会给每一行画勾选框，点整行也能勾
        HistoryList.SelectionMode = on ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        HistoryList.IsItemClickEnabled = !on;
        SelectButton.Content = on ? "完成" : "选择";
        UpdateSelectAllButton();
        UpdateDeleteButton();
    }

    /// <summary>全选和取消全选是同一个按钮：已经全勾上了就全清掉。</summary>
    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (Entries.Count > 0 && HistoryList.SelectedItems.Count == Entries.Count) HistoryList.SelectedItems.Clear();
        else HistoryList.SelectAll();
        UpdateDeleteButton();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDeleteButton();

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var picked = HistoryList.SelectedItems.Cast<HistoryEntry>().ToList();
        if (picked.Count == 0) return;

        HistoryList.SelectedItems.Clear();
        AppServices.History.Remove(picked);
        if (Entries.Count == 0) SetPicking(false);
        UpdateDeleteButton();
    }

    private void UpdateSelectAllButton() => SelectAllButton.IsEnabled = _picking && Entries.Count > 0;

    private void UpdateDeleteButton() => DeleteButton.IsEnabled = _picking && HistoryList.SelectedItems.Count > 0;
}
