using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TenE0.Core.Permissions;

namespace TenE0.Core.Tests.Permissions;

/// <summary>
/// 单元测试：<see cref="PermissionAuthorizationHandler"/> 把
/// <see cref="IPermissionEvaluator"/> 接入 ASP.NET Core Authorization middleware。
///
/// 覆盖 #119 涉及的边界：
/// - 未认证 → 不 Succeed（Authorization middleware 写出 401）
/// - 已认证但角色无 permission → 不 Succeed（403）
/// - 已认证持有 permission → Succeed（200）
/// - super_admin → Succeed（IPermissionEvaluator 内置 IsSuperUser 短路）
/// - 空 permission key → 构造时抛 ArgumentException（防止 endpoint 配错）
/// </summary>
public sealed class PermissionAuthorizationHandlerTests
{
    private static AuthorizationHandlerContext BuildContext(IAuthorizationRequirement requirement)
    {
        return new AuthorizationHandlerContext(new[] { requirement }, new ClaimsPrincipal(), resource: null);
    }

    // ── Succeed 路径 ─────────────────────────────────────────

    [Fact]
    public async Task GivenUserWithPermission_WhenHandlerEvaluates_ThenContextSucceeds()
    {
        // Arrange — IPermissionEvaluator.HasAsync 返回 true（用户持有 perm.admin）
        var evaluator = new Mock<IPermissionEvaluator>();
        evaluator.Setup(e => e.HasAsync("perm.admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var handler = new PermissionAuthorizationHandler(evaluator.Object);

        var requirement = new PermissionRequirement("perm.admin");
        var ctx = BuildContext(requirement);

        // Act
        await handler.HandleAsync(ctx);

        // Assert — HasAsync 调用 1 次
        evaluator.Verify(e => e.HasAsync("perm.admin", It.IsAny<CancellationToken>()), Times.Once);
        ctx.HasSucceeded.Should().BeTrue(
            "持有 permission 的用户必须 Succeed，让 Authorization middleware 放行");
        ctx.HasFailed.Should().BeFalse(
            "handler 不应显式 Fail；未 Succeed 时 Authorization middleware 自动按 Forbidden 处理");
    }

    [Fact]
    public async Task GivenSuperUser_WhenHandlerEvaluates_ThenContextSucceedsRegardlessOfPermissionKey()
    {
        // Arrange — IPermissionEvaluator.HasAsync 返回 true（PermissionEvaluator 内部
        // 已含 IsSuperUser 短路，handler 不重复实现）
        var evaluator = new Mock<IPermissionEvaluator>();
        evaluator.Setup(e => e.HasAsync("perm.admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var handler = new PermissionAuthorizationHandler(evaluator.Object);

        var ctx = BuildContext(new PermissionRequirement("perm.admin"));

        // Act
        await handler.HandleAsync(ctx);

        // Assert — super_admin 走 IPermissionEvaluator 内部短路返回 true → handler Succeed
        ctx.HasSucceeded.Should().BeTrue(
            "super_admin 必须在所有 permission key 上 bypass；具体短路逻辑在 PermissionEvaluator 内");
    }

    // ── 不 Succeed 路径 ─────────────────────────────────────

    [Fact]
    public async Task GivenUnauthenticatedUser_WhenHandlerEvaluates_ThenContextDoesNotSucceed()
    {
        // Arrange — IPermissionEvaluator.HasAsync 返回 false（PermissionEvaluator
        // 第一个分支就是 !IsAuthenticated → return false）
        var evaluator = new Mock<IPermissionEvaluator>();
        evaluator.Setup(e => e.HasAsync("perm.admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var handler = new PermissionAuthorizationHandler(evaluator.Object);

        var ctx = BuildContext(new PermissionRequirement("perm.admin"));

        // Act
        await handler.HandleAsync(ctx);

        // Assert — 未认证 → Authorization middleware 写 401
        ctx.HasSucceeded.Should().BeFalse(
            "未认证用户的 HasAsync 立即返回 false，handler 不 Succeed → 401");
    }

    [Fact]
    public async Task GivenUserWithoutPermission_WhenHandlerEvaluates_ThenContextDoesNotSucceed()
    {
        // Arrange — 已认证但无 perm.admin（如 alice = viewer + editor）
        var evaluator = new Mock<IPermissionEvaluator>();
        evaluator.Setup(e => e.HasAsync("perm.admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var handler = new PermissionAuthorizationHandler(evaluator.Object);

        var ctx = BuildContext(new PermissionRequirement("perm.admin"));

        // Act
        await handler.HandleAsync(ctx);

        // Assert — 已认证但无权限 → 403
        ctx.HasSucceeded.Should().BeFalse(
            "viewer/editor 持有者无 perm.admin，HasAsync 返回 false → handler 不 Succeed → 403");
    }

    [Fact]
    public async Task GivenEvaluatorThrows_WhenHandlerEvaluates_ThenExceptionPropagatesAndContextUnchanged()
    {
        // Arrange — IPermissionEvaluator 抛异常（如 DbContext 异常）。handler 不吞异常，
        // 由 ExceptionHandler middleware / TenE0ExceptionHandler 统一映射到 5xx。
        var evaluator = new Mock<IPermissionEvaluator>();
        evaluator.Setup(e => e.HasAsync("perm.admin", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var handler = new PermissionAuthorizationHandler(evaluator.Object);

        var ctx = BuildContext(new PermissionRequirement("perm.admin"));

        // Act + Assert — 异常向上抛，ctx 状态不变
        var act = () => handler.HandleAsync(ctx);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "handler 必须让异常透传，权限评估异常应走 TenE0ExceptionHandler 统一映射");
        ctx.HasSucceeded.Should().BeFalse();
    }

    // ── PermissionRequirement 契约 ──────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GivenNullOrWhitespaceKey_WhenConstructingRequirement_ThenThrowsArgumentException(string? badKey)
    {
        // Arrange / Act + Assert — 防止 endpoint 拼错 policy key 静默放行
        var act = () => new PermissionRequirement(badKey!);
        act.Should().Throw<ArgumentException>(
            "空 permission key 会让 handler 调 HasAsync(\"\") → PermissionEvaluator 短路返回 false，" +
            "看似'无害'但实际会绕过权限检查 — 必须构造期 fail-fast");
    }

    [Fact]
    public void GivenValidKey_WhenConstructingRequirement_ThenStoresKeyVerbatim()
    {
        // Arrange / Act
        var req = new PermissionRequirement("custom.key");

        // Assert — 必须原样存储，不能 trim/lowercase，否则与 PermissionCatalog key 不一致
        req.PermissionKey.Should().Be("custom.key",
            "PermissionRequirement 必须原样存储 key，权限 catalog 大小写敏感");
    }
}
