using TenE0.Core.Abstractions;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Behaviors;

namespace TenE0.Core.Tests.Permissions.Behaviors;

/// <summary>
/// BDD-style acceptance tests for the permission pipeline behavior.
/// Each test method name encodes a complete business scenario:
///   Given{State}_When{Action}_Then{ExpectedOutcome}
/// These tests complement PermissionBehaviorTests by covering business scenarios
/// from a behavior-driven perspective. Some AND/OR/deny scenarios overlap intentionally.
/// </summary>
[Trait("Category", "BDD")]
public sealed class PermissionBddTests
{
    // ── Test commands ─────────────────────────────────────────

    [RequirePermission("inventory.read")]
    private sealed record ViewInventory : ICommand<Unit>;

    [RequirePermission("order.create")]
    private sealed record CreateOrder : ICommand<Unit>;

    [RequirePermission("inventory.read")]
    [RequirePermission("inventory.write")]
    private sealed record UpdateInventory : ICommand<Unit>;

    [RequirePermission("inventory.read", "order.read")]
    private sealed record ViewReports : ICommand<Unit>;

    // ── Happy path ────────────────────────────────────────────

    [Fact]
    public async Task GivenUserWithRequiredPermission_WhenExecutingCommand_ThenCommandSucceeds()
    {
        // Arrange: user has inventory.read permission
        var evaluator = CreateEvaluator(granted: "inventory.read");
        var behavior = new PermissionBehavior<ViewInventory, Unit>(evaluator.Object);
        var nextCalled = false;

        // Act
        await behavior.HandleAsync(
            new ViewInventory(),
            _ => { nextCalled = true; return Unit.Task; },
            CancellationToken.None);

        // Assert
        nextCalled.Should().BeTrue("command should execute when permission is granted");
    }

    [Fact]
    public async Task GivenUserWithAllRequiredPermissions_WhenExecutingMultiAttrCommand_ThenCommandSucceeds()
    {
        // Arrange: user has both inventory.read AND inventory.write
        var evaluator = CreateEvaluator("inventory.read", "inventory.write");
        var behavior = new PermissionBehavior<UpdateInventory, Unit>(evaluator.Object);
        var nextCalled = false;

        // Act
        await behavior.HandleAsync(
            new UpdateInventory(),
            _ => { nextCalled = true; return Unit.Task; },
            CancellationToken.None);

        // Assert
        nextCalled.Should().BeTrue("all required permissions are satisfied");
        evaluator.Verify(
            e => e.HasAnyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "both [RequirePermission] attributes should be evaluated (AND semantics)");
    }

    [Fact]
    public async Task GivenUserWithOneOfORPermissions_WhenExecutingMultiKeyCommand_ThenCommandSucceeds()
    {
        // Arrange: [RequirePermission("inventory.read", "order.read")] = OR
        // User has "inventory.read"
        var evaluator = new Mock<IPermissionEvaluator>();
        evaluator
            .Setup(e => e.HasAnyAsync(
                It.Is<IEnumerable<string>>(keys =>
                    keys.Contains("inventory.read") && keys.Contains("order.read")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var behavior = new PermissionBehavior<ViewReports, Unit>(evaluator.Object);
        var nextCalled = false;

        // Act
        await behavior.HandleAsync(
            new ViewReports(),
            _ => { nextCalled = true; return Unit.Task; },
            CancellationToken.None);

        // Assert
        nextCalled.Should().BeTrue("at least one OR-key is granted");
    }

    // ── Authorization failure ─────────────────────────────────

    [Fact]
    public async Task GivenUserWithoutRequiredPermission_WhenExecutingCommand_ThenPermissionDeniedExceptionThrown()
    {
        // Arrange: user has NO permissions
        var evaluator = CreateEvaluatorDenyAll();
        var behavior = new PermissionBehavior<CreateOrder, Unit>(evaluator.Object);

        // Act
        var act = () => behavior.HandleAsync(
            new CreateOrder(),
            _ => throw new InvalidOperationException("must not reach handler"),
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<PermissionDeniedException>(
            "unauthorized users must be blocked before reaching the handler");
    }

    [Fact]
    public async Task GivenUserWithPartialPermissions_WhenExecutingMultiAttrCommand_ThenPermissionDeniedExceptionThrown()
    {
        // Arrange: user has inventory.read but NOT inventory.write (AND fails)
        var evaluator = new Mock<IPermissionEvaluator>();
        evaluator
            .SetupSequence(e => e.HasAnyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)   // inventory.read  → passes
            .ReturnsAsync(false); // inventory.write → fails

        var behavior = new PermissionBehavior<UpdateInventory, Unit>(evaluator.Object);

        // Act
        var act = () => behavior.HandleAsync(
            new UpdateInventory(),
            _ => throw new InvalidOperationException("must not reach handler"),
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<PermissionDeniedException>(
            "all AND-attributes must pass; missing inventory.write blocks execution");
    }

    [Fact]
    public async Task GivenUnauthorizedUser_WhenExecutingCommand_ThenExceptionContainsCommandName()
    {
        // Arrange
        var evaluator = CreateEvaluatorDenyAll();
        var behavior = new PermissionBehavior<CreateOrder, Unit>(evaluator.Object);

        // Act
        var act = () => behavior.HandleAsync(
            new CreateOrder(),
            _ => throw new InvalidOperationException("must not reach handler"),
            CancellationToken.None);

        // Assert: exception must carry enough context for debugging
        var ex = await act.Should().ThrowAsync<PermissionDeniedException>();
        ex.Which.CommandName.Should().Be("CreateOrder",
            "exception must identify which command was blocked");
        ex.Which.RequiredKeys.Should().Contain("order.create",
            "exception must list the missing permission key");
    }

    // ── Helpers ───────────────────────────────────────────────

    /// <summary>Creates an evaluator that grants all specified keys.</summary>
    private static Mock<IPermissionEvaluator> CreateEvaluator(params string[] granted)
    {
        var mock = new Mock<IPermissionEvaluator>();
        mock.Setup(e => e.HasAnyAsync(
                It.Is<IEnumerable<string>>(keys => keys.Any(granted.Contains)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return mock;
    }

    /// <summary>Creates an evaluator that denies everything.</summary>
    private static Mock<IPermissionEvaluator> CreateEvaluatorDenyAll()
    {
        var mock = new Mock<IPermissionEvaluator>();
        mock.Setup(e => e.HasAnyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return mock;
    }
}
