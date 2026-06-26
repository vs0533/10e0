using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Events;

namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 状态机注册表 — 启动期一次性扫描 <see cref="IStateMachineDefinition"/> 实现并构建，
/// 运行时 O(1) 字典查找。
///
/// 引擎实例与 <see cref="IDomainEventDispatcher"/> 绑定（按 DI scope），故 Registry
/// 注册为 Scoped；定义本身是冻结的不可变对象，可安全共享。
/// </summary>
public interface IStateMachineRegistry
{
    /// <summary>获取指定 (TState, TAction) 的引擎实例。未注册抛 <see cref="InvalidOperationException"/>。</summary>
    StateMachine<TState, TAction> Get<TState, TAction>() where TState : notnull where TAction : notnull;

    /// <summary>查询是否注册了指定 (TState, TAction) 组合。</summary>
    bool IsRegistered<TState, TAction>() where TState : notnull where TAction : notnull;
}

internal sealed class StateMachineRegistry : IStateMachineRegistry
{
    private readonly IServiceProvider _services;
    // (typeof(TState), typeof(TAction)) → 已冻结的定义（缓存，避免重复获取）
    private readonly ConcurrentDictionary<(Type, Type), object> _definitions = new();
    // 引擎实例按 scope 缓存（含 event dispatcher 绑定）
    private readonly ConcurrentDictionary<(Type, Type), object> _engines = new();

    public StateMachineRegistry(IServiceProvider services, IEnumerable<IStateMachineDefinition> definitions)
    {
        _services = services;

        // 启动期一次性构建并冻结所有定义
        foreach (var def in definitions)
        {
            var key = (def.StateType, def.ActionType);
            _definitions[key] = def.Build();
        }
    }

    public StateMachine<TState, TAction> Get<TState, TAction>()
        where TState : notnull where TAction : notnull
    {
        var key = (typeof(TState), typeof(TAction));
        return (StateMachine<TState, TAction>)_engines.GetOrAdd(key, _ =>
        {
            if (!_definitions.TryGetValue(key, out var defObj))
                throw new InvalidOperationException(
                    $"未注册状态机定义：({typeof(TState).Name}, {typeof(TAction).Name})。" +
                    "请实现 IStateMachineDefinition<TState,TAction> 并通过 AddTenE0WorkflowStateMachine<TContext>() 注册。");

            var dispatcher = _services.GetService<IDomainEventDispatcher>();
            var definition = (StateMachineDefinition<TState, TAction>)defObj;
            return StateMachine.Create(definition, dispatcher!);
        });
    }

    public bool IsRegistered<TState, TAction>()
        where TState : notnull where TAction : notnull
        => _definitions.ContainsKey((typeof(TState), typeof(TAction)));
}
