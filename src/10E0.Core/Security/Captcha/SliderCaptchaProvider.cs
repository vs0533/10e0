using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TenE0.Core.Security.Captcha;

/// <summary>
/// 滑块验证码默认实现（issue #162）。
///
/// <para>
/// 生成一张背景图 + 一个带缺口（透明）的滑块小图；客户端拖动滑块到缺口位置，
/// <see cref="ValidateAsync"/> 比对拖动距离与缺口 X 坐标，差距 ≤ <see cref="CaptchaOptions.SliderTolerance"/> 算通过。
/// </para>
///
/// <para>
/// <b>本实现是"够用版"</b>：用纯色 + 噪点 + 缺口矩形构造，不依赖外部图库素材。
/// 业务方可 Replace 切到带背景图素材 / 行为轨迹分析（如极验）的增强实现。
/// </para>
/// </summary>
public sealed class SliderCaptchaProvider : ICaptchaProvider
{
    private const int Width = 300;
    private const int Height = 150;
    private const int SliderSize = 50;

    private readonly CaptchaStore _store;
    private readonly IOptions<CaptchaOptions> _options;

    public SliderCaptchaProvider(CaptchaStore store, IOptions<CaptchaOptions> options)
    {
        _store = store;
        _options = options;
    }

    public CaptchaKind Kind => CaptchaKind.Slider;

    public async Task<CaptchaResult> GenerateAsync(CancellationToken ct = default)
    {
        var rng = new Random();
        var sliderX = rng.Next(SliderSize + 10, Width - SliderSize - 10);
        var sliderY = rng.Next(5, Height - SliderSize - 5);

        // 背景图：渐变色 + 噪点 + 缺口（用深色矩形示意缺口位置）
        using var bg = new Image<Rgba32>(Width, Height);
        bg.Mutate(ctx =>
        {
            // 渐变背景（ImageSharp 渐变需 Drawing；这里用纵向纯色填充近似）
            var c1 = new Color(new Rgba32(60, 90, 140));
            var c2 = new Color(new Rgba32(120, 160, 200));
            ctx.Fill(c1);

            // 噪点（用 1x1 像素填充模拟）
            for (var i = 0; i < 200; i++)
            {
                var x = rng.Next(Width);
                var y = rng.Next(Height);
                var v = (byte)rng.Next(180, 230);
                ctx.Fill(new Color(new Rgba32(v, v, v)), new Rectangle(x, y, 1, 1));
            }

            // 缺口：透明矩形带描边，提示用户拖到这里
            var gapRect = new Rectangle(sliderX, sliderY, SliderSize, SliderSize);
            ctx.Fill(Color.White.WithAlpha(0.3f), gapRect);
            // 边框：用 1px 矩形点描出（避免 Pen 依赖）
            DrawRectBorder(ctx, gapRect, Color.Black);
        });

        var bgMs = new MemoryStream();
        await bg.SaveAsync(bgMs, new PngEncoder(), ct);
        bgMs.Position = 0;

        // 滑块图：纯色方块（客户端初始显示在最左，拖动到缺口 X）
        using var slider = new Image<Rgba32>(SliderSize, SliderSize);
        slider.Mutate(ctx =>
        {
            ctx.Fill(new Color(new Rgba32(80, 120, 180)));
            DrawRectBorder(ctx, new Rectangle(0, 0, SliderSize - 1, SliderSize - 1), Color.White);
        });

        var sliderMs = new MemoryStream();
        await slider.SaveAsync(sliderMs, new PngEncoder(), ct);
        sliderMs.Position = 0;

        var captchaId = Guid.NewGuid().ToString("N");
        // 答案 = 缺口左上角 X 坐标（客户端拖动距离与之比对）
        await _store.SetAsync(captchaId, sliderX.ToString(), ct);

        return new CaptchaResult(
            captchaId,
            "image/png",
            bgMs,
            CaptchaKind.Slider,
            SliderImage: sliderMs,
            SliderSize: (SliderSize, SliderSize));
    }

    public async Task<bool> ValidateAsync(string captchaId, string userInput, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(captchaId) || string.IsNullOrEmpty(userInput))
            return false;
        if (!int.TryParse(userInput, out var distance))
            return false;

        var answer = await _store.TryGetAndRemoveAsync(captchaId, ct);
        if (answer is null || !int.TryParse(answer, out var target))
            return false;

        var tolerance = _options.Value.SliderTolerance;
        return Math.Abs(distance - target) <= tolerance;
    }

    /// <summary>用 1px 矩形点描出矩形四边（避免依赖 Pen / Draw 扩展签名跨版本兼容性）。</summary>
    private static void DrawRectBorder(IImageProcessingContext ctx, Rectangle rect, Color color)
    {
        // 上 / 下边
        ctx.Fill(color, new Rectangle(rect.X, rect.Y, rect.Width, 1));
        ctx.Fill(color, new Rectangle(rect.X, rect.Y + rect.Height, rect.Width, 1));
        // 左 / 右边
        ctx.Fill(color, new Rectangle(rect.X, rect.Y, 1, rect.Height));
        ctx.Fill(color, new Rectangle(rect.X + rect.Width, rect.Y, 1, rect.Height));
    }
}
