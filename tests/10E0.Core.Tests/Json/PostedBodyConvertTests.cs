using TenE0.Core.Json;

namespace TenE0.Core.Tests.Json;

public sealed class PostedBodyConvertTests
{
    #region ExtractPaths - string overload

    [Fact]
    public void ExtractPaths_EmptyString_ShouldReturnEmpty()
    {
        var result = PostedBodyConvert.ExtractPaths("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractPaths_Null_ShouldReturnEmpty()
    {
        var result = PostedBodyConvert.ExtractPaths(null!);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractPaths_Whitespace_ShouldReturnEmpty()
    {
        var result = PostedBodyConvert.ExtractPaths("   ");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractPaths_InvalidJson_ShouldReturnEmpty()
    {
        var result = PostedBodyConvert.ExtractPaths("{ not valid json }");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractPaths_Primitive_ShouldReturnEmpty()
    {
        // Primitive values (string, number, bool) don't produce paths
        var result1 = PostedBodyConvert.ExtractPaths("\"hello\"");
        var result2 = PostedBodyConvert.ExtractPaths("42");
        var result3 = PostedBodyConvert.ExtractPaths("true");

        result1.Should().BeEmpty();
        result2.Should().BeEmpty();
        result3.Should().BeEmpty();
    }

    [Fact]
    public void ExtractPaths_SimpleObject_ShouldReturnPropertyNames()
    {
        var result = PostedBodyConvert.ExtractPaths(@"{""name"": ""张三"", ""age"": 30}");

        result.Should().BeEquivalentTo("name", "age");
    }

    [Fact]
    public void ExtractPaths_NestedObject_ShouldReturnDottedPaths()
    {
        var result = PostedBodyConvert.ExtractPaths(@"{""address"": {""city"": ""北京"", ""zip"": ""100000""}}");

        result.Should().Contain("address");
        result.Should().Contain("address.city");
        result.Should().Contain("address.zip");
    }

    [Fact]
    public void ExtractPaths_FlatAndNested_ShouldReturnAllPaths()
    {
        var result = PostedBodyConvert.ExtractPaths(@"{""name"": ""test"", ""address"": {""city"": ""BJ""}}");

        result.Should().BeEquivalentTo("name", "address", "address.city");
    }

    [Fact]
    public void ExtractPaths_Array_ShouldNotExpandElements()
    {
        var result = PostedBodyConvert.ExtractPaths(@"{""tags"": [""a"", ""b""]}");

        result.Should().Contain("tags");
        result.Should().NotContain("tags[0]");
        result.Should().NotContain("tags[1]");
    }

    [Fact]
    public void ExtractPaths_ComplexJson_ShouldExtractAllPaths()
    {
        var result = PostedBodyConvert.ExtractPaths("""
            {
              "name": "张三",
              "address": { "city": "北京", "zip": "100000" },
              "tags": ["a", "b"]
            }
            """);

        result.Should().Contain("name");
        result.Should().Contain("address");
        result.Should().Contain("address.city");
        result.Should().Contain("address.zip");
        result.Should().Contain("tags");
    }

    [Fact]
    public void ExtractPaths_EmptyObject_ShouldReturnEmpty()
    {
        var result = PostedBodyConvert.ExtractPaths("{}");

        result.Should().BeEmpty();
    }

    #endregion

    #region ExtractPathsAsync - stream overload

    [Fact]
    public async Task ExtractPathsAsync_ValidJson_ShouldReturnPaths()
    {
        var json = @"{""name"": ""test"", ""level"": {""score"": 90}}";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var result = await PostedBodyConvert.ExtractPathsAsync(stream);

        result.Should().BeEquivalentTo("name", "level", "level.score");
    }

    [Fact]
    public async Task ExtractPathsAsync_InvalidJson_ShouldReturnEmpty()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("invalid"));

        var result = await PostedBodyConvert.ExtractPathsAsync(stream);

        result.Should().BeEmpty();
    }

    #endregion
}
