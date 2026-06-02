using TenE0.Core.Abstractions;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Behaviors;

namespace TenE0.Core.Tests.Permissions.Behaviors;

[Trait("Category", "Unit")]
public sealed class PermissionBehaviorTests
{
    /* ── Test Commands ────────────────────────────────────────── */

    private sealed record NoAttrCommand : ICommand<Unit>;

    [RequirePermission("demo.view")]
    private sealed record SingleAttrCommand : ICommand<Unit>;

    [RequirePermission("demo.view")]
    [RequirePermission("demo.update")]
    private sealed record MultiAttrCommand : ICommand<Unit>;

    [RequirePermission("demo.view", "demo.update")]
    private sealed record OrKeysCommand : ICommand<Unit>;

    /* ── Tests ────────────────────────────────────────────────── */

    [Fact]
    public async Task NoAttribute_ShouldCallNext()
    {
        var mockEvaluator = new Mock<IPermissionEvaluator>();
        var sut = new PermissionBehavior<NoAttrCommand, Unit>(mockEvaluator.Object);

        var called = false;
        var result = await sut.HandleAsync(
            new NoAttrCommand(),
            async ct => { called = true; return await Unit.Task; },
            CancellationToken.None);

        called.Should().BeTrue("next should be called when no [RequirePermission] is present");
        mockEvaluator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HasPermission_ShouldCallNext()
    {
        var mockEvaluator = new Mock<IPermissionEvaluator>();
        mockEvaluator.Setup(e => e.HasAnyAsync(It.Is<IReadOnlyCollection<string>>(k => k.Contains("demo.view")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new PermissionBehavior<SingleAttrCommand, Unit>(mockEvaluator.Object);

        var called = false;
        var result = await sut.HandleAsync(
            new SingleAttrCommand(),
            async ct => { called = true; return await Unit.Task; },
            CancellationToken.None);

        called.Should().BeTrue("next should be called when permission is granted");
    }

    [Fact]
    public async Task MissingPermission_ShouldThrow()
    {
        var mockEvaluator = new Mock<IPermissionEvaluator>();
        mockEvaluator.Setup(e => e.HasAnyAsync(It.Is<IReadOnlyCollection<string>>(k => k.Contains("demo.view")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = new PermissionBehavior<SingleAttrCommand, Unit>(mockEvaluator.Object);

        var act = () => sut.HandleAsync(
            new SingleAttrCommand(),
            async ct => throw new InvalidOperationException("should not reach next"),
            CancellationToken.None);

        await act.Should().ThrowAsync<PermissionDeniedException>()
            .Where(ex => ex.CommandName == "SingleAttrCommand");
    }

    [Fact]
    public async Task MultipleAttributes_AND_ShouldAllPass()
    {
        // First attr passes, second fails → AND fails → throw
        var mockEvaluator = new Mock<IPermissionEvaluator>();
        mockEvaluator.SetupSequence(e => e.HasAnyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)   // "demo.view" passes
            .ReturnsAsync(false); // "demo.update" fails

        var sut = new PermissionBehavior<MultiAttrCommand, Unit>(mockEvaluator.Object);

        var act = () => sut.HandleAsync(
            new MultiAttrCommand(),
            async ct => throw new InvalidOperationException("should not reach next"),
            CancellationToken.None);

        await act.Should().ThrowAsync<PermissionDeniedException>();
    }

    [Fact]
    public async Task MultipleAttributes_AllPass_ShouldContinue()
    {
        var mockEvaluator = new Mock<IPermissionEvaluator>();
        mockEvaluator.Setup(e => e.HasAnyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new PermissionBehavior<MultiAttrCommand, Unit>(mockEvaluator.Object);

        var called = false;
        await sut.HandleAsync(
            new MultiAttrCommand(),
            async ct => { called = true; return await Unit.Task; },
            CancellationToken.None);

        called.Should().BeTrue("next should be called when all attributes pass");
        mockEvaluator.Verify(e => e.HasAnyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SingleAttribute_MultipleKeys_OR_ShouldPass()
    {
        // [RequirePermission("demo.view", "demo.update")] — OR semantics
        // "demo.view" does NOT match but "demo.update" does → HasAnyAsync returns true
        var mockEvaluator = new Mock<IPermissionEvaluator>();

        // Capture the actual keys passed to HasAnyAsync to verify OR semantics
        string[]? capturedKeys = null;
        mockEvaluator.Setup(e => e.HasAnyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<string> keys, CancellationToken ct) =>
            {
                capturedKeys = keys.ToArray();
                return Task.FromResult(true);
            });

        var sut = new PermissionBehavior<OrKeysCommand, Unit>(mockEvaluator.Object);

        var called = false;
        await sut.HandleAsync(
            new OrKeysCommand(),
            async ct => { called = true; return await Unit.Task; },
            CancellationToken.None);

        called.Should().BeTrue();
        capturedKeys.Should().Contain("demo.view");
        capturedKeys.Should().Contain("demo.update");
        mockEvaluator.Verify(
            e => e.HasAnyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "OR-key attribute should trigger exactly one HasAnyAsync check");
    }

    [Fact]
    public async Task Exception_ShouldHaveCorrectProperties()
    {
        var mockEvaluator = new Mock<IPermissionEvaluator>();
        mockEvaluator.Setup(e => e.HasAnyAsync(It.Is<IReadOnlyCollection<string>>(k => k.Contains("demo.view")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = new PermissionBehavior<SingleAttrCommand, Unit>(mockEvaluator.Object);

        var ex = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            sut.HandleAsync(
                new SingleAttrCommand(),
                async ct => throw new InvalidOperationException("should not reach next"),
                CancellationToken.None));

        ex.CommandName.Should().Be("SingleAttrCommand");
        ex.RequiredKeys.Should().ContainSingle().Which.Should().Be("demo.view");
    }
}
