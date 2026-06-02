using TenE0.Core.Abstractions;

namespace TenE0.Core.Tests.Abstractions;

public sealed class EntityContractsTests
{
    #region Unit

    [Fact]
    public void Unit_Value_ShouldBeSingleton()
    {
        var v1 = Unit.Value;
        var v2 = Unit.Value;

        v1.Should().Be(v2, "Unit.Value should be a singleton");
    }

    [Fact]
    public void Unit_Task_ShouldCompleteImmediately()
    {
        var task = Unit.Task;

        task.IsCompleted.Should().BeTrue("Unit.Task should complete synchronously");
        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Unit_Task_Result_ShouldBeValue()
    {
        var result = await Unit.Task;

        result.Should().Be(Unit.Value);
    }

    #endregion

    #region JwtClaims

    [Fact]
    public void JwtClaims_StaticProperties_ShouldMatchJwtSpec()
    {
        JwtClaims.Subject.Should().Be("sub");
        JwtClaims.Name.Should().Be("name");
        JwtClaims.Role.Should().Be("role");
        JwtClaims.UserType.Should().Be("user_type");
    }

    #endregion

    #region CacheKeys

    [Fact]
    public void CacheKeys_UserInfo_ShouldMatchConvention()
    {
        CacheKeys.UserInfo.Should().Be("user_info");
    }

    #endregion

    #region UserType

    [Fact]
    public void UserType_Enum_ShouldHavePersonAndUnit()
    {
        var values = Enum.GetValues<UserType>().ToList();

        values.Should().Contain(UserType.Person, "should have Person value");
        values.Should().Contain(UserType.Unit, "should have Unit value");
    }

    #endregion
}
