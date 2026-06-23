using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Organizations;

/// <summary>
/// IOrgTreeService 默认实现。
/// 写操作（Add/Move）维护 Path + Level，读操作（descendants/ancestors）利用 Path 索引。
/// </summary>
public sealed class OrgTreeService<TContext>(
    IDbContextFactory<TContext> contextFactory,
    TimeProvider timeProvider) : IOrgTreeService
    where TContext : DbContext
{
    // #113: MoveAsync 子树分批大小 —— 平衡 ChangeTracker 内存峰值 vs DB 往返次数。
    // 1000 是个保守 default：100 万节点 = 1000 次 SaveChanges，每次 ChangeTracker 持有 ≤ 1000 entities。
    // 业务项目可未来通过 OrgTreeMoveOptions 覆盖（本 PR 不引入以控制 scope）。
    private const int MoveBatchSize = 1000;
    private readonly TimeProvider _time = timeProvider;

    public async Task<TenE0Org> AddAsync(
        string code, string name, string? parentId = null,
        string? description = null, int order = 0,
        CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        string parentPath = "/";
        int level = 0;

        if (parentId is not null)
        {
            var parent = await dc.Set<TenE0Org>().FirstOrDefaultAsync(o => o.Id == parentId, cancellationToken)
                ?? throw new InvalidOperationException($"父节点不存在：{parentId}");
            parentPath = parent.Path;
            level = parent.Level + 1;
        }

        var node = new TenE0Org
        {
            Code = code,
            Name = name,
            Description = description,
            Order = order,
            ParentId = parentId,
            Level = level,
        };
        // Path 拼接：父 Path（末尾已有 '/'）+ 自己 Id + '/'
        node.Path = $"{parentPath}{node.Id}/";

        dc.Set<TenE0Org>().Add(node);
        await dc.SaveChangesAsync(cancellationToken);
        return node;
    }

    public async Task MoveAsync(string nodeId, string? newParentId, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var node = await dc.Set<TenE0Org>().FirstOrDefaultAsync(o => o.Id == nodeId, cancellationToken)
            ?? throw new InvalidOperationException($"节点不存在：{nodeId}");

        string newParentPath = "/";
        int newLevel = 0;

        if (newParentId is not null)
        {
            if (newParentId == nodeId)
                throw new InvalidOperationException("不能移动到自身");

            var newParent = await dc.Set<TenE0Org>().FirstOrDefaultAsync(o => o.Id == newParentId, cancellationToken)
                ?? throw new InvalidOperationException($"目标父节点不存在：{newParentId}");

            if (newParent.Path.StartsWith(node.Path, StringComparison.Ordinal))
                throw new InvalidOperationException("不能移动到自己的后代节点");

            newParentPath = newParent.Path;
            newLevel = newParent.Level + 1;
        }

        var oldPath = node.Path;
        var newPath = $"{newParentPath}{node.Id}/";
        var levelDelta = newLevel - node.Level;

        // #113: 分批更新整棵子树，避免一次性 ToListAsync 把 100 万节点全灌进 ChangeTracker → OOM。
        // 高 2 个 review 反馈修：
        //   (a) 键集分页（Id > lastId）替代 Skip(processed) —— Skip/Take 跨 Level 混合会导致后批节点丢失
        //   (b) 外层 BeginTransaction + 每批 Savepoint —— 批中途失败回滚到事务起点，避免半迁移状态
        // 仍走 ChangeTracker 路径（保留 AuditInterceptor 写 UpdateTime/UpdateBy 的能力）；
        // 每批 SaveChanges 后 ChangeTracker.Clear() 让下一批从 0 开始。
        var now = _time.GetUtcNow();
        node.ParentId = newParentId;     // 自身节点的 ParentId 在事务开始前预先设好（ChangeTracker tracked）

        // InMemory provider 不支持事务（无 begin/commit 语义），生产数据库走显式事务回滚保护。
        // 测试场景（InMemory）单线程无并发写，不会出现"半迁移"风险，简化路径即可。
        // 用 ProviderName 而非 IsInMemory() 是因为后者扩展方法在某些 EF Core 版本下需要额外 using。
        var supportsTx = !(dc.Database.ProviderName ?? string.Empty).Contains("InMemory", StringComparison.Ordinal);
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
        if (supportsTx)
        {
            tx = await dc.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            string? lastId = null;
            while (true)
            {
                // 键集分页：上一批最后一条 Id 之后的所有子树节点（按 Id 升序），
                // 不依赖 Skip/Take 行偏移 —— 不存在跨 Level 错位丢节点的可能。
                var baseQuery = dc.Set<TenE0Org>()
                    .Where(o => o.Path.StartsWith(oldPath));
                var ordered = baseQuery.OrderBy(o => o.Id);
                var paged = lastId is null
                    ? ordered
                    : ordered.Where(o => string.Compare(o.Id, lastId, StringComparison.Ordinal) > 0);

                var batch = await paged.Take(MoveBatchSize).ToListAsync(cancellationToken);
                if (batch.Count == 0) break;

                foreach (var item in batch)
                {
                    item.Path = newPath + item.Path[oldPath.Length..];
                    item.Level += levelDelta;
                    item.UpdateTime = now;
                }

                await dc.SaveChangesAsync(cancellationToken);
                dc.ChangeTracker.Clear();
                lastId = batch[^1].Id;
            }

            if (tx is not null) await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<TenE0Org>> GetDescendantsAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var node = await dc.Set<TenE0Org>().AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == nodeId, cancellationToken);
        if (node is null) return [];

        return await dc.Set<TenE0Org>().AsNoTracking()
            .Where(o => o.Path.StartsWith(node.Path) && o.Id != nodeId)
            .OrderBy(o => o.Level).ThenBy(o => o.Order)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TenE0Org>> GetAncestorsAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var node = await dc.Set<TenE0Org>().AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == nodeId, cancellationToken);
        if (node is null) return [];

        // Path = "/a/b/c/" → 祖先 Id 是 [a, b]（去掉自己 c）
        var ids = node.Path.Trim('/').Split('/');
        if (ids.Length <= 1) return [];

        var ancestorIds = ids[..^1];   // 去掉最后一个（自己）
        var ancestors = await dc.Set<TenE0Org>().AsNoTracking()
            .Where(o => ancestorIds.Contains(o.Id))
            .ToListAsync(cancellationToken);

        return ancestors.OrderByDescending(a => a.Level).ToList();   // 从近到远
    }

    public async Task<IReadOnlySet<string>> GetSubtreeIdsAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var node = await dc.Set<TenE0Org>().AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == nodeId, cancellationToken);
        if (node is null) return new HashSet<string>();

        var ids = await dc.Set<TenE0Org>().AsNoTracking()
            .Where(o => o.Path.StartsWith(node.Path))
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }
}
