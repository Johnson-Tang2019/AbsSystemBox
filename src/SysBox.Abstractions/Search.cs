// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public enum SearchSort { Relevance, Path, SizeDescending, ModifiedDescending }
public sealed record SearchPageRequest(string Query, SearchSort Sort = SearchSort.Relevance,
    int Offset = 0, int Limit = 100, string? Snapshot = null, bool IncludeHidden = false);
public sealed record SearchPageResult(IReadOnlyList<FileSearchHit> Hits, bool HasMore,
    string Snapshot, DataCompleteness Completeness, string Notice, double ElapsedMilliseconds);
public interface IIndexedSearchService
{
    Task<SearchPageResult> QueryAsync(SearchPageRequest request, CancellationToken cancellationToken);
}
