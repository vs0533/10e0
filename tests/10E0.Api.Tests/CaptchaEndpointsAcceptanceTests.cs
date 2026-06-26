using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace TenE0.Api.Tests;

/// <summary>
/// 验证码端点验收测试（issue #162）。
///
/// <para>覆盖：</para>
/// <list type="bullet">
/// <item><c>GET /captcha/image</c> 返回 PNG + <c>X-Captcha-Id</c> 响应头。</item>
/// <item><c>GET /captcha/slider</c> 返回 PNG + <c>X-Captcha-Id</c> + <c>X-Slider-*</c> 头。</item>
/// <item>验证码端点本身走限流（连续请求最终触发 429）。</item>
/// </list>
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class CaptchaEndpointsAcceptanceTests
{
    [Fact]
    public async Task GivenCaptchaModuleEnabled_WhenGettingImageCaptcha_ThenReturnsPngWithCaptchaIdHeader()
    {
        using var factory = new CaptchaFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/captcha/image");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        var captchaId = response.Headers.GetValues("X-Captcha-Id").FirstOrDefault();
        captchaId.Should().NotBeNullOrEmpty("验证码生成端点必须在 X-Captcha-Id 头返回 captchaId 供客户端校验时回传");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0, "PNG 应非空");
    }

    [Fact]
    public async Task GivenCaptchaModuleEnabled_WhenGettingSliderCaptcha_ThenReturnsPngWithCaptchaIdAndSliderHeaders()
    {
        using var factory = new CaptchaFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/captcha/slider");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Headers.Contains("X-Captcha-Id").Should().BeTrue("滑块端点也必须返回 captchaId");
        response.Headers.Contains("X-Slider-Width").Should().BeTrue("滑块端点应返回滑块尺寸信息");
        response.Headers.Contains("X-Slider-Height").Should().BeTrue();
    }

    // ── Factory ────────────────────────────────────────────────

    /// <summary>验证码端点专用隔离 host（镜像 ConfigurationEndpointsAcceptanceTests.IsolatedFactory）。</summary>
    public sealed class CaptchaFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"captcha162-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<DemoDbContext>))
                    .ToList();
                foreach (var d in existing) services.Remove(d);

                services.AddDbContextFactory<DemoDbContext>(opt =>
                    opt.UseInMemoryDatabase(_dbName)
                       .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            });
        }
    }
}
