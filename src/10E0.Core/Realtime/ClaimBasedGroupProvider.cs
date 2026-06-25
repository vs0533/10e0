using System.Security.Claims;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Realtime;

/// <summary>
/// <see cref="IRealtimeGroupProvider"/> 默认实现（#155）—— 从 JWT claims 零 I/O 派生组。
///
/// 产出组（值缺省时安全跳过，不产出空名组）：
/// <list type="bullet">
/// <item><c>user:{sub}</c>：用户自身组 —— 推给"自己"时也走组（与 <c>Clients.User</c> 互补）。</item>
/// <item><c>role:{role}</c>：每个角色一个组（role 可多值）—— 按角色广播。</item>
/// <item><c>tenant:{tenant_id}</c>：租户组 —— 按租户广播（可空）。</item>
/// <item><c>org:{org}</c>：组织组 —— 按组织广播（org claim 值为 org 节点 Id，可空）。</item>
/// </list>
///
/// org 与 tenant 正交：org 是全局树（<c>TenE0Org</c> 不含 TenantId），二者互不蕴含。
/// 自定义组（如 <c>project:{id}</c>）由业务方替换本接口实现。
///
/// 生命周期：Singleton（无状态，纯 claims 读取）。
/// </summary>
public sealed class ClaimBasedGroupProvider(IOptions<RealtimeOptions> options) : IRealtimeGroupProvider
{
    private readonly RealtimeGroupPrefixes _prefixes = options.Value.GroupPrefixes;

    public IReadOnlyList<string> GetGroups(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var groups = new List<string>();

        // user:{sub}
        var userCode = user.FindFirstValue(JwtClaims.Subject);
        if (!string.IsNullOrWhiteSpace(userCode))
            groups.Add(_prefixes.User + userCode);

        // role:{role}（多值）
        foreach (var roleClaim in user.FindAll(JwtClaims.Role))
        {
            if (!string.IsNullOrWhiteSpace(roleClaim.Value))
                groups.Add(_prefixes.Role + roleClaim.Value);
        }

        // tenant:{tenant_id}
        var tenantId = user.FindFirstValue(JwtClaims.TenantId);
        if (!string.IsNullOrWhiteSpace(tenantId))
            groups.Add(_prefixes.Tenant + tenantId);

        // org:{org}（org 节点 Id；与 tenant 正交）
        var orgId = user.FindFirstValue(JwtClaims.Org);
        if (!string.IsNullOrWhiteSpace(orgId))
            groups.Add(_prefixes.Org + orgId);

        return groups;
    }
}
