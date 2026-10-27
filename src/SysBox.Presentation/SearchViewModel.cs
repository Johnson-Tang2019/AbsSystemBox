// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SysBox.Abstractions;

namespace SysBox.Presentation;

public sealed record SearchResultRow(FileSearchHit Hit)
{
    public override string ToString() => Name;
    public string Name => Hit.Entry.Name;
    public string Path => Hit.Entry.Path;
    public string Detail => $"{(Hit.Entry.IsDirectory ? "文件夹" : System.IO.Path.GetExtension(Path))} · " +
        $"{(Hit.Entry.LogicalSizeBytes is { } bytes ? bytes.ToString("N0", CultureInfo.CurrentCulture) + " 字节" : "大小未知")} · " +
        $"{Hit.Entry.LastWriteTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "日期未知"} · {Hit.Completeness}";
}

public sealed class SearchViewModel(IIndexedSearchService service) : ObservableObject
{
    private readonly SemaphoreSlim queryGate = new(1, 1);
    private CancellationTokenSource? operation;
    private int generation;
    private bool active;
    private string query = "";
    private SearchSort sort;
    private bool includeHidden;
    public bool IncludeHidden
    {
        get => includeHidden;
        set { if (SetProperty(ref includeHidden, value)) CurrentSearch = SearchAsync(0, false); }
    }
    private string status = "输入关键词，或使用 ext:pt、type:folder 等筛选。";
    private bool busy;
    private int offset;
    private string? snapshot;
    private bool hasMore;
    private SearchResultRow? selected;
    public SearchResultRow? Selected { get => selected; set => SetProperty(ref selected, value); }
    public ObservableCollection<SearchResultRow> Results { get; } = [];
    public string Query
    {
        get => query;
        set { if (SetProperty(ref query, value)) CurrentSearch = SearchAsync(0, true); }
    }
    public SearchSort Sort
    {
        get => sort;
        set { if (SetProperty(ref sort, value)) CurrentSearch = SearchAsync(0, false); }
    }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public bool IsBusy { get => busy; private set => SetProperty(ref busy, value); }
    public bool HasMore { get => hasMore; private set => SetProperty(ref hasMore, value); }
    public Task CurrentSearch { get; private set; } = Task.CompletedTask;
    public void Activate() { active = true; if (!string.IsNullOrWhiteSpace(Query)) CurrentSearch = SearchAsync(0, false); }
    public Task FirstPageAsync() => CurrentSearch = SearchAsync(0, false);
    public Task NextPageAsync() => IsBusy || !HasMore ? CurrentSearch : CurrentSearch = SearchAsync(offset + 100, false);
    public Task PreviousPageAsync() => IsBusy ? CurrentSearch : CurrentSearch = SearchAsync(Math.Max(0, offset - 100), false);

    private async Task SearchAsync(int requestedOffset, bool debounce)
    {
        var request = ++generation; operation?.Cancel();
        if (!active) return;
        using var source = new CancellationTokenSource(); operation = source;
        var text = Query; var sorting = Sort; var token = source.Token;
        IsBusy = true; HasMore = false;
        Selected = null;
        try
        {
            if (string.IsNullOrWhiteSpace(text)) { Results.Clear(); Status = "请输入关键词；空查询不会读取全部文件。"; return; }
            if (debounce) await Task.Delay(180, token);
            await queryGate.WaitAsync(token);
            SearchPageResult page;
            try { page = await service.QueryAsync(new(text, sorting, requestedOffset, 100, requestedOffset == 0 ? null : snapshot, IncludeHidden), token); }
            finally { queryGate.Release(); }
            if (request != generation || token.IsCancellationRequested) return;
            Selected = null; Results.Clear(); foreach (var hit in page.Hits) Results.Add(new(hit));
            offset = requestedOffset; snapshot = page.Snapshot; HasMore = page.HasMore;
            Status = page.Hits.Count == 0 ? $"没有匹配结果。可以减少筛选条件，或重扫对应目录。 {page.Notice}" :
                $"第 {offset + 1}–{offset + page.Hits.Count} 条{(HasMore ? "，还有更多" : "")} · {page.ElapsedMilliseconds:F0} ms。{page.Notice}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SysBoxOperationException ex) { if (request == generation) { Results.Clear(); Status = ex.Error.Message; } }
        catch (UnauthorizedAccessException) { if (request == generation) { Results.Clear(); Status = "没有读取索引缓存的权限。"; } }
        catch { if (request == generation) { Results.Clear(); Status = "无法查询索引，请检查缓存是否可用。"; } }
        finally { if (request == generation) { operation = null; IsBusy = false; } }
    }
    public void Deactivate()
    {
        active = false; ++generation; operation?.Cancel(); operation = null; IsBusy = false; HasMore = false; Selected = null; Results.Clear();
    }
}
