// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SysBox.Abstractions;

namespace SysBox.Search;

public sealed record SearchTerm(string Field, string Operator, string Value);
public sealed record SearchQuery(IReadOnlyList<SearchTerm> Terms);

public static partial class SearchQueryParser
{
    public static SearchQuery Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048 || text.Any(char.IsControl))
            throw Invalid("查询应为 1–2048 个可见字符。");
        var tokens = new List<(string Text, bool Quoted)>();
        var buffer = new StringBuilder(); var inQuote = false; var quotedStart = false; var started = false;
        foreach (var ch in text)
        {
            if (ch == '"') { if (!started) quotedStart = true; inQuote = !inQuote; started = true; continue; }
            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (started) { tokens.Add((buffer.ToString(), quotedStart)); buffer.Clear(); started = false; quotedStart = false; }
            }
            else { buffer.Append(ch); started = true; }
        }
        if (inQuote) throw Invalid("双引号未闭合。");
        if (started) tokens.Add((buffer.ToString(), quotedStart));
        if (tokens.Count is < 1 or > 32) throw Invalid("查询最多包含 32 个条件。");
        var terms = new List<SearchTerm>();
        foreach (var (token, quoted) in tokens)
        {
            if (token.Length == 0) throw Invalid("查询条件不能为空。");
            if (!quoted && token.StartsWith("*.", StringComparison.Ordinal))
            { AddExtension(token[2..]); continue; }
            var colon = quoted ? -1 : token.IndexOf(':');
            if (colon < 0)
            {
                if (!quoted && (token.Contains('*') || token.Contains('?'))) throw Invalid("目前只支持 *.扩展名 通配语法。");
                terms.Add(new("name", "contains", token.ToUpperInvariant())); continue;
            }
            var field = token[..colon].ToLowerInvariant(); var value = token[(colon + 1)..];
            if (value.Length == 0) throw Invalid("筛选条件缺少值。");
            switch (field)
            {
                case "ext": AddExtension(value.TrimStart('.')); break;
                case "path":
                    if (!Path.IsPathFullyQualified(value) || value.Length > 1024) throw Invalid("path 需要完整文件夹路径，含空格时使用双引号。");
                    terms.Add(new(field, "scope", Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)).ToUpperInvariant())); break;
                case "type":
                    if (value is not ("file" or "folder")) throw Invalid("type 只支持 file 或 folder。");
                    terms.Add(new(field, "=", value)); break;
                case "size":
                    var size = SizePattern().Match(value);
                    if (!size.Success || !decimal.TryParse(size.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                        throw Invalid("size 示例：size:>1GB，必须指定 B/KB/MB/GB/TB（1024 进制）。");
                    var factor = size.Groups[3].Value.ToUpperInvariant() switch { "B" => 1m, "KB" => 1024m, "MB" => 1048576m, "GB" => 1073741824m, _ => 1099511627776m };
                    if (number > long.MaxValue / factor) throw Invalid("大小超出支持范围。");
                    terms.Add(new(field, size.Groups[1].Value, decimal.Truncate(number * factor).ToString(CultureInfo.InvariantCulture))); break;
                case "modified":
                    var date = DatePattern().Match(value);
                    if (!date.Success || !DateTime.TryParseExact(date.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        throw Invalid("modified 示例：modified:>=2026-01-01，日期按 UTC 零点解释。");
                    terms.Add(new(field, date.Groups[1].Value, DateTime.SpecifyKind(parsed, DateTimeKind.Utc).ToString("O"))); break;
                default: throw Invalid($"不支持的筛选字段：{field}。");
            }
        }
        return new(terms.AsReadOnly());
        void AddExtension(string extension)
        {
            if (extension.Length is < 1 or > 32 || extension.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '-')))
                throw Invalid("扩展名只能包含字母、数字、下划线或短横线。");
            terms.Add(new("ext", "=", extension.ToUpperInvariant()));
        }
    }
    private static SysBoxOperationException Invalid(string message) => new(new("QUERY_INVALID", message));
    [GeneratedRegex(@"^(>=|<=|>|<|=)([0-9]+(?:\.[0-9]+)?)(B|KB|MB|GB|TB)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
    [GeneratedRegex(@"^(>=|<=|>|<|=)([0-9]{4}-[0-9]{2}-[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}
