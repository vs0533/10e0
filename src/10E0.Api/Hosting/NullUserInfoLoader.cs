using TenE0.Core.Abstractions;
using TenE0.Core.Auth;

namespace TenE0.Api.Hosting;

/// <summary>
/// 空实现：在 demo 项目里我们不关心从外部 IdP 拉用户扩展信息。
/// </summary>
internal sealed class NullUserInfoLoader : IUserInfoLoader
{
    public ValueTask<ICurrentUserInfo?> LoadAsync(string userCode, UserType userType, CancellationToken cancellationToken)
        => ValueTask.FromResult<ICurrentUserInfo?>(null);
    public string Serialize(ICurrentUserInfo info) => string.Empty;
    public ICurrentUserInfo? Deserialize(string payload, UserType userType) => null;
}
