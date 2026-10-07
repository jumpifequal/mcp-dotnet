namespace MCPPOC.Analysis;

/// <summary>A page of results. <see cref="NextOffset"/> is null when there are no more items.</summary>
public sealed record Page<T>(int Total, int Offset, int Count, int? NextOffset, IReadOnlyList<T> Items);

public static class Paging
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 2000;

    /// <summary>
    /// Slices <paramref name="items"/>. A <paramref name="limit"/> of 0 (or less) returns everything from
    /// <paramref name="offset"/> up to <see cref="MaxLimit"/> items.
    /// </summary>
    public static Page<T> Paginate<T>(IEnumerable<T> items, int offset = 0, int limit = DefaultLimit)
    {
        var list = items as IReadOnlyList<T> ?? items.ToList();
        offset = Math.Max(0, offset);
        if (limit <= 0 || limit > MaxLimit)
            limit = MaxLimit;

        if (offset >= list.Count)
            return new Page<T>(list.Count, offset, 0, null, Array.Empty<T>());

        var count = Math.Min(limit, list.Count - offset);
        var slice = new T[count];
        for (var i = 0; i < count; i++)
            slice[i] = list[offset + i];
        int? next = offset + count < list.Count ? offset + count : null;
        return new Page<T>(list.Count, offset, count, next, slice);
    }
}
