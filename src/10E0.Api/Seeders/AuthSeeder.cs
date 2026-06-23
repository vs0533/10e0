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
///
/// #126 fail-closed：默认密码不再硬编码常量，必须从配置 <c>Seed:DefaultPassword</c>
/// 读取。构造函数阶段就校验：缺配置 / 空字符串 → 抛 <see cref="InvalidOperationException"/>，
/// 异常消息明确点名 "Seed:DefaultPassword"，与 Jwt:SigningKey 启动期校验同语义。
/// 这样部署 demo 镜像不会继承一个写死在源码里的 admin 默认活凭证。
/// </summary>
internal sealed class AuthSeeder(
    IDbContextFactory<DemoDbContext> dcFactory,
    IPasswordHasher passwordHasher,
    IOrgTreeService orgTree,
    IConfiguration configuration) : IDataSeeder
{
    /// <summary>配置键名（双冒号分隔，对应 <c>Seed:DefaultPassword</c>）。</summary>
    internal const string DefaultPasswordConfigKey = "Seed:DefaultPassword";

    /// <summary>
    /// 启动期解析默认密码：缺配置 / 空 / 纯空白 → fail-closed 抛异常。
    /// 任何环境（Development / Production / Test）都走这条路径，
    /// 不存在"开发环境 fallback 到硬编码值"的兜底逻辑。
    /// </summary>
    private string ResolveDefaultPassword()
    {
        var value = configuration[DefaultPasswordConfigKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{DefaultPasswordConfigKey} 未配置。请通过 appsettings.json / " +
                $"环境变量 SEED__DEFAULTPASSWORD / dotnet user-secrets 注入一个非空密码。" +
                $"为了避免 issue #126 描述的 'demo 镜像发布即泄露 admin 默认活凭证'，" +
                $"任何环境下都拒绝使用硬编码默认密码启动。");
        }
        return value;
    }

    public int Order => 200;   // 在 PermissionSeeder(100) 之后跑，保证角色已存在

    public async Task SeedAsync(DbContext _, CancellationToken ct)
    {
        // 启动期 fail-closed：缺配置 → 抛异常，DatabaseInitializerService 启动失败，
        // 整个 host 不进入监听端口。一定要在 SeedAsync 顶部（而不是 await dcFactory 之后），
        // 让 #126 语义最先被识别。
        var defaultPassword = ResolveDefaultPassword();
        var defaultPasswordHash = passwordHasher.Hash(defaultPassword);

        await using var dc = await dcFactory.CreateDbContextAsync(ct);

        if (!await dc.Users.AnyAsync(ct))
        {
            // 默认管理员：admin — 密码来自 Seed:DefaultPassword 配置，演示项目可自定义
            dc.Users.Add(new AppUser
            {
                UserCode = "admin",
                DisplayName = "系统管理员",
                PasswordHash = defaultPasswordHash,
                IsActive = true,
                UserType = UserType.Person,
                Avatar = "/avatars/admin.png",
                Department = "信息中心",
            });

            // 普通用户：alice — 同样使用配置的默认密码
            dc.Users.Add(new AppUser
            {
                UserCode = "alice",
                DisplayName = "Alice",
                PasswordHash = defaultPasswordHash,
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
