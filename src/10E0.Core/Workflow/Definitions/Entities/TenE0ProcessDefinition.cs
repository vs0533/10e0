using TenE0.Core.Abstractions;
using TenE0.Core.Entities;

namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 审批流程定义（模板）— 节点 + 连线 + 条件的有向图，JSON 存储。
///
/// 版本管理：同 <see cref="Code"/> 下多版本，<see cref="IsLatest"/>=true 标识当前生效版本；
/// 启动新实例取 latest，已启动实例锁定创建时的 <see cref="Version"/>（模板改版不影响存量）。
///
/// 多租户：实现 <see cref="IMultiTenantEntity"/>，EF 自动按 TenantId 过滤。
/// </summary>
public class TenE0ProcessDefinition : AuditedEntity, IMultiTenantEntity
{
    /// <summary>业务编码，如 "expense-claim"。同 Code 下多版本。</summary>
    public string Code { get; set; } = "";

    /// <summary>流程名称。</summary>
    public required string Name { get; set; }

    /// <summary>版本号。Code + Version 唯一。</summary>
    public int Version { get; set; } = 1;

    /// <summary>流程分类编码（关联 #153 数据字典 DictType）。</summary>
    public string? CategoryCode { get; set; }

    /// <summary>开始节点编码（流程入口，Build 期校验存在）。</summary>
    public string StartNodeCode { get; set; } = "";

    /// <summary>节点图 JSON（序列化的 IProcessNode 集合，含多态）。</summary>
    public string NodesJson { get; set; } = "[]";

    /// <summary>连线 JSON（含条件路由），本期以节点内 NextNodeCode/Routes 表达为主，此字段保留扩展。</summary>
    public string EdgesJson { get; set; } = "[]";

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否为同 Code 下最新版本（启动实例时取 latest）。</summary>
    public bool IsLatest { get; set; } = true;

    /// <summary>流程描述。</summary>
    public string? Description { get; set; }

    /// <summary>租户 ID。</summary>
    public string TenantId { get; set; } = "";
}
