using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;
using TenE0.Core.Common;
using TenE0.Core.Errors;

namespace TenE0.Core.Tests.Common;

/// <summary>
/// BDD acceptance tests for issue #50 — unify the IErrs-driven validation-failure
/// envelope to the same <see cref="ApiResult{T}"/> shape every other endpoint
/// already uses (followup from PR #48).
///
/// The fix has three layers:
///   1. <see cref="ApiResult{T}.FromErrs"/> carries the <c>IErrs.Keys</c> snapshot
///      into <c>nameBound</c> so clients can render field-level errors.
///   2. The resulting envelope always carries <c>success = false</c> plus
///      <c>errorMessage</c> so the success/failure DTO is deserializable as
///      a single shape.
///   3. <see cref="ApiResultResult.Api"/> renders the failed envelope as
///      <c>HTTP 400</c> with the full <see cref="ApiResult{T}"/> body —
///      not a stripped-down <c>{ error: "..." }</c> payload.
///
/// Each scenario encodes a Given/When/Then business behavior. The endpoint
/// migration in <c>AuthEndpoints</c> / <c>FileEndpoints</c> / <c>AdminEndpoints</c>
/// still emits the legacy <c>{ error: "..." }</c> shape today, so the
/// Api-layer acceptance tests in <c>10E0.Api.Tests</c> fail RED. The Core-layer
/// tests here pin the lower-level contract — they must all pass already
/// (and stay green as a regression net).
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Unit")]
public sealed class UnifiedValidationEnvelopeAcceptanceTests
{
    // ── Layer 1: ApiResult<T>.FromErrs carries IErrs.Keys → nameBound ───

    [Fact]
    public void GivenIErrsWithMultipleFieldKeys_WhenFromErrs_ThenNameBoundMatchesKeysExactly()
    {
        // Arrange — handler ran and pushed field-level errors via errs.Add(message, key)
        var errs = new Mock<IErrs>();
        errs.Setup(e => e.IsValid).Returns(false);
        errs.Setup(e => e.GetFirstError()).Returns("username already taken");
        errs.Setup(e => e.Keys).Returns(new[] { "UserCode", "Email" });

        // Act — endpoint converts IErrs into the uniform envelope
        var envelope = ApiResult<object>.FromErrs(errs.Object);

        // Assert — clients deserialize nameBound to highlight the offending form fields
        envelope.nameBound.Should().BeEquivalentTo(new[] { "UserCode", "Email" },
            "every key from IErrs must surface as nameBound so the UI can field-pin errors");
    }

    [Fact]
    public void GivenIErrsWithNoFieldKeys_WhenFromErrs_ThenNameBoundIsEmpty()
    {
        // Arrange — non-field-level error (e.g. business rule violation)
        var errs = new Mock<IErrs>();
        errs.Setup(e => e.IsValid).Returns(false);
        errs.Setup(e => e.GetFirstError()).Returns("operation not allowed");
        errs.Setup(e => e.Keys).Returns(Array.Empty<string>());

        // Act
        var envelope = ApiResult<object>.FromErrs(errs.Object);

        // Assert — must NOT be null (clients branch on null vs [])
        envelope.nameBound.Should().NotBeNull(
            "nameBound must always serialize, even when empty, so the DTO shape stays stable");
        envelope.nameBound.Should().BeEmpty();
    }

    // ── Layer 2: Failed envelope is uniform with the success envelope ──

    [Fact]
    public void GivenIErrsWithFirstErrorMessage_WhenFromErrs_ThenEnvelopeCarriesSuccessFalseAndErrorMessage()
    {
        // Arrange
        var errs = new Mock<IErrs>();
        errs.Setup(e => e.IsValid).Returns(false);
        errs.Setup(e => e.GetFirstError()).Returns("用户名或密码错误");
        errs.Setup(e => e.Keys).Returns(Array.Empty<string>());

        // Act
        var envelope = ApiResult<AuthResult>.FromErrs(errs.Object);

        // Assert — the failed envelope MUST carry the same shape as the success
        // envelope so clients deserialize a single DTO either way.
        envelope.success.Should().BeFalse(
            "a failed envelope must be unambiguously marked false, " +
            "otherwise clients cannot branch on success");
        envelope.errorMessage.Should().Be("用户名或密码错误",
            "the human-readable reason must travel through the envelope, " +
            "not be lost when switching from the legacy { error } shape");
        envelope.data.Should().BeNull(
            "data must be null on failure so the envelope is honest about what it carries");
    }

    [Fact]
    public void GivenOkResult_WhenSerialized_ThenEnvelopeCarriesSuccessTrueAndData()
    {
        // Arrange — symmetric success path
        var authResult = new AuthResult("tok-1", "ref-1", DateTime.UtcNow.AddMinutes(30));

        // Act
        var envelope = ApiResult<AuthResult>.Ok(authResult);

        // Assert — the envelope shape is identical between success and failure,
        // so clients share a single DTO.
        envelope.success.Should().BeTrue();
        envelope.data.Should().BeSameAs(authResult);
        envelope.errorMessage.Should().BeNull();
    }

    // ── Layer 3: ApiResultResult.Api renders failed envelope as 400 ──

    [Fact]
    public async Task GivenFailedEnvelopeFromErrs_WhenApiResultResultApi_ThenResponseIs400WithUniformEnvelopeJson()
    {
        // Arrange — endpoint produces a failed envelope via ApiResult<T>.FromErrs
        var errs = new Mock<IErrs>();
        errs.Setup(e => e.IsValid).Returns(false);
        errs.Setup(e => e.GetFirstError()).Returns("用户名或密码错误");
        errs.Setup(e => e.Keys).Returns(new[] { "UserCode" });
        var envelope = ApiResult<object>.FromErrs(errs.Object);

        // Act
        var response = ToHttpResponse(ApiResultResult.Api(envelope));

        // Assert — HTTP status code
        response.StatusCode.Should().Be(400,
            "the IErrs-driven validation failure path must return HTTP 400, " +
            "matching the exception-driven validation path");

        // Assert — body is the full ApiResult envelope, NOT the legacy { error } shape
        var raw = await ReadBodyAsync(response);
        raw.GetProperty("success").GetBoolean().Should().BeFalse(
            "the failed body must expose `success = false` for clients to branch on");
        raw.GetProperty("errorMessage").GetString().Should().Be("用户名或密码错误",
            "the failed body must carry the human-readable error message");
        raw.TryGetProperty("error", out _).Should().BeFalse(
            "the legacy upper-level `error` field must NOT be present, " +
            "otherwise clients will pick up two error keys and confuse the UI");
        raw.TryGetProperty("nameBound", out var nb).Should().BeTrue(
            "the failed body must carry `nameBound` so the UI can field-pin errors");
        nb.GetArrayLength().Should().Be(1);
        nb[0].GetString().Should().Be("UserCode");
    }

    [Fact]
    public async Task GivenFailedEnvelopeFromErrs_WhenApiResultResultApi_ThenResponseDeclaresJsonContentType()
    {
        // Arrange
        var errs = new Mock<IErrs>();
        errs.Setup(e => e.IsValid).Returns(false);
        errs.Setup(e => e.GetFirstError()).Returns("invalid input");
        errs.Setup(e => e.Keys).Returns(Array.Empty<string>());
        var envelope = ApiResult<object>.FromErrs(errs.Object);

        // Act
        var response = ToHttpResponse(ApiResultResult.Api(envelope));

        // Assert — every API response, success or failure, must declare JSON
        response.ContentType.Should().StartWith("application/json",
            "uniform content-type across success and failure paths");
    }

    // ── Layer 4: success envelope through ApiResultResult.Api is HTTP 200 + uniform shape ─

    [Fact]
    public async Task GivenSuccessEnvelope_WhenApiResultResultApi_ThenResponseIs200WithUniformEnvelopeJson()
    {
        // Arrange — endpoint succeeded; we wrap the business payload in the
        // uniform ApiResult<T> envelope.
        var envelope = ApiResult<AuthResult>.Ok(
            new AuthResult("tok-1", "ref-1", DateTime.UtcNow.AddMinutes(30)));

        // Act
        var response = ToHttpResponse(ApiResultResult.Api(envelope));

        // Assert — HTTP 200 on success (per #39 ApiResultResult.Api contract)
        response.StatusCode.Should().Be(200,
            "the success path of ApiResultResult.Api must return HTTP 200, " +
            "so the migration to a uniform envelope does not change the success status");

        // Assert — body carries the full envelope with the payload under data
        var raw = await ReadBodyAsync(response);
        raw.GetProperty("success").GetBoolean().Should().BeTrue(
            "the success body must carry success = true so clients can branch on it");
        raw.GetProperty("data").GetProperty("accessToken").GetString().Should().Be("tok-1",
            "the success body must expose the data payload under the uniform `data` field");
        raw.GetProperty("data").GetProperty("refreshToken").GetString().Should().Be("ref-1");
        raw.TryGetProperty("errorMessage", out var em).Should().BeTrue(
            "the success envelope must still expose errorMessage (null on success) " +
            "so the client DTO has a single uniform shape");
        em.ValueKind.Should().Be(JsonValueKind.Null);
        response.ContentType.Should().StartWith("application/json");
    }

    // ── Helpers ────────────────────────────────────────────────

    private static HttpResponse ToHttpResponse(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        ctx.Response.Body = new MemoryStream();
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        ctx.Response.Body.Position = 0;
        return ctx.Response;
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(response.Body);
        return doc.RootElement.Clone();
    }

    // ── Wire DTOs (mirrors the real handler return type) ──────

    private sealed record AuthResult(string AccessToken, string RefreshToken, DateTime ExpiresAt);
}
