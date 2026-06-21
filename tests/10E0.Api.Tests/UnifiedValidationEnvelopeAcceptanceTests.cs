using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace TenE0.Api.Tests;

/// <summary>
/// BDD acceptance tests for issue #50 — followup from PR #48: unify the
/// IErrs-driven business-validation envelope to the same
/// <c>ApiResult&lt;T&gt;</c> shape every other endpoint already emits.
///
/// PR #48 unified the EXCEPTION path (PermissionDenied → 403,
/// Validation → 400, DbUpdate → 409) and migrated the DEMO endpoints to
/// the uniform envelope. It deliberately left the IErrs-driven path
/// alone because that requires touching every endpoint that uses
/// <c>IErrs</c>. Issue #50 closes that gap.
///
/// Each scenario encodes a Given/When/Then business behavior. Today the
/// following endpoints still emit the legacy <c>{ error: "..." }</c> shape
/// (or 401-plain-JSON), so these tests fail RED:
///
///   - <c>POST /auth/login</c>   — 401 with <c>{ error: "..." }</c>
///   - <c>POST /auth/refresh</c> — 401 with <c>{ error: "..." }</c>
///   - <c>POST /files/upload</c> (empty file)  — 400 with <c>{ error: "..." }</c>
///   - <c>POST /files/upload/image</c> (non-image) — 400 with <c>{ error: "..." }</c>
///
/// After issue #50 lands, every endpoint above must return the full
/// <c>ApiResult&lt;T&gt;</c> envelope so a client can deserialize the same
/// DTO for success and validation-failure of the same endpoint.
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class UnifiedValidationEnvelopeAcceptanceTests
{
    // ── AuthEndpoints: login ───────────────────────────────────

    [Fact]
    public async Task GivenInvalidLoginCredentials_WhenPostingAuthLogin_ThenResponseBodyHasApiResultShape()
    {
        // Arrange — alice is seeded with password 111111, so anything else fails
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        // Act — wrong password → IErrs gets "用户名或密码错误" with code "AUTH_INVALID"
        var response = await client.PostAsJsonAsync(
            "/auth/login",
            new { userCode = "alice", password = "wrong-password" });

        // Assert — HTTP status must signal business-validation failure.
        // The current endpoint uses 401, the migrated version can keep 401 or
        // downgrade to 400 — the issue doesn't pin the code, only the body shape.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);

        // Assert — body must be the uniform ApiResult<T> envelope
        var body = await ReadEnvelopeAsync(response);
        body.Success.Should().BeFalse(
            "the failed login body must follow ApiResult success=false, " +
            "not the legacy { error: '...' } shape");
        body.ErrorMessage.Should().NotBeNullOrEmpty(
            "the failed login body must carry the human-readable reason");
    }

    [Fact]
    public async Task GivenInvalidLoginCredentials_WhenPostingAuthLogin_ThenResponseBodyOmitsLegacyBareErrorKey()
    {
        // Arrange
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(
            "/auth/login",
            new { userCode = "alice", password = "wrong-password" });
        var raw = await response.Content.ReadAsStringAsync();

        // Assert — the migrated shape must carry the lowerCamelCase envelope fields.
        // Today the endpoint returns { error: "..." } so these assertions fail.
        var json = JsonDocument.Parse(raw).RootElement;
        json.TryGetProperty("success", out _).Should().BeTrue(
            "the migrated failed-login body must expose `success = false` for clients to branch on");
        json.TryGetProperty("errorMessage", out _).Should().BeTrue(
            "the migrated failed-login body must expose `errorMessage`, not the legacy bare `error` field");
        json.TryGetProperty("data", out _).Should().BeTrue(
            "the migrated failed-login body must still expose the `data` envelope field (null on failure)");
    }

    [Fact]
    public async Task GivenInvalidLoginCredentials_WhenPostingAuthLogin_ThenResponseDeclaresJsonContentType()
    {
        // Arrange
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(
            "/auth/login",
            new { userCode = "alice", password = "wrong-password" });

        // Assert — every failure response, like every success response, must be JSON
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task GivenValidLoginCredentials_WhenPostingAuthLogin_ThenSuccessResponseKeepsApiResultShape()
    {
        // Arrange — alice seeded with password 111111 (AuthSeeder)
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(
            "/auth/login",
            new { userCode = "alice", password = "111111" });

        // Assert — the migration must be additive: the success path keeps its
        // envelope shape (today's success returns LoginResult JSON; after
        // migration it must wrap into ApiResult<T> so the DTO is shared).
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "alice is seeded with password 111111, so login must succeed");
        var raw = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(raw).RootElement;
        json.TryGetProperty("success", out var success).Should().BeTrue(
            "the migrated success body must carry the ApiResult envelope");
        success.GetBoolean().Should().BeTrue();
        json.TryGetProperty("data", out _).Should().BeTrue(
            "the migrated success body must carry the data envelope");
    }

    // ── AuthEndpoints: refresh ─────────────────────────────────

    [Fact]
    public async Task GivenInvalidRefreshToken_WhenPostingAuthRefresh_ThenResponseBodyHasApiResultShape()
    {
        // Arrange
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        // Act — bogus refresh token → IErrs collects "无效的刷新令牌" with code "REFRESH_INVALID"
        var response = await client.PostAsJsonAsync(
            "/auth/refresh",
            new { refreshToken = "this-token-does-not-exist" });

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
        var body = await ReadEnvelopeAsync(response);
        body.Success.Should().BeFalse(
            "the failed refresh body must follow ApiResult success=false");
        body.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GivenInvalidRefreshToken_WhenPostingAuthRefresh_ThenResponseBodyOmitsLegacyBareErrorKey()
    {
        // Arrange
        using var factory = new IsolatedFactory();
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(
            "/auth/refresh",
            new { refreshToken = "bogus" });
        var raw = await response.Content.ReadAsStringAsync();

        // Assert
        var json = JsonDocument.Parse(raw).RootElement;
        json.TryGetProperty("success", out _).Should().BeTrue();
        json.TryGetProperty("errorMessage", out _).Should().BeTrue(
            "the migrated refresh-failure body must expose `errorMessage`, " +
            "not the legacy bare `error` field");
    }

    // ── Source-level contract: IErrs endpoints must not return the
    //    legacy { error = ... } shape. Issue #50's hard rule says any
    //    endpoint that uses IErrs must funnel through ApiResult<T>.FromErrs.
    //    We assert this at the source level because the file-upload
    //    endpoints sit behind ASP.NET Core's antiforgery gate, which makes
    //    end-to-end multipart testing brittle in this test host (the
    //    antiforgery metadata rejects multipart before reaching the
    //    endpoint body). The source-level scan gives us a stable, fast
    //    regression net for the parts that integration tests can't
    //    reach today.

    private const string LegacyIErrsShapePattern =
        @"Results\.(BadRequest|Json)\s*\(\s*new\s*\{\s*error\s*=";

    [Fact]
    public void GivenIssue50HardRule_WhenScanningAuthEndpoints_ThenNoLegacyBareErrorShapeRemains()
    {
        AssertEndpointFileHasNoLegacyShape(Path.Combine("src", "10E0.Api", "Endpoints", "AuthEndpoints.cs"));
    }

    [Fact]
    public void GivenIssue50HardRule_WhenScanningFileEndpoints_ThenNoLegacyBareErrorShapeRemains()
    {
        AssertEndpointFileHasNoLegacyShape(Path.Combine("src", "10E0.Api", "Endpoints", "FileEndpoints.cs"));
    }

    private static void AssertEndpointFileHasNoLegacyShape(string relativePath)
    {
        var absolutePath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(absolutePath).Should().BeTrue(
            $"endpoint source file `{relativePath}` must exist for the scan");

        var content = File.ReadAllText(absolutePath);
        var matches = System.Text.RegularExpressions.Regex.Matches(
            content, LegacyIErrsShapePattern);

        matches.Should().BeEmpty(
            $"endpoint file `{relativePath}` must not contain the legacy " +
            "`Results.BadRequest(new {{ error = ... }})` or " +
            "`Results.Json(new {{ error = ... }})` shape — issue #50 requires " +
            "every IErrs-driven validation failure to funnel through " +
            "`ApiResultResult.Api(ApiResult<T>.FromErrs(errs))` so the client " +
            "deserializes a single uniform envelope.");
    }

    private static string FindRepoRoot()
    {
        // tests/10E0.Api.Tests/bin/Debug/net10.0/<this.dll> → repo root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "10e0.slnx")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("must be able to locate the repository root via 10e0.slnx");
        return dir!.FullName;
    }

    // ── Helpers ────────────────────────────────────────────────

    /// <summary>Reads the response body as a permissive ApiResult-like DTO.</summary>
    private static async Task<EnvelopeBody> ReadEnvelopeAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotBeNullOrWhiteSpace(
            "the response body must contain a JSON payload, not be empty");
        var body = JsonSerializer.Deserialize<EnvelopeBody>(
            raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        body.Should().NotBeNull(
            "the response body must deserialize as the ApiResult<T> envelope");
        return body!;
    }

    /// <summary>Per-test isolated host — mirrors CentralizedExceptionHandlingAcceptanceTests.</summary>
    public sealed class IsolatedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"issue50-{Guid.NewGuid():N}";

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
    }

    // ── Wire DTOs (lenient deserialization — only the fields we assert) ──

    private sealed record EnvelopeBody(
        bool Success,
        object? Data,
        string? ErrorCode,
        string? ErrorMessage,
        string[]? NameBound);
}
