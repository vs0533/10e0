namespace TenE0.Core.ApiVersioning;

/// <summary>
/// API 版本化模块配置（#163）。
/// </summary>
/// <remarks>
/// 默认策略为「版本透明」：未指定版本的请求按 <see cref="DefaultMajorVersion"/>.<see cref="DefaultMinorVersion"/>
/// 处理，保证既有裸路由端点（如 <c>/demo</c>）引入版本化后行为零变化（向后兼容）。
/// 同时支持 URL segment（<c>/v1/demo</c>）、query string（<c>?api-version=1.0</c>）、
/// header（<c>X-Api-Version: 1.0</c>）三种版本声明方式并存。
/// </remarks>
public sealed class ApiVersioningOptions
{
    /// <summary>
    /// 默认主版本号。未在请求中声明版本时按此值（与 <see cref="DefaultMinorVersion"/>）解析。
    /// 配合 <see cref="AssumeDefaultVersionWhenUnspecified"/> 实现向后兼容。
    /// </summary>
    public int DefaultMajorVersion { get; set; } = 1;

    /// <summary>
    /// 默认次版本号。未在请求中声明版本时按此值（与 <see cref="DefaultMajorVersion"/>）解析。
    /// </summary>
    public int DefaultMinorVersion { get; set; } = 0;

    /// <summary>
    /// 请求未携带版本信息时是否按默认版本处理。默认 <c>true</c>，保证既有裸路由向后兼容。
    /// 设为 <c>false</c> 则未声明版本的请求会被拒绝（强制客户端显式声明版本）。
    /// </summary>
    public bool AssumeDefaultVersionWhenUnspecified { get; set; } = true;

    /// <summary>
    /// 是否在响应头 <c>api-supported-versions</c> 中通告支持的全部版本，便于客户端探测升级路径。默认 <c>true</c>。
    /// </summary>
    public bool ReportApiVersions { get; set; } = true;
}
