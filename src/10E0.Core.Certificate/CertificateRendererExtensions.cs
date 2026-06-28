using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TenE0.Core.Certificate.Pdf;

/// <summary>
/// Certificate PDF 渲染器的 DI 注册扩展（issue #185）。
///
/// <para>
/// 用法：先 <c>AddTenE0Certificate&lt;TContext&gt;()</c>（注册 ICertificateService + 占位渲染器），
/// 再 <c>AddTenE0PdfCertificateRenderer()</c> <b>Replace</b> 为 PDFsharp 渲染器 —— 业务代码零改动。
/// </para>
/// <code>
/// builder.Services.AddTenE0Certificate&lt;AppDbContext&gt;();
/// builder.Services.AddTenE0PdfCertificateRenderer();   // Replace 为 PDFsharp 渲染器
/// </code>
/// </summary>
public static class CertificateRendererExtensions
{
    /// <summary>
    /// 把 <see cref="ICertificateRenderer"/> 替换为 <see cref="PdfCertificateRenderer"/>（PDFsharp）。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    public static IServiceCollection AddTenE0PdfCertificateRenderer(
        this IServiceCollection services)
    {
        // Replace 而非 TryAdd —— 显式覆盖 AddTenE0Certificate 注册的占位 NullCertificateRenderer。
        // Scoped 与 ICertificateService 现有注册生命周期一致。
        services.Replace(ServiceDescriptor.Scoped<ICertificateRenderer, PdfCertificateRenderer>());
        return services;
    }

    /// <summary>
    /// 重载：从 <see cref="IConfiguration"/> 的 <c>"Certificate"</c> 段绑定 <see cref="CertificateOptions"/>，
    /// 再叠加可选回调。便于从 appsettings.json 配置 DefaultFont / SequenceKey 等。
    /// </summary>
    public static IServiceCollection AddTenE0PdfCertificateRenderer(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<CertificateOptions>? configure = null)
    {
        services.Configure<CertificateOptions>(configuration.GetSection("Certificate"));
        if (configure is not null)
            services.Configure(configure);

        services.Replace(ServiceDescriptor.Scoped<ICertificateRenderer, PdfCertificateRenderer>());
        return services;
    }
}
