using Microsoft.Extensions.Options;

namespace TenE0.Core.Files.Storage;

/// <summary>
/// <see cref="AliyunOssOptions"/> 的 <see cref="IValidateOptions{TOptions}"/> 实现。
/// <para>
/// 取代 PR #6 中构造函数内调用的静态 <c>AliyunOssOptions.Validate(opts)</c>。
/// 启动期由 <c>services.AddOptions&lt;AliyunOssOptions&gt;().ValidateOnStart()</c>
/// 在 DI 容器 build 前一次性聚合所有字段错误并抛
/// <see cref="OptionsValidationException"/>，错误消息可同时列出多个字段，
/// 比构造期失败（每次解析 <c>IOptions&lt;AliyunOssOptions&gt;.Value</c> 都可能抛）
/// 更早、更聚合。
/// </para>
/// </summary>
public sealed class AliyunOssOptionsValidator : IValidateOptions<AliyunOssOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AliyunOssOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        PlaceholderPatternsValidator.EnsureValid(
            options.Endpoint, nameof(AliyunOssOptions), nameof(AliyunOssOptions.Endpoint),
            "OSS", "Aliyun RAM role", failures);
        PlaceholderPatternsValidator.EnsureValid(
            options.AccessKeyId, nameof(AliyunOssOptions), nameof(AliyunOssOptions.AccessKeyId),
            "OSS", "Aliyun RAM role", failures);
        PlaceholderPatternsValidator.EnsureValid(
            options.AccessKeySecret, nameof(AliyunOssOptions), nameof(AliyunOssOptions.AccessKeySecret),
            "OSS", "Aliyun RAM role", failures);
        PlaceholderPatternsValidator.EnsureValid(
            options.BucketName, nameof(AliyunOssOptions), nameof(AliyunOssOptions.BucketName),
            "OSS", "Aliyun RAM role", failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
