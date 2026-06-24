using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Auditing;
using TenE0.Core.Entities;

namespace TenE0.Core.Tests.Auditing;

/// <summary>
/// <see cref="AuditLogInterceptor"/> 单元测试 — 验证 Added/Modified/Deleted 三态字段 diff、
/// 脱敏、无 HTTP 上下文跳过、自引用实体（审计表自身）跳过（issue #152 §4/§6）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AuditLogInterceptorTests
{
    private readonly DefaultAuditFieldFilter _filter = new();

    // ---- 测试实体 ----
    private sealed class TestEntity : TimedEntity
    {
        public string Name { get; set; } = "";
        public string? PasswordHash { get; set; }
        public decimal? Salary { get; set; }
    }

    private sealed class CapturingSink : IAuditLogSink
    {
        public List<AuditLogEntry> Ops { get; } = [];
        public List<LoginLogEntry> Logins { get; } = [];

        public Task EnqueueAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Ops.Add(entry);
            return Task.CompletedTask;
        }

        public Task WriteLoginAsync(LoginLogEntry entry, CancellationToken ct = default)
        {
            Logins.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class TestDbContext : DbContext
    {
        private readonly AuditLogInterceptor _interceptor;
        public DbSet<TestEntity> Entities => Set<TestEntity>();

        public TestDbContext(DbContextOptions<TestDbContext> options, AuditLogInterceptor interceptor)
            : base(options) => _interceptor = interceptor;

        protected override void OnConfiguring(DbContextOptionsBuilder b)
        {
            base.OnConfiguring(b);
            b.AddInterceptors(_interceptor);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestEntity>(e =>
            {
                e.HasKey(x => x.Id);
            });
        }
    }

    /// <summary>
    /// 构造一个带 HTTP 上下文的拦截器测试环境。
    /// httpContext.RequestServices 提供 CapturingSink + ICurrentUserContext。
    /// </summary>
    private (TestDbContext db, CapturingSink sink) CreateWithHttpContext(
        string? actorCode = "alice",
        string? ip = "1.2.3.4")
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLogSink>(sink);

        var userMock = new Moq.Mock<ICurrentUserContext>();
        userMock.SetupGet(u => u.IsAuthenticated).Returns(actorCode is not null);
        userMock.SetupGet(u => u.UserCode).Returns(actorCode);
        services.AddSingleton(userMock.Object);

        var sp = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Connection.RemoteIpAddress = ip is null ? null : System.Net.IPAddress.Parse(ip);
        httpContext.Request.Headers.UserAgent = "TestUA/1.0";

        var accessor = new Moq.Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContext);

        var interceptor = new AuditLogInterceptor(
            accessor.Object, _filter, Options.Create(new AuditOptions()));

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new TestDbContext(options, interceptor);
        return (db, sink);
    }

    private (TestDbContext db, CapturingSink sink) CreateWithoutHttpContext()
    {
        var sink = new CapturingSink();
        var accessor = new Moq.Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);

        var interceptor = new AuditLogInterceptor(
            accessor.Object, _filter, Options.Create(new AuditOptions()));

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new TestDbContext(options, interceptor);
        return (db, sink);
    }

    /// <summary>
    /// 解析 ChangedFieldsJson 为 field→(old,new) 字典，值统一转字符串便于断言。
    /// 注意：FieldChange.OldValue/NewValue 是 object?，反序列化后是 JsonElement，
    /// 直接 cast 会抛 InvalidCastException，故用 GetRawText() 统一成字符串。
    /// </summary>
    private static Dictionary<string, (string? oldVal, string? newVal)> ParseChanges(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var field = el.GetProperty("Field").GetString()!;
            string? oldVal = el.TryGetProperty("OldValue", out var o) && o.ValueKind != JsonValueKind.Null
                ? o.GetRawText().Trim('"') : null;
            string? newVal = el.TryGetProperty("NewValue", out var n) && n.ValueKind != JsonValueKind.Null
                ? n.GetRawText().Trim('"') : null;
            result[field] = (oldVal, newVal);
        }
        return result;
    }

    [Fact]
    public async Task Added_CapturesAllNonNullableScalarValuesAsCreate()
    {
        var (db, sink) = CreateWithHttpContext();
        db.Entities.Add(new TestEntity { Name = "n1", Salary = 100m });
        await db.SaveChangesAsync();

        sink.Ops.Should().ContainSingle();
        var op = sink.Ops[0];
        op.Action.Should().Be("Create");
        op.EntityType.Should().Be("TestEntity");
        op.ActorCode.Should().Be("alice");
        op.IpAddress.Should().Be("1.2.3.4");
        op.UserAgent.Should().Be("TestUA/1.0");

        var changes = ParseChanges(op.ChangedFieldsJson);
        changes.Should().ContainKey("Name");
        changes["Name"].newVal.Should().Be("n1");
        changes.Should().ContainKey("Salary");
        changes["Salary"].newVal.Should().Be("100");
        changes.Values.Should().AllSatisfy(v => v.oldVal.Should().BeNull("Added 旧值为 null"));
    }

    [Fact]
    public async Task Modified_RecordsOnlyChangedFields()
    {
        var (db, sink) = CreateWithHttpContext();
        var e = new TestEntity { Name = "old", Salary = 100m };
        db.Entities.Add(e);
        await db.SaveChangesAsync();
        sink.Ops.Clear();

        e.Name = "new";          // 改
        // Salary 不动
        db.Entities.Update(e);
        await db.SaveChangesAsync();

        sink.Ops.Should().ContainSingle();
        var op = sink.Ops[0];
        op.Action.Should().Be("Update");

        var changes = ParseChanges(op.ChangedFieldsJson);
        changes.Should().ContainKey("Name");
        changes["Name"].oldVal.Should().Be("old");
        changes["Name"].newVal.Should().Be("new");
        changes.Should().NotContainKey("Salary", "未变字段不应记录");
    }

    [Fact]
    public async Task Modified_NoScalarChange_SkipsEntry()
    {
        // 只改导航/无变化 → Update 动作但无标量 diff → 跳过（避免噪音）
        var (db, sink) = CreateWithHttpContext();
        var e = new TestEntity { Name = "same" };
        db.Entities.Add(e);
        await db.SaveChangesAsync();
        sink.Ops.Clear();

        // 不改任何字段就 Update
        db.Entities.Update(e);
        await db.SaveChangesAsync();

        sink.Ops.Should().BeEmpty("Update 无标量变化时应跳过");
    }

    [Fact]
    public async Task Deleted_CapturesOriginalValuesAsDelete()
    {
        var (db, sink) = CreateWithHttpContext();
        var e = new TestEntity { Name = "doomed", Salary = 50m };
        db.Entities.Add(e);
        await db.SaveChangesAsync();
        sink.Ops.Clear();

        db.Entities.Remove(e);
        await db.SaveChangesAsync();

        sink.Ops.Should().ContainSingle();
        var op = sink.Ops[0];
        op.Action.Should().Be("Delete");
        var changes = ParseChanges(op.ChangedFieldsJson);
        changes.Should().ContainKey("Name");
        changes["Name"].oldVal.Should().Be("doomed");
        changes.Values.Should().AllSatisfy(v => v.newVal.Should().BeNull("Delete 新值为 null"));
    }

    [Fact]
    public async Task SensitiveField_IsMaskedInDiff()
    {
        var (db, sink) = CreateWithHttpContext();
        db.Entities.Add(new TestEntity { Name = "x", PasswordHash = "super-secret" });
        await db.SaveChangesAsync();

        var op = sink.Ops.Single();
        var changes = ParseChanges(op.ChangedFieldsJson);
        changes.Should().ContainKey("PasswordHash");
        changes["PasswordHash"].newVal.Should().Be("***", "PasswordHash 必须脱敏");
    }

    [Fact]
    public async Task NoHttpContext_SkipsCapture()
    {
        // 模拟 Seeder / 后台 Worker：无 HTTP 上下文 → 不审计
        var (db, sink) = CreateWithoutHttpContext();
        db.Entities.Add(new TestEntity { Name = "seed" });
        await db.SaveChangesAsync();

        sink.Ops.Should().BeEmpty("无 HTTP 上下文时应跳过审计");
    }

    [Fact]
    public async Task Disabled_DoesNotCapture()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLogSink>(sink);
        var sp = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = sp };

        var accessor = new Moq.Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(httpContext);

        var interceptor = new AuditLogInterceptor(
            accessor.Object, _filter,
            Options.Create(new AuditOptions { Enabled = false }));

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new TestDbContext(options, interceptor);

        db.Entities.Add(new TestEntity { Name = "x" });
        await db.SaveChangesAsync();

        sink.Ops.Should().BeEmpty("Enabled=false 时不捕获");
    }
}
