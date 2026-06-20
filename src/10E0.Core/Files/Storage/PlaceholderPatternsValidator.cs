using Microsoft.Extensions.Options;

namespace TenE0.Core.Files.Storage;

/// <summary>
/// 占位符黑名单共享校验逻辑。
/// <para>
/// OSS / S3 等云存储凭据在配置阶段容易遗留 <c>TODO</c> / <c>CHANGE_ME</c> /
/// <c>PLACEHOLDER</c> / <c>your-</c> 等未替换占位符；通过此 helper 统一拦截，
/// 避免在每个 <see cref="IValidateOptions{TOptions}"/> 实现里重复硬编码
/// 模式数组和分支判断。
/// </para>
/// <para>
/// 行为契约（与 PR #6 旧 <c>Validate(opts)</c> 静态方法一致）：
/// </para>
/// <list type="bullet">
///   <item>值为 null / 空 / 纯空白 → 追加 required 失败。</item>
///   <item>值包含任一占位符模式（大小写不敏感）→ 追加 placeholder 失败。</item>
/// </list>
/// <para>
/// 本 helper 不直接抛异常，而是把失败描述追加到调用方的
/// <paramref name="failures"/> 列表里 — 这样 <c>IValidateOptions&lt;T&gt;</c>
/// 实现可一次性聚合多个字段错误，由 <c>OptionsBuilder.ValidateOnStart()</c>
/// 在启动期统一抛出 <see cref="OptionsValidationException"/>。
/// </para>
/// </summary>
internal static class PlaceholderPatternsValidator
{
    /// <summary>
    /// 占位符黑名单（大小写不敏感）。配置值若包含其中任一模式将被视为未配置。
    /// </summary>
    internal static readonly string[] PlaceholderPatterns =
    {
        "TODO", "CHANGE_ME", "PLACEHOLDER", "your-"
    };

    /// <summary>
    /// 校验单个字符串字段是否符合"已真实配置"要求。
    /// </summary>
    /// <param name="value">待校验的字段值。</param>
    /// <param name="optionsTypeName">选项类型名（用于失败消息前缀，如 <c>AliyunOssOptions</c>）。</param>
    /// <param name="fieldName">字段名（对应 <see cref="IValidateOptions{TOptions}"/> 实现里给出的失败定位）。</param>
    /// <param name="configKey">配置节短名（用于"OSS" / "AWS"），决定环境变量前缀（<c>OSS__</c> / <c>AWS__</c>）。</param>
    /// <param name="providerHint">Provider 友好的获取提示（如 <c>Aliyun RAM role</c> / <c>AWS IAM role, AWS SSO, or AWS Secrets Manager</c>）。</param>
    /// <param name="failures">失败描述累加器（来自 <see cref="IValidateOptions{TOptions}.Validate"/> 调用方）。</param>
    internal static void EnsureValid(
        string? value,
        string optionsTypeName,
        string fieldName,
        string configKey,
        string providerHint,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(
                $"{optionsTypeName}.{fieldName} is required. " +
                $"Configure it via configuration key '{configKey}:{fieldName}' " +
                $"(or environment variable '{configKey}__{fieldName}'), " +
                $"{providerHint}, or `dotnet user-secrets` for local development. " +
                $"Do NOT commit credentials to source control.");
            return;
        }

        foreach (var pattern in PlaceholderPatterns)
        {
            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"{optionsTypeName}.{fieldName} contains placeholder '{pattern}' " +
                    $"and appears to be unconfigured. " +
                    $"Replace it with a real value sourced from environment variable " +
                    $"'{configKey}__{fieldName}' or {providerHint}. " +
                    $"Do NOT commit credentials to source control.");
            }
        }
    }
}
