using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Auditing;

/// <summary>
/// 审计日志表 EF Core 配置（<see cref="TenE0AuditLog"/> / <see cref="TenE0LoginLog"/>）。
///
/// 风格对齐 <c>OutboxModelBuilderExtensions</c>：列长常量集中管理，
/// 索引面向管理后台典型查询（按操作人 / 实体 / 时间区间）建复合索引。
/// </summary>
public static class AuditLogModelBuilderExtensions
{
    // ---- 列长权威来源（与 OutboxModelBuilderExtensions 风格一致）----
    public const int TraceIdMaxLength = 64;
    public const int ActorTypeMaxLength = 32;
    public const int ActorCodeMaxLength = 64;
    public const int EntityTypeMaxLength = 128;
    public const int EntityIdMaxLength = 64;
    public const int ActionMaxLength = 32;
    public const int IpAddressMaxLength = 64;
    public const int UserAgentMaxLength = 512;
    public const int EventTypeMaxLength = 32;
    public const int FailureReasonMaxLength = 256;

    public static ModelBuilder ConfigureTenE0AuditTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0AuditLog>(b =>
        {
            b.Property(a => a.TraceId).HasMaxLength(TraceIdMaxLength);
            b.Property(a => a.ActorType).HasMaxLength(ActorTypeMaxLength).IsRequired();
            b.Property(a => a.ActorCode).HasMaxLength(ActorCodeMaxLength);
            b.Property(a => a.EntityType).HasMaxLength(EntityTypeMaxLength).IsRequired();
            b.Property(a => a.EntityId).HasMaxLength(EntityIdMaxLength).IsRequired();
            b.Property(a => a.Action).HasMaxLength(ActionMaxLength).IsRequired();
            // ChangedFieldsJson 默认 nvarchar(max)，不设 MaxLength —— 单条审计 diff 可能较大，
            // 限长反而误伤（导航属性虽跳过，但宽表一次改几十个标量字段的场景仍常见）。
            b.Property(a => a.ChangedFieldsJson).IsRequired();
            b.Property(a => a.IpAddress).HasMaxLength(IpAddressMaxLength);
            b.Property(a => a.UserAgent).HasMaxLength(UserAgentMaxLength);

            // 管理后台查询模式：
            //   - 按操作人查（"张三最近改了什么"）+ 时间倒序
            //   - 按实体查（"订单 #123 的变更历史"）+ 时间倒序
            //   - 按时间区间审计
            b.HasIndex(a => new { a.ActorCode, a.CreateTime });
            b.HasIndex(a => new { a.EntityType, a.EntityId, a.CreateTime });
            b.HasIndex(a => a.CreateTime);
        });

        mb.Entity<TenE0LoginLog>(b =>
        {
            b.Property(l => l.UserCode).HasMaxLength(ActorCodeMaxLength).IsRequired();
            b.Property(l => l.EventType).HasMaxLength(EventTypeMaxLength).IsRequired();
            b.Property(l => l.IpAddress).HasMaxLength(IpAddressMaxLength);
            b.Property(l => l.UserAgent).HasMaxLength(UserAgentMaxLength);
            b.Property(l => l.FailureReason).HasMaxLength(FailureReasonMaxLength);

            // 登录日志查询模式：
            //   - 按用户查登录历史（"alice 最近登录记录"）
            //   - 异常登录排查（按 IP + 时间）
            //   - 按时间区间审计
            b.HasIndex(l => new { l.UserCode, l.CreateTime });
            b.HasIndex(l => new { l.IpAddress, l.CreateTime });
            b.HasIndex(l => l.CreateTime);
        });

        return mb;
    }
}
