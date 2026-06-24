namespace TenE0.Core.Configuration;

/// <summary>
/// 系统参数存储 — Key-Value 类型化读取 + 运行时修改，带多级缓存与变更通知。
///
/// <para>
/// 约束（Issue #153 决策点 2）：仅预定义 key（<see cref="ISystemParameterDefinition"/>）的值可被
/// <see cref="SetAsync"/> 修改；<c>IsReadOnly=true</c> 的参数拒绝运行时修改。
/// 写操作顺序：<c>SaveChangesAsync</c> → 失效该 key 缓存 → 派发 <see cref="SystemParameterChangedEvent"/>。
/// </para>
/// </summary>
public interface ISystemParameterStore
{
    /// <summary>类型化读取参数值；未命中 DB 或转换失败返回 <paramref name="defaultValue"/>。</summary>
    /// <typeparam name="T">目标类型（String/Int/Bool/Decimal/Json 反序列化目标）。</typeparam>
    /// <param name="key">参数 Key（无需预定义即可读 —— 兼容 DB 中已有但注册表漏登的历史 key）。</param>
    /// <param name="defaultValue">缺省值。</param>
    Task<T?> GetAsync<T>(string key, T? defaultValue = default, CancellationToken cancellationToken = default);

    /// <summary>修改参数值。key 必须在 <see cref="SystemParameterRegistry"/> 中预定义；只读参数抛异常。</summary>
    /// <exception cref="InvalidOperationException">key 未定义或参数为只读。</exception>
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>按分组获取参数列表（隐藏参数的 Value 是否脱敏由调用方决定）。</summary>
    Task<IReadOnlyList<SystemParameterDto>> GetByGroupAsync(string group, CancellationToken cancellationToken = default);
}
