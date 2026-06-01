using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Organizations;

/// <summary>
/// IOrgTreeService 默认实现。
/// 写操作（Add/Move）维护 Path + Level，读操作（descendants/ancestors）利用 Path 索引。
/// </summary>
public sealed class OrgTreeService<TContext>(IDbContextFactory<TContext> contextFactory) : IOrgTreeService
    where TContext : DbContext
{
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

        // 加载整棵子树（含自身）一次性更新 — 简单可靠，企业级树规模通常不大
        var subtree = await dc.Set<TenE0Org>()
            .Where(o => o.Path.StartsWith(oldPath))
            .ToListAsync(cancellationToken);

        foreach (var item in subtree)
        {
            item.Path = newPath + item.Path[oldPath.Length..];
            item.Level += levelDelta;
        }
        node.ParentId = newParentId;

        await dc.SaveChangesAsync(cancellationToken);
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
