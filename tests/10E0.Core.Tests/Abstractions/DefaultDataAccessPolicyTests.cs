using TenE0.Core.Abstractions;

namespace TenE0.Core.Tests.Abstractions;

public sealed class DefaultDataAccessPolicyTests
{
    [Fact]
    public void DefaultDataAccessPolicy_BypassFilters_ShouldReturnFalse()
    {
        var policy = new DefaultDataAccessPolicy();

        policy.BypassFilters.Should().BeFalse();
    }
}
