// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SysBox.Abstractions;

namespace SysBox.Presentation;

public sealed record StorageRow(StorageItem Item)
{
    public string Name => Item.Name;
    public string Path => Item.Path;
    public string Detail => $"{(Item.Completeness == DataCompleteness.Complete ? "" : "已知部分：")}{Item.LogicalBytes:N0} 逻辑字节 · {Item.FileCount:N0} 文件 · " +
        (Item.UnknownAllocatedFiles == 0 ? $"{Item.KnownAllocatedBytes:N0} 已分配字节" : $"{Item.KnownAllocatedBytes:N0} 已知分配字节（{Item.UnknownAllocatedFiles:N0} 个未知）");
    public override string ToString() => Name;
}

public sealed class StorageViewModel(IStorageBrowser browser, Func<IIndexControl> createControl) : ObservableObject
{
    private CancellationTokenSource? operation;
    private IIndexControl? control;
    private int generation;
    private bool busy;
    private string path = "", status = "请选择文件夹；不会自动扫描磁盘。", summary = "尚未计算";
    private StorageView? current;
    private StorageSort sort;
    private int offset;
    private bool more;
    private readonly List<string> history = [];
    private int historyPosition = -1;
    public ObservableCollection<StorageRow> Children { get; } = [];
    public bool IsBusy { get => busy; private set => SetProperty(ref busy, value); }
    public string Path { get => path; set => SetProperty(ref path, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Summary { get => summary; private set => SetProperty(ref summary, value); }
    public StorageView? Current { get => current; private set => SetProperty(ref current, value); }
    public StorageSort Sort { get => sort; set { if (SetProperty(ref sort, value)) _ = LoadAsync(Current?.Summary.Path ?? Path, false); } }
    public bool HasMore { get => more; private set => SetProperty(ref more, value); }
    public async Task AnalyzeAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Path)) return;
        var request = ++generation;
        using var source = new CancellationTokenSource(); operation = source; control = createControl(); IsBusy = true;
        var selectedPath = Path; var acceptProgress = true;
        try
        {
            Status = "正在分析，数字可能增大…";
            var progress = new Progress<IndexProgress>(p =>
            {
                if (request == generation && acceptProgress)
                    Status = $"{(p.State == IndexRunState.Paused ? "已暂停" : "正在分析")} · 已统计 {p.Files:N0} 文件，{p.LogicalBytes:N0} 逻辑字节（尚未完成）";
            });
            var report = await browser.AnalyzeAsync(selectedPath, control, progress, source.Token);
            acceptProgress = false;
            if (request != generation) return;
            if (report.State is not (IndexRunState.Complete or IndexRunState.Partial))
            { Status = $"本次分析未完成（{report.State}），之前的快照保留。"; return; }
            IsBusy = false; operation = null; control = null;
            await LoadAsync(selectedPath, true, resetScope: true);
        }
        catch (OperationCanceledException) { if (request == generation) Status = "分析已取消，旧快照保留。"; }
        catch (SysBoxOperationException ex) { if (request == generation) Status = ex.Error.Message; }
        catch { if (request == generation) Status = "无法分析目录，请检查路径、权限或缓存。"; }
        finally { acceptProgress = false; if (request == generation) { operation = null; control = null; IsBusy = false; } }
    }
    public async Task LoadAsync(string target, bool addHistory = true, int pageOffset = 0, bool resetScope = false)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(target)) return;
        var request = ++generation; using var source = new CancellationTokenSource(); operation = source; IsBusy = true; HasMore = false;
        try
        {
            var keepScope = !resetScope && Current is not null && (target == Current.ScopeRoot || target.StartsWith(Current.ScopeRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var view = await browser.ReadAsync(target, Sort, pageOffset, 100, source.Token,
                keepScope ? Current!.ScopeRoot : null, keepScope ? Current!.Generation : null);
            if (request != generation) return;
            Path = view.Summary.Path; Current = view; offset = pageOffset; HasMore = view.HasMore;
            Children.Clear(); foreach (var item in view.Children) Children.Add(new(item));
            Summary = $"快照：{view.Summary.Path}\n{(view.Summary.Completeness == DataCompleteness.Complete ? "" : "已知部分：")}{view.Summary.LogicalBytes:N0} 逻辑字节 · {view.Summary.FileCount:N0} 文件 · {view.Summary.FolderCount:N0} 子目录 · {(view.Summary.Completeness == DataCompleteness.Complete ? "完整覆盖" : "部分覆盖")}";
            Summary += view.Summary.UnknownAllocatedFiles == 0 ? $"\n{view.Summary.KnownAllocatedBytes:N0} 已分配字节（按目录项）" :
                $"\n{view.Summary.KnownAllocatedBytes:N0} 已知分配字节，{view.Summary.UnknownAllocatedFiles:N0} 个文件未知";
            Status = view.Notice + $" 快照时间：{view.Summary.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}。";
            if (addHistory && (historyPosition < 0 || history[historyPosition] != Path))
            {
                if (historyPosition + 1 < history.Count) history.RemoveRange(historyPosition + 1, history.Count - historyPosition - 1);
                history.Add(Path); if (history.Count > 128) history.RemoveAt(0); historyPosition = history.Count - 1;
            }
        }
        catch (OperationCanceledException) { if (request == generation) Status = "已取消读取目录。"; }
        catch (SysBoxOperationException ex) { if (request == generation) { Status = ex.Error.Message; Current = null; Children.Clear(); Summary = "尚未计算"; } }
        catch { if (request == generation) { Status = "目录大小快照不可用，请重新分析。"; Current = null; Children.Clear(); Summary = "尚未计算"; } }
        finally { if (request == generation) { operation = null; IsBusy = false; } }
    }
    public Task BackAsync() => NavigateHistoryAsync(-1);
    public Task ForwardAsync() => NavigateHistoryAsync(1);
    private async Task NavigateHistoryAsync(int delta)
    {
        var target = historyPosition + delta;
        if (IsBusy || target < 0 || target >= history.Count) return;
        var path = history[target];
        await LoadAsync(path, false);
        if (Current?.Summary.Path == path) historyPosition = target;
    }
    public Task UpAsync()
    {
        if (Current is null || Current.Summary.Path == Current.ScopeRoot) return Task.CompletedTask;
        var parent = System.IO.Directory.GetParent(Current.Summary.Path)?.FullName;
        return parent is null ? Task.CompletedTask : LoadAsync(parent);
    }
    public Task NextPageAsync() => !HasMore ? Task.CompletedTask : LoadAsync(Current?.Summary.Path ?? Path, false, offset + 100);
    public Task PreviousPageAsync() => LoadAsync(Current?.Summary.Path ?? Path, false, Math.Max(0, offset - 100));
    public void TogglePause() { if (control?.IsPaused == true) control.Resume(); else control?.Pause(); }
    public void Cancel() => operation?.Cancel();
    public void ReportError(string message) => Status = message;
    public void Deactivate() { ++generation; operation?.Cancel(); operation = null; control = null; IsBusy = false; }
}
