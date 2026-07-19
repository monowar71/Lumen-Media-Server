namespace FreePlex.Application.Common;

public sealed class PagedResult<T>
{
    public PagedResult(IReadOnlyList<T> items, int page, int pageSize, int total, string? nextCursor = null)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        Total = total;
        TotalPages = pageSize > 0 ? (int)Math.Ceiling(total / (double)pageSize) : 0;
        NextCursor = nextCursor;
    }

    public IReadOnlyList<T> Items { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int Total { get; }
    public int TotalPages { get; }
    public string? NextCursor { get; }
}
