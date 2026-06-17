using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Hosting;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Api.Seeders;

/// <summary>
/// 启动时初始化角色 + 默认 grants。
/// </summary>
internal sealed class PermissionSeeder(IDbContextFactory<DemoDbContext> dcFactory) : IDataSeeder
{
    public int Order => 100;

    public async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);

        if (await dc.Roles.AnyAsync(cancellationToken)) return; // 幂等

        dc.Roles.AddRange(
            new TenE0Role { Code = "viewer", Name = "查看者" },
            new TenE0Role { Code = "editor", Name = "编辑者" },
            new TenE0Role { Code = "manager", Name = "管理者" },
            new TenE0Role { Code = "hr", Name = "人事（管薪资）" },
            new TenE0Role { Code = "super_admin", Name = "超级管理员" });

        dc.RolePermissions.AddRange(
            // viewer
            new() { RoleCode = "viewer", PermissionKey = DemoPermissions.View },
            // editor
            new() { RoleCode = "editor", PermissionKey = DemoPermissions.View },
            new() { RoleCode = "editor", PermissionKey = DemoPermissions.Create },
            new() { RoleCode = "editor", PermissionKey = DemoPermissions.Update },
            // manager（含删除，但不含薪资字段）
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.View },
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.Create },
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.Update },
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.Delete },
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.Admin },
            // hr（专门管薪资字段；含 Create 用于演示创建带 Salary 的实体）
            new() { RoleCode = "hr", PermissionKey = DemoPermissions.View },
            new() { RoleCode = "hr", PermissionKey = DemoPermissions.Create },
            new() { RoleCode = "hr", PermissionKey = DemoPermissions.Update },
            new() { RoleCode = "hr", PermissionKey = DemoPermissions.ManageSalary });

        await dc.SaveChangesAsync(cancellationToken);
    }
}
