using Microsoft.Extensions.Options;

namespace TenE0.Core.Files.Storage;

/// <summary>
/// <see cref="AwsS3Options"/> 的 <see cref="IValidateOptions{TOptions}"/> 实现。
/// <para>
/// 取代 PR #6 中构造函数内调用的静态 <c>AwsS3Options.Validate(opts)</c>。
/// 启动期由 <c>services.AddOptions&lt;AwsS3Options&gt;().ValidateOnStart()</c>
/// 在 DI 容器 build 前一次性聚合所有字段错误并抛
/// <see cref="OptionsValidationException"/>，错误消息可同时列出多个字段，
/// 比构造期失败（每次解析 <c>IOptions&lt;AwsS3Options&gt;.Value</c> 都可能抛）
/// 更早、更聚合。
/// </para>
/// </summary>
public sealed class AwsS3OptionsValidator : IValidateOptions<AwsS3Options>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AwsS3Options options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        const string providerHint = "AWS IAM role, AWS SSO, or AWS Secrets Manager";

        PlaceholderPatternsValidator.EnsureValid(
            options.AccessKey, nameof(AwsS3Options), nameof(AwsS3Options.AccessKey),
            "AWS", providerHint, failures);
        PlaceholderPatternsValidator.EnsureValid(
            options.SecretKey, nameof(AwsS3Options), nameof(AwsS3Options.SecretKey),
            "AWS", providerHint, failures);
        PlaceholderPatternsValidator.EnsureValid(
            options.Region, nameof(AwsS3Options), nameof(AwsS3Options.Region),
            "AWS", providerHint, failures);
        PlaceholderPatternsValidator.EnsureValid(
            options.BucketName, nameof(AwsS3Options), nameof(AwsS3Options.BucketName),
            "AWS", providerHint, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
