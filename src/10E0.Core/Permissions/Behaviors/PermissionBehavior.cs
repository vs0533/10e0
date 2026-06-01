using System.Reflection;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Permissions.Behaviors;

/// <summary>
/// 命令管道权限拦截行为。
///
/// 在命令 Handler 执行前检查 <see cref="RequirePermissionAttribute"/>：
/// - 未登录 → 抛 <see cref="PermissionDeniedException"/>
/// - 已登录但权限不足 → 同上
/// - 一个命令多个 RequirePermission attribute → AND 语义
/// - 单个 attribute 内多个 key → OR 语义
///
/// 替代旧 CUDValidator 在 SaveChanges 之前做的实体级检查 — 但更早、更声明式。
/// 字段级检查在三期 3.2（EntityService 钩子）实现。
/// </summary>
public sealed class PermissionBehavior<TCommand, TResult>(
    IPermissionEvaluator evaluator) : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    // 每个命令类型只反射一次 attribute，命中缓存
    private static readonly IReadOnlyList<RequirePermissionAttribute> RequiredAttrs =
        typeof(TCommand).GetCustomAttributes<RequirePermissionAttribute>(inherit: false).ToList();

    public async Task<TResult> HandleAsync(
        TCommand command,
        CommandHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
    {
        if (RequiredAttrs.Count == 0)
            return await next(cancellationToken);

        foreach (var attr in RequiredAttrs)
        {
            // 单个 attribute 内多个 key 走 Any（OR），多个 attribute 走 All（AND）
            var ok = await evaluator.HasAnyAsync(attr.PermissionKeys, cancellationToken);
            if (!ok)
                throw new PermissionDeniedException(typeof(TCommand).Name, attr.PermissionKeys);
        }

        return await next(cancellationToken);
    }
}

/// <summary>
/// 权限拒绝异常。建议在 API 层捕获并转换为 403 响应。
/// </summary>
public sealed class PermissionDeniedException(string commandName, IReadOnlyList<string> requiredKeys)
    : Exception($"权限不足：命令 {commandName} 需要以下权限之一：{string.Join(" / ", requiredKeys)}")
{
    public string CommandName { get; } = commandName;
    public IReadOnlyList<string> RequiredKeys { get; } = requiredKeys;
}
