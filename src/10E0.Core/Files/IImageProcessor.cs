namespace TenE0.Core.Files;

/// <summary>
/// 图片处理抽象层
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// 处理图片（裁剪、压缩、水印）
    /// </summary>
    Task<ImageProcessResult> ProcessAsync(Stream inputStream, ImageProcessOptions options, CancellationToken ct = default);

    /// <summary>
    /// 生成缩略图
    /// </summary>
    Task<Stream> GenerateThumbnailAsync(Stream inputStream, int width, int height, CancellationToken ct = default);

    /// <summary>
    /// 获取图片尺寸
    /// </summary>
    Task<(int Width, int Height)> GetDimensionsAsync(Stream inputStream, CancellationToken ct = default);
}

public record ImageProcessResult(
    Stream ProcessedStream,
    int Width,
    int Height,
    long FileSize,
    bool Success,
    string? ErrorMessage = null
);
