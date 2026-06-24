using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Auditing;

/// <summary>
/// 业务审计拦截器 —— 在 <c>SaveChanges</c> 时捕获字段级 diff，推入 <see cref="IAuditLogSink"/>。
///
/// <para>
/// <b>职责分离（与现有 <c>AuditInterceptor</c> 互补）：</b>
/// <list type="bullet">
/// <item><c>AuditInterceptor</c>（现有）：时间戳填充 + 软删除 —— 同步、必须成功。</item>
/// <item><see cref="AuditLogInterceptor"/>（本类）：业务审计落库 —— 异步、best-effort、失败不阻断业务。</item>
/// </list>
/// 拦截器执行顺序无关（两者读/写不同字段），EF Core 按注册顺序依次调用。
/// </para>
///
/// <para>
/// <b>#95 captive-dependency 处理（对齐 <c>AuditInterceptor</c> 模式）：</b>
/// 本类注册为 Singleton（.NET 10 AddDbContextFactory 把拦截器嵌进 Singleton optionsAction），
/// 构造只接 Singleton 依赖（<see cref="IHttpContextAccessor"/> / <see cref="IAuditFieldFilter"/> /
/// <see cref="IOptions{AuditOptions}"/>）。<see cref="IAuditLogSink"/> 是 Scoped，
/// 在 <see cref="SavingChangesAsync"/> 时通过 <c>HttpContext.RequestServices</c> 按需解析当前请求
/// scope 的实例 —— 不在 ctor 钉死，避免 captive dependency。
/// </para>
/// </summary>
public sealed class AuditLogInterceptor(
    IHttpContextAccessor httpContextAccessor,
    IAuditFieldFilter fieldFilter,
    IOptions<AuditOptions> options) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly AuditOptions _options = options.Value;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
            CaptureAsync(eventData.Context, sync: true).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
            await CaptureAsync(eventData.Context, sync: false, cancellationToken);
        return result;
    }

    /// <summary>
    /// 扫描 ChangeTracker，为每个 Added/Modified/Deleted 条目构造审计日志并入队。
    /// </summary>
    /// <remarks>
    /// 入队（<see cref="AuditLogSink"/>）实际同步返回，保留 async 签名以便未来切换到真正的异步写入路径。
    /// </remarks>
    private async Task CaptureAsync(DbContext context, bool sync, CancellationToken ct = default)
    {
        if (!_options.Enabled) return;

        var sink = ResolveSink();
        if (sink is null) return; // 无 HTTP 上下文（Seeder/后台 Worker）：跳过审计

        var ctx = AuditContext.FromHttp(httpContextAccessor.HttpContext);
        var traceId = Activity.Current?.TraceId.ToString();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var entityType = entry.Entity.GetType().Name;
            if (_options.IgnoredEntityTypeNames.Contains(entityType))
                continue;

            var (action, changes) = BuildDiff(entry);
            // Create / Delete 即使无标量字段变化也要记录（Create=新增一条、Delete=删除一条都是有意义的审计事件）；
            // Update 若无字段变化（如只改了导航属性）则跳过，避免噪音。
            if (action == "Update" && changes.Count == 0) continue;

            var auditEntry = new AuditLogEntry
            {
                TraceId = traceId,
                ActorType = "User",
                ActorCode = ctx.ActorCode,
                EntityType = entityType,
                EntityId = entry.Entity is IBaseEntity be ? be.Id : entry.Property("Id")?.CurrentValue?.ToString() ?? "",
                Action = action,
                ChangedFieldsJson = JsonSerializer.Serialize(changes, JsonOptions),
                IpAddress = ctx.IpAddress,
                UserAgent = ctx.UserAgent,
            };

            if (sync)
                sink.EnqueueAsync(auditEntry, ct).GetAwaiter().GetResult();
            else
                await sink.EnqueueAsync(auditEntry, ct);
        }
    }

    /// <summary>
    /// 解析当前请求 scope 的 Sink。无 HTTP 上下文时返回 null（启动期 Seeder / 后台 Worker 不审计）。
    /// 注意：不回退到 root provider（会触发 "Cannot resolve scoped service from root provider"）。
    /// </summary>
    private IAuditLogSink? ResolveSink()
        => httpContextAccessor.HttpContext?.RequestServices.GetService<IAuditLogSink>();

    /// <summary>
    /// 构造字段 diff。<c>entry.Properties</c> 只含标量属性（导航属性走 NavigationEntry，
    /// 不会出现在此集合中），天然满足 issue #152 决策点 #3"只记标量字段、跳过导航/集合"。
    /// </summary>
    private (string action, List<FieldChange> changes) BuildDiff(EntityEntry entry)
    {
        List<FieldChange> changes = [];
        string action;
        switch (entry.State)
        {
            case EntityState.Added:
                action = "Create";
                foreach (var prop in entry.Properties)
                {
                    var newVal = prop.CurrentValue;
                    // Added：旧值视为 null；null 新值不记录（减少噪音）
                    if (newVal is null) continue;
                    changes.Add(new FieldChange(prop.Metadata.Name, null, MaskIfSensitive(prop.Metadata.Name, newVal)));
                }
                break;

            case EntityState.Deleted:
                action = entry.Entity is ISoftDeleteEntity ? "SoftDelete" : "Delete";
                foreach (var prop in entry.Properties)
                {
                    var oldVal = prop.OriginalValue;
                    if (oldVal is null) continue;
                    changes.Add(new FieldChange(prop.Metadata.Name, MaskIfSensitive(prop.Metadata.Name, oldVal), null));
                }
                break;

            case EntityState.Modified:
                action = "Update";
                foreach (var prop in entry.Properties)
                {
                    // 只记 EF 已标记 IsModified 的字段，未变字段不记录
                    if (!prop.IsModified) continue;
                    var oldVal = prop.OriginalValue;
                    var newVal = prop.CurrentValue;
                    if (Equals(oldVal, newVal)) continue;
                    changes.Add(new FieldChange(
                        prop.Metadata.Name,
                        MaskIfSensitive(prop.Metadata.Name, oldVal),
                        MaskIfSensitive(prop.Metadata.Name, newVal)));
                }
                break;

            default:
                action = entry.State.ToString();
                break;
        }
        return (action, changes);
    }

    private object? MaskIfSensitive(string name, object? value)
        => fieldFilter.IsSensitive(name) ? fieldFilter.Mask(name, value) : value;

    /// <summary>从 HTTP 上下文提取的审计元数据。</summary>
    private sealed record AuditContext(string? ActorCode, string? IpAddress, string? UserAgent)
    {
        public static AuditContext FromHttp(HttpContext? http)
        {
            if (http is null) return new AuditContext(null, null, null);
            var user = http.RequestServices.GetService<ICurrentUserContext>();
            var actor = user is { IsAuthenticated: true } ? user.UserCode : null;
            var ip = http.Connection.RemoteIpAddress?.ToString();
            var ua = http.Request.Headers.UserAgent.ToString();
            return new AuditContext(actor, string.IsNullOrEmpty(ip) ? null : ip, string.IsNullOrEmpty(ua) ? null : ua);
        }
    }
}

/// <summary>单字段变更记录（序列化后存入 ChangedFieldsJson）。</summary>
public sealed record FieldChange(string Field, object? OldValue, object? NewValue);
