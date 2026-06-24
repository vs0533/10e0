namespace TenE0.Core.Configuration;

/// <summary>
/// Configuration 模块（数据字典 + 系统参数）可选项。
/// 镜像 <c>WorkflowRuntimeOptions</c> 的简单 Options 范式。
/// </summary>
public sealed class ConfigurationOptions
{
    /// <summary>数据字典选项列表缓存 L2 过期时间（写入低频，默认 10 分钟）。</summary>
    public TimeSpan DictCacheL2 { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>系统参数值缓存 L2 过期时间（写入低频，默认 10 分钟）。</summary>
    public TimeSpan ParamCacheL2 { get; set; } = TimeSpan.FromMinutes(10);
}
