using TenE0.Core.Files;

namespace TenE0.Core.Tests.Files;

public sealed class FilesModelsTests
{
    #region StorageBackend

    [Fact]
    public void StorageBackend_ShouldHaveExpectedValues()
    {
        var values = Enum.GetValues<StorageBackend>().ToList();

        values.Should().Contain(StorageBackend.Local);
        values.Should().Contain(StorageBackend.AliyunOss);
        values.Should().Contain(StorageBackend.AwsS3);
        values.Should().HaveCount(3);
    }

    #endregion

    #region UploadRequest

    [Fact]
    public void UploadRequest_Defaults_ShouldBeCorrect()
    {
        var req = new UploadRequest();

        req.Backend.Should().Be(StorageBackend.Local);
        req.Category.Should().BeNull();
        req.RelatedEntityId.Should().BeNull();
        req.RelatedEntityType.Should().BeNull();
    }

    [Fact]
    public void UploadRequest_CanSetAllProperties()
    {
        var req = new UploadRequest
        {
            Backend = StorageBackend.AliyunOss,
            Category = "avatar",
            RelatedEntityId = "user-123",
            RelatedEntityType = "User"
        };

        req.Backend.Should().Be(StorageBackend.AliyunOss);
        req.Category.Should().Be("avatar");
        req.RelatedEntityId.Should().Be("user-123");
        req.RelatedEntityType.Should().Be("User");
    }

    #endregion

    #region UploadResponse

    [Fact]
    public void UploadResponse_Constructor_ShouldSetAllFields()
    {
        var resp = new UploadResponse("id", "file.jpg", "/storage/file.jpg", "https://cdn/file.jpg", 1024, "image/jpeg");

        resp.Id.Should().Be("id");
        resp.FileName.Should().Be("file.jpg");
        resp.StoragePath.Should().Be("/storage/file.jpg");
        resp.AccessUrl.Should().Be("https://cdn/file.jpg");
        resp.FileSize.Should().Be(1024);
        resp.ContentType.Should().Be("image/jpeg");
    }

    #endregion

    #region ImageProcessOptions

    [Fact]
    public void ImageProcessOptions_Defaults_ShouldBeCorrect()
    {
        var opts = new ImageProcessOptions();

        opts.Width.Should().BeNull();
        opts.Height.Should().BeNull();
        opts.WatermarkText.Should().BeNull();
        opts.Quality.Should().Be(85);
        opts.GenerateThumbnail.Should().BeFalse();
        opts.ThumbnailWidth.Should().Be(200);
        opts.ThumbnailHeight.Should().Be(200);
    }

    [Fact]
    public void ImageProcessOptions_CanSetAllProperties()
    {
        var opts = new ImageProcessOptions
        {
            WatermarkText = "© 10E0",
            ThumbnailWidth = 300,
            ThumbnailHeight = 150
        };

        opts.WatermarkText.Should().Be("© 10E0");
        opts.ThumbnailWidth.Should().Be(300);
        opts.ThumbnailHeight.Should().Be(150);
        opts.Quality.Should().Be(85, "default quality should be preserved");
    }

    #endregion

    #region ImageProcessResult

    [Fact]
    public void ImageProcessResult_Success_ShouldHaveCorrectValues()
    {
        var stream = new MemoryStream();
        var result = new ImageProcessResult(stream, 800, 600, 4096, true);

        result.Width.Should().Be(800);
        result.Height.Should().Be(600);
        result.FileSize.Should().Be(4096);
        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void ImageProcessResult_Failure_ShouldHaveErrorMessage()
    {
        var result = new ImageProcessResult(Stream.Null, 0, 0, 0, false, "Image format not supported");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Image format not supported");
    }

    #endregion

    #region FileResponse

    [Fact]
    public void FileResponse_AllFields_ShouldBeSettable()
    {
        var resp = new FileResponse(
            "id-123", "photo.png", "image/png", 2048,
            "https://cdn/photo.png", "https://cdn/thumb.png",
            1920, 1080, "photos", DateTimeOffset.UtcNow);

        resp.Id.Should().Be("id-123");
        resp.FileName.Should().Be("photo.png");
        resp.Width.Should().Be(1920);
        resp.Height.Should().Be(1080);
        resp.Category.Should().Be("photos");
        resp.ThumbnailUrl.Should().Be("https://cdn/thumb.png");
    }

    [Fact]
    public void FileResponse_NullableFields_ShouldAcceptNull()
    {
        var resp = new FileResponse("id", "doc.pdf", "application/pdf", 512, "https://cdn/doc.pdf", null, null, null, null, null);

        resp.ThumbnailUrl.Should().BeNull();
        resp.Width.Should().BeNull();
        resp.Height.Should().BeNull();
        resp.Category.Should().BeNull();
        resp.CreateTime.Should().BeNull();
    }

    #endregion

    #region TenE0FileAttachment

    [Fact]
    public void TenE0FileAttachment_InheritsFromBaseEntity()
    {
        var attachment = new TenE0FileAttachment
        {
            FileName = "test.jpg",
            StoragePath = "/uploads/test.jpg",
            ContentType = "image/jpeg",
            StorageBackend = "Local"
        };

        attachment.Should().BeOfType<TenE0FileAttachment>();
        attachment.FileName.Should().Be("test.jpg");
        attachment.IsDeleted.Should().BeFalse();
    }

    #endregion
}
