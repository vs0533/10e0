using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Common;
using TenE0.Core.Errors;

namespace TenE0.Core.Tests.Errors;

/// <summary>
/// Unit tests for <see cref="ApiResultResult"/>: confirms that the static
/// facade correctly maps an <see cref="ApiResult{T}"/> to
/// <c>200 OK</c> on success and <c>400 Bad Request</c> on failure, so
/// Minimal API handlers can use it without an extension-method receiver.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ApiResultResultTests
{
    [Fact]
    public async Task Api_WithSuccess_ReturnsHttp200()
    {
        // Arrange
        var result = ApiResult<string>.Ok("hello");

        // Act
        var response = ToHttpResponse(ApiResultResult.Api(result));

        // Assert
        response.StatusCode.Should().Be(200);
        var raw = await ReadBodyAsync(response);
        raw.GetProperty("success").GetBoolean().Should().BeTrue();
        raw.GetProperty("data").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task Api_WithFailure_ReturnsHttp400()
    {
        // Arrange
        var result = ApiResult<string>.Fail("validation failed", code: "VALIDATION_ERROR");

        // Act
        var response = ToHttpResponse(ApiResultResult.Api(result));

        // Assert
        response.StatusCode.Should().Be(400);
        var raw = await ReadBodyAsync(response);
        raw.GetProperty("success").GetBoolean().Should().BeFalse();
        raw.GetProperty("errorCode").GetString().Should().Be("VALIDATION_ERROR");
        raw.GetProperty("errorMessage").GetString().Should().Be("validation failed");
    }

    [Fact]
    public async Task Api_WithObjectPayload_PreservesDataShape()
    {
        // Arrange — anonymous objects (the common POST /demo case)
        var id = "new-id-123";
        var result = ApiResult<object>.Ok(new { id });

        // Act
        var response = ToHttpResponse(ApiResultResult.Api(result));

        // Assert
        response.StatusCode.Should().Be(200);
        var raw = await ReadBodyAsync(response);
        raw.GetProperty("data").GetProperty("id").GetString().Should().Be(id);
    }

    // ── Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Executes the IResult against a real <see cref="HttpContext"/> so we
    /// can inspect status code + body without a network.
    /// </summary>
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

    private static async Task<System.Text.Json.JsonElement> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Position = 0;
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(response.Body);
        return doc.RootElement.Clone();
    }
}
