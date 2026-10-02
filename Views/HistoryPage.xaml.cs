using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Jicun.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jicun.Desktop.Views;

public sealed partial class HistoryPage : Page
{
    public ObservableCollection<HistoryEntry> Entries => AppServices.History.Items;

    /// <summary>「选择 / 全选 / 删除」那套跟下载页共用，见 <see cref="SelectableList{T}"/>。</summary>
    private readonly SelectableList<HistoryEntry> _picking;

    public HistoryPage()
    {
        InitializeComponent();

        // 比下载页多一件事：勾选模式下「点整行」是选中，所以要把「点行重新解析」关掉。
        _picking = new SelectableList<HistoryEntry>(
            HistoryList, SelectButton, SelectAllButton, DeleteButton,
            count: () => Entries.Count,
            delete: AppServices.History.Remove,
            pickingChanged: on => HistoryList.IsItemClickEnabled = !on);

        UpdateEmptyHint();

        // 页面没设 NavigationCacheMode，每次导航都是新实例，而集合是 AppServices 里的单例：
        // 只在 Loaded 订阅、Unloaded 退订 —— 否则每进一次历史页就把上一页钉在订阅表里，
        // 泄页，而且列表一变死页也跟着回调。
        Loaded += (_, _) => Entries.CollectionChanged += OnEntriesChanged;
        Unloaded += (_, _) => Entries.CollectionChanged -= OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyHint();
        _picking.Refresh();
    }

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>点一条就带着链接回解析页重跑一遍，省得再粘一次。</summary>
    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (_picking.IsPicking) return; // 勾选模式下点整行是「选中」，不是「重新解析」
        if (e.ClickedItem is not HistoryEntry entry) return;
        AppServices.PendingInput = entry.Url;
        AppServices.Navigate("parse");
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        AppServices.History.Clear();
        UpdateEmptyHint();
    }

    // ---- 选择 / 全选 / 删除：逻辑都在 SelectableList 里，这里只转发 ----

    private void OnSelectClick(object sender, RoutedEventArgs e) => _picking.Toggle();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => _picking.SelectAllOrClear();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => _picking.Refresh();

    private void OnDeleteClick(object sender, RoutedEventArgs e) => _picking.DeleteSelected();
}
