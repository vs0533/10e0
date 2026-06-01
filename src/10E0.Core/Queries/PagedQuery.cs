namespace TenE0.Core.Queries;

/// <summary>
/// 通用分页查询参数。
/// </summary>
public record PagedQuery(
    int Page = 1,
    int PageSize = 20,
    string? OrderBy = null,
    string? Where = null
);

/// <summary>
/// 通用分页结果。
/// </summary>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize,
    int TotalPages
)
{
    public static PagedResult<T> Create(IReadOnlyList<T> items, int total, int page, int pageSize)
    {
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return new PagedResult<T>(items, total, page, pageSize, totalPages);
    }
}
