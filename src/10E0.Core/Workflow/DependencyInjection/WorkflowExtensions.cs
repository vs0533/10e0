using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Workflow.Definitions;
using TenE0.Core.Workflow.Runtime;
using TenE0.Core.Workflow.StateMachine;

namespace TenE0.Core.Workflow.DependencyInjection;

/// <summary>
/// 工作流模块 DI 扩展方法。
///
/// 三个子模块按需组合（与项目其他 <c>AddTenE0Xxx&lt;TContext&gt;()</c> 一致）：
/// <list type="bullet">
/// <item><see cref="AddTenE0WorkflowStateMachine"/> — #157 状态机（独立）</item>
/// <item><see cref="AddTenE0WorkflowDefinitions{TContext}"/> — #158 流程定义（依赖 #157）</item>
/// <item><see cref="AddTenE0WorkflowRuntime{TContext}"/> — #159 流程运行时（依赖 #157+#158）</item>
/// </list>
/// </summary>
public static class WorkflowExtensions
{
    /// <summary>
    /// 注册状态机引擎（#157）。
    ///
    /// 扫描 <paramref name="assembly"/> 中所有 <see cref="IStateMachineDefinition"/> 实现，
    /// 启动期构建冻结，运行时通过 <see cref="IStateMachineRegistry"/> O(1) 查找。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="assembly">扫描程序集（通常是业务入口程序集）。</param>
    public static IServiceCollection AddTenE0WorkflowStateMachine(
        this IServiceCollection services,
        Assembly assembly)
    {
        // 扫描并注册每个 IStateMachineDefinition 实现（同时作为非泛型 IStateMachineDefinition 注册）
        foreach (var type in assembly.GetTypes()
                     .Where(t => !t.IsAbstract && !t.IsInterface)
                     .Where(t => typeof(IStateMachineDefinition).IsAssignableFrom(t)))
        {
            services.TryAddScoped(type);
            services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IStateMachineDefinition), type));
        }

        services.TryAddScoped<IStateMachineRegistry, StateMachineRegistry>();
        return services;
    }

    /// <summary>
    /// 注册流程定义模块（#158）。前置：先调用 <see cref="AddTenE0WorkflowStateMachine"/>。
    /// TContext 需包含 TenE0ProcessDefinition 表（由 TenE0SystemDbContext 自动接入）。
    /// </summary>
    public static IServiceCollection AddTenE0WorkflowDefinitions<TContext>(
        this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<IProcessDefinitionStore, ProcessDefinitionStore<TContext>>();
        services.TryAddSingleton<IProcessDefinitionValidator, ProcessDefinitionValidator>();

        // 内置 AssigneeResolver 注册（按 AssigneePolicyKind 匹配）
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAssigneeResolver, RoleAssigneeResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAssigneeResolver, ManagerAssigneeResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAssigneeResolver, UserAssigneeResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAssigneeResolver, ExpressionAssigneeResolver>());

        return services;
    }

    /// <summary>
    /// 注册流程运行时（#159）。前置：先调用 <see cref="AddTenE0WorkflowStateMachine"/> +
    /// <see cref="AddTenE0WorkflowDefinitions{TContext}"/>。
    /// TContext 需包含运行时表（TenE0ProcessInstance/Task/History，由 TenE0SystemDbContext 自动接入）。
    /// </summary>
    public static IServiceCollection AddTenE0WorkflowRuntime<TContext>(
        this IServiceCollection services,
        Action<WorkflowRuntimeOptions>? configure = null)
        where TContext : DbContext
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<WorkflowRuntimeOptions>();

        // Engine + Services（Scoped，按请求解析 IAssigneeResolver）
        services.TryAddScoped<IWorkflowEngine, WorkflowEngine<TContext>>();
        services.TryAddScoped<IProcessRuntimeService, ProcessRuntimeService<TContext>>();
        services.TryAddScoped<ITaskService, TaskService<TContext>>();

        // Action handlers（Scoped，ProcessRuntimeService 注入 IEnumerable 解析）
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProcessActionHandler, ApproveActionHandler<TContext>>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProcessActionHandler, RejectActionHandler<TContext>>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProcessActionHandler, DelegateActionHandler<TContext>>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProcessActionHandler, AddSignerActionHandler<TContext>>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProcessActionHandler, RollbackActionHandler<TContext>>());

        // 超时后台处理器
        services.AddHostedService<TimeoutProcessor<TContext>>();

        return services;
    }
}
