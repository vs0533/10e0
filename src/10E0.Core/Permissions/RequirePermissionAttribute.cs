namespace TenE0.Core.Permissions;

/// <summary>
/// 命令上声明执行所需的权限。<see cref="Behaviors.PermissionBehavior{TCommand, TResult}"/> 在管道入口前置检查。
///
/// 同一命令可标注多次表示"全部需要"（AND 语义）。多个 key 写在一个 attribute 里走 OR 语义。
///
/// 替代旧 BaseHandler 内联 AuthCUD 检查 + CUDValidator 后置检查，将策略从 handler 内迁到声明式。
/// </summary>
/// <example>
/// <code>
/// [RequirePermission(DemoPermissions.Create)]
/// public sealed record CreateDemoCommand(string Name) : ICommand&lt;string&gt;;
///
/// // 任一权限即可
/// [RequirePermission(DemoPermissions.View, DemoPermissions.Update)]
/// public sealed record ViewDemoCommand(string Id) : IQuery&lt;DemoView&gt;;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class RequirePermissionAttribute(params string[] permissionKeys) : Attribute
{
    /// <summary>本次声明要求的权限 key 集合（OR 语义，任一满足即可）。</summary>
    public IReadOnlyList<string> PermissionKeys { get; } = permissionKeys;
}
