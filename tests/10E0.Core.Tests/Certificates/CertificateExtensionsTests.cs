using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Certificate;
using TenE0.Core.DependencyInjection;

namespace TenE0.Core.Tests.Certificates;

/// <summary>
/// Certificate 模块 DI 注册测试（issue #185）：
/// 验证 <see cref="CertificateExtensions.AddTenE0Certificate{TContext}"/> 注册了 ICertificateService，
/// 且 ICertificateRenderer 默认为占位实现（NullCertificateRenderer 抛异常）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class CertificateExtensionsTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options);

    [Fact]
    public void AddTenE0Certificate_RegistersCertificateService_AndPlaceholderRenderer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenE0Certificate<TestDbContext>();

        // 通过服务描述符断言（不解析实例 —— CertificateService 需要 IDbContextFactory，DI 测试不注册它）。
        services.Should().Contain(s => s.ServiceType == typeof(ICertificateService));
        services.Should().Contain(s => s.ServiceType == typeof(ICertificateRenderer));

        // 占位渲染器注册（NullCertificateRenderer，internal —— 通过行为验证：Render 抛异常）。
        // 单独构造一个实例测（不经过 DI）。
        var sp = services.BuildServiceProvider();
        var renderer = sp.GetRequiredService<ICertificateRenderer>();
        renderer.Format.Should().Be("null");

        var act = () => renderer.RenderAsync(
            new CertificateDefinition("t", PaperKind.A4, CertificateOrientation.Portrait, []),
            new Dictionary<string, object?>(),
            CancellationToken.None);
        act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*未配置证书渲染器*");
    }

    [Fact]
    public void AddTenE0Certificate_OptionsCallback_AppliedToOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenE0Certificate<TestDbContext>(opt =>
        {
            opt.SequenceKey = "my-cert";
            opt.DefaultFont = "SimSun";
        });

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var opts = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CertificateOptions>>();
        opts.Value.SequenceKey.Should().Be("my-cert");
        opts.Value.DefaultFont.Should().Be("SimSun");
    }

    [Fact]
    public void TenE0Options_HasCertificateSwitch_DefaultFalse()
    {
        // 验证聚合选项上 Certificate 开关存在且默认 false（完全向后兼容）。
        var opt = new TenE0Options();
        opt.Certificate.Should().BeFalse();
        opt.CertificateOptions.Should().BeNull();
    }
}
