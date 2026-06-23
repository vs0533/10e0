using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Workflow.Runtime;

/// <summary>
/// 流程运行时表 EF Core 配置（TenE0ProcessInstance / TenE0ProcessTask / TenE0ProcessHistory）。
/// </summary>
public static class WorkflowRuntimeModelBuilderExtensions
{
    public static ModelBuilder ConfigureTenE0WorkflowRuntimeTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0ProcessInstance>(b =>
        {
            b.Property(i => i.DefinitionId).HasMaxLength(64).IsRequired();
            b.Property(i => i.DefinitionCode).HasMaxLength(64).IsRequired();
            b.Property(i => i.BusinessKey).HasMaxLength(128).IsRequired();
            b.Property(i => i.EntityType).HasMaxLength(128).IsRequired();
            b.Property(i => i.EntityId).HasMaxLength(64).IsRequired();
            b.Property(i => i.CurrentNodeCode).HasMaxLength(64).IsRequired();
            b.Property(i => i.Initiator).HasMaxLength(64).IsRequired();
            b.Property(i => i.InitiatorOrgId).HasMaxLength(64);
            b.Property(i => i.Title).HasMaxLength(256);
            b.Property(i => i.TenantId).HasMaxLength(64).IsRequired();

            b.HasIndex(i => i.Initiator);
            b.HasIndex(i => new { i.EntityType, i.EntityId });
            b.HasIndex(i => i.BusinessKey);

            // 乐观并发控制（与 #100 序列号一致）
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        mb.Entity<TenE0ProcessTask>(b =>
        {
            b.Property(t => t.InstanceId).HasMaxLength(64).IsRequired();
            b.Property(t => t.NodeCode).HasMaxLength(64).IsRequired();
            b.Property(t => t.Assignee).HasMaxLength(64).IsRequired();
            b.Property(t => t.DelegatedBy).HasMaxLength(64);
            b.Property(t => t.CompletedBy).HasMaxLength(64);
            b.Property(t => t.Comment).HasMaxLength(1024);

            // 我的待办查询核心索引（Assignee + Status）
            b.HasIndex(t => new { t.Assignee, t.Status });
            b.HasIndex(t => t.InstanceId);
            // 超时扫描索引（Status + Deadline）
            b.HasIndex(t => new { t.Status, t.Deadline });

            // 乐观并发控制
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        mb.Entity<TenE0ProcessHistory>(b =>
        {
            b.Property(h => h.InstanceId).HasMaxLength(64).IsRequired();
            b.Property(h => h.NodeCode).HasMaxLength(64).IsRequired();
            b.Property(h => h.Action).HasMaxLength(32).IsRequired();
            b.Property(h => h.Actor).HasMaxLength(64).IsRequired();
            b.Property(h => h.Assignee).HasMaxLength(64);
            b.Property(h => h.Comment).HasMaxLength(1024);

            b.HasIndex(h => h.InstanceId);
            b.HasIndex(h => h.Actor);
        });

        return mb;
    }
}
