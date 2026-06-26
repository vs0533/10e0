using Microsoft.Extensions.Diagnostics.HealthChecks;
using TenE0.Core.Files;

namespace TenE0.Core.Observability.HealthChecks;

/// <summary>
/// 文件存储可写性健康检查（#161）。
///
/// <para>
/// 探测底层 <see cref="IFileStorage"/>（本地磁盘 / OSS / S3）的读写删往返：
/// 写一个唯一命名的临时空 blob → 读回校验存在 → 删除。
/// 任何步骤失败即视为存储不可写，readiness 应摘流（上传/下载功能将不可用）。
/// </para>
/// <para>
/// <b>仅当 Files 模块启用</b>（<see cref="IFileStorage"/> 已注册）时由
/// <c>AddTenE0Observability</c> 挂载；未启用文件功能的项目不会触发此检查。
/// </para>
/// </summary>
public sealed class FileStorageHealthCheck : IHealthCheck
{
    private const string ProbePrefix = ".healthprobe/";

    private readonly IFileStorage _storage;

    /// <summary>构造。</summary>
    public FileStorageHealthCheck(IFileStorage storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var probeName = $"{ProbePrefix}{Guid.NewGuid():N}";
        try
        {
            // 写一个空 blob。用 MemoryStream 避免分配大 buffer。
            await using var empty = new MemoryStream();
            var stored = await _storage.StoreAsync(empty, probeName, "application/octet-stream", cancellationToken);
            if (!stored.Success)
                return HealthCheckResult.Unhealthy($"文件存储写入失败：{stored.ErrorMessage}");

            // 读回校验（验证可读 + 数据路径双向可达）。
            var stream = await _storage.RetrieveAsync(stored.StoragePath, cancellationToken);
            if (stream is null)
                return HealthCheckResult.Unhealthy("文件存储读取返回 null（写入后立即读取失败）");
            await stream.DisposeAsync();

            // 清理探测文件；删除失败不致命（属残留垃圾，不影响可用性判定），仅记录到 description。
            var deleted = await _storage.DeleteAsync(stored.StoragePath, cancellationToken);
            return deleted
                ? HealthCheckResult.Healthy("文件存储读写删往返正常")
                : HealthCheckResult.Degraded("文件存储读写正常，但探测文件删除失败（残留垃圾，建议清理）");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("文件存储探测失败", ex);
        }
    }
}
