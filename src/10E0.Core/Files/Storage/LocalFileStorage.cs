using Microsoft.Extensions.Options;

namespace TenE0.Core.Files.Storage;

/// <summary>
/// 本地文件系统存储实现
/// </summary>
public class LocalFileStorage : IFileStorage
{
    private readonly LocalStorageOptions _options;

    public LocalFileStorage(IOptions<LocalStorageOptions> options)
    {
        _options = options.Value;
        Directory.CreateDirectory(_options.BasePath);
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
        var fullPath = Path.Combine(_options.BasePath, storagePath);
        if (!File.Exists(fullPath)) return null;

        var ms = new MemoryStream();
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
        await fs.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public Task<bool> DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(_options.BasePath, storagePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public string GetAccessUrl(string storagePath)
    {
        return $"{_options.BaseUrl}/{storagePath.Replace('\\', '/')}";
    }

    public Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(_options.BasePath, storagePath);
        return Task.FromResult(File.Exists(fullPath));
    }

    private string GenerateStoragePath(string fileName)
    {
        var date = DateTime.Now;
        var ext = Path.GetExtension(fileName);
        var uniqueName = $"{Guid.NewGuid()}{ext}";
        return Path.Combine(date.ToString("yyyy"), date.ToString("MM"), uniqueName);
    }
}

public class LocalStorageOptions
{
    public string BasePath { get; set; } = "uploads";
    public string BaseUrl { get; set; } = "/uploads";
}
