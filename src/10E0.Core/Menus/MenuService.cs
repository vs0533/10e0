using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.Menus.Storage;
using StorageMenuType = TenE0.Core.Menus.Storage.MenuType;

namespace TenE0.Core.Menus;

/// <summary>
/// 菜单服务 — 核心 CRUD + 物化路径维护。
/// </summary>
public sealed partial class MenuService<TContext>(
    IDbContextFactory<TContext> contextFactory,
    ICurrentUserContext currentUser) : IMenuService
    where TContext : DbContext
{
    public async Task<TenE0Menu> AddAsync(MenuCreateRequest request, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        string parentTreePath = "/";
        int level = 0;

        if (request.ParentId is not null)
        {
            var parent = await dc.Set<TenE0Menu>().FirstOrDefaultAsync(m => m.Id == request.ParentId, ct)
                ?? throw new InvalidOperationException($"父菜单不存在：{request.ParentId}");
            parentTreePath = parent.TreePath;
            level = parent.Level + 1;
        }

        var menu = new TenE0Menu
        {
            Name = request.Name,
            RoutePath = request.RoutePath,
            ParentId = request.ParentId,
            Icon = request.Icon,
            Component = request.Component,
            Redirect = request.Redirect,
            Layout = request.Layout,
            Order = request.Order,
            MenuType = (StorageMenuType)request.MenuType,
            Level = level,
            TreePath = "",
        };

        dc.Set<TenE0Menu>().Add(menu);
        await dc.SaveChangesAsync(ct);

        menu.TreePath = $"{parentTreePath}{menu.Id}/";
        await dc.SaveChangesAsync(ct);

        return menu;
    }

    public async Task UpdateAsync(string menuId, MenuUpdateRequest request, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var menu = await dc.Set<TenE0Menu>().FirstOrDefaultAsync(m => m.Id == menuId, ct)
            ?? throw new InvalidOperationException($"菜单不存在：{menuId}");

        if (request.Name is not null) menu.Name = request.Name;
        if (request.RoutePath is not null) menu.RoutePath = request.RoutePath;
        if (request.Icon is not null) menu.Icon = request.Icon;
        if (request.Component is not null) menu.Component = request.Component;
        if (request.Redirect is not null) menu.Redirect = request.Redirect;
        if (request.Layout.HasValue) menu.Layout = request.Layout.Value;
        if (request.Order.HasValue) menu.Order = request.Order.Value;
        if (request.MenuType.HasValue) menu.MenuType = (StorageMenuType)request.MenuType.Value;
        if (request.IsActive.HasValue) menu.IsActive = request.IsActive.Value;

        await dc.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string menuId, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var menu = await dc.Set<TenE0Menu>().FirstOrDefaultAsync(m => m.Id == menuId, ct)
            ?? throw new InvalidOperationException($"菜单不存在：{menuId}");

        // 删除走 EF Core 软删除契约：Remove() 把 State 标 Deleted，
        // AuditInterceptor 看到 ISoftDeleteEntity 实体 + State=Deleted 自动转
        // Modified + 填 IsSoftDelete/DeleteTime/DeleteBy 三个字段（issue #95 修复）。
        // 业务代码不再手动赋值审计字段；当前用户从 ICurrentUserContext 注入，
        // 当前时间从 TimeProvider 注入（测试用 FakeTimeProvider 完全控制）。
        dc.Set<TenE0Menu>().Remove(menu);

        await dc.SaveChangesAsync(ct);
    }

    public async Task MoveAsync(string menuId, string? newParentId, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var menu = await dc.Set<TenE0Menu>().FirstOrDefaultAsync(m => m.Id == menuId, ct)
            ?? throw new InvalidOperationException($"菜单不存在：{menuId}");

        string newParentTreePath = "/";
        int newLevel = 0;

        if (newParentId is not null)
        {
            if (newParentId == menuId)
                throw new InvalidOperationException("不能移动到自身");

            var newParent = await dc.Set<TenE0Menu>().FirstOrDefaultAsync(m => m.Id == newParentId, ct)
                ?? throw new InvalidOperationException($"目标父菜单不存在：{newParentId}");

            if (newParent.TreePath.StartsWith(menu.TreePath, StringComparison.Ordinal))
                throw new InvalidOperationException("不能移动到自己的后代节点");

            newParentTreePath = newParent.TreePath;
            newLevel = newParent.Level + 1;
        }

        var oldTreePath = menu.TreePath;
        var newTreePath = $"{newParentTreePath}{menu.Id}/";
        var levelDiff = newLevel - menu.Level;

        var subtree = await dc.Set<TenE0Menu>()
            .Where(m => m.TreePath.StartsWith(oldTreePath))
            .ToListAsync(ct);

        foreach (var item in subtree)
        {
            item.TreePath = newTreePath + item.TreePath[oldTreePath.Length..];
            item.Level += levelDiff;
        }
        menu.ParentId = newParentId;

        await dc.SaveChangesAsync(ct);
    }
}
