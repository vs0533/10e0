using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Certificate;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// Certificate 模块的 DI 注册扩展（issue #185）。
///
/// <para>
/// 一次性注册以下组件（调用方无需额外步骤）：
/// <list type="bullet">
/// <item><see cref="ICertificateService"/> / <see cref="CertificateService{TContext}"/>：渲染 + 落库 + 查询。</item>
/// <item><see cref="ICertificateRenderer"/>：渲染器抽象。默认注册 <c>NullCertificateRenderer</c>（占位）——
/// 渲染时抛"请引用独立包"明确异常。引用独立 NuGet 包 <c>TenE0.Core.Certificate</c> 后调
/// <c>AddTenE0PdfCertificateRenderer()</c> Replace 为 <c>PdfCertificateRenderer</c>（PDFsharp）。
/// 业务方也可注册自定义渲染器覆盖。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>依赖 Files 模块</b>：证书存储复用 <c>IFileService</c>。装配期不强制校验（避免启动顺序耦合），
/// 运行期 <see cref="CertificateService{TContext}"/> 解析 IFileService 失败时抛明确异常。
/// 用 <c>AddTenE0All</c> 时建议同时 <c>opt.Files = true</c>。
/// </para>
/// </summary>
public static class CertificateExtensions
{
    /// <summary>
    /// 注册 Certificate 模块。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="configure">选项回调；不传用默认值。</param>
    /// <typeparam name="TContext">承载证书表的 EF Core DbContext 类型。</typeparam>
    public static IServiceCollection AddTenE0Certificate<TContext>(
        this IServiceCollection services,
        Action<CertificateOptions>? configure = null)
        where TContext : DbContext
    {
        // 配置 options：业务方 callback 透传。
        services.AddOptions<CertificateOptions>()
            .Configure(configure ?? (_ => { }));

        // 证书服务：Scoped（每请求一个 scope，注入 IDbContextFactory + 渲染器 + IFileService）。
        services.TryAddScoped<ICertificateService, CertificateService<TContext>>();

        // 渲染器抽象：默认 NullCertificateRenderer 占位。
        // 主包零 PDF 依赖 —— 引用独立包 10E0.Core.Certificate 后 Replace 为 PdfCertificateRenderer。
        // TryAdd 让业务方的自定义渲染器或独立包的 AddTenE0PdfCertificateRenderer() 都能覆盖。
        services.TryAddScoped<ICertificateRenderer, NullCertificateRenderer>();

        return services;
    }
}
