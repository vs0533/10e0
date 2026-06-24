using TenE0.Core.Configuration.Storage;

namespace TenE0.Core.Configuration;

/// <summary>
/// 系统参数定义 — 声明一个合法的可配置 key 及其默认值/类型/分组。
///
/// <para>
/// 设计（Issue #153 决策点 2）：系统参数仅支持预定义 key 的值修改，运行时不可新增未定义 key，
/// 避免脏数据。业务项目实现此接口并在 DI 注册，启动期由 <see cref="SystemParameterRegistry"/> 收集。
/// Seeder 再据此把缺失的 key 落库（含默认值），保证 DB 与注册表一致。
/// </para>
///
/// <para>
/// 实现示例（业务侧）：
/// <code>
/// public sealed record PasswordMinLengthDefinition : ISystemParameterDefinition
/// {
///     public string Key =&gt; "password.min_length";
///     public string DefaultValue =&gt; "8";
///     public ParameterValueType ValueType =&gt; ParameterValueType.Int;
///     public string Group =&gt; "Security";
///     public string? Description =&gt; "密码最小长度";
///     public bool IsReadOnly =&gt; false;
/// }
/// </code>
/// </para>
/// </summary>
public interface ISystemParameterDefinition
{
    /// <summary>唯一 key，如 "password.min_length"。</summary>
    string Key { get; }

    /// <summary>默认值（字符串形式，按 <see cref="ValueType"/> 转换）。</summary>
    string DefaultValue { get; }

    /// <summary>值类型。</summary>
    ParameterValueType ValueType { get; }

    /// <summary>分组。</summary>
    string Group { get; }

    /// <summary>描述。</summary>
    string? Description { get; }

    /// <summary>是否运行时只读（关键参数锁定）。</summary>
    bool IsReadOnly { get; }
}

/// <summary>
/// 系统参数定义注册表 — 启动期收集所有 <see cref="ISystemParameterDefinition"/>，提供校验/查询。
/// 生命周期 Singleton（启动后只读）。注册到 DI 供 <see cref="SystemParameterStore{TContext}"/> 使用。
/// </summary>
public sealed class SystemParameterRegistry
{
    private readonly IReadOnlyDictionary<string, ISystemParameterDefinition> _byKey;

    public SystemParameterRegistry(IEnumerable<ISystemParameterDefinition> definitions)
    {
        _byKey = definitions.ToDictionary(d => d.Key, d => d, StringComparer.Ordinal);
    }

    /// <summary>当前已注册的全部参数定义。</summary>
    public IReadOnlyCollection<ISystemParameterDefinition> All => _byKey.Values.ToList();

    /// <summary>key 是否已定义。</summary>
    public bool IsDefined(string key) => _byKey.ContainsKey(key);

    /// <summary>获取 key 的定义；未定义返回 null。</summary>
    public ISystemParameterDefinition? GetDefinition(string key) =>
        _byKey.TryGetValue(key, out var def) ? def : null;
}
