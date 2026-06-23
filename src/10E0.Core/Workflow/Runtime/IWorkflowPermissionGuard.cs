namespace TenE0.Core.Workflow.Runtime;

/// <summary>
/// 工作流节点权限守卫 — 按<b>指定 actor</b>评估是否具备某权限 key。
///
/// 为什么不直接用 <c>IPermissionEvaluator.HasAsync</c>：
/// <list type="bullet">
/// <item><c>IPermissionEvaluator</c> 基于当前 HTTP 请求用户（<c>ICurrentUserContext</c>）</item>
/// <item>工作流的 actor 是显式参数（来自请求 / 后台超时处理器 / 测试），不一定有 HTTP 上下文</item>
/// <item>审批节点的 <c>PermissionKey</c> 应针对"执行操作的 actor"评估，而非"当前登录用户"</item>
/// </list>
///
/// Core 只依赖此抽象；Api 层实现内部复用 <c>IPermissionStore</c> + 角色映射查询。
/// </summary>
public interface IWorkflowPermissionGuard
{
    /// <summary>指定 actor 是否具备指定权限 key。</summary>
    /// <param name="actor">操作者用户编码。</param>
    /// <param name="permissionKey">权限 key。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true 具备；false 不具备。</returns>
    Task<bool> HasPermissionAsync(string actor, string permissionKey, CancellationToken ct = default);
}

/// <summary>
/// 默认实现：无权限系统时放行所有操作（保证不阻塞已配置 PermissionKey 的节点）。
///
/// 生产部署应在 Api 层用 <c>Replace</c> 覆盖为真正查 <c>IPermissionStore</c> 的实现。
/// </summary>
public sealed class NullWorkflowPermissionGuard : IWorkflowPermissionGuard
{
    public Task<bool> HasPermissionAsync(string actor, string permissionKey, CancellationToken ct = default)
        => Task.FromResult(true);
}
