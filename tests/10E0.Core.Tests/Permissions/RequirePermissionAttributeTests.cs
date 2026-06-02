using TenE0.Core.Abstractions;
using TenE0.Core.Permissions;

namespace TenE0.Core.Tests.Permissions;

public sealed class RequirePermissionAttributeTests
{
    [Fact]
    public void Constructor_SinglePermission_ShouldStoreKey()
    {
        var attr = new RequirePermissionAttribute("demo.view");

        attr.PermissionKeys.Should().ContainSingle().Which.Should().Be("demo.view");
    }

    [Fact]
    public void Constructor_MultiplePermissions_ShouldStoreAllKeys()
    {
        var attr = new RequirePermissionAttribute("demo.view", "demo.update", "demo.delete");

        attr.PermissionKeys.Should().BeEquivalentTo("demo.view", "demo.update", "demo.delete");
    }

    [Fact]
    public void Attribute_AllowsMultipleOnSameType()
    {
        var attrs = typeof(TestWithMultipleAttributes).GetCustomAttributes(typeof(RequirePermissionAttribute), false)
            .Cast<RequirePermissionAttribute>()
            .ToList();

        attrs.Should().HaveCount(2);
        attrs[0].PermissionKeys.Should().ContainSingle().Which.Should().Be("demo.view");
        attrs[1].PermissionKeys.Should().ContainSingle().Which.Should().Be("demo.update");
    }

    [Fact]
    public void Attribute_IsNotInherited()
    {
        var attr = typeof(TestDerived).GetCustomAttributes(typeof(RequirePermissionAttribute), false).FirstOrDefault() as RequirePermissionAttribute;

        attr.Should().BeNull("RequirePermissionAttribute should not inherit to derived types");
    }

    [Fact]
    public void Attribute_CanBeAppliedToClass()
    {
        var attrs = typeof(TestWithMultipleAttributes).GetCustomAttributes(typeof(RequirePermissionAttribute), false);
        attrs.Should().NotBeEmpty();
    }

    [Fact]
    public void Attribute_CannotBeAppliedToMethod()
    {
        var method = typeof(TestCommand).GetMethod("SomeMethod");
        var attrs = method!.GetCustomAttributes(typeof(RequirePermissionAttribute), false);

        attrs.Should().BeEmpty();
    }

    private sealed record TestCommand : ICommand<Unit>
    {
        public void SomeMethod() { }
    }

    [RequirePermission("demo.view")]
    [RequirePermission("demo.update")]
    private sealed class TestWithMultipleAttributes { }

    [RequirePermission("parent.permission")]
    private class TestBase { }

    private sealed class TestDerived : TestBase { }
}
