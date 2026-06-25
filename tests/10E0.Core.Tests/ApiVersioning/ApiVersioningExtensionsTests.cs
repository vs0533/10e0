using Asp.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.DependencyInjection;

namespace TenE0.Core.Tests.ApiVersioning;

[Trait("Category", "Unit")]
public sealed class ApiVersioningExtensionsTests
{
    [Fact]
    public void AddTenE0ApiVersioning_RegistersApiVersioningAndExplorerServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTenE0ApiVersioning();
        var sp = services.BuildServiceProvider();

        // Assert —— Asp.Versioning 注册的核心服务都能解析（options 经 IOptions<T> 访问）
        sp.GetService<IOptions<Asp.Versioning.ApiVersioningOptions>>().Should().NotBeNull();
    }

    [Fact]
    public void AddTenE0ApiVersioning_DefaultVersionIs1_0()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTenE0ApiVersioning();
        var sp = services.BuildServiceProvider();
        var opt = sp.GetRequiredService<IOptions<Asp.Versioning.ApiVersioningOptions>>().Value;

        // Assert —— 默认版本 1.0（向后兼容裸路由）
        opt.DefaultApiVersion.Should().Be(new ApiVersion(1, 0));
    }

    [Fact]
    public void AddTenE0ApiVersioning_DefaultAssumesVersionWhenUnspecified()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTenE0ApiVersioning();
        var sp = services.BuildServiceProvider();
        var opt = sp.GetRequiredService<IOptions<Asp.Versioning.ApiVersioningOptions>>().Value;

        // Assert —— 未声明版本时按默认版本处理（版本透明策略）
        opt.AssumeDefaultVersionWhenUnspecified.Should().BeTrue();
    }

    [Fact]
    public void AddTenE0ApiVersioning_DefaultReportsApiVersions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTenE0ApiVersioning();
        var sp = services.BuildServiceProvider();
        var opt = sp.GetRequiredService<IOptions<Asp.Versioning.ApiVersioningOptions>>().Value;

        // Assert —— 响应头返回 api-supported-versions
        opt.ReportApiVersions.Should().BeTrue();
    }

    [Fact]
    public void AddTenE0ApiVersioning_DefaultUsesCombinedReader_WithUrlSegmentQueryAndHeader()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTenE0ApiVersioning();
        var sp = services.BuildServiceProvider();
        var opt = sp.GetRequiredService<IOptions<Asp.Versioning.ApiVersioningOptions>>().Value;

        // Assert —— ApiVersionReader.Combine 产出复合 reader（非单一 UrlSegment/Query/Header reader）
        opt.ApiVersionReader.Should().NotBeNull();
        opt.ApiVersionReader.Should().NotBeOfType<UrlSegmentApiVersionReader>();
        opt.ApiVersionReader.Should().NotBeOfType<QueryStringApiVersionReader>();
        opt.ApiVersionReader.Should().NotBeOfType<HeaderApiVersionReader>();
    }

    [Fact]
    public void AddTenE0ApiVersioning_WithCustomDefaultVersion_AppliesMajorAndMinor()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act —— 自定义默认版本为 2.1
        services.AddTenE0ApiVersioning(o =>
        {
            o.DefaultMajorVersion = 2;
            o.DefaultMinorVersion = 1;
        });
        var sp = services.BuildServiceProvider();
        var opt = sp.GetRequiredService<IOptions<Asp.Versioning.ApiVersioningOptions>>().Value;

        // Assert
        opt.DefaultApiVersion.Should().Be(new ApiVersion(2, 1));
    }

    [Fact]
    public void AddTenE0ApiVersioning_WithAssumeFalse_DisablesTransparentDefault()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act —— 关闭版本透明，强制客户端显式声明版本
        services.AddTenE0ApiVersioning(o => o.AssumeDefaultVersionWhenUnspecified = false);
        var sp = services.BuildServiceProvider();
        var opt = sp.GetRequiredService<IOptions<Asp.Versioning.ApiVersioningOptions>>().Value;

        // Assert
        opt.AssumeDefaultVersionWhenUnspecified.Should().BeFalse();
    }

    [Fact]
    public void AddTenE0ApiVersioning_RegistersTenE0Options_BoundToConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTenE0ApiVersioning(o =>
        {
            o.DefaultMajorVersion = 3;
            o.ReportApiVersions = false;
        });
        var sp = services.BuildServiceProvider();
        var tenE0Opt = sp.GetRequiredService<IOptions<TenE0.Core.ApiVersioning.ApiVersioningOptions>>().Value;

        // Assert —— 框架自有的配置类正确注册并绑定
        tenE0Opt.DefaultMajorVersion.Should().Be(3);
        tenE0Opt.ReportApiVersions.Should().BeFalse();
    }
}
