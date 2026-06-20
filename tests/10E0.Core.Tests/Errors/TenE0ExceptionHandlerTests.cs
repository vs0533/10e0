using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.Errors;
using TenE0.Core.Permissions.Behaviors;

namespace TenE0.Core.Tests.Errors;

/// <summary>
/// Unit tests for <see cref="TenE0ExceptionHandler"/>: confirms that the
/// handler routes every exception type through <see cref="IApiErrorMapper"/>
/// and emits the correct status code + ApiResult JSON body.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenE0ExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_PermissionDeniedException_Writes403AndApiResultJson()
    {
        // Arrange
        var handler = CreateHandler();
        var ctx = NewHttpContext();
        var ex = new PermissionDeniedException(
            commandName: "X",
            requiredKeys: new[] { "x.perm" });

        // Act
        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        // Assert
        handled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ctx.Response.ContentType.Should().Be("application/json; charset=utf-8");
        var body = await ReadBodyAsync(ctx);
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("errorCode").GetString().Should().Be("PERM_DENIED");
        body.GetProperty("errorMessage").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TryHandleAsync_ArgumentException_Writes400AndValidationErrorCode()
    {
        // Arrange
        var handler = CreateHandler();
        var ctx = NewHttpContext();
        var ex = new ArgumentException("bad input");

        // Act
        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        // Assert
        handled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = await ReadBodyAsync(ctx);
        body.GetProperty("errorCode").GetString().Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task TryHandleAsync_InvalidOperationException_Writes400AndInvalidOperationCode()
    {
        // Arrange
        var handler = CreateHandler();
        var ctx = NewHttpContext();
        var ex = new InvalidOperationException("domain rule violation");

        // Act
        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        // Assert
        handled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = await ReadBodyAsync(ctx);
        body.GetProperty("errorCode").GetString().Should().Be("INVALID_OPERATION");
    }

    [Fact]
    public async Task TryHandleAsync_UnexpectedException_Writes500AndDoesNotLeakMessage()
    {
        // Arrange
        var handler = CreateHandler();
        var ctx = NewHttpContext();
        var ex = new Exception("secret detail: /etc/passwd, SELECT * FROM secrets");

        // Act
        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        // Assert
        handled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var body = await ReadBodyAsync(ctx);
        body.GetProperty("errorCode").GetString().Should().Be("INTERNAL_ERROR");
        body.GetProperty("errorMessage").GetString().Should().Be("Internal server error");
        // Original message must not appear anywhere in the response body.
        var raw = body.GetRawText();
        raw.Should().NotContain("/etc/passwd", "internal paths must never leak to the client");
        raw.Should().NotContain("SELECT * FROM secrets", "raw SQL must never leak to the client");
    }

    [Fact]
    public async Task TryHandleAsync_NullHttpContext_ThrowsArgumentNullException()
    {
        // Arrange
        var handler = CreateHandler();
        var ex = new InvalidOperationException("x");

        // Act
        var act = () => handler.TryHandleAsync(null!, ex, CancellationToken.None).AsTask();

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task TryHandleAsync_NullException_ThrowsArgumentNullException()
    {
        // Arrange
        var handler = CreateHandler();
        var ctx = NewHttpContext();

        // Act
        var act = () => handler.TryHandleAsync(ctx, null!, CancellationToken.None).AsTask();

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task TryHandleAsync_ResetsStatusCode_BeforeWritingJson()
    {
        // Arrange — simulate a half-written response (e.g. a streaming
        // endpoint that threw mid-stream after setting status 200). The
        // handler must overwrite the status code with the mapped status
        // so the client never sees a misleading 200 alongside a 4xx body.
        var handler = CreateHandler();
        var ctx = NewHttpContext();
        ctx.Response.StatusCode = 200;

        // Act
        var handled = await handler.TryHandleAsync(
            ctx, new InvalidOperationException("oops"), CancellationToken.None);

        // Assert
        handled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest,
            "the handler must overwrite any pre-existing status with the mapped status");
    }

    // ── DI registration smoke test ──────────────────────────────

    [Fact]
    public void AddTenE0ExceptionHandler_RegistersMapperAndHandler()
    {
        // Arrange
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTenE0ExceptionHandler();

        // Assert
        var sp = services.BuildServiceProvider();
        sp.GetService<IApiErrorMapper>().Should().NotBeNull()
            .And.BeOfType<DefaultApiErrorMapper>();
        sp.GetServices<Microsoft.AspNetCore.Diagnostics.IExceptionHandler>()
            .Should().ContainSingle(h => h is TenE0ExceptionHandler);
    }

    [Fact]
    public void AddTenE0ExceptionHandler_IsIdempotent()
    {
        // Arrange
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTenE0ExceptionHandler();
        services.AddTenE0ExceptionHandler();

        // Assert — TryAddSingleton + TryAddEnumerable: only one mapper,
        // only one handler (no duplicate registrations on re-bootstrap).
        var sp = services.BuildServiceProvider();
        sp.GetServices<Microsoft.AspNetCore.Diagnostics.IExceptionHandler>()
            .OfType<TenE0ExceptionHandler>().Should().HaveCount(1);
    }

    // ── Helpers ────────────────────────────────────────────────

    private static TenE0ExceptionHandler CreateHandler() =>
        new(new DefaultApiErrorMapper(), NullLogger<TenE0ExceptionHandler>.Instance);

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
        // Clone so the element survives doc disposal.
        return doc.RootElement.Clone();
    }
}
