using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Common;

namespace TenE0.Core.Permissions;

/// <summary>
/// ASP.NET Core <see cref="IAuthorizationHandler"/>：把 <see cref="IPermissionEvaluator"/>
/// 接到 Authorization Policy 上，让 Minimal API 端点能用 <c>[Authorize(Policy="perm.admin")]</c>
/// 走同一套权限评估（含 super_admin bypass + 版本号过期检测）。
///
/// <para>
/// 设计动机：CQRS 命令走 <see cref="Behaviors.PermissionBehavior{TCommand,TResult}"/>；
/// 但 Minimal API 直挂的 endpoint（如 <c>/admin/outbox</c>）不经过 dispatcher，
/// 必须依赖 Authorization middleware 在 HTTP 边界拦截。
/// </para>
///
/// <para>
/// #119：<c>/admin/outbox</c> 之前只挂了 <c>[Authorize]</c> 等同于"认证即可"，
/// 任何登录用户（含 viewer/editor）都能拉到 Outbox Payload；现在端点改挂
/// <c>[Authorize(Policy = PermissionPolicies.Admin)]</c>，本 handler 调用
/// <c>IPermissionEvaluator.HasAsync("perm.admin")</c>：
/// </para>
/// <list type="bullet">
///   <item>未认证 → <see cref="IPermissionEvaluator.HasAsync"/> 立即返回 false → 401</item>
///   <item>已认证但角色无 perm.admin（如 alice = viewer+editor）→ 403</item>
///   <item>角色含 perm.admin（manager / super_admin）→ 200</item>
///   <item>super_admin → <see cref="PermissionEvaluator"/> 短路返回 true → 200</item>
/// </list>
/// </summary>
public sealed class PermissionAuthorizationHandler(
    IPermissionEvaluator evaluator) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        // HasAsync 内部已含 IsAuthenticated + IsSuperUser + role-version 检查
        // — 不在此处再判 IsAuthenticated，避免重复短路逻辑。
        // CancellationToken 不通过 AuthorizationHandlerContext 传递：handler
        // 自身不引用 HttpContext（避免在 Core lib 引入 ASP.NET 强耦合）。
        if (await evaluator.HasAsync(requirement.PermissionKey))
        {
            context.Succeed(requirement);
        }
        // 不显式 context.Fail()：Authorization middleware 在 handler 未 Succeed 时
        // 会自然按 Forbidden() 处理，符合默认 AuthorizeAttribute 语义。
    }
}

/// <summary>
/// 单 permission key 的 Authorization 要求。<see cref="PermissionPolicies.Admin"/>
/// 用 <c>perm.admin</c> 实例化它；其他 endpoint 可按需扩展。
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permissionKey)
    {
        if (string.IsNullOrWhiteSpace(permissionKey))
            throw new ArgumentException("permissionKey 不能为空", nameof(permissionKey));
        PermissionKey = permissionKey;
    }

    /// <summary>被检查的权限 key（如 <c>"perm.admin"</c>）。</summary>
    public string PermissionKey { get; }
}

/// <summary>
/// 框架预置的 Authorization Policy 名常量。集中管理避免 endpoint 拼写漂移。
/// </summary>
public static class PermissionPolicies
{
    /// <summary>
    /// 后台管理（permissions / menus / data-filters / outbox 等）的统一门禁。
    /// 持有 <c>perm.admin</c> 的角色可访问；<c>super_admin</c> 自动 bypass。
    /// </summary>
    public const string Admin = "perm.admin";
}

/// <summary>
/// #119：JwtBearer OnForbidden 路径写 ApiResult&lt;T&gt; 信封，让 Authorization 失败的 403 响应
/// 也走与正常 4xx 错误一致的 JSON 形状（issue #39 统一响应壳）。
///
/// <para>
/// 默认 ASP.NET Core 在 Authorization 失败时 body 为空，客户端需要分别处理"空 body"
/// 和"JSON 信封"两个分支。集中到这个 writer 后，403 也走
/// <c>{"success":false,"errorCode":"PERM_DENIED","errorMessage":"..."}</c>。
/// </para>
///
/// <para>
/// 通过 <c>HttpContext.RequestServices</c> 解析 <see cref="IOptions{JsonOptions}"/>
/// 以拿到 host 配置的 <see cref="JsonSerializerOptions"/> —— 与
/// <c>TenE0ExceptionHandler</c> 同一来源（issue #49 一致性）。
/// </para>
/// </summary>
internal static class ForbiddenResponseWriter
{
    /// <summary>
    /// 把当前 <paramref name="response"/> 改成 ApiResult JSON body。
    /// 调用方需自行设置 <c>StatusCode = 403</c>（OnForbidden 在调用此方法前已设置）。
    /// </summary>
    public static async Task WriteAsync(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var jsonOptions = response.HttpContext.RequestServices
            .GetService<IOptions<JsonOptions>>()?.Value;

        var body = ApiResult<object>.Fail("Permission denied.", code: "PERM_DENIED");

        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            response.Body,
            body,
            jsonOptions?.SerializerOptions,
            cancellationToken: response.HttpContext.RequestAborted);
    }
}
