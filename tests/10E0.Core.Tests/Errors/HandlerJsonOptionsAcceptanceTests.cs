using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenE0.Core.Errors;
using TenE0.Core.Permissions.Behaviors;

namespace TenE0.Core.Tests.Errors;

/// <summary>
/// BDD acceptance tests for issue #49 — followup from PR #48:
/// inject <c>IOptions&lt;JsonOptions&gt;</c> (the
/// <c>Microsoft.AspNetCore.Http.Json</c> one) into
/// <see cref="TenE0ExceptionHandler"/> so the error envelope is serialized
/// with the SAME <see cref="JsonSerializerOptions"/> the API host uses for
/// the success path.
///
/// Why this matters: today the handler hard-codes
/// <c>new JsonSerializerOptions { PropertyNamingPolicy = CamelCase }</c>.
/// The success path serializes with the host-configured
/// <c>Microsoft.AspNetCore.Http.Json.JsonOptions.SerializerOptions</c>
/// (which may set <c>DefaultIgnoreCondition = WhenWritingNull</c>,
/// <c>DictionaryKeyPolicy</c>, <c>NumberHandling</c>,
/// <c>WriteIndented</c>, etc.). The two paths can subtly diverge on the
/// wire — e.g. the error envelope emits <c>"data": null</c> while the
/// success envelope omits it. That divergence is exactly what #49 has to
/// eliminate: a single DTO must deserialize both success and error
/// responses consistently.
///
/// Each scenario encodes a Given/When/Then business behavior. Today
/// <see cref="TenE0ExceptionHandler"/> has no constructor parameter for
/// <see cref="IOptions{TOptions}"/> and no DI hookup for
/// <c>IOptions&lt;JsonOptions&gt;</c>, so these tests fail to compile —
/// the RED state the issue asks for.
/// </summary>
[Trait("Category", "BDD")]
public sealed class HandlerJsonOptionsAcceptanceTests
{
    // ── Core wire-shape pin from the issue body ─────────────────
    //
    // "error body omits null data (or whatever the project standardizes
    //  on) — same shape as success." The host configures
    // JsonOptions.SerializerOptions.DefaultIgnoreCondition = WhenWritingNull
    // so a null `data` is dropped from the success envelope. After #49 the
    // error envelope MUST honor the same option. This is the headline
    // scenario of the issue and the regression we cannot afford to lose.

    [Fact]
    public async Task GivenJsonOptionsWithIgnoreNullCondition_WhenHandlingException_ThenErrorBodyOmitsNullDataField()
    {
        // Arrange — IOptions<JsonOptions> configured with WhenWritingNull,
        // mirroring the host's expected configuration (and matching the
        // success-path behavior on every other endpoint).
        var jsonOptions = BuildJsonOptions(
            JsonIgnoreCondition.WhenWritingNull);
        var handler = CreateHandler(jsonOptions);
        var ctx = NewHttpContext();
        // PermissionDeniedException → mapper returns 403 / PERM_DENIED;
        // the resulting ApiResult<object> has data = null on failure.
        var ex = new PermissionDeniedException(
            commandName: "CreateDemoCommand",
            requiredKeys: new[] { "demo.create" });

        // Act
        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        // Assert — handler must use the injected options, so the null `data`
        // property is dropped (WhenWritingNull) and never reaches the wire.
        handled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        var raw = await ReadRawBodyAsync(ctx);

        // The null `data` field must NOT appear on the wire, because the
        // success path under the same JsonOptions also omits it. This is
        // the exact divergence issue #49 fixes.
        raw.Should().NotContain("\"data\"",
            "when DefaultIgnoreCondition=WhenWritingNull is configured the " +
            "null `data` must be dropped from the error envelope so the wire " +
            "shape matches the success envelope (issue #49 core pin)");

        // The required error fields must still be present — we are NOT
        // accidentally dropping the whole body, just the null `data`.
        raw.Should().Contain("\"success\":false");
        raw.Should().Contain("\"errorCode\":\"PERM_DENIED\"");
    }

    // ── Naming policy alignment ─────────────────────────────────
    //
    // The host's JsonOptions sets PropertyNamingPolicy = CamelCase so
    // success envelopes expose `errorCode` / `errorMessage` / `success`.
    // After #49 the error envelope MUST use the same policy; if the
    // handler falls back to its private hard-coded options, the error
    // body would still be camelCase here — but a future change to the
    // host's naming policy (snake_case, PascalCase) must propagate. This
    // test pins that propagation.

    [Fact]
    public async Task GivenJsonOptionsWithCamelCasePolicy_WhenHandlingException_ThenErrorBodyUsesCamelCaseKeys()
    {
        // Arrange — explicit camelCase on the injected options so the
        // test fails if the handler ever falls back to defaults.
        // Arrange — explicit camelCase on the injected options so the
        // test fails if the handler ever falls back to defaults.
        var jsonOptions = BuildJsonOptions(); // CamelCase + no WhenWritingNull
        var handler = CreateHandler(jsonOptions);
        var ctx = NewHttpContext();
        var ex = new ArgumentException("name must not be empty", "name");

        // Act
        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        // Assert — every wire key is camelCase, matching the success path.
        handled.Should().BeTrue();
        var body = await ReadBodyAsync(ctx);

        body.TryGetProperty("success", out _).Should().BeTrue(
            "the lowerCamelCase `success` key must be present (matches success envelope)");
        body.TryGetProperty("errorCode", out var errorCode).Should().BeTrue(
            "the lowerCamelCase `errorCode` key must be present (matches success envelope)");
        errorCode.GetString().Should().Be("VALIDATION_ERROR");
        body.TryGetProperty("errorMessage", out var errorMessage).Should().BeTrue();
        errorMessage.GetString().Should().Be("name must not be empty");
    }

    // ── Propagation of non-default host options ─────────────────
    //
    // CamelCase alone wouldn't prove the handler reads the injected
    // options (the hard-coded fallback also produces camelCase). We
    // therefore set an option the hard-coded fallback CAN'T produce —
    // WriteIndented = true. The handler's private static options leave
    // WriteIndented at its default (false). Setting it on the injected
    // options proves the handler actually reads them. If the handler
    // ignored IOptions<JsonOptions>, the wire would NOT show the
    // custom indentation behavior.

    [Fact]
    public async Task GivenJsonOptionsWithWriteIndentedTrue_WhenHandlingException_ThenErrorBodyIsPrettyPrinted()
    {
        // Arrange — WriteIndented is intentionally NOT set on the
        // handler's hard-coded fallback (so the default is `false`).
        // Setting it on the injected options proves the handler
        // actually reads them.
        var jsonOptions = new JsonOptions();
        jsonOptions.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        jsonOptions.SerializerOptions.WriteIndented = true;
        var handler = CreateHandler(jsonOptions);
        var ctx = NewHttpContext();
        var ex = new InvalidOperationException("domain rule violation");

        // Act
        await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        // Assert — pretty-printed JSON inserts newlines + indentation
        // between fields. The handler's hard-coded fallback does not,
        // so this assertion fails if IOptions<JsonOptions> is ignored.
        var raw = await ReadRawBodyAsync(ctx);
        raw.Should().Contain("\n",
            "the injected JsonOptions has WriteIndented=true, so the error body " +
            "must be pretty-printed with newlines — proving the handler uses the " +
            "injected options rather than its hard-coded fallback (issue #49)");
        raw.Should().Contain("  \"",
            "pretty-printed JSON indents nested fields with spaces; this is the " +
            "default indentation produced by WriteIndented=true");
    }

    // ── DI registration: handler must be resolvable when the host
    //    has registered IOptions<JsonOptions> via ConfigureHttpJsonOptions.
    //    Today the handler has no IOptions<JsonOptions> dependency so the
    //    custom hookup is impossible. After #49 the registration must
    //    succeed end-to-end, otherwise the bug is hidden behind a
    //    compile-time crash in the host.

    [Fact]
    public async Task GivenServicesWithConfiguredJsonOptions_WhenResolvingHandler_ThenHandlerReceivesInjectedOptions()
    {
        // Arrange — minimal host-like service collection: logging + a
        // configured JsonOptions (mirrors `builder.Services.ConfigureHttpJsonOptions(...)`).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenE0ExceptionHandler();
        services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        // Act — resolve the handler via DI
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetServices<Microsoft.AspNetCore.Diagnostics.IExceptionHandler>()
            .OfType<TenE0ExceptionHandler>()
            .Single();

        // Assert — the handler must accept an IOptions<JsonOptions>
        // dependency and use it. We can't reach into a private field,
        // so we verify behavior: invoking the handler with the DI-
        // configured options produces a wire shape consistent with those
        // options (null `data` omitted).
        var ctx = NewHttpContext();
        var ex = new PermissionDeniedException(
            commandName: "X", requiredKeys: new[] { "x.perm" });

        await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        var raw = await ReadRawBodyAsync(ctx);
        raw.Should().NotContain("\"data\"",
            "the DI-resolved handler must read the host's configured " +
            "JsonOptions (WhenWritingNull) so the error envelope matches " +
            "the success envelope — proving the IOptions<JsonOptions> " +
            "dependency is wired end-to-end (issue #49)");
        raw.Should().Contain("\"errorCode\":\"PERM_DENIED\"");
    }

    // ── DI registration: handler must remain idempotent even after it
    //    takes a new dependency. The existing exception-handler tests
    //    rely on this; #49 must not silently flip it to scoped or
    //    double-register the handler.

    [Fact]
    public void GivenHandlerRegisteredViaAddTenE0ExceptionHandler_WhenResolved_ThenRegistrationStaysIdempotentSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTenE0ExceptionHandler();
        services.AddTenE0ExceptionHandler();

        // Assert — adding the new IOptions<JsonOptions> dependency must
        // NOT make the handler registered twice or change its lifetime.
        using var sp = services.BuildServiceProvider();
        var handlers = sp.GetServices<Microsoft.AspNetCore.Diagnostics.IExceptionHandler>()
            .OfType<TenE0ExceptionHandler>()
            .ToList();

        handlers.Should().ContainSingle(
            "the existing TryAddEnumerable contract must survive the new " +
            "IOptions<JsonOptions> dependency (issue #49 must not regress #39)");
    }

    // ── Null-guard contract: even with IOptions<JsonOptions>
    //    plumbed in, the handler must still refuse null httpContext /
    //    exception inputs (existing contract from #39).

    [Fact]
    public async Task GivenInjectedJsonOptions_WhenHandlingWithNullHttpContext_ThenArgumentNullExceptionIsThrown()
    {
        // Arrange
        var jsonOptions = BuildJsonOptions();
        var handler = CreateHandler(jsonOptions);
        var ex = new InvalidOperationException("x");

        // Act
        var act = () => handler.TryHandleAsync(null!, ex, CancellationToken.None).AsTask();

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>(
            "the existing null-httpContext contract from #39 must survive #49");
    }

    // ── Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="JsonOptions"/> with the project-standard
    /// <c>CamelCase</c> naming policy and an optional
    /// <c>DefaultIgnoreCondition</c>. <see cref="JsonOptions.SerializerOptions"/>
    /// is a read-only property, so we mutate the default instance rather
    /// than swapping it out — this mirrors how the host's
    /// <c>ConfigureHttpJsonOptions</c> callback actually configures the
    /// options in production.
    /// </summary>
    private static JsonOptions BuildJsonOptions(
        JsonIgnoreCondition? ignoreCondition = null)
    {
        var jsonOptions = new JsonOptions();
        jsonOptions.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        if (ignoreCondition is not null)
        {
            jsonOptions.SerializerOptions.DefaultIgnoreCondition = ignoreCondition.Value;
        }
        return jsonOptions;
    }

    /// <summary>
    /// Constructs the handler under test with an injected
    /// <see cref="IOptions{JsonOptions}"/>. Today the handler ctor takes
    /// only <c>(IApiErrorMapper, ILogger&lt;...&gt;)</c>, so this helper
    /// intentionally fails to compile until #49 lands.
    /// </summary>
    private static TenE0ExceptionHandler CreateHandler(JsonOptions jsonOptions) =>
        new(
            new DefaultApiErrorMapper(),
            NullLogger<TenE0ExceptionHandler>.Instance,
            Microsoft.Extensions.Options.Options.Create(jsonOptions));

    private static HttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        return doc.RootElement.Clone();
    }

    private static async Task<string> ReadRawBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return await reader.ReadToEndAsync();
    }
}
