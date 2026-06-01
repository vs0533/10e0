using Microsoft.EntityFrameworkCore;
using TenE0.Core.Menus.Storage;

namespace TenE0.Core.Menus;

public sealed partial class MenuService<TContext>
    where TContext : DbContext
{
    public async Task<IReadOnlyList<TenE0Menu>> GetAllAsync(CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        return await dc.Set<TenE0Menu>()
            .AsNoTracking()
            .Where(m => !m.IsSoftDelete)
            .OrderBy(m => m.Order)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MenuTreeNode>> GetMenuTreeAsync(CancellationToken ct = default)
    {
        var allMenus = await GetAllAsync(ct);
        return BuildTree(allMenus, null);
    }

    public async Task<IReadOnlyList<TenE0Menu>> GetUserMenusAsync(CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated)
            return [];

        var roleIds = currentUser.RoleIds;
        if (roleIds.Count == 0)
            return [];

        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var menuIds = await dc.Set<TenE0RoleMenu>()
            .AsNoTracking()
            .Where(rm => roleIds.Contains(rm.RoleCode))
            .Select(rm => rm.MenuId)
            .Distinct()
            .ToListAsync(ct);

        return await dc.Set<TenE0Menu>()
            .AsNoTracking()
            .Where(m => menuIds.Contains(m.Id) && m.IsActive && !m.IsSoftDelete)
            .OrderBy(m => m.Order)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MenuTreeNode>> GetUserMenuTreeAsync(CancellationToken ct = default)
    {
        var allowedMenus = await GetUserMenusAsync(ct);
        if (allowedMenus.Count == 0)
            return [];

        // 收集所有祖先 Id，确保树不断裂
        var allowedIds = allowedMenus.Select(m => m.Id).ToHashSet();
        var allMenus = await GetAllAsync(ct);
        var menuById = allMenus.ToDictionary(m => m.Id);

        var completeIds = new HashSet<string>(allowedIds);
        foreach (var menu in allowedMenus)
        {
            var parentId = menu.ParentId;
            while (parentId is not null && completeIds.Add(parentId))
            {
                if (menuById.TryGetValue(parentId, out var parent))
                    parentId = parent.ParentId;
                else
                    break;
            }
        }

        return BuildTree(allMenus, completeIds);
    }

    private static IReadOnlyList<MenuTreeNode> BuildTree(
        IReadOnlyList<TenE0Menu> menus, HashSet<string>? allowedIds)
    {
        var filtered = allowedIds is not null
            ? menus.Where(m => allowedIds.Contains(m.Id)).ToList()
            : menus.ToList();

        var roots = filtered
            .Where(m => m.ParentId is null)
            .OrderBy(m => m.Order)
            .ToList();

        var byParent = filtered
            .Where(m => m.ParentId is not null)
            .GroupBy(m => m.ParentId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Order).ToList());

        return roots.Select(m => BuildNode(m, byParent)).ToList();
    }

    private static MenuTreeNode BuildNode(
        TenE0Menu m, Dictionary<string, List<TenE0Menu>> byParent)
    {
        byParent.TryGetValue(m.Id, out var children);

        return new MenuTreeNode
        {
            Id = m.Id,
            Name = m.Name,
            RoutePath = m.RoutePath,
            Icon = m.Icon,
            Component = m.Component,
            Redirect = m.Redirect,
            Layout = m.Layout,
            Order = m.Order,
            MenuType = (MenuType)m.MenuType,
            IsActive = m.IsActive,
            Children = children?.Select(c => BuildNode(c, byParent)).ToList() ?? new(),
        };
    }
}
