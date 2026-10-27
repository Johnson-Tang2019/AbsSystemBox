// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SysBox.Abstractions;

namespace SysBox.Presentation;

public sealed class IndexViewModel(IFileIndexService service, Func<IIndexControl> createControl) : ObservableObject
{
    private CancellationTokenSource? operation;
    private IIndexControl? control;
    private int generation;
    private bool busy;
    private string status = "请选择要索引的文件夹。只读取名称、大小和日期，不读取正文。";
    private string root = "";
    public ObservableCollection<IndexRoot> Roots { get; } = [];
    public ObservableCollection<FileEntry> Preview { get; } = [];
    public bool IsBusy { get => busy; private set => SetProperty(ref busy, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Root { get => root; set => SetProperty(ref root, value); }

    public async Task RefreshAsync()
    {
        var request = generation;
        try
        {
            var roots = await service.ReadRootsAsync(CancellationToken.None);
            if (request != generation) return;
            Roots.Clear(); foreach (var item in roots) Roots.Add(item);
            if (roots.Count == 0 && !IsBusy) Status = "尚未建立索引，请先选择文件夹。";
        }
        catch { if (request == generation) Status = "索引缓存不可读，请检查权限与数据库版本。"; }
    }

    public async Task ScanAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Root)) return;
        var request = ++generation;
        using var source = new CancellationTokenSource();
        operation = source; control = createControl(); IsBusy = true; Preview.Clear();
        var selectedRoot = Root;
        var acceptProgress = true;
        Status = "正在建立目录索引…";
        try
        {
            var progress = new Progress<IndexProgress>(value => { if (request == generation && acceptProgress) Status = Describe(value); });
            var result = await service.ScanAsync(selectedRoot, new(), control, progress, source.Token);
            acceptProgress = false;
            if (request != generation) return;
            Status = Describe(result);
            var entries = await service.ReadEntriesAsync(selectedRoot, 100, source.Token);
            if (request != generation) return;
            foreach (var entry in entries) Preview.Add(entry);
        }
        catch (OperationCanceledException) { if (request == generation) Status = "已取消，保留上一次可用索引。"; }
        catch (SysBoxOperationException ex) { if (request == generation) Status = ex.Error.Message; }
        catch { if (request == generation) Status = "无法建立索引，请检查目录、权限或缓存是否被其他实例使用。"; }
        finally
        {
            acceptProgress = false;
            if (request == generation) { operation = null; control = null; IsBusy = false; await RefreshAsync(); }
        }
    }

    public void TogglePause()
    {
        if (control is null) return;
        if (control.IsPaused) { control.Resume(); Status = "继续扫描…"; }
        else { control.Pause(); Status = "正在暂停…"; }
    }
    public void Cancel() => operation?.Cancel();
    public async Task ClearAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        var request = ++generation;
        using var source = new CancellationTokenSource(); operation = source;
        try
        {
            await service.ClearAsync(source.Token);
            if (request != generation) return;
            Roots.Clear(); Preview.Clear(); Status = "索引缓存已清除，源文件未改动。";
        }
        catch (OperationCanceledException) { if (request == generation) Status = "已取消缓存操作。"; }
        catch { if (request == generation) Status = "无法清除缓存，请等待扫描结束并检查权限。"; }
        finally { if (request == generation) { operation = null; IsBusy = false; } }
    }
    public void Deactivate()
    {
        ++generation; operation?.Cancel(); operation = null; control = null; IsBusy = false; Preview.Clear();
    }
    private static string Describe(IndexProgress value)
    {
        var state = value.State switch
        {
            IndexRunState.Running => "扫描中", IndexRunState.Paused => "已暂停", IndexRunState.Complete => "扫描完成",
            IndexRunState.Partial => "部分覆盖", IndexRunState.Cancelled => "已取消（保留旧索引）",
            IndexRunState.LimitReached => "达到扫描上限（保留旧索引）", _ => "扫描失败（保留旧索引）"
        };
        return $"{state} · {value.Files:N0} 文件 / {value.Directories:N0} 目录 · {value.LogicalBytes:N0} 逻辑字节\n拒绝访问 {value.AccessDenied} · 读取错误 {value.IoErrors} · 跳过链接 {value.ReparsePoints} · 排除 {value.Excluded}";
    }
}
