using TenE0.Core.Abstractions;

namespace TenE0.Core.Auth;

/// <summary>
/// 框架壳提供的默认 <see cref="IUserInfoLoader"/> 空实现 —— 始终返回 <c>null</c>，
/// 序列化/反序列化返回空串/null。
///
/// 业务模块可在自己的 <see cref="DependencyInjection.IAppModule.ConfigureServices"/>
/// 里覆盖：
/// <code>
/// services.Replace(ServiceDescriptor.Scoped&lt;IUserInfoLoader, MyLoader&gt;());
/// </code>
///
/// 设计依据（#43）：原 10E0.Api.Hosting.NullUserInfoLoader 属于 demo 项目，
/// 框架不应反向依赖。把空实现下沉到 10E0.Core 后：
///   - <c>AddTenE0Core()</c> 通过 <c>TryAddScoped</c> 默认注册（#55 同模式）
///   - demo / 业务模块用 <c>Replace</c> 覆盖为自己的实现
///   - 共享框架不强制要求调用方注册 —— 启动即可工作
/// </summary>
internal sealed class NullUserInfoLoader : IUserInfoLoader
{
    public ValueTask<ICurrentUserInfo?> LoadAsync(
        string userCode, UserType userType, CancellationToken cancellationToken)
        => ValueTask.FromResult<ICurrentUserInfo?>(null);

    public string Serialize(ICurrentUserInfo info) => string.Empty;

    public ICurrentUserInfo? Deserialize(string payload, UserType userType) => null;
}
