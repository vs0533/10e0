using Microsoft.Extensions.Options;

namespace TenE0.Core.Auth.Jwt;

/// <summary>
/// <see cref="JwtOptions"/> 的 <see cref="IValidateOptions{TOptions}"/> 实现。
/// <para>
/// 启动期由 <c>services.AddOptions&lt;JwtOptions&gt;().ValidateOnStart()</c>
/// 在 DI 容器 build 前一次性聚合所有字段错误并抛
/// <see cref="OptionsValidationException"/>。
/// </para>
/// <para>
/// 来源：issue #92 [P0][Security] JWT SigningKey 硬编码 fallback 允许伪造 token。
/// 拒绝：
/// </para>
/// <list type="bullet">
///   <item>SigningKey 为 null / 空 / 纯空白。</item>
///   <item>SigningKey 包含 <c>TODO</c> / <c>CHANGE_ME</c> / <c>PLACEHOLDER</c> / <c>your-</c> 等占位符。</item>
///   <item>SigningKey 长度 &lt; 32 字节（HS256 推荐最低 32 字节）。</item>
///   <item>Issuer / Audience 为空字符串。</item>
/// </list>
/// </summary>
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    private const int MinKeyByteLength = 32;

    private static readonly string[] PlaceholderPatterns =
    {
        "TODO", "CHANGE_ME", "PLACEHOLDER", "your-"
    };

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            failures.Add(
                "JwtOptions.SigningKey is required. " +
                "Configure it via configuration key 'Jwt:SigningKey' " +
                "(or environment variable 'JWT__SigningKey'), or a secret manager. " +
                "Do NOT commit keys to source control.");
        }
        else
        {
            foreach (var pattern in PlaceholderPatterns)
            {
                if (options.SigningKey.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"JwtOptions.SigningKey contains placeholder '{pattern}' " +
                        "and appears to be unconfigured. " +
                        "Replace it with a real value sourced from environment variable " +
                        "'JWT__SigningKey' or a secret manager. " +
                        "Do NOT commit keys to source control.");
                }
            }

            var byteLength = System.Text.Encoding.UTF8.GetByteCount(options.SigningKey);
            if (byteLength < MinKeyByteLength)
            {
                failures.Add(
                    $"JwtOptions.SigningKey is {byteLength} bytes; " +
                    $"HS256 requires at least {MinKeyByteLength} bytes of entropy. " +
                    "Generate a key with: openssl rand -base64 32");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Issuer))
            failures.Add("JwtOptions.Issuer is required.");
        if (string.IsNullOrWhiteSpace(options.Audience))
            failures.Add("JwtOptions.Audience is required.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
