namespace TenE0.Core.Files;

/// <summary>
/// 图片处理抽象层
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// 处理图片（裁剪、压缩、水印）。
    /// 调用方应该 <c>using var result = await processor.ProcessAsync(...)</c> —— #104: result 实现 IDisposable
    /// 自动释放 <see cref="ImageProcessResult.ProcessedStream"/>，避免 MemoryStream 堆积到 LOH 碎片。
    /// </summary>
    Task<ImageProcessResult> ProcessAsync(Stream inputStream, ImageProcessOptions options, CancellationToken ct = default);

    /// <summary>
    /// 生成缩略图。返回的 <see cref="Stream"/> 由调用方负责 Dispose（通常是 <c>using</c> 块）。
    /// </summary>
    Task<Stream> GenerateThumbnailAsync(Stream inputStream, int width, int height, CancellationToken ct = default);

    /// <summary>
    /// 获取图片尺寸
    /// </summary>
    Task<(int Width, int Height)> GetDimensionsAsync(Stream inputStream, CancellationToken ct = default);
}

/// <summary>
/// 图片处理结果。<see cref="IDisposable"/> 实现 #104 修复 —— 调用方 <c>using var result</c>
/// 自动释放 <see cref="ProcessedStream"/>（MemoryStream），避免 LOH 碎片。
///
/// 失败结果（<see cref="Success"/> = false）的 <see cref="ProcessedStream"/> = <see cref="Stream.Null"/>，
/// Dispose 时跳过释放（Stream.Null 本身就是静态空流，无需释放）。
/// </summary>
public sealed record ImageProcessResult(
    Stream ProcessedStream,
    int Width,
    int Height,
    long FileSize,
    bool Success,
    string? ErrorMessage = null) : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// 释放 <see cref="ProcessedStream"/>。调用方应通过 <c>using var result = ...</c> 触发。
    /// 多次调用安全：第一次释放，后续空操作。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ProcessedStream is not null && ProcessedStream != Stream.Null && ProcessedStream.CanWrite)
        {
            ProcessedStream.Dispose();
        }
    }
}
