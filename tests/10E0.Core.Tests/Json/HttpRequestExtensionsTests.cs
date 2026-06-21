using Microsoft.AspNetCore.Http;
using TenE0.Core.Json;

namespace TenE0.Core.Tests.Json;

[Trait("Category", "Unit")]
public sealed class HttpRequestExtensionsTests
{
    [Fact]
    public async Task GetPostedPropertiesAsync_WithSeekableStream_ReturnsPaths()
    {
        var json = """{"name":"test","email":"x@y.com"}"""u8.ToArray();
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(json);
        context.Request.ContentType = "application/json";

        var props = await context.Request.GetPostedPropertiesAsync();

        props.Should().Contain("name");
        props.Should().Contain("email");
    }

    [Fact]
    public async Task GetPostedPropertiesAsync_NonSeekableStream_BuffersAndReturnsPaths()
    {
        var json = """{"key":"val"}"""u8.ToArray();
        var context = new DefaultHttpContext();
        context.Request.Body = new NonSeekableStream(json);
        context.Request.ContentType = "application/json";

        var props = await context.Request.GetPostedPropertiesAsync();

        props.Should().Contain("key");
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
