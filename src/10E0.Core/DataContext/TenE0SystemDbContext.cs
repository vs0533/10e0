using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.DynamicFilters;
using TenE0.Core.DynamicFilters.Storage;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Menus.Storage;
using TenE0.Core.Organizations;
using TenE0.Core.Permissions.DataFilter;
using TenE0.Core.Permissions.Storage;
using TenE0.Core.Files;
using TenE0.Core.Files.Storage;
using TenE0.Core.Sequences.Storage;

namespace TenE0.Core.DataContext;

/// <summary>
/// 框架"系统级 DbContext 基类" — 业务 DbContext 继承此类即可接入框架全部表。
///
/// 这是 ASP.NET Core Identity 同款模式：
///     业务方 DbContext 不需要：
///       - 实现任何 IXxxDataContext 接口
///       - 声明任何框架表的 DbSet 属性
///       - 调用任何 ConfigureTenE0XxxTables() 方法
///     都由本类自动完成。
///
/// 用法 A — 不需要扩展用户/角色：
///     public class AppDbContext(...) : TenE0SystemDbContext(...)
///     {
///         public DbSet&lt;Course&gt; Courses =&gt; Set&lt;Course&gt;();
///     }
///
/// 用法 B — 扩展用户/角色字段：
///     public class AppUser : TenE0User { public string? Avatar; }
///     public class AppRole : TenE0Role { public string? Color; }
///     public class AppDbContext(...) : TenE0SystemDbContext&lt;AppUser, AppRole&gt;(...)
///     {
///         public DbSet&lt;Course&gt; Courses =&gt; Set&lt;Course&gt;();
///     }
/// </summary>
public abstract class TenE0SystemDbContext<TUser, TRole>(
    DbContextOptions options,
    ICurrentUserContext currentUser,
    IDataAccessPolicy accessPolicy,
    IEnumerable<IEntityFilterContributor> filterContributors,
    IDynamicFilterProvider dynamicFilterProvider,
    ITenantContext tenantContext)
    : BaseDataContext(options, currentUser, accessPolicy, filterContributors, dynamicFilterProvider, tenantContext)
    where TUser : TenE0User
    where TRole : TenE0Role
{
    // ============================================================
    // 框架表 — 业务方自动获得
    // ============================================================
    public DbSet<TUser> Users => Set<TUser>();
    public DbSet<TRole> Roles => Set<TRole>();
    public DbSet<TenE0UserRole> UserRoles => Set<TenE0UserRole>();
    public DbSet<TenE0RefreshToken> RefreshTokens => Set<TenE0RefreshToken>();
    public DbSet<TenE0RolePermission> RolePermissions => Set<TenE0RolePermission>();
    public DbSet<TenE0Org> Orgs => Set<TenE0Org>();
    public DbSet<TenE0Sequence> Sequences => Set<TenE0Sequence>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<TenE0Menu> Menus => Set<TenE0Menu>();
    public DbSet<TenE0RoleMenu> RoleMenus => Set<TenE0RoleMenu>();
    public DbSet<TenE0DataFilterRule> DataFilterRules => Set<TenE0DataFilterRule>();
    public DbSet<TenE0FileAttachment> FileAttachments => Set<TenE0FileAttachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // 一次性配置全部框架表，业务子类只需关心自己的实体
        modelBuilder.ConfigureTenE0AuthTables<TUser>();
        modelBuilder.ConfigureTenE0PermissionTables<TRole>();
        modelBuilder.ConfigureTenE0OrgTables();
        modelBuilder.ConfigureTenE0SequenceTables();
        modelBuilder.ConfigureTenE0OutboxTables();
        modelBuilder.ConfigureTenE0MenuTables();
        modelBuilder.ConfigureTenE0DataFilterTables();
        modelBuilder.ConfigureTenE0FileAttachmentTables();
    }
}

/// <summary>
/// 非泛型快捷别名 — 业务方不需要扩展用户/角色时用这个。
/// 等价于 <c>TenE0SystemDbContext&lt;TenE0User, TenE0Role&gt;</c>。
/// </summary>
public abstract class TenE0SystemDbContext(
    DbContextOptions options,
    ICurrentUserContext currentUser,
    IDataAccessPolicy accessPolicy,
    IEnumerable<IEntityFilterContributor> filterContributors,
    IDynamicFilterProvider dynamicFilterProvider,
    ITenantContext tenantContext)
    : TenE0SystemDbContext<TenE0User, TenE0Role>(options, currentUser, accessPolicy, filterContributors, dynamicFilterProvider, tenantContext);
