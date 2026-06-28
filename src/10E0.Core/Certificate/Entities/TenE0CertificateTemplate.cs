using TenE0.Core.Abstractions;
using TenE0.Core.Entities;

namespace TenE0.Core.Certificate.Entities;

/// <summary>
/// 证书模板（issue #185）—— 配置数据，描述「证书长什么样 + 数据从哪来」。
///
/// <para>
/// <b>业务编码 <see cref="Code"/></b>：唯一（全局跨租户，与 <c>TenE0ScheduledJob.Code</c> 同款语义）。
/// 渲染时 <c>ICertificateService.RenderAsync(templateCode, ...)</c> 按此查找模板。
/// </para>
///
/// <para>
/// <b>模板 DSL <see cref="TemplateJson"/></b>：<see cref="CertificateDefinition"/> 序列化后的 JSON 字符串。
/// 结构化对象（非脚本/非用户输入字符串），不接受可执行模板字符串（issue 安全考量 #2）。
/// </para>
///
/// <para>
/// <b>启用/禁用</b>：通过 <see cref="Enable"/> / <see cref="Disable"/> 方法切换 <see cref="IsEnabled"/>。
/// 禁用的模板不被 <c>RenderAsync</c> 接受（抛 <see cref="InvalidOperationException"/>）。
/// </para>
///
/// <para>
/// <b>租户隔离</b>：实现 <see cref="IMultiTenantEntity"/>，自动走租户 Named Query Filter（见 docs/20）。
/// </para>
/// </summary>
public sealed class TenE0CertificateTemplate : AuditedEntity, IMultiTenantEntity
{
    /// <summary>业务编码（唯一，如 <c>research-completion</c>）。渲染入口按此查找模板。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>显示名称（如"科研项目结题证书"）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 模板 DSL（<see cref="CertificateDefinition"/> 序列化 JSON）。结构化对象，不接受可执行模板字符串。
    /// </summary>
    public string TemplateJson { get; set; } = string.Empty;

    /// <summary>是否启用。禁用模板不能渲染。通过 <see cref="Enable"/>/<see cref="Disable"/> 切换。</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>启用模板。</summary>
    public void Enable() => IsEnabled = true;

    /// <summary>禁用模板（停止渲染但不删除，保留历史追溯）。</summary>
    public void Disable() => IsEnabled = false;

    /// <inheritdoc />
    public string TenantId { get; set; } = string.Empty;
}
