using TenE0.Core.EntityService;

namespace TenE0.Core.Tests.EntityService;

public sealed class EntityWriteOptionsTests
{
    [Fact]
    public void Default_ShouldBeNonNull()
    {
        EntityWriteOptions.Default.Should().NotBeNull();
    }

    [Fact]
    public void Default_PostedProperties_ShouldBeNull()
    {
        EntityWriteOptions.Default.PostedProperties.Should().BeNull();
    }

    [Fact]
    public void Default_PostedNavigations_ShouldBeNull()
    {
        EntityWriteOptions.Default.PostedNavigations.Should().BeNull();
    }

    [Fact]
    public void Default_KeepNavigationProperties_ShouldBeFalse()
    {
        EntityWriteOptions.Default.KeepNavigationProperties.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithPostedProperties_ShouldSetValues()
    {
        var opts = new EntityWriteOptions
        {
            PostedProperties = new HashSet<string> { "Name", "Email" },
            PostedNavigations = new HashSet<string> { "Tags" },
            KeepNavigationProperties = true
        };

        opts.PostedProperties.Should().BeEquivalentTo("Name", "Email");
        opts.PostedNavigations.Should().Contain("Tags");
        opts.KeepNavigationProperties.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithSingletonDefault_ShouldBeSameReference()
    {
        var d1 = EntityWriteOptions.Default;
        var d2 = EntityWriteOptions.Default;

        d1.Should().BeSameAs(d2, "Default should be a singleton reference");
    }
}
