using TenE0.Core.Events;
using TenE0.Core.ImportExport.Mapping;
using TenE0.Core.Sequences;

namespace TenE0.Api.Domain;

// DemoEntity 升级为聚合根：业务方法触发事件，OutboxInterceptor 自动持久化事件
internal sealed class DemoEntity : AggregateRoot
{
    // 流水号自动生成：每天重置，4 位补零，前缀 "DEMO-"
    [Sequence("demo", "DEMO-{yyyyMMdd}-{0000}")]
    [ImportColumn("编码")]
    [ExportColumn("编码", Order = 1)]
    public string Code { get; set; } = "";

    [ImportColumn("名称", Required = true)]
    [ExportColumn("名称", Order = 2)]
    public string Name { get; set; } = "";

    [ImportColumn("组织ID")]
    [ExportColumn("组织ID", Order = 3)]
    public string? OrgId { get; set; }

    [ImportColumn("薪资")]
    [ExportColumn("薪资", Order = 4, Format = "N2")]
    public decimal? Salary { get; set; }

    /// <summary>
    /// 标记"已发布"。仅业务方法可以触发状态变化，并附带事件。
    /// 这是 DDD 的典型用法：状态变更通过聚合方法暴露，外界用 method 而不是直接 set。
    /// </summary>
    public bool IsPublished { get; private set; }

    public void Publish(string publisherCode)
    {
        if (IsPublished)
            throw new InvalidOperationException($"Demo {Id} 已发布，不可重复发布");

        IsPublished = true;
        Raise(new Events.DemoPublishedEvent(Id, Code, Name, publisherCode, OrgId));
    }
}
