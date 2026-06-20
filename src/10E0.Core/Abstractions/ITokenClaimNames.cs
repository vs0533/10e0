namespace TenE0.Core.Abstractions;

/// <summary>
/// JWT / Token claim 名称的抽象注入点（#37 Part 1）。
///
/// 解决 Core 早期把 claim 名（"sub" / "name" / "role" / "user_type" /
/// "role_versions" / "tenant_id"）硬编在 <see cref="JwtClaims"/> 静态类里——
/// 换 IdP（Keycloak / Auth0 / 企业 SAML）时业务方必须改 Core 源码。
/// 注入 <see cref="ITokenClaimNames"/> 即可整体替换，业务项目零源码改动。
///
/// 生命周期：Singleton（纯只读字符串映射，无状态）。
/// 替换示例：
/// <code>
/// services.Replace(ServiceDescriptor.Singleton&lt;ITokenClaimNames, MyKeycloakClaimNames&gt;());
/// </code>
/// </summary>
public interface ITokenClaimNames
{
    /// <summary>主体（用户唯一标识）的 claim 名。默认 "sub"。</summary>
    string Subject { get; }

    /// <summary>显示名（user principal name）的 claim 名。默认 "name"。</summary>
    string Name { get; }

    /// <summary>角色 claim 名。默认 "role"。</summary>
    string Role { get; }

    /// <summary>用户类型（person / unit）的 claim 名。默认 "user_type"。</summary>
    string UserType { get; }

    /// <summary>角色版本快照（#7 instant permission revocation）的 claim 名。默认 "role_versions"。</summary>
    string RoleVersion { get; }

    /// <summary>租户 ID（#11 multi-tenancy）的 claim 名。默认 "tenant_id"。</summary>
    string TenantId { get; }
}

/// <summary>
/// <see cref="ITokenClaimNames"/> 的默认实现。
///
/// 类名刻意定为 <c>JwtClaimsTokenClaimNames</c>（而非 <c>DefaultTokenClaimNames</c>）
/// —— 强调"基于遗留 <see cref="JwtClaims"/> 常量"，便于排查时一眼识别零改造路径。
///
/// 各属性值与 <see cref="JwtClaims"/> 静态常量逐字一致，保持向后兼容：
/// <c>Subject="sub"</c> / <c>Name="name"</c> / <c>Role="role"</c> /
/// <c>UserType="user_type"</c> / <c>RoleVersion="role_versions"</c> /
/// <c>TenantId="tenant_id"</c>。
/// </summary>
public sealed class JwtClaimsTokenClaimNames : ITokenClaimNames
{
    /// <inheritdoc />
    public string Subject => "sub";

    /// <inheritdoc />
    public string Name => "name";

    /// <inheritdoc />
    public string Role => "role";

    /// <inheritdoc />
    public string UserType => "user_type";

    /// <inheritdoc />
    public string RoleVersion => "role_versions";

    /// <inheritdoc />
    public string TenantId => "tenant_id";
}
