using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt;
using TenE0.Core.Auth.Jwt.Services;

namespace TenE0.Core.Tests.Abstractions;

/// <summary>
/// BDD acceptance tests for #37 — Part 1: <c>ITokenClaimNames</c> abstraction.
///
/// 验证目标：
/// 1. 抽离 JWT claim 名为接口，业务方可 Replace 为自定义实现（如 Keycloak 风格的 `preferred_username` / `groups`）
/// 2. JwtTokenService 在签发时必须读 ITokenClaimNames 而非硬编码 const
/// 3. 默认实现必须保留所有现有常量值（向后兼容）
/// 4. 接口注册应支持 DI Replace 模式（业务方覆盖无需改 Core 源码）
///
/// 失败模式：未实现前，所有硬编码都在 JwtClaims 常量上，无法通过 DI 替换。
///
/// 设计说明：
/// - 用反射探测目标接口/类是否存在，编译通过
/// - JwtTokenService 构造签名变更（增加 ITokenClaimNames 参数）由 #37 实现者负责，
///   本测试通过 DI 容器验证注入点生效，而非直接 ctor 调用
/// </summary>
[Trait("Category", "BDD")]
public sealed class TokenClaimNamesAcceptanceTests
{
    private const string SigningKey = "test-signing-key-CHANGE-ME-must-be-at-least-32-bytes-long";

    private static readonly Assembly CoreAssembly = typeof(ErrorEntry).Assembly;

    private static Type? GetTokenClaimNamesInterface() =>
        CoreAssembly.GetType("TenE0.Core.Abstractions.ITokenClaimNames", throwOnError: false);

    private static Type? GetDefaultImpl() =>
        CoreAssembly.GetType("TenE0.Core.Abstractions.JwtClaimsTokenClaimNames", throwOnError: false);

    private static object CreateDefaultImpl() =>
        Activator.CreateInstance(GetDefaultImpl()!)!;

    private static string GetInterfaceProperty(object impl, string name)
    {
        var prop = impl.GetType().GetProperty(name)!;
        return (string)prop.GetValue(impl)!;
    }

    private static IJwtTokenService CreateTokenServiceThroughContainer(object? claimNamesImpl = null)
    {
        // 通过 DI 容器构造，#37 实现者把 ITokenClaimNames 接入 JwtTokenService 后此路径生效
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new JwtOptions
        {
            Issuer = "10e0-tests",
            Audience = "10e0-tests",
            SigningKey = SigningKey,
            AccessTokenLifetime = TimeSpan.FromMinutes(30),
            RefreshTokenLifetime = TimeSpan.FromDays(7),
        }));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new FakeTimeProvider());

        var iface = GetTokenClaimNamesInterface();
        if (iface is not null)
        {
            if (claimNamesImpl is not null)
                services.AddSingleton(iface, claimNamesImpl);
            else
                services.TryAddSingleton(iface, GetDefaultImpl()!);
        }

        services.AddScoped<IJwtTokenService, JwtTokenService>();

        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IJwtTokenService>();
    }

    // ── 接口与默认实现必须存在 ────────────────────────────────

    [Fact]
    public void GivenRefactor_WhenLoaded_ThenITokenClaimNamesInterfaceExists()
    {
        // Arrange + Act
        var iface = GetTokenClaimNamesInterface();

        // Assert
        iface.Should().NotBeNull(
            "#37 must introduce TenE0.Core.Abstractions.ITokenClaimNames interface for claim name injection");
        iface!.IsInterface.Should().BeTrue("ITokenClaimNames must be a contract interface, not a class");
    }

    [Fact]
    public void GivenRefactor_WhenLoaded_ThenDefaultImplementationExists()
    {
        var iface = GetTokenClaimNamesInterface();
        var impl = GetDefaultImpl();

        iface.Should().NotBeNull();
        impl.Should().NotBeNull(
            "#37 must ship a default JwtClaimsTokenClaimNames implementation that mirrors the legacy const values");
        iface!.IsAssignableFrom(impl!).Should().BeTrue(
            "the default implementation must implement ITokenClaimNames — otherwise DI Replace won't work");
    }

    [Fact]
    public void GivenITokenClaimNamesInterface_WhenInspected_ThenExposesAllSixClaimNames()
    {
        var iface = GetTokenClaimNamesInterface();
        iface.Should().NotBeNull();

        var expected = new[] { "Subject", "Name", "Role", "UserType", "RoleVersion", "TenantId" };
        foreach (var name in expected)
        {
            iface!.GetProperty(name).Should().NotBeNull(
                $"ITokenClaimNames must expose a '{name}' property — this is the seam business code hooks into");
            iface.GetProperty(name)!.PropertyType.Should().Be(typeof(string),
                $"ITokenClaimNames.{name} must be string — claim names are wire-format identifiers");
        }
    }

    // ── 默认实现必须保持向后兼容 ────────────────────────────────

    [Fact]
    public void GivenDefaultImplementation_WhenInspected_ThenAllConstantsMatchLegacyValues()
    {
        var impl = CreateDefaultImpl();

        // Assert — 必须一字不差保留旧 const 值，否则破坏现有 token
        GetInterfaceProperty(impl, "Subject").Should().Be(JwtClaims.Subject,
            "default impl.Subject must equal the legacy JwtClaims.Subject const");
        GetInterfaceProperty(impl, "Name").Should().Be(JwtClaims.Name);
        GetInterfaceProperty(impl, "Role").Should().Be(JwtClaims.Role);
        GetInterfaceProperty(impl, "UserType").Should().Be(JwtClaims.UserType);
        GetInterfaceProperty(impl, "RoleVersion").Should().Be(JwtClaims.RoleVersion);
        GetInterfaceProperty(impl, "TenantId").Should().Be(JwtClaims.TenantId);
    }

    [Fact]
    public void GivenJwtClaimsStaticClass_WhenInspected_ThenStillExposesConstants()
    {
        // Arrange + Act
        var subject = JwtClaims.Subject;
        var name = JwtClaims.Name;
        var role = JwtClaims.Role;

        // Assert — 旧的 static const 必须保留（向后兼容：业务方可能直接引用 JwtClaims.Name）
        subject.Should().Be("sub");
        name.Should().Be("name");
        role.Should().Be("role");
    }

    // ── JwtTokenService 必须读 ITokenClaimNames ─────────────────

    [Fact]
    public void GivenCustomTokenClaimNames_WhenIssuingAccessToken_ThenJwtUsesCustomClaimNames()
    {
        // Arrange — 模拟 Keycloak: 用 preferred_username / groups 替代 name / role
        var custom = CreateDynamicImpl(
            subject: "sub",
            name: "preferred_username",
            role: "groups",
            userType: "user_type",
            roleVersion: "role_versions",
            tenantId: "tenant_id");

        var svc = CreateTokenServiceThroughContainer(custom);

        // Act
        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long>(),
            tenantId: "t-acme");

        // Assert
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);

        jwt.Claims.Should().Contain(c => c.Type == "preferred_username" && c.Value == "Alice",
            "JwtTokenService must read the name claim from ITokenClaimNames, not from a hard-coded const");
        jwt.Claims.Should().Contain(c => c.Type == "groups" && c.Value == "editor",
            "JwtTokenService must read the role claim from ITokenClaimNames, not from a hard-coded const");
    }

    [Fact]
    public void GivenDefaultTokenClaimNames_WhenIssuingAccessToken_ThenJwtUsesDefaultClaimNames()
    {
        // Arrange — 默认实现：保持与 #37 之前完全一致的 claim 名
        var svc = CreateTokenServiceThroughContainer();

        // Act
        var issued = svc.Issue(
            userCode: "alice",
            displayName: "Alice",
            userType: UserType.Person,
            roles: new[] { "editor" },
            roleVersions: new Dictionary<string, long>(),
            tenantId: null);

        // Assert
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);

        jwt.Claims.Should().Contain(c => c.Type == JwtClaims.Name && c.Value == "Alice",
            "default ITokenClaimNames must produce the same claim names as the legacy JwtClaims const");
        jwt.Claims.Should().Contain(c => c.Type == JwtClaims.Role && c.Value == "editor");
    }

    // ── DI Replace 模式：业务方可以覆盖 ──────────────────────────

    [Fact]
    public void GivenServicesContainer_WhenReplacingITokenClaimNames_ThenCustomImplementationIsResolved()
    {
        // Arrange
        var iface = GetTokenClaimNamesInterface();
        iface.Should().NotBeNull();

        var services = new ServiceCollection();
        var custom = CreateDynamicImpl(
            subject: "sub",
            name: "preferred_username",
            role: "groups",
            userType: "user_type",
            roleVersion: "role_versions",
            tenantId: "tenant_id");
        services.AddSingleton(iface!, custom);

        // Act
        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService(iface!);

        // Assert
        GetInterfaceProperty(resolved, "Name").Should().Be("preferred_username",
            "business code must be able to swap in Keycloak-style claim names via DI Replace");
        GetInterfaceProperty(resolved, "Role").Should().Be("groups");
    }

    [Fact]
    public void GivenServicesContainer_WhenResolvingDefaultITokenClaimNames_ThenJwtClaimsImplementationIsUsed()
    {
        // Arrange — 模拟 AddTenE0JwtAuth 注册了默认实现
        var iface = GetTokenClaimNamesInterface();
        var impl = GetDefaultImpl();
        iface.Should().NotBeNull();
        impl.Should().NotBeNull();

        var services = new ServiceCollection();
        services.TryAddSingleton(iface!, impl!);

        // Act
        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService(iface!);

        // Assert — 必须返回默认实现，且 claim 名与遗留 const 一致
        resolved.Should().BeOfType(impl!,
            "the default DI registration must be JwtClaimsTokenClaimNames — backward compatibility");
        GetInterfaceProperty(resolved, "Subject").Should().Be(JwtClaims.Subject);
        GetInterfaceProperty(resolved, "Role").Should().Be(JwtClaims.Role);
    }

    [Fact]
    public void GivenTryAddSingleton_WhenAddedTwice_ThenFirstRegistrationWins()
    {
        // Arrange
        var iface = GetTokenClaimNamesInterface();
        iface.Should().NotBeNull();

        var services = new ServiceCollection();
        var first = CreateDefaultImpl();
        var second = CreateDynamicImpl(
            subject: "x", name: "y", role: "z", userType: "u", roleVersion: "v", tenantId: "t");

        // TryAddSingleton(Type, Func<IServiceProvider, object>) — 显式包装避免重载歧义
        services.TryAddSingleton(iface!, _ => first);
        services.TryAddSingleton(iface!, _ => second);

        // Act
        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService(iface!);

        // Assert — TryAdd 保证业务方可以决定是否覆盖
        resolved.Should().BeSameAs(first);
    }

    // ── Test doubles ───────────────────────────────────────────
    //
    // 用 DispatchProxy 动态实现 ITokenClaimNames 接口，避免静态引用未定义的类型，
    // 保持测试在 #37 未实现时仍能编译通过。

    private static object CreateDynamicImpl(
        string subject,
        string name,
        string role,
        string userType,
        string roleVersion,
        string tenantId)
    {
        var iface = GetTokenClaimNamesInterface()
            ?? throw new InvalidOperationException("ITokenClaimNames not loaded — #37 not implemented");
        // DispatchProxy.Create(Type proxyType, Type dispatchType) 返回代理，
        // dispatch 实例由该工厂方法内部通过 Activator.CreateInstance(dispatchType) 创建。
        // 这里把 dispatch 字段以"参数包"形式静态暴露在嵌套类型中（见下），
        // 让生成的代理 Invoke 时能读取到测试传入的 claim 值。
        TestTokenClaimNamesDispatch.SetValues(subject, name, role, userType, roleVersion, tenantId);
        var createMethod = typeof(DispatchProxy).GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Type), typeof(Type) },
            null)!;
        return createMethod.Invoke(null, new object[] { iface, typeof(TestTokenClaimNamesDispatch) })!;
    }

    private class TestTokenClaimNamesDispatch : DispatchProxy
    {
        // 静态字段：DispatchProxy.Create 内部会 Activator.CreateInstance(dispatchType)，
        // 不会把测试参数传进 ctor —— 把参数写到静态槽位，让生成的代理 Invoke 时读。
        private static string _subject = "";
        private static string _name = "";
        private static string _role = "";
        private static string _userType = "";
        private static string _roleVersion = "";
        private static string _tenantId = "";

        public static void SetValues(string subject, string name, string role, string userType, string roleVersion, string tenantId)
        {
            _subject = subject;
            _name = name;
            _role = role;
            _userType = userType;
            _roleVersion = roleVersion;
            _tenantId = tenantId;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            return targetMethod.Name switch
            {
                "get_Subject" => _subject,
                "get_Name" => _name,
                "get_Role" => _role,
                "get_UserType" => _userType,
                "get_RoleVersion" => _roleVersion,
                "get_TenantId" => _tenantId,
                _ => null,
            };
        }
    }
}
