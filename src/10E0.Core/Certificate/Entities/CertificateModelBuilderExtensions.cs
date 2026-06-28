using Microsoft.EntityFrameworkCore;
using TenE0.Core.Certificate.Entities;

namespace TenE0.Core.Certificate;

/// <summary>
/// Certificate 模块的 EF Core 表映射（issue #185）。
///
/// <para>
/// 列长常量集中管理（仿 <c>SchedulingModelBuilderExtensions</c> / <c>OutboxModelBuilderExtensions</c>），
/// 后续若新增 Schema 升级 seeder 必须从此处读取，避免 entity 改 MaxLength 后 seeder ALTER 出旧长度导致漂移。
/// </para>
/// </summary>
public static class CertificateModelBuilderExtensions
{
    /// <summary>模板 / 证书的 <c>Code</c> / <c>TemplateCode</c> 列长（业务编码）。</summary>
    public const int CodeMaxLength = 128;

    /// <summary>Name / Title 列长。</summary>
    public const int NameMaxLength = 256;

    /// <summary>CertificateNo 列长（证书编号，可能含日期 + 序号 + 前缀）。</summary>
    public const int CertificateNoMaxLength = 128;

    /// <summary>TemplateJson / DataJson 列长（JSON，留宽松上限）。</summary>
    public const int JsonMaxLength = 4096;

    /// <summary>RelatedEntityType 列长（业务实体类型名）。</summary>
    public const int RelatedEntityTypeMaxLength = 128;

    /// <summary>RelatedEntityId / FileAttachmentId 列长（GUID "N" 32 位，留余量）。</summary>
    public const int IdRefMaxLength = 64;

    /// <summary>TenantId 列长（与 Scheduling / 其他模块一致）。</summary>
    public const int TenantIdMaxLength = 64;

    /// <summary>表名：证书模板。权威源。</summary>
    public const string CertificateTemplatesTableName = "TenE0CertificateTemplates";

    /// <summary>表名：证书实例。权威源。</summary>
    public const string CertificatesTableName = "TenE0Certificates";

    /// <summary>
    /// 配置 Certificate 模块表（由 <c>TenE0SystemDbContext</c> 自动调用）。
    /// </summary>
    public static ModelBuilder ConfigureTenE0CertificateTables(this ModelBuilder mb)
    {
        // 显式 ToTable 钉死表名 —— 与 SchedulingModelBuilderExtensions 同款理由：
        // 避免未来引入 pluralizer / snake_case 全局约定后表名漂移。
        mb.Entity<TenE0CertificateTemplate>(b =>
        {
            b.ToTable(CertificateTemplatesTableName);
            b.HasKey(e => e.Id);
            b.Property(t => t.Code).HasMaxLength(CodeMaxLength).IsRequired();
            b.Property(t => t.Name).HasMaxLength(NameMaxLength).IsRequired();
            b.Property(t => t.TemplateJson).HasMaxLength(JsonMaxLength).IsRequired();
            b.Property(t => t.TenantId).HasMaxLength(TenantIdMaxLength).IsRequired();

            // Code 全局唯一（跨租户）—— 模板通常组织级共享，租户隔离由渲染期校验 + TenantId 过滤双重保障。
            b.HasIndex(t => t.Code).IsUnique();
            // 启用状态查询索引（渲染入口 WHERE Code = ? AND IsEnabled）
            b.HasIndex(t => new { t.Code, t.IsEnabled });
        });

        mb.Entity<TenE0Certificate>(b =>
        {
            b.ToTable(CertificatesTableName);
            b.HasKey(e => e.Id);
            b.Property(c => c.TemplateCode).HasMaxLength(CodeMaxLength).IsRequired();
            b.Property(c => c.Title).HasMaxLength(NameMaxLength).IsRequired();
            b.Property(c => c.CertificateNo).HasMaxLength(CertificateNoMaxLength).IsRequired();
            b.Property(c => c.DataJson).HasMaxLength(JsonMaxLength).IsRequired();
            b.Property(c => c.FileAttachmentId).HasMaxLength(IdRefMaxLength);
            b.Property(c => c.RelatedEntityId).HasMaxLength(IdRefMaxLength);
            b.Property(c => c.RelatedEntityType).HasMaxLength(RelatedEntityTypeMaxLength);
            b.Property(c => c.TenantId).HasMaxLength(TenantIdMaxLength).IsRequired();

            // GetByRelatedEntityAsync 查询路径：WHERE RelatedEntityType=? AND RelatedEntityId=?
            b.HasIndex(c => new { c.RelatedEntityType, c.RelatedEntityId });
            // 按模板查证书（运维 / 重生成场景）
            b.HasIndex(c => c.TemplateCode);
            // 证书编号唯一（防 Sequence 并发重复 / 手动传入冲突）
            b.HasIndex(c => c.CertificateNo).IsUnique();
        });

        return mb;
    }
}
