using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 流程定义表 EF Core 配置。
/// </summary>
public static class WorkflowDefinitionModelBuilderExtensions
{
    /// <summary>配置 TenE0ProcessDefinition 表（由 TenE0SystemDbContext 自动调用）。</summary>
    public static ModelBuilder ConfigureTenE0WorkflowDefinitionTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0ProcessDefinition>(b =>
        {
            b.Property(d => d.Code).HasMaxLength(64).IsRequired();
            b.Property(d => d.Name).HasMaxLength(128).IsRequired();
            b.Property(d => d.CategoryCode).HasMaxLength(64);
            b.Property(d => d.StartNodeCode).HasMaxLength(64).IsRequired();
            b.Property(d => d.Description).HasMaxLength(512);
            b.Property(d => d.TenantId).HasMaxLength(64).IsRequired();

            // 节点图 / 连线 JSON 作为大文本（不限长，JSON 可能较大）
            b.Property(d => d.NodesJson).IsRequired();
            b.Property(d => d.EdgesJson).IsRequired();

            // Code + Version 唯一（多租户下同 Code 也唯一）
            b.HasIndex(d => new { d.Code, d.Version }).IsUnique();

            // 按 Code 查 latest 的常见路径
            b.HasIndex(d => new { d.Code, d.IsLatest });

            // 版本切换的乐观并发控制（与 #100 序列号一致）
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });
        return mb;
    }
}
