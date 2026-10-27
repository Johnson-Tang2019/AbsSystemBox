// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Presentation;

public sealed record TreemapWeight(string Name, string? Path, long Bytes);
public sealed record TreemapTile(TreemapWeight Item, double X, double Y, double Width, double Height);
public static class TreemapLayout
{
    public static IReadOnlyList<TreemapTile> Create(IReadOnlyList<TreemapWeight> source, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 0 || height < 0 || source.Count > 10000 || source.Any(x => x.Bytes < 0))
            throw new ArgumentOutOfRangeException(nameof(source));
        if (width == 0 || height == 0) return [];
        var weights = source.Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).ToList();
        if (weights.Count > 64)
        {
            var remainder = weights.Skip(63).Sum(x => (decimal)x.Bytes);
            if (remainder > long.MaxValue) throw new OverflowException();
            weights = weights.Take(63).Append(new TreemapWeight("其他", null, (long)remainder)).ToList();
        }
        if (weights.Count == 0) return [];
        var result = new List<TreemapTile>();
        var stack = new Stack<(int Start, int End, double X, double Y, double W, double H)>();
        stack.Push((0, weights.Count, 0, 0, width, height));
        while (stack.TryPop(out var box))
        {
            if (box.End - box.Start == 1) { result.Add(new(weights[box.Start], box.X, box.Y, box.W, box.H)); continue; }
            var total = 0m;
            for (var i = box.Start; i < box.End; i++) total += weights[i].Bytes;
            var sum = (decimal)weights[box.Start].Bytes; var split = box.Start + 1;
            while (split < box.End - 1 && Math.Abs(total / 2 - sum - weights[split].Bytes) < Math.Abs(total / 2 - sum)) sum += weights[split++].Bytes;
            var ratio = (double)(sum / total);
            if (box.W >= box.H)
            {
                var first = box.W * ratio;
                stack.Push((split, box.End, box.X + first, box.Y, box.W - first, box.H));
                stack.Push((box.Start, split, box.X, box.Y, first, box.H));
            }
            else
            {
                var first = box.H * ratio;
                stack.Push((split, box.End, box.X, box.Y + first, box.W, box.H - first));
                stack.Push((box.Start, split, box.X, box.Y, box.W, first));
            }
        }
        return result;
    }
}
