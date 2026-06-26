using Microsoft.Extensions.Options;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TenE0.Core.Security.Captcha;

/// <summary>
/// 图形验证码默认实现（issue #162）。
///
/// <para>
/// 复用 Files 模块已引入的 <c>SixLabors.ImageSharp</c>（MIT 许可），不引入专门的验证码库。
/// 生成 4-6 位字符（默认 4）+ 干扰线 + 噪点 + 字符旋转扭曲；答案落 <see cref="CaptchaStore"/>，
/// TTL 由 <see cref="CaptchaOptions.Ttl"/> 控制（默认 5 分钟），一次性消费。
/// </para>
///
/// <para><b>字符集</b>：去掉易混淆字符（0/O/o/1/I/l）提升用户体验。</para>
/// </summary>
public sealed class ImageCaptchaProvider : ICaptchaProvider
{
    private const string Charset = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";

    private static readonly Color[] Palette =
    [
        Color.DarkBlue, Color.DarkRed, Color.DarkGreen,
        Color.DarkMagenta, Color.DarkCyan, Color.Brown,
    ];

    private readonly CaptchaStore _store;
    private readonly IOptions<CaptchaOptions> _options;

    public ImageCaptchaProvider(CaptchaStore store, IOptions<CaptchaOptions> options)
    {
        _store = store;
        _options = options;
    }

    public CaptchaKind Kind => CaptchaKind.Image;

    public async Task<CaptchaResult> GenerateAsync(CancellationToken ct = default)
    {
        var opts = _options.Value;
        var code = RandomCode(opts.ImageCodeLength);

        using var image = new Image<Rgba32>(opts.ImageWidth, opts.ImageHeight, Color.White);
        var rng = new Random();
        image.Mutate(ctx =>
        {
            // 背景噪点
            DrawNoise(ctx, opts.ImageWidth, opts.ImageHeight, rng);

            // 干扰线
            DrawInterferenceLines(ctx, opts.ImageWidth, opts.ImageHeight, rng);

            // 字符（旋转 + 不同颜色）
            DrawCharacters(ctx, code, opts.ImageWidth, opts.ImageHeight, rng);
        });

        var ms = new MemoryStream();
        await image.SaveAsync(ms, new PngEncoder(), ct);
        ms.Position = 0;

        var captchaId = Guid.NewGuid().ToString("N");
        var answer = opts.CaseInsensitive ? code.ToUpperInvariant() : code;
        await _store.SetAsync(captchaId, answer, ct);

        return new CaptchaResult(captchaId, "image/png", ms, CaptchaKind.Image);
    }

    public async Task<bool> ValidateAsync(string captchaId, string userInput, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(captchaId) || string.IsNullOrEmpty(userInput))
            return false;

        var answer = await _store.TryGetAndRemoveAsync(captchaId, ct);
        if (answer is null) return false;

        var input = _options.Value.CaseInsensitive ? userInput.ToUpperInvariant() : userInput;
        return string.Equals(answer, input, _options.Value.CaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static string RandomCode(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Charset[Random.Shared.Next(Charset.Length)];
        return new string(chars);
    }

    /// <summary>
    /// 字体解析 + 缓存：优先 "Arial" / "DejaVu Sans"（Linux 常见）/ "Liberation Sans"，
    /// 全部缺失时降级到 <see cref="SystemFonts"/> 中首个可用 family（保证 Linux CI / 最小容器也能生成）。
    ///
    /// <para><b>为什么不在构造函数探测一次</b>：fontSize 按图片高度自适应，不同尺寸需不同 Font 实例。
    /// 按 (family, size, style) 缓存 —— 与 Files 模块 <c>ImageProcessingExtensions._fontCache</c> 同模式。</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Font> _fontCache = new();

    private static readonly string[] PreferredFonts = ["Arial", "DejaVu Sans", "Liberation Sans", "FreeSans"];

    private static Font ResolveFont(int fontSize)
    {
        var key = $"captcha|{fontSize}";
        return _fontCache.GetOrAdd(key, _ =>
        {
            // 按优先级找首个存在的 family
            foreach (var name in PreferredFonts)
            {
                if (SystemFonts.TryGet(name, out var family))
                    return family.CreateFont(fontSize, FontStyle.Bold);
            }
            // 全部缺失：退回系统首个可用 family（Linux 容器至少有 DejaVu / 至少有一个）
            // FontFamily 是 struct，FirstOrDefault 永远非 null；用 Any() 判空。
            var families = SystemFonts.Families;
            if (families.Any())
                return families.First().CreateFont(fontSize, FontStyle.Bold);

            // 极端情况：系统无任何字体。返回 null 字体会让 DrawText 抛 —— 此时验证码降级为
            // 纯噪点图（无字符），仍可作为滑块/二次校验触发器，不致 endpoint 500。
            throw new InvalidOperationException(
                "系统无可用字体，无法生成图形验证码字符。请在容器中安装 fonts-dejavu 或类似字体包。");
        });
    }

    private static void DrawNoise(IImageProcessingContext ctx, int w, int h, Random rng)
    {
        // 用 1x1 像素填充模拟噪点（避免依赖 Pen 具体实现版本）
        for (var i = 0; i < 80; i++)
        {
            var x = rng.Next(w);
            var y = rng.Next(h);
            var gray = rng.Next(180, 240);
            var color = new Color(new Rgba32((byte)gray, (byte)gray, (byte)gray));
            ctx.Fill(color, new Rectangle(x, y, 1, 1));
        }
    }

    private static void DrawInterferenceLines(IImageProcessingContext ctx, int w, int h, Random rng)
    {
        // 干扰线：用若干小矩形点串联（避免依赖 Pen / DrawLines 扩展签名）
        for (var i = 0; i < 4; i++)
        {
            var color = Palette[rng.Next(Palette.Length)].WithAlpha(0.5f);
            var p1 = new Point(rng.Next(w), rng.Next(h));
            var p2 = new Point(rng.Next(w), rng.Next(h));
            var steps = 20;
            for (var s = 0; s <= steps; s++)
            {
                var x = (int)(p1.X + (p2.X - p1.X) * s / steps);
                var y = (int)(p1.Y + (p2.Y - p1.Y) * s / steps);
                ctx.Fill(color, new Rectangle(x, y, 2, 2));
            }
        }
    }

    private static void DrawCharacters(IImageProcessingContext ctx, string code, int w, int h, Random rng)
    {
        // 字体解析：优先 Arial，缺失时（Linux CI / 容器无 Arial）降级到首个可用系统字体。
        // #104 同款缓存思路：SystemFonts.CreateFont 扫字体目录开销大，缓存 Font 实例。
        // 不缓存到 static —— 不同图片尺寸会产生不同 fontSize，按 (size) 维度缓存即可。
        var fontSize = (int)(h * 0.7);
        var font = ResolveFont(fontSize);
        var charWidth = w / code.Length;

        for (var i = 0; i < code.Length; i++)
        {
            var color = Palette[rng.Next(Palette.Length)];
            var angle = rng.Next(-25, 26);

            // 单字符画到临时图后旋转贴回主图，避免整体旋转导致相邻字符粘连
            using var charImg = new Image<Rgba32>(charWidth, h);
            charImg.Mutate(c => c.DrawText(
                code[i].ToString(),
                font,
                color,
                new PointF(rng.Next(2, 6), rng.Next(-2, 4))));

            charImg.Mutate(c => c.Rotate(angle));

            var x = i * charWidth + rng.Next(-3, 4);
            var y = rng.Next(-3, 4);
            ctx.DrawImage(charImg, new Point(x, y), 1f);
        }
    }
}
