using Microsoft.Extensions.Options;

namespace TenE0.Core.Files.Storage;

/// <summary>
/// 本地文件系统存储实现
/// </summary>
public class LocalFileStorage : IFileStorage
{
    private readonly TimeProvider _timeProvider;
    private readonly LocalStorageOptions _options;
    private readonly string _baseFullPath;

    public LocalFileStorage(TimeProvider timeProvider, IOptions<LocalStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _options = options.Value;
        Directory.CreateDirectory(_options.BasePath);
        _baseFullPath = Path.GetFullPath(_options.BasePath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public async Task<StorageResult> StoreAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        try
        {
            var relativePath = GenerateStoragePath(fileName);
            var fullPath = Path.Combine(_options.BasePath, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(fs, ct);

            var accessUrl = GetAccessUrl(relativePath);
            return new StorageResult(relativePath, accessUrl, true);
        }
        catch (Exception ex)
        {
            return new StorageResult(string.Empty, string.Empty, false, ex.Message);
        }
    }

    public async Task<Stream?> RetrieveAsync(string storagePath, CancellationToken ct = default)
    {
        if (!TryResolveSafePath(storagePath, out var fullPath)) return null;
        if (!File.Exists(fullPath)) return null;

        var ms = new MemoryStream();
        await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
        await fs.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public Task<bool> DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        if (!TryResolveSafePath(storagePath, out var fullPath)) return Task.FromResult(false);
        if (!File.Exists(fullPath)) return Task.FromResult(false);

        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    public string GetAccessUrl(string storagePath)
    {
        return $"{_options.BaseUrl}/{storagePath.Replace('\\', '/')}";
    }

    public Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default)
    {
        if (!TryResolveSafePath(storagePath, out var fullPath)) return Task.FromResult(false);
        return Task.FromResult(File.Exists(fullPath));
    }

    private string GenerateStoragePath(string fileName)
    {
        var date = _timeProvider.GetUtcNow().UtcDateTime;
        var ext = Path.GetExtension(fileName);
        var uniqueName = $"{Guid.NewGuid()}{ext}";
        return Path.Combine(date.ToString("yyyy"), date.ToString("MM"), uniqueName);
    }

    /// <summary>
    /// 把 storagePath 解析为绝对路径，并校验其落在 BasePath 沙箱内。
    /// 拒绝 ../、绝对路径、以及任何逃出 BasePath 的形式（含 symlink 绕过）。
    /// </summary>
    private bool TryResolveSafePath(string? storagePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(storagePath)) return false;

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(_options.BasePath, storagePath));
        }
        catch
        {
            return false;
        }

        if (!resolved.StartsWith(_baseFullPath, StringComparison.Ordinal)) return false;

        fullPath = resolved;
        return true;
    }
}

public class LocalStorageOptions
{
    public string BasePath { get; set; } = "uploads";
    public string BaseUrl { get; set; } = "/uploads";
}
