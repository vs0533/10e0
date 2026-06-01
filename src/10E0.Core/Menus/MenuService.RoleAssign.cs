using Microsoft.EntityFrameworkCore;
using TenE0.Core.Menus.Storage;

namespace TenE0.Core.Menus;

public sealed partial class MenuService<TContext>
    where TContext : DbContext
{
    public async Task AssignToRoleAsync(string roleCode, IEnumerable<string> menuIds, CancellationToken ct = default)
    {
        var newIds = new HashSet<string>(menuIds);

        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var set = dc.Set<TenE0RoleMenu>();

        var current = await set
            .Where(rm => rm.RoleCode == roleCode)
            .ToListAsync(ct);
        var currentIds = current.Select(rm => rm.MenuId).ToHashSet();

        set.RemoveRange(current.Where(rm => !newIds.Contains(rm.MenuId)));
        set.AddRange(newIds.Where(id => !currentIds.Contains(id))
            .Select(id => new TenE0RoleMenu { RoleCode = roleCode, MenuId = id }));

        await dc.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlySet<string>> GetRoleMenuIdsAsync(string roleCode, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var ids = await dc.Set<TenE0RoleMenu>()
            .AsNoTracking()
            .Where(rm => rm.RoleCode == roleCode)
            .Select(rm => rm.MenuId)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }
}
