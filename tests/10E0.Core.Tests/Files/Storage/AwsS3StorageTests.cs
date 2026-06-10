using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Moq;
using TenE0.Core.Files;
using TenE0.Core.Files.Storage;

namespace TenE0.Core.Tests.Files.Storage;

/// <summary>
/// Tests for <see cref="AwsS3Storage"/> that exercise the v4 SDK API surface
/// (<c>Amazon.S3</c> namespace is preserved in v4) using a mocked
/// <see cref="IAmazonS3"/> — the storage was previously untestable because the
/// client was constructed internally from credentials.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AwsS3StorageTests
{
    private const string ValidAccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string ValidSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string ValidRegion = "us-east-1";
    private const string ValidBucket = "my-bucket";

    private static AwsS3Options ValidOptions() => new()
    {
        AccessKey = ValidAccessKey,
        SecretKey = ValidSecretKey,
        Region = ValidRegion,
        BucketName = ValidBucket
    };

    private static AwsS3Storage Create(
        IAmazonS3 s3Client,
        AwsS3Options? options = null) =>
        new(options ?? ValidOptions(), s3Client);

    // ---------------------------------------------------------------------
    // Construction-time validation (security-critical: must reject placeholders)
    // ---------------------------------------------------------------------

    [Fact]
    public void Ctor_NullClient_Throws()
    {
        var act = () => new AwsS3Storage(ValidOptions(), client: null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("client");
    }

    [Fact]
    public void Ctor_EmptyAccessKey_ThrowsOptionsValidation()
    {
        var options = ValidOptions();
        options.AccessKey = string.Empty;
        var s3 = new Mock<IAmazonS3>().Object;

        var act = () => new AwsS3Storage(options, s3);

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch("*AccessKey*is required*");
    }

    [Fact]
    public void Ctor_PlaceholderBucket_ThrowsOptionsValidation()
    {
        var options = ValidOptions();
        options.BucketName = "your-bucket-name";
        var s3 = new Mock<IAmazonS3>().Object;

        var act = () => new AwsS3Storage(options, s3);

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch("*BucketName*contains placeholder*your-*");
    }

    [Fact]
    public void Ctor_AllValid_DoesNotThrow()
    {
        var s3 = new Mock<IAmazonS3>().Object;

        var act = () => new AwsS3Storage(ValidOptions(), s3);

        act.Should().NotThrow();
    }

    // ---------------------------------------------------------------------
    // StoreAsync — uses v4 IAmazonS3.PutObjectAsync(PutObjectRequest, CancellationToken)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StoreAsync_ValidStream_CallsPutAndReturnsKey()
    {
        var s3 = new Mock<IAmazonS3>();
        PutObjectRequest? captured = null;
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
          .Callback<PutObjectRequest, CancellationToken>((r, _) => captured = r)
          .ReturnsAsync(new PutObjectResponse { HttpStatusCode = System.Net.HttpStatusCode.OK });
        var sut = Create(s3.Object);

        using var ms = new MemoryStream("payload"u8.ToArray());
        var result = await sut.StoreAsync(ms, "hello.txt", "text/plain");

        result.Success.Should().BeTrue();
        result.StoragePath.Should().EndWith(".txt");
        result.StoragePath.Should().MatchRegex(@"^\d{4}/\d{2}/.+\.txt$");
        result.AccessUrl.Should().Be($"https://{ValidBucket}.s3.{ValidRegion}.amazonaws.com/{result.StoragePath}");

        captured.Should().NotBeNull();
        captured!.BucketName.Should().Be(ValidBucket);
        captured.ContentType.Should().Be("text/plain");
        captured.Key.Should().Be(result.StoragePath);
    }

    [Fact]
    public async Task StoreAsync_S3Throws_ReturnsFailureResult()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(new AmazonS3Exception("boom"));
        var sut = Create(s3.Object);

        using var ms = new MemoryStream("x"u8.ToArray());
        var result = await sut.StoreAsync(ms, "f.bin", "application/octet-stream");

        result.Success.Should().BeFalse();
        result.StoragePath.Should().BeEmpty();
        result.ErrorMessage.Should().Contain("boom");
    }

    // ---------------------------------------------------------------------
    // RetrieveAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RetrieveAsync_ExistingKey_ReturnsStreamContent()
    {
        var s3 = new Mock<IAmazonS3>();
        var payload = "hello world"u8.ToArray();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new GetObjectResponse
          {
              ResponseStream = new MemoryStream(payload)
          });
        var sut = Create(s3.Object);

        var stream = await sut.RetrieveAsync("2026/06/abc.txt");

        stream.Should().NotBeNull();
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task RetrieveAsync_S3Throws_ReturnsNull()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(new AmazonS3Exception("missing"));
        var sut = Create(s3.Object);

        var stream = await sut.RetrieveAsync("nope.txt");

        stream.Should().BeNull();
    }

    // ---------------------------------------------------------------------
    // DeleteAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_Success_ReturnsTrue()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new DeleteObjectResponse { HttpStatusCode = System.Net.HttpStatusCode.NoContent });
        var sut = Create(s3.Object);

        (await sut.DeleteAsync("any/key")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_S3Throws_ReturnsFalse()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(new AmazonS3Exception("denied"));
        var sut = Create(s3.Object);

        (await sut.DeleteAsync("any/key")).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // ExistsAsync — must use GetObjectMetadataAsync (not GetObject) to avoid download
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ExistsAsync_MetadataOk_ReturnsTrue()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(x => x.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new GetObjectMetadataResponse());
        var sut = Create(s3.Object);

        (await sut.ExistsAsync("k")).Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_NotFound_ReturnsFalse()
    {
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(x => x.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(new AmazonS3Exception("404"));
        var sut = Create(s3.Object);

        (await sut.ExistsAsync("missing")).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // GetAccessUrl — pure URL composition
    // ---------------------------------------------------------------------

    [Fact]
    public void GetAccessUrl_ReturnsVirtualHostedStyleUrl()
    {
        var sut = Create(new Mock<IAmazonS3>().Object);

        sut.GetAccessUrl("2026/06/x.png")
           .Should().Be($"https://{ValidBucket}.s3.{ValidRegion}.amazonaws.com/2026/06/x.png");
    }
}
