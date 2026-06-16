using Amazon.S3;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using TenE0.Core.Files;
using TenE0.Core.Files.Storage;

namespace TenE0.Core.Tests.Files.Storage;

/// <summary>
/// Acceptance tests for issue #38 — verify all three FileStorage backends derive
/// their generated storage path/key from an injected <see cref="TimeProvider"/>
/// (via <see cref="FakeTimeProvider"/>) instead of <c>DateTime.Now</c>.
/// <para>
/// Given a frozen clock at 2026-06-11 UTC, when a file is stored, then the
/// resulting path/key must contain the "2026/06" segment.
/// </para>
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class FileStorageTimeProviderAcceptanceTests : IDisposable
{
    private static readonly DateTimeOffset FrozenUtc =
        new(2026, 6, 11, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _timeProvider = new(FrozenUtc);
    private readonly string _tempDir;

    public FileStorageTimeProviderAcceptanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"10e0-tp-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // ---------------------------------------------------------------------
    // LocalFileStorage — GenerateStoragePath must use TimeProvider.GetUtcNow()
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GivenFrozenClockAt2026June_WhenLocalFileStorageStoresFile_ThenStoragePathContains2026Slash06()
    {
        // Arrange
        var options = Options.Create(new LocalStorageOptions
        {
            BasePath = _tempDir,
            BaseUrl = "http://localhost/uploads"
        });
        // LocalFileStorage(TimeProvider, IOptions<LocalStorageOptions>) — expected after #38 fix
        var sut = new LocalFileStorage(_timeProvider, options);

        using var stream = new MemoryStream("payload"u8.ToArray());

        // Act
        var result = await sut.StoreAsync(stream, "report.pdf", "application/pdf");

        // Assert
        result.Success.Should().BeTrue();
        result.StoragePath.Should().NotBeNullOrEmpty();
        result.StoragePath.Should().Contain("2026/06");
    }

    // ---------------------------------------------------------------------
    // AliyunOssStorage — GenerateObjectKey must use TimeProvider.GetUtcNow()
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GivenFrozenClockAt2026June_WhenAliyunOssStorageStoresFile_ThenObjectKeyContains2026Slash06()
    {
        // Arrange — valid options to bypass placeholder rejection
        var options = new AliyunOssOptions
        {
            Endpoint = "oss-cn-hangzhou.aliyuncs.com",
            AccessKeyId = "LTAI5tFakeKeyIdForTesting",
            AccessKeySecret = "FakeSecretForTestingPurposesOnly",
            BucketName = "test-bucket"
        };
        // AliyunOssStorage(TimeProvider, AliyunOssOptions) — expected after #38 fix.
        // Constructed with FakeTimeProvider; does not hit network (covered by StoreAsync try/catch
        // for verification of the generated key).
        var sut = new AliyunOssStorage(_timeProvider, options);

        using var stream = new MemoryStream("payload"u8.ToArray());

        // Act — store will fail at the SDK call (no real OSS endpoint), but GenerateObjectKey
        // runs before the upload, so the failure result still exposes the generated key.
        var result = await sut.StoreAsync(stream, "report.pdf", "application/pdf");

        // Assert — regardless of upload success, the key was generated from TimeProvider
        result.StoragePath.Should().Contain("2026/06");
    }

    // ---------------------------------------------------------------------
    // AwsS3Storage — GenerateObjectKey must use TimeProvider.GetUtcNow()
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GivenFrozenClockAt2026June_WhenAwsS3StorageStoresFile_ThenObjectKeyContains2026Slash06()
    {
        // Arrange — capture the PutObjectRequest.Key to verify the generated path
        var s3 = new Mock<IAmazonS3>();
        string? capturedKey = null;
        s3.Setup(x => x.PutObjectAsync(It.IsAny<Amazon.S3.Model.PutObjectRequest>(), It.IsAny<CancellationToken>()))
          .Callback<Amazon.S3.Model.PutObjectRequest, CancellationToken>((r, _) => capturedKey = r.Key)
          .ReturnsAsync(new Amazon.S3.Model.PutObjectResponse
          {
              HttpStatusCode = System.Net.HttpStatusCode.OK
          });

        var options = new AwsS3Options
        {
            AccessKey = "AKIAIOSFODNN7EXAMPLE",
            SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            Region = "us-east-1",
            BucketName = "test-bucket"
        };

        // AwsS3Storage(TimeProvider, AwsS3Options, IAmazonS3) — expected after #38 fix
        var sut = new AwsS3Storage(_timeProvider, options, s3.Object);

        using var stream = new MemoryStream("payload"u8.ToArray());

        // Act
        var result = await sut.StoreAsync(stream, "report.pdf", "application/pdf");

        // Assert
        result.Success.Should().BeTrue();
        capturedKey.Should().NotBeNullOrEmpty();
        capturedKey.Should().Contain("2026/06");
        result.StoragePath.Should().Contain("2026/06");
    }
}
