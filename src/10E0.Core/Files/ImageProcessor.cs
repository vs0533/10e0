using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TenE0.Core.Files;

/// <summary>
/// 基于 ImageSharp 的图片处理实现
/// </summary>
public class ImageProcessor : IImageProcessor
{
    public async Task<ImageProcessResult> ProcessAsync(Stream inputStream, ImageProcessOptions options, CancellationToken ct = default)
    {
        try
        {
            using var image = await Image.LoadAsync<Rgba32>(inputStream, ct);

            // 调整尺寸
            if (options.Width.HasValue || options.Height.HasValue)
            {
                var width = options.Width ?? image.Width;
                var height = options.Height ?? image.Height;
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(width, height),
                    Mode = ResizeMode.Max
                }));
            }

            // 添加文字水印
            if (!string.IsNullOrEmpty(options.WatermarkText))
            {
                image.Mutate(x => x.ApplyWatermark(options.WatermarkText));
            }

            // 输出处理后的图片（默认 JPEG）
            var outputStream = new MemoryStream();
            IImageEncoder encoder = new JpegEncoder { Quality = options.Quality };
            await image.SaveAsync(outputStream, encoder, ct);
            outputStream.Position = 0;

            return new ImageProcessResult(
                outputStream,
                image.Width,
                image.Height,
                outputStream.Length,
                true
            );
        }
        catch (Exception ex)
        {
            return new ImageProcessResult(
                Stream.Null,
                0,
                0,
                0,
                false,
                ex.Message
            );
        }
    }

    public async Task<Stream> GenerateThumbnailAsync(Stream inputStream, int width, int height, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<Rgba32>(inputStream, ct);

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Crop
        }));

        var outputStream = new MemoryStream();
        await image.SaveAsJpegAsync(outputStream, new JpegEncoder { Quality = 80 }, ct);
        outputStream.Position = 0;
        return outputStream;
    }

    public async Task<(int Width, int Height)> GetDimensionsAsync(Stream inputStream, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<Rgba32>(inputStream, ct);
        return (image.Width, image.Height);
    }
}

/// <summary>
/// 水印扩展方法
/// </summary>
internal static class ImageProcessingExtensions
{
    public static IImageProcessingContext ApplyWatermark(this IImageProcessingContext context, string text)
    {
        const int fontSize = 24;
        const int padding = 10;

        var font = SystemFonts.CreateFont("Arial", fontSize, FontStyle.Regular);

        // 获取处理上下文的尺寸（通过绘制测量）
        var textSize = TextMeasurer.MeasureSize(text, new TextOptions(font));
        var imageWidth = context.GetCurrentSize().Width;
        var imageHeight = context.GetCurrentSize().Height;

        var x = imageWidth - textSize.Width - padding;
        var y = imageHeight - textSize.Height - padding;

        context.DrawText(text, font, Color.White.WithAlpha(0.5f), new PointF(x, y));
        return context;
    }
}
