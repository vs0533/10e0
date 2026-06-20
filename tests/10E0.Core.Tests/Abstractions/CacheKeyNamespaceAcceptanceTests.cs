using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Caching;

namespace TenE0.Core.Tests.Abstractions;

/// <summary>
/// BDD acceptance tests for #37 — Part 3: <c>ICacheKeyNamespace</c> abstraction.
///
/// 验证目标：
/// 1. 三处硬编码 cache key 前缀（<c>perm-cache:version</c> / <c>perm-role:v{version}:{roleCode}</c> /
///    <c>role-version:{roleCode}</c> / <c>user_info:{userCode}</c>）必须统一从 ICacheKeyNamespace 读
/// 2. 默认实现必须保留所有遗留前缀（向后兼容）
/// 3. 业务方可通过 DI Replace 实现多租户共享 Redis：
///    - 顶级 namespace（如 <c>acme</c>）拼到所有 key 顶部
///    - 跨租户不串数据
/// 4. DistributedPermissionCache（按角色缓存权限集合）和 EfRoleVersionStore（按角色缓存版本号）
///    必须读同一接口，避免两处 prefix 漂移
///
/// 失败模式：未实现前，cache key 是 const 字符串拼接，多租户共享 Redis 会跨租户串数据。
///
/// 设计说明：本测试用反射访问目标接口/类，编译通过，
/// #37 未实现时（ICacheKeyNamespace 类型不存在）运行时失败。
/// </summary>
[Trait("Category", "BDD")]
public sealed class CacheKeyNamespaceAcceptanceTests
{
    private static readonly Assembly CoreAssembly = typeof(CacheOptions).Assembly;

    private static Type? GetNamespaceInterface() =>
        CoreAssembly.GetType("TenE0.Core.Abstractions.ICacheKeyNamespace", throwOnError: false);

    private static Type? GetDefaultImpl() =>
        CoreAssembly.GetType("TenE0.Core.Abstractions.DefaultCacheKeyNamespace", throwOnError: false);

    private static object CreateNamespace(string? tenantId)
    {
        var impl = GetDefaultImpl()!;
        // 用反射构造：DefaultCacheKeyNamespace(string? tenantId)
        return Activator.CreateInstance(impl, tenantId)!;
    }

    private static string GetStringProperty(object instance, string propertyName)
    {
        var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!;
        return (string)prop.GetValue(instance)!;
    }

    private static string InvokeStringMethod(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)!;
        return (string)method.Invoke(instance, args)!;
    }

    // ── 接口与默认实现必须存在 ────────────────────────────────

    [Fact]
    public void GivenRefactor_WhenLoaded_ThenICacheKeyNamespaceInterfaceExists()
    {
        var iface = GetNamespaceInterface();
        iface.Should().NotBeNull(
            "#37 must introduce TenE0.Core.Abstractions.ICacheKeyNamespace interface for cache key prefix injection");
        iface!.IsInterface.Should().BeTrue();
    }

    [Fact]
    public void GivenRefactor_WhenLoaded_ThenDefaultImplementationExists()
    {
        var iface = GetNamespaceInterface();
        var impl = GetDefaultImpl();
        iface.Should().NotBeNull();
        impl.Should().NotBeNull(
            "#37 must ship a DefaultCacheKeyNamespace implementation that mirrors the legacy hard-coded prefixes");
        iface!.IsAssignableFrom(impl!).Should().BeTrue();
    }

    [Fact]
    public void GivenICacheKeyNamespaceInterface_WhenInspected_ThenExposesAllPrefixesAndBuilders()
    {
        var iface = GetNamespaceInterface();
        iface.Should().NotBeNull();

        // 必须暴露 4 个前缀属性 + 4 个 key 构建方法
        var prefixes = new[] { "PermissionVersionPrefix", "PermissionRolePrefix", "RoleVersionPrefix", "UserInfoPrefix" };
        foreach (var p in prefixes)
        {
            iface!.GetProperty(p).Should().NotBeNull(
                $"ICacheKeyNamespace must expose a '{p}' property — current code hard-codes this string in 3+ files");
            iface.GetProperty(p)!.PropertyType.Should().Be(typeof(string));
        }

        var builders = new[] { "PermissionVersionKey", "PermissionRoleKey", "RoleVersionKey", "UserInfoKey" };
        foreach (var m in builders)
        {
            iface!.GetMethod(m).Should().NotBeNull(
                $"ICacheKeyNamespace must expose a '{m}' builder — this is the seam where tenantId gets injected");
            iface.GetMethod(m)!.ReturnType.Should().Be(typeof(string));
        }
    }

    // ── 默认实现必须保持向后兼容 ────────────────────────────────

    [Fact]
    public void GivenDefaultCacheKeyNamespace_WhenInspectingPermissionVersionPrefix_ThenMatchesLegacyLiteral()
    {
        var ns = CreateNamespace(tenantId: null);

        GetStringProperty(ns, "PermissionVersionPrefix").Should().Be("perm-cache:version",
            "DistributedPermissionCache hard-codes 'perm-cache:version' — default namespace must keep the exact value");
    }

    [Fact]
    public void GivenDefaultCacheKeyNamespace_WhenInspectingPermissionRolePrefix_ThenMatchesLegacyLiteral()
    {
        var ns = CreateNamespace(tenantId: null);

        GetStringProperty(ns, "PermissionRolePrefix").Should().Be("perm-role",
            "DistributedPermissionCache hard-codes 'perm-role:v{version}:{roleCode}' — the role prefix part must be preserved");
    }

    [Fact]
    public void GivenDefaultCacheKeyNamespace_WhenInspectingRoleVersionPrefix_ThenMatchesLegacyLiteral()
    {
        var ns = CreateNamespace(tenantId: null);

        GetStringProperty(ns, "RoleVersionPrefix").Should().Be("role-version",
            "EfRoleVersionStore hard-codes 'role-version:{roleCode}' — the prefix part must be preserved");
    }

    [Fact]
    public void GivenDefaultCacheKeyNamespace_WhenInspectingUserInfoPrefix_ThenMatchesLegacyLiteral()
    {
        var ns = CreateNamespace(tenantId: null);

        GetStringProperty(ns, "UserInfoPrefix").Should().Be("user_info",
            "HttpCurrentUserContext hard-codes 'user_info:{UserCode}' — prefix part must be preserved");
    }

    [Fact]
    public void GivenDefaultCacheKeyNamespace_WhenInspecting_ThenAllValuesMatchLegacyLiterals()
    {
        var ns = CreateNamespace(tenantId: null);

        GetStringProperty(ns, "PermissionVersionPrefix").Should().Be("perm-cache:version");
        GetStringProperty(ns, "PermissionRolePrefix").Should().Be("perm-role");
        GetStringProperty(ns, "RoleVersionPrefix").Should().Be("role-version");
        GetStringProperty(ns, "UserInfoPrefix").Should().Be("user_info");
    }

    // ── 多租户：namespace 顶级前缀必须生效 ─────────────────────

    [Fact]
    public void GivenCustomTenantNamespace_WhenBuildingRoleVersionKey_ThenTenantPrefixIsApplied()
    {
        // Arrange — 业务方：多租户共享 Redis，必须在所有 key 顶部加 tenantId
        var ns = CreateNamespace(tenantId: "acme");

        // Act
        var key = InvokeStringMethod(ns, "RoleVersionKey", "editor");

        // Assert — 必须以 tenant 顶级前缀开头，避免与其它租户串数据
        key.Should().StartWith("acme",
            "tenantId must appear at the top of the key — otherwise tenants sharing one Redis would corrupt each other");
        key.Should().Contain("role-version",
            "the legacy 'role-version' prefix must still be present (the tenant prefix is an ADDITION, not a replacement)");
        key.Should().EndWith("editor");
    }

    [Fact]
    public void GivenTwoTenants_WhenBuildingSameRoleKey_ThenKeysAreDifferent()
    {
        // Arrange
        var acme = CreateNamespace(tenantId: "acme");
        var globex = CreateNamespace(tenantId: "globex");

        // Act
        var acmeKey = InvokeStringMethod(acme, "RoleVersionKey", "admin");
        var globexKey = InvokeStringMethod(globex, "RoleVersionKey", "admin");

        // Assert — 同一角色名 → 不同 cache key，绝不串
        acmeKey.Should().NotBe(globexKey,
            "two tenants asking for the same role code must resolve to different cache keys — otherwise the cache returns cross-tenant data");
        acmeKey.Should().Contain("acme");
        globexKey.Should().Contain("globex");
    }

    [Fact]
    public void GivenCustomTenantNamespace_WhenBuildingPermissionVersionKey_ThenTenantPrefixIsApplied()
    {
        var ns = CreateNamespace(tenantId: "t1");

        var key = InvokeStringMethod(ns, "PermissionVersionKey");

        key.Should().StartWith("t1",
            "permission version key also needs the tenant prefix — otherwise one tenant's InvalidateAll would reset another's version counter");
        key.Should().Contain("perm-cache:version");
    }

    [Fact]
    public void GivenCustomTenantNamespace_WhenBuildingPermissionRoleKey_ThenTenantPrefixIsApplied()
    {
        var ns = CreateNamespace(tenantId: "t1");

        var key = InvokeStringMethod(ns, "PermissionRoleKey", 7L, "editor");

        key.Should().StartWith("t1");
        key.Should().Contain("perm-role");
        key.Should().Contain("v7");
        key.Should().EndWith("editor");
    }

    [Fact]
    public void GivenCustomTenantNamespace_WhenBuildingUserInfoKey_ThenTenantPrefixIsApplied()
    {
        var ns = CreateNamespace(tenantId: "t1");

        var key = InvokeStringMethod(ns, "UserInfoKey", "alice");

        key.Should().StartWith("t1");
        key.Should().Contain("user_info");
        key.Should().EndWith("alice");
    }

    [Fact]
    public void GivenNullTenantId_WhenBuildingKey_ThenKeyMatchesLegacySingleTenantShape()
    {
        // Arrange — 单租户场景：tenant 为 null，key 形状必须与 #37 之前完全一致
        var ns = CreateNamespace(tenantId: null);

        // Act
        var roleKey = InvokeStringMethod(ns, "RoleVersionKey", "editor");
        var permVerKey = InvokeStringMethod(ns, "PermissionVersionKey");
        var permRoleKey = InvokeStringMethod(ns, "PermissionRoleKey", 1L, "editor");
        var userKey = InvokeStringMethod(ns, "UserInfoKey", "alice");

        // Assert — 必须与遗留 literal 完全相同，否则破坏现有部署
        roleKey.Should().Be("role-version:editor");
        permVerKey.Should().Be("perm-cache:version");
        permRoleKey.Should().Be("perm-role:v1:editor");
        userKey.Should().Be("user_info:alice");
    }

    // ── 空字符串/空白 tenant 必须当作 null（避免生成 ":xxx" 残缺 key） ──

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void GivenBlankTenantId_WhenBuildingKey_ThenTreatsAsNoTenant(string blank)
    {
        // Arrange
        var ns = CreateNamespace(tenantId: blank);

        // Act
        var key = InvokeStringMethod(ns, "RoleVersionKey", "editor");

        // Assert — 不能以 ":" 开头，不能包含空 tenant 段
        key.Should().NotStartWith(":",
            "blank tenant must not produce a malformed key like ':role-version:editor'");
        key.Should().Be("role-version:editor");
    }

    // ── DI Replace 模式 ────────────────────────────────────────

    [Fact]
    public void GivenCustomNamespaceImplementation_WhenResolvingFromDI_ThenContainerReturnsIt()
    {
        var iface = GetNamespaceInterface();
        var impl = GetDefaultImpl();
        iface.Should().NotBeNull();
        impl.Should().NotBeNull();

        var services = new ServiceCollection();
        var custom = CreateNamespace("acme");
        services.AddSingleton(iface!, custom);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService(iface!);

        // Assert — 业务方注入的 namespace（含 tenantId）必须能从容器取出
        var key = InvokeStringMethod(resolved, "RoleVersionKey", "editor");
        key.Should().StartWith("acme",
            "DI-injected namespace must drive cache key construction end-to-end");
    }

    [Fact]
    public void GivenDefaultRegistration_WhenResolvingFromDI_ThenDefaultImplIsUsed()
    {
        var iface = GetNamespaceInterface();
        var impl = GetDefaultImpl();
        iface.Should().NotBeNull();
        impl.Should().NotBeNull();

        var services = new ServiceCollection();
        services.TryAddSingleton(iface!, impl!);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService(iface!);

        resolved.Should().BeOfType(impl!,
            "the default DI registration must be DefaultCacheKeyNamespace");
        var key = InvokeStringMethod(resolved, "RoleVersionKey", "editor");
        key.Should().Be("role-version:editor",
            "without a tenantId the default impl must produce the legacy key shape verbatim");
    }
}
