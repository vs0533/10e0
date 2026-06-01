using System.Security.Claims;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Auth;

/// <summary>
/// 非 HTTP 场景下的 ICurrentUserContext 实现，基于 AsyncLocal&lt;T&gt; 携带用户上下文。
///
/// 使用场景：
/// - 后台 Worker / IHostedService（定时任务、清理作业等）
/// - 消息队列消费者（RabbitMQ / Kafka）
/// - 控制台应用 / 单元测试
/// - 任何"没有 HttpContext"的执行环境
///
/// 用法（以定时任务为例）：
///   public class CourseExpireJob(
///       ICurrentUserContextSetter setter,
///       ICommandDispatcher dispatcher)
///   {
///       public async Task Run()
///       {
///           var systemPrincipal = AmbientCurrentUserContext.BuildPrincipal("system", ["super_admin"]);
///           using (setter.Impersonate(systemPrincipal))
///           {
///               // 内部所有命令的 ICurrentUserContext 都返回 system 用户
///               await dispatcher.SendAsync(new ExpireCoursesCommand(), CancellationToken.None);
///           }
///       }
///   }
/// </summary>
public sealed class AmbientCurrentUserContext : ICurrentUserContext, ICurrentUserContextSetter
{
    // AsyncLocal 跨 await 流转，且各异步任务互不污染
    private static readonly AsyncLocal<ClaimsPrincipal?> Current = new();

    public bool IsAuthenticated => Current.Value?.Identity?.IsAuthenticated == true;

    public string? UserCode => Current.Value?.FindFirstValue(JwtClaims.Subject);

    public UserType UserType =>
        Enum.TryParse<UserType>(Current.Value?.FindFirstValue(JwtClaims.UserType), out var t)
            ? t : UserType.Person;

    public IReadOnlyList<string> RoleIds =>
        Current.Value?.FindAll(JwtClaims.Role).Select(c => c.Value).ToList() ?? [];

    public ValueTask<ICurrentUserInfo?> GetUserInfoAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<ICurrentUserInfo?>(null);

    public IDisposable Impersonate(ClaimsPrincipal user)
    {
        var previous = Current.Value;
        Current.Value = user;
        return new RestoreOnDispose(() => Current.Value = previous);
    }

    /// <summary>
    /// 快捷构造 ClaimsPrincipal — 用于服务端机器人/系统用户。
    /// 生产场景下用真实 JWT 解析出来的 ClaimsPrincipal 即可。
    /// </summary>
    public static ClaimsPrincipal BuildPrincipal(string userCode, IEnumerable<string> roles, UserType userType = UserType.Person)
    {
        var claims = new List<Claim>
        {
            new(JwtClaims.Subject, userCode),
            new(JwtClaims.UserType, userType.ToString()),
        };
        foreach (var role in roles) claims.Add(new Claim(JwtClaims.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Ambient"));
    }

    private sealed class RestoreOnDispose(Action restore) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            restore();
        }
    }
}

/// <summary>
/// 写入端接口（与读取接口分离，调用方依赖最少表面）。
/// </summary>
public interface ICurrentUserContextSetter
{
    /// <summary>在当前 async 流内冒充指定用户，using 结束后恢复。</summary>
    IDisposable Impersonate(ClaimsPrincipal user);
}
