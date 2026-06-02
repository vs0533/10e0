using TenE0.Core.Abstractions;
using TenE0.Core.Entities;

namespace TenE0.Core.Tests.Entities;

public sealed class BaseEntityTests
{
    [Fact]
    public void BaseEntity_Id_ShouldBeGuid()
    {
        // Arrange & Act
        var entity = new TestBaseEntity();

        // Assert
        Guid.TryParse(entity.Id, out _).Should().BeTrue("Id should be a valid GUID string");
    }

    [Fact]
    public void BaseEntity_Id_ShouldNotBeEmpty()
    {
        // Arrange & Act
        var entity = new TestBaseEntity();

        // Assert
        entity.Id.Should().NotBeNullOrEmpty();
        entity.Id.Should().NotBe(Guid.Empty.ToString("N"));
    }

    [Fact]
    public void TimedEntity_ShouldHaveTimestampFields()
    {
        // Arrange & Act
        var entity = new TestTimedEntity();

        // Assert
        entity.CreateTime.Should().BeNull();
        entity.CreateBy.Should().BeNull();
        entity.UpdateTime.Should().BeNull();
        entity.UpdateBy.Should().BeNull();
    }

    [Fact]
    public void AuditedEntity_ShouldHaveSoftDeleteFields()
    {
        // Arrange & Act
        var entity = new TestAuditedEntity();

        // Assert
        entity.IsSoftDelete.Should().BeFalse();
        entity.DeleteTime.Should().BeNull();
        entity.DeleteBy.Should().BeNull();
    }

    [Fact]
    public void TreeAuditedEntity_ShouldHaveParentId()
    {
        // Arrange & Act
        var entity = new TestTreeAuditedEntity();

        // Assert
        entity.ParentId.Should().BeNull();
    }

    [Fact]
    public void Inheritance_Chain_ShouldImplementInterfaces()
    {
        // Arrange & Act
        var entity = new TestTreeAuditedEntity();

        // Assert
        entity.Should().BeAssignableTo<IBaseEntity>();
        entity.Should().BeAssignableTo<ITimerEntity>();
        entity.Should().BeAssignableTo<ISoftDeleteEntity>();
        entity.Should().BeAssignableTo<ITreeEntity>();
    }

    #region Concrete Subclasses

    internal sealed class TestBaseEntity : BaseEntity { }
    internal sealed class TestTimedEntity : TimedEntity { }
    internal sealed class TestAuditedEntity : AuditedEntity { }
    internal sealed class TestTreeAuditedEntity : TreeAuditedEntity { }

    #endregion
}
