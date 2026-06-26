using System.Reflection;
using TenE0.Core.Auditing;
using TenE0.Core.Configuration;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Files.Storage;
using TenE0.Core.ImportExport;
using TenE0.Core.Realtime;
using TenE0.Core.Workflow.Runtime;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddTenE0All{TContext}"/> 的聚合配置选项（issue #160）。
///
/// <para>
/// 把分散在十几个 <c>AddTenE0Xxx</c> 调用上的开关 / 子模块 options 收口到一个对象，
/// 让 <c>Program.cs</c> 从"15+ 行样板注册"瘦身为"一段 options 配置 + 一行 <c>AddTenE0All</c>"。
/// </para>
///
/// <para>
/// <b>默认启用策略</b>：基础套件（Menus / Sequences / DomainEvents / DynamicFilters /
/// Configuration）默认 <c>true</c> —— 几乎所有项目都要；按需启用项（Files / Auditing /
/// ImportExport / Realtime / Workflow）默认 <c>false</c> —— 避免引入不必要的依赖。
/// Core / EntityService / DataContext / Cqrs / Permissions / Identity 始终注册，不设开关。
/// </para>
///
/// <para>
/// 各子模块的 <c>Action&lt;TOptions&gt;</c> 直接透传给对应的 <c>AddTenE0Xxx</c>，
/// 与单独调用这些方法的配置能力完全等价。
/// </para>
/// </summary>
public sealed class TenE0Options
{
    /// <summary>
    /// 数据库连接串。为 <c>null</c> 时 <c>AddTenE0All</c> 从 <paramref name="configuration"/>
    /// 的 <c>ConnectionStrings:Default</c> 读取（参见 <see cref="ServiceCollectionExtensions.AddTenE0All{TContext}"/>）。
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// 指定 provider（覆盖连接串探测）。为 <c>null</c> 时由 <c>AddTenE0All</c> 调用
    /// <see cref="ConnectionStringProbe.Detect"/> 自动探测。
    /// </summary>
    public DatabaseProvider? Provider { get; set; }

    /// <summary>
    /// CQRS / 权限扫描的程序集。为 <c>null</c> 时用入口程序集 <c>Assembly.GetEntryAssembly()</c>。
    /// </summary>
    public Assembly[]? HandlerAssemblies { get; set; }

    /// <summary>Identity 配置（JWT + Permissions + Organizations）。必填 —— JWT <c>SigningKey</c> 不可省略。</summary>
    public Action<TenE0IdentityOptions>? Identity { get; set; }

    // ---------- 基础套件（默认 true）----------

    /// <summary>启用菜单服务（默认 true）。</summary>
    public bool Menus { get; set; } = true;

    /// <summary>启用流水号生成器（默认 true）。</summary>
    public bool Sequences { get; set; } = true;

    /// <summary>启用领域事件 + Outbox 基础设施（默认 true）。</summary>
    public bool DomainEvents { get; set; } = true;

    /// <summary>领域事件 Outbox relay 配置。</summary>
    public Action<OutboxRelayOptions>? DomainEventsOptions { get; set; }

    /// <summary>启用动态数据过滤（默认 true）。</summary>
    public bool DynamicFilters { get; set; } = true;

    /// <summary>启用数据字典 + 系统参数（默认 true）。对应 <c>AddTenE0Configuration</c>。</summary>
    public bool Configuration { get; set; } = true;

    /// <summary>数据字典 / 系统参数配置。</summary>
    public Action<ConfigurationOptions>? ConfigurationOptions { get; set; }

    // ---------- 按需启用项（默认 false）----------

    /// <summary>启用文件上传（本地存储，默认 false）。</summary>
    public bool Files { get; set; }

    /// <summary>文件上传（本地存储）配置。</summary>
    public Action<LocalStorageOptions>? FilesOptions { get; set; }

    /// <summary>启用审计日志（默认 false）。对应 issue #152。</summary>
    public bool Auditing { get; set; }

    /// <summary>审计日志配置。</summary>
    public Action<AuditOptions>? AuditingOptions { get; set; }

    /// <summary>启用导入导出（默认 false）。对应 issue #154。</summary>
    public bool ImportExport { get; set; }

    /// <summary>导入导出配置。</summary>
    public Action<ImportExportOptions>? ImportExportOptions { get; set; }

    /// <summary>启用实时推送 SignalR（默认 false）。对应 issue #155。</summary>
    public bool Realtime { get; set; }

    /// <summary>实时推送配置。</summary>
    public Action<RealtimeOptions>? RealtimeOptions { get; set; }

    /// <summary>启用工作流（状态机 + 流程定义 + 运行时，默认 false）。对应 issue #156 epic。</summary>
    public bool Workflow { get; set; }

    /// <summary>工作流运行时配置。</summary>
    public Action<WorkflowRuntimeOptions>? WorkflowOptions { get; set; }

    /// <summary>工作流状态机扫描的程序集（默认与 <see cref="HandlerAssemblies"/> 一致）。</summary>
    public Assembly[]? WorkflowAssemblies { get; set; }
}

/// <summary>
/// 支持的 EF Core database provider 枚举。
/// 用于 <see cref="ConnectionStringProbe"/> 探测结果与
/// <c>AddTenE0DataContext&lt;TContext&gt;(IServiceCollection, string, DatabaseProvider?)</c>
/// 重载之间的契约传递。
/// </summary>
public enum DatabaseProvider
{
    /// <summary>Microsoft SQL Server。</summary>
    SqlServer,

    /// <summary>PostgreSQL (Npgsql)。</summary>
    PostgreSQL,

    /// <summary>SQLite。</summary>
    SQLite,

    /// <summary>EF Core InMemory（仅测试场景）。</summary>
    InMemory,
}
