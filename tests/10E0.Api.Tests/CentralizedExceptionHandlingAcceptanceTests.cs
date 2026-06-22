using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Events.Outbox;

namespace TenE0.Api.Tests;

/// <summary>
/// BDD acceptance tests for issue #39 — centralized exception handling
/// + uniform <c>ApiResult&lt;T&gt;</c> response shape.
///
/// The issue pins four behaviors we lock in here:
///
///   - <c>PermissionDeniedException</c> thrown deep inside a CQRS handler
///     bubbles to the HTTP boundary as <c>HTTP 403</c> with an
///     <c>ApiResult</c>-shaped JSON body (NOT the current bare
///     <c>{ error: "..." }</c> shape).
///
///   - The 403 body must carry the stable <c>errorCode = "PERM_DENIED"</c>
///     so clients can branch on it.
///
///   - The response must declare <c>content-type: application/json</c>
///     (uniform with every other API response).
///
///   - A successful endpoint that already returned <c>ApiResult</c>-shaped
///     JSON continues to do so unchanged — proving the new wrapper is
///     additive, not destructive.
///
/// Each scenario encodes a Given/When/Then business behavior. Today the
/// current handler returns <c>Results.Json(new {{ error = ex.Message }})</c>
/// (no <c>success</c>, no <c>errorCode</c>) so these tests fail RED.
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class CentralizedExceptionHandlingAcceptanceTests
{
    // ── 403 from a permission failure ───────────────────────────

    [Fact]
    public async Task GivenAuthenticatedUserWithoutDemoCreate_WhenPostingDemo_ThenResponseIs403WithApiResultShape()
    {
        // Arrange — alice has only viewer, NOT editor → no demo.create
        using var factory = new IsolatedFactory(aliceRoles: new[] { "viewer" });
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "111111");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        // Act — POST /demo triggers PermissionDeniedException in PermissionBehavior
        var response = await client.PostAsJsonAsync(
            "/demo", new { name = "should-deny", orgId = (string?)null, salary = (decimal?)null });

        // Assert — HTTP 403
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "PermissionDeniedException must surface as HTTP 403, exactly as the issue requires");

        // Assert — JSON content-type (uniform with the rest of the API)
        response.Content.Headers.ContentType
            .Should().NotBeNull("all error responses must be JSON");
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/json",
                "the centralized handler must emit JSON, never plain text or empty body");

        // Assert — ApiResult<T> shape (uniform with success-path responses)
        var body = await ReadApiResultAsync(response);
        body.Success.Should().BeFalse(
            "the 403 body must follow the ApiResult success=false shape, " +
            "NOT the bare { error: '...' } shape that today leaks from per-endpoint try/catch");
        body.ErrorCode.Should().Be("PERM_DENIED",
            "clients must key off the stable PERM_DENIED code to render the 403 banner");
        body.ErrorMessage.Should().NotBeNullOrEmpty(
            "the human-readable reason must still be present so the UI can show it");
    }

    [Fact]
    public async Task GivenPermissionDeniedException_WhenMapped_ThenResponseBodyOmitsRawExceptionObject()
    {
        // Arrange — alice only has viewer
        using var factory = new IsolatedFactory(aliceRoles: new[] { "viewer" });
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "111111");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        // Act
        var response = await client.PostAsJsonAsync(
            "/demo", new { name = "should-deny", orgId = (string?)null, salary = (decimal?)null });
        var raw = await response.Content.ReadAsStringAsync();

        // Assert — the new shape uses lowerCamelCase ApiResult fields,
        // never the legacy upper-case "Error" singular property.
        var json = JsonDocument.Parse(raw).RootElement;
        json.TryGetProperty("success", out _).Should().BeTrue(
            "ApiResult exposes `success` (lowerCamelCase), not `Success`");
        json.TryGetProperty("errorMessage", out _).Should().BeTrue(
            "ApiResult exposes `errorMessage`, not the legacy `Error` field");
        json.TryGetProperty("errorCode", out _).Should().BeTrue(
            "ApiResult exposes `errorCode` so clients can branch programmatically");
        json.TryGetProperty("data", out _).Should().BeTrue(
            "the success envelope must still be present (null on failure) " +
            "so clients can deserialize with the same DTO either way");
    }

    [Fact]
    public async Task GivenAuthenticatedUserWithoutDemoDelete_WhenDeletingDemo_ThenResponseIs403WithApiResultShape()
    {
        // Arrange — alice has viewer + editor but NOT demo.delete
        using var factory = new IsolatedFactory(aliceRoles: new[] { "viewer", "editor" });
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "111111");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        // Act — DELETE /demo/{id} is gated by [RequirePermission(DemoPermissions.Delete)]
        var response = await client.DeleteAsync("/demo/non-existent-id");

        // Assert — every protected endpoint, not just POST, must funnel through
        // the centralized handler so the response shape stays uniform.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await ReadApiResultAsync(response);
        body.Success.Should().BeFalse();
        body.ErrorCode.Should().Be("PERM_DENIED");
    }

    [Fact]
    public async Task GivenAuthenticatedUserWithoutDemoView_WhenListingDemo_ThenResponseIs403WithApiResultShape()
    {
        // Arrange — alice has NO demo-related permissions at all
        using var factory = new IsolatedFactory(aliceRoles: Array.Empty<string>());
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "111111");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        // Act — GET /demo also gates on [RequirePermission(DemoPermissions.View)]
        var response = await client.GetAsync("/demo");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await ReadApiResultAsync(response);
        body.Success.Should().BeFalse();
        body.ErrorCode.Should().Be("PERM_DENIED");
    }

    // ── Success path still works through the new wrapper ───────

    [Fact]
    public async Task GivenAuthenticatedUserWithDemoCreate_WhenPostingDemo_ThenResponseKeepsApiResultShape()
    {
        // Arrange — alice has editor (grants demo.create)
        using var factory = new IsolatedFactory(aliceRoles: new[] { "viewer", "editor" });
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "111111");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        // Act
        var response = await client.PostAsJsonAsync(
            "/demo", new { name = "happy-path", orgId = (string?)null, salary = (decimal?)null });

        // Assert — the centralized handler is additive: it must not break the
        // success path, and the success body still carries the ApiResult envelope.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "alice has demo.create, so POST /demo must succeed");

        var raw = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(raw).RootElement;
        json.TryGetProperty("success", out var success).Should().BeTrue(
            "the success envelope must still expose `success` for clients");
        success.GetBoolean().Should().BeTrue();
        // POST /demo has been migrated to Results.Api(ApiResult<object>.Ok(new { id })),
        // so `id` now lives under `data` per the uniform ApiResult envelope.
        json.TryGetProperty("data", out var data).Should().BeTrue(
            "the migrated success body must carry the data envelope");
        data.ValueKind.Should().Be(JsonValueKind.Object);
        data.TryGetProperty("id", out _).Should().BeTrue(
            "the migrated success payload must still expose the new entity id");
    }

    // ── Issue #93: end-to-end coverage for the RaiseInternal path ─────

    [Fact]
    public async Task GivenSuccessfulCreateDemo_WhenPostingDemo_ThenDemoCreatedEventLandsInOutbox()
    {
        // Arrange — alice has demo.create, so the handler runs end-to-end and
        // BeforeSaveAsync's RaiseInternal call must produce an OutboxMessage row
        // (issue #93 替代反射：现在走 AggregateRoot.RaiseInternal，断言事件真的进了 Outbox)。
        using var factory = new IsolatedFactory(aliceRoles: new[] { "viewer", "editor" });
        await factory.ResetAliceRolesAsync();
        var client = factory.CreateClient();
        var aliceAuth = await LoginAsAsync(client, "alice", "111111");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", aliceAuth.AccessToken);

        // Act
        var response = await client.PostAsJsonAsync(
            "/demo", new { name = "outbox-probe", orgId = (string?)null, salary = (decimal?)null });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert — OutboxMessage 行必须存在且 EventType 指向 DemoCreatedEvent。
        // 与 OutboxInterceptor.SavingChangesAsync 共事务：业务实体写入 + Outbox 行原子提交。
        using var probe = await factory.Services
            .GetRequiredService<IDbContextFactory<DemoDbContext>>()
            .CreateDbContextAsync();
        var outboxMessages = await probe.Set<OutboxMessage>().AsNoTracking().ToListAsync();
        outboxMessages.Should().NotBeEmpty(
            "CreateDemoCommandHandler.BeforeSaveAsync 调 RaiseInternal → OutboxInterceptor 必须把 DemoCreatedEvent 序列化入 OutboxMessage 表");
        outboxMessages.Should().Contain(m => m.EventType.Contains(nameof(TenE0.Api.Events.DemoCreatedEvent)),
            "EventType 必须指向 DemoCreatedEvent，不能是 placeholder 或错误类型");
        outboxMessages.Should().Contain(m => m.Payload.Contains("outbox-probe"),
            "Payload JSON 必须包含新建 demo 的 name，验证 OutboxInterceptor 真的拿到了 RaiseInternal 注册的事件");
    }

    // ── Helpers ────────────────────────────────────────────────

    /// <summary>Logs in as a seeded user and returns the auth payload.</summary>
    private static async Task<AuthResponseDto> LoginAsAsync(
        HttpClient client, string userCode, string password)
    {
        var resp = await client.PostAsJsonAsync(
            "/auth/login", new { userCode, password });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"{userCode} is seeded in AuthSeeder with password {password}");
        // #50: /auth/login now returns the uniform ApiResult<T> envelope
        // (success = true, data = { accessToken, refreshToken, ... }) so a
        // single DTO deserializes both success and failure responses.
        var env = await resp.Content.ReadFromJsonAsync<LoginEnvelope>();
        env.Should().NotBeNull();
        env!.Success.Should().BeTrue("the success body must carry success = true");
        env.Data.Should().NotBeNull("the success body must carry the data envelope");
        env.Data!.AccessToken.Should().NotBeNullOrWhiteSpace();
        env.Data.RefreshToken.Should().NotBeNullOrWhiteSpace();
        return env.Data;
    }

    /// <summary>Reads the response body as a permissive ApiResult-like DTO.</summary>
    private static async Task<ApiResultBody> ReadApiResultAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotBeNullOrWhiteSpace(
            "the response body must contain a JSON payload, not be empty");
        var body = JsonSerializer.Deserialize<ApiResultBody>(
            raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        body.Should().NotBeNull(
            "the response body must deserialize as the ApiResult<T> envelope");
        return body!;
    }

    /// <summary>Per-test isolated host — mirrors RoleRevocationEndToEndAcceptanceTests.</summary>
    public sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"issue39-{Guid.NewGuid():N}";
        private readonly string[] _aliceRoles;

        public IsolatedFactory(string[] aliceRoles) => _aliceRoles = aliceRoles;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<DemoDbContext>))
                    .ToList();
                foreach (var d in existing) services.Remove(d);

                var dbName = _dbName;
                services.AddDbContextFactory<DemoDbContext>(opt =>
                    opt.UseInMemoryDatabase(dbName)
                       .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            });
        }

        public async Task ResetAliceRolesAsync()
        {
            using var client = CreateClient();
            using var resp = await client.GetAsync("/");
            using var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync();
            var existing = ctx.UserRoles.Where(ur => ur.UserCode == "alice");
            ctx.UserRoles.RemoveRange(existing);
            foreach (var r in _aliceRoles)
                ctx.UserRoles.Add(new TenE0UserRole
                {
                    UserCode = "alice",
                    RoleCode = r
                });
            await ctx.SaveChangesAsync();
        }
    }

    // ── Wire DTOs (lenient deserialization — only the fields we assert) ──

    private sealed record AuthResponseDto(string AccessToken, string RefreshToken);

    /// <summary>
    /// #50: /auth/login success body is the uniform <c>ApiResult&lt;T&gt;</c>
    /// envelope. <c>Data</c> carries the <see cref="AuthResponseDto"/> payload
    /// (the same DTO a client uses to deserialize failure responses, since
    /// <c>Data</c> is null there).
    /// </summary>
    private sealed record LoginEnvelope(bool Success, AuthResponseDto? Data);

    private sealed record ApiResultBody(
        bool Success,
        object? Data,
        string? ErrorCode,
        string? ErrorMessage,
        string[]? NameBound);
}
