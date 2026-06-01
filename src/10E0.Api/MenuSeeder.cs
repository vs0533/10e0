using Microsoft.EntityFrameworkCore;
using TenE0.Core.Hosting;
using TenE0.Core.Menus;
using TenE0.Core.Menus.Storage;
using MenuType = TenE0.Core.Menus.MenuType;

/// <summary>
/// 菜单种子数据 — 仪表盘 + 系统管理（用户/角色/菜单）。
/// 使用 IMenuService 创建菜单，确保 TreePath / Level 由服务统一维护。
/// </summary>
internal sealed class MenuSeeder(IMenuService menuService, IDbContextFactory<DemoDbContext> dcFactory)
    : IDataSeeder
{
    public int Order => 300; // 在 PermissionSeeder(100) 和 AuthSeeder(200) 之后

    public async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);

        if (await dc.Set<TenE0Menu>().AnyAsync(cancellationToken))
            return; // 幂等：已有菜单数据则跳过

        // 仪表盘
        await menuService.AddAsync(new MenuCreateRequest(
            Name: "仪表盘",
            RoutePath: "/dashboard",
            ParentId: null,
            Icon: "dashboard",
            Component: "dashboard/index",
            Redirect: null,
            Layout: true,
            Order: 1,
            MenuType: MenuType.Menu), cancellationToken);

        // 系统管理（目录）
        var sysDir = await menuService.AddAsync(new MenuCreateRequest(
            Name: "系统管理",
            RoutePath: "/system",
            ParentId: null,
            Icon: "setting",
            Component: null,
            Redirect: null,
            Layout: true,
            Order: 99,
            MenuType: MenuType.Directory), cancellationToken);

        // 用户管理
        await menuService.AddAsync(new MenuCreateRequest(
            Name: "用户管理",
            RoutePath: "/system/users",
            ParentId: sysDir.Id,
            Icon: null,
            Component: "system/users/index",
            Redirect: null,
            Layout: true,
            Order: 1,
            MenuType: MenuType.Menu), cancellationToken);

        // 角色管理
        await menuService.AddAsync(new MenuCreateRequest(
            Name: "角色管理",
            RoutePath: "/system/roles",
            ParentId: sysDir.Id,
            Icon: null,
            Component: "system/roles/index",
            Redirect: null,
            Layout: true,
            Order: 2,
            MenuType: MenuType.Menu), cancellationToken);

        // 菜单管理
        await menuService.AddAsync(new MenuCreateRequest(
            Name: "菜单管理",
            RoutePath: "/system/menus",
            ParentId: sysDir.Id,
            Icon: null,
            Component: "system/menus/index",
            Redirect: null,
            Layout: true,
            Order: 3,
            MenuType: MenuType.Menu), cancellationToken);
    }
}
