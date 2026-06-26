using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TenE0.Core.Files;
using TenE0.Core.Files.Storage;
using TenE0.Core.Observability.HealthChecks;

namespace TenE0.Core.Tests.Observability.HealthChecks;

/// <summary>
/// #161 FileStorageHealthCheck：本地存储写读删往返成功 → Healthy。
/// </summary>
[Trait("Category", "Unit")]
public sealed class FileStorageHealthCheckTests
{
    private static HealthCheckContext EmptyContext => new();

    private static LocalFileStorage CreateLocalStorage(string basePath)
        => new(TimeProvider.System, Options.Create(new LocalStorageOptions { BasePath = basePath }));

    [Fact]
    public async Task CheckHealthAsync_LocalStorageRoundTrip_ReturnsHealthy()
    {
        // 每个测试独立临时目录，测后清理。
        var dir = Path.Combine(Path.GetTempPath(), $"obs-files-{Guid.NewGuid():N}");
        var storage = CreateLocalStorage(dir);
        var check = new FileStorageHealthCheck(storage);

        try
        {
            var result = await check.CheckHealthAsync(EmptyContext);
            result.Status.Should().Be(HealthStatus.Healthy);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 一个总是失败的 IFileStorage —— 模拟存储不可写。
    /// </summary>
    private sealed class FailingStorage : IFileStorage
    {
        public Task<StorageResult> StoreAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default) =>
            Task.FromResult(new StorageResult(fileName, "", false, "写入失败（模拟）"));
        public Task<Stream?> RetrieveAsync(string storagePath, CancellationToken ct = default) =>
            Task.FromResult<Stream?>(null);
        public Task<bool> DeleteAsync(string storagePath, CancellationToken ct = default) => Task.FromResult(false);
        public string GetAccessUrl(string storagePath) => "";
        public Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact]
    public async Task CheckHealthAsync_StoreFails_ReturnsUnhealthy()
    {
        var check = new FileStorageHealthCheck(new FailingStorage());

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
