namespace TenE0.Core.Cqrs;

/// <summary>
/// Pipeline Behavior 行为的可配置选项。
///
/// #41 引入 — 解决"集成测试想临时关掉权限检查只能 monkey patch evaluator"的脆弱性。
/// </summary>
public sealed class BehaviorOptions
{
    /// <summary>
    /// 当前运行环境。默认 "Production"。
    /// 与 <see cref="SkipBehaviorInTestEnvAttribute"/> 配合：
    /// 当 <see cref="Environment"/> == "Test" 时，标注了 <see cref="SkipBehaviorInTestEnvAttribute"/>
    /// （或 Scope 包含 "Test"）的 behavior 会被 CommandDispatcher 自动跳过。
    /// </summary>
    public string Environment { get; set; } = "Production";

    /// <summary>
    /// 在 Test 环境下需要跳过的 behavior 类型全名集合。
    /// 默认包含 <c>PermissionBehavior&lt;,&gt;</c>，所以测试环境开箱即用就跳过权限检查。
    ///
    /// 比 <see cref="SkipBehaviorInTestEnvAttribute"/> 更灵活：测试代码可以
    /// 在不修改生产代码的情况下，运行时追加要跳过的 behavior。
    /// </summary>
    public HashSet<string> DisabledInTest { get; } = new(StringComparer.Ordinal)
    {
        // 默认带 PermissionBehavior — 它是测试"我不在乎权限"场景最常被跳过的
        "TenE0.Core.Permissions.Behaviors.PermissionBehavior`2",
    };
}

/// <summary>
/// 标注于 <see cref="Abstractions.IPipelineBehavior{TCommand, TResult}"/> 实现类，
/// 在指定 Scope（默认 <c>"Test"</c>）环境下该 behavior 会被 CommandDispatcher 自动跳过。
///
/// 用途：把"测试环境不想跑的横切逻辑"以声明式表达出来，不用每次都去改 DI 注册。
/// </summary>
/// <example>
/// <code>
/// [SkipBehaviorInTestEnv]  // 测试环境跳过此 behavior
/// public sealed class MyBehavior&lt;TCommand, TResult&gt; : IPipelineBehavior&lt;TCommand, TResult&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SkipBehaviorInTestEnvAttribute : Attribute
{
    /// <summary>
    /// 跳过的作用域。常用值：
    /// <list type="bullet">
    ///   <item><c>"Test"</c>：仅当 <see cref="BehaviorOptions.Environment"/> 等于 <c>"Test"</c> 时跳过</item>
    ///   <item><c>"Production"</c>：仅生产跳过（罕见，多用于"实验性"行为）</item>
    ///   <item><c>"All"</c>：任何环境都跳过（用于临时禁用单个 behavior）</item>
    /// </list>
    /// </summary>
    public string Scope { get; }

    public SkipBehaviorInTestEnvAttribute(string scope = "Test")
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }
}
