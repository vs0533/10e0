using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Hosting;
using TenE0.Core.Organizations;

namespace TenE0.Api.Seeders;

/// <summary>
/// 初始数据：管理员账号 + 一棵示例组织树。
/// </summary>
internal sealed class AuthSeeder(
    IDbContextFactory<DemoDbContext> dcFactory,
    IPasswordHasher passwordHasher,
    IOrgTreeService orgTree) : IDataSeeder
{
    public int Order => 200;   // 在 PermissionSeeder(100) 之后跑，保证角色已存在

    public async Task SeedAsync(DbContext _, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);

        if (!await dc.Users.AnyAsync(ct))
        {
            // 默认管理员：admin / 111111 — 用扩展的 AppUser 类型，演示新增字段也能直接用
            dc.Users.Add(new AppUser
            {
                UserCode = "admin",
                DisplayName = "系统管理员",
                PasswordHash = passwordHasher.Hash("111111"),
                IsActive = true,
                UserType = UserType.Person,
                Avatar = "/avatars/admin.png",
                Department = "信息中心",
            });

            // 普通用户：alice / 111111
            dc.Users.Add(new AppUser
            {
                UserCode = "alice",
                DisplayName = "Alice",
                PasswordHash = passwordHasher.Hash("111111"),
                IsActive = true,
                Avatar = "/avatars/alice.png",
                Department = "市场部",
                Birthday = new DateOnly(1995, 6, 15),
            });

            // 角色绑定
            // alice 同时拥有 viewer + editor — 用于 #7 E2E 测试（revoke viewer/permissions/demo.view 后 alice 立即 403）
            // editor 角色用于 CreateDemoCommand（demo.create）和 UpdateDemoCommand（demo.update）
            dc.UserRoles.AddRange(
                new TenE0UserRole { UserCode = "admin", RoleCode = "super_admin" },
                new TenE0UserRole { UserCode = "admin", RoleCode = "manager" },
                new TenE0UserRole { UserCode = "alice", RoleCode = "viewer" },
                new TenE0UserRole { UserCode = "alice", RoleCode = "editor" });

            await dc.SaveChangesAsync(ct);
        }

        if (!await dc.Orgs.AnyAsync(ct))
        {
            // 组织树：集团 → 北京/上海 → 销售/技术
            var hq = await orgTree.AddAsync("HQ", "集团总部", cancellationToken: ct);
            var bj = await orgTree.AddAsync("BJ", "北京分公司", parentId: hq.Id, cancellationToken: ct);
            var sh = await orgTree.AddAsync("SH", "上海分公司", parentId: hq.Id, cancellationToken: ct);
            await orgTree.AddAsync("BJ-SALES", "北京销售部", parentId: bj.Id, cancellationToken: ct);
            await orgTree.AddAsync("BJ-TECH", "北京技术部", parentId: bj.Id, cancellationToken: ct);
            await orgTree.AddAsync("SH-SALES", "上海销售部", parentId: sh.Id, cancellationToken: ct);
        }
    }
}
