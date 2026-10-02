using Microsoft.UI.Xaml.Controls;

namespace Jicun.Desktop.Views;

/// <summary>
/// 列表页那套「选择 / 全选 / 删除」的公共状态机。
///
/// 下载页和历史页原来是各抄一份（连注释都一模一样），行为改一处就容易漏一处；
/// 这里收一份，两个页面只留各自的数据来源、删除动作和额外差异（历史页要开关「点整行重新解析」）。
/// </summary>
internal sealed class SelectableList<T>
{
    private readonly ListView _list;
    private readonly Button _selectButton;
    private readonly Button _selectAllButton;
    private readonly Button _deleteButton;
    private readonly Func<int> _count;
    private readonly Action<IReadOnlyList<T>> _delete;
    private readonly Action<bool>? _pickingChanged;

    private bool _picking;

    /// <param name="count">当前有多少条。用回调而不是直接收集合：两个页面的集合类型不一样。</param>
    /// <param name="delete">把选中的几条交出去删掉（只删记录，磁盘上的文件不归它管）。</param>
    /// <param name="pickingChanged">进出勾选模式时的额外动作，没有就传 null。</param>
    public SelectableList(
        ListView list, Button selectButton, Button selectAllButton, Button deleteButton,
        Func<int> count, Action<IReadOnlyList<T>> delete, Action<bool>? pickingChanged = null)
    {
        _list = list;
        _selectButton = selectButton;
        _selectAllButton = selectAllButton;
        _deleteButton = deleteButton;
        _count = count;
        _delete = delete;
        _pickingChanged = pickingChanged;
    }

    /// <summary>是不是在勾选模式里。</summary>
    public bool IsPicking => _picking;

    /// <summary>选中项或条数变了：两个按钮的可用性跟着变。</summary>
    public void Refresh()
    {
        _selectAllButton.IsEnabled = _picking && _count() > 0;
        _deleteButton.IsEnabled = _picking && _list.SelectedItems.Count > 0;
    }

    /// <summary>「选择」是个开关：点开进勾选模式（按钮变「完成」），再点一下就退出。</summary>
    public void Toggle() => SetPicking(!_picking);

    /// <summary>
    /// 进出勾选模式。
    /// **退出时必须在把 SelectionMode 切回 None 之前清空选中项**：切回 None 之后再碰
    /// SelectedItems，WinUI 会抛 COMException(0x8000FFFF)，整个进程 fail-fast 直接消失。
    /// </summary>
    public void SetPicking(bool on)
    {
        if (!on && _list.SelectedItems.Count > 0) _list.SelectedItems.Clear();

        _picking = on;
        // Multiple 模式下 ListView 自己会给每一行画勾选框，点整行也能勾
        _list.SelectionMode = on ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        _selectButton.Content = on ? "完成" : "选择";
        _pickingChanged?.Invoke(on);
        Refresh();
    }

    /// <summary>全选和取消全选是同一个按钮：已经全勾上了就全清掉。</summary>
    public void SelectAllOrClear()
    {
        if (_count() > 0 && _list.SelectedItems.Count == _count()) _list.SelectedItems.Clear();
        else _list.SelectAll();
        Refresh();
    }

    public void DeleteSelected()
    {
        var picked = _list.SelectedItems.Cast<T>().ToList();
        if (picked.Count == 0) return;

        _list.SelectedItems.Clear();
        _delete(picked);
        // 删空了就顺手退出勾选模式，别留一个空列表还挂在勾选态
        if (_count() == 0) SetPicking(false);
        Refresh();
    }
}
