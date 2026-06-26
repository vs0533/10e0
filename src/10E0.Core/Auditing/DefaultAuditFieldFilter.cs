using System.Text.RegularExpressions;

namespace TenE0.Core.Auditing;

/// <summary>
/// 默认敏感字段脱敏过滤器。
///
/// <para>命中约定（issue #152 §6）：属性名（不区分大小写）包含
/// password / token / secret / signingkey / apikey 之一时视为敏感，值替换为 "***"。</para>
///
/// <para>注册为 Singleton。业务方可 <c>services.Replace(...)</c> 注册自定义实现
/// 替换默认规则。</para>
/// </summary>
public sealed class DefaultAuditFieldFilter : IAuditFieldFilter
{
    /// <summary>
    /// 敏感关键字正则。预编译 + 静态共享，避免每次 SaveChanges 重新编译。
    /// </summary>
    private static readonly Regex SensitivePattern = new(
        "password|token|secret|signingkey|apikey|accesstoken|refreshtoken|pwd",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string MaskedValue = "***";

    public bool IsSensitive(string propertyName)
        => SensitivePattern.IsMatch(propertyName);

    public object? Mask(string propertyName, object? value) => MaskedValue;
}
