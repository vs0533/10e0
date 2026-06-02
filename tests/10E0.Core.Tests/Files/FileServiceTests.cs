using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Files;
using TenE0.Core.Permissions.DataFilter;

namespace TenE0.Core.Tests.Files;

[Trait("Category", "Unit")]
public sealed class FileServiceTests
{
    private sealed class TestSystemDbContext : TenE0SystemDbContext
    {
        public TestSystemDbContext(DbContextOptions options, ICurrentUserContext currentUser, IDataAccessPolicy accessPolicy)
            : base(options, currentUser, accessPolicy, Enumerable.Empty<IEntityFilterContributor>(), Mock.Of<IDynamicFilterProvider>())
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Don't call base — we don't need all framework tables
            modelBuilder.Entity<TenE0FileAttachment>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedOnAdd();
            });
        }
    }

    private static TestSystemDbContext CreateDbContext(string dbName)
    {
        var currentUser = new Mock<ICurrentUserContext>();
        currentUser.SetupGet(c => c.IsAuthenticated).Returns(true);
        currentUser.SetupGet(c => c.UserCode).Returns("test-user");
        currentUser.SetupGet(c => c.RoleIds).Returns([]);
        var accessPolicy = new Mock<IDataAccessPolicy>();
        accessPolicy.SetupGet(p => p.BypassFilters).Returns(false);
        var options = new DbContextOptionsBuilder<TestSystemDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new TestSystemDbContext(options, currentUser.Object, accessPolicy.Object);
    }

    // ──────────────────────────────────────────
    // UploadAsync
    // ──────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_ValidStream_StoresFileAndReturnsMetadata()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        var storageResult = new StorageResult("/files/test.txt", "http://test/test.txt", true);
        storage.Setup(s => s.StoreAsync(It.IsAny<Stream>(), "test.txt", "text/plain", It.IsAny<CancellationToken>()))
               .ReturnsAsync(storageResult);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        var stream = new MemoryStream("test content"u8.ToArray());

        // Act
        var response = await sut.UploadAsync(stream, "test.txt", "text/plain");

        // Assert
        response.FileName.Should().Be("test.txt");
        response.AccessUrl.Should().Be("http://test/test.txt");
        response.StoragePath.Should().Be("/files/test.txt");
        response.ContentType.Should().Be("text/plain");
        response.FileSize.Should().Be(12);
        response.Id.Should().NotBeNullOrEmpty();

        using var verifyCtx = CreateDbContext(dbName);
        var attachments = await verifyCtx.FileAttachments.ToListAsync();
        attachments.Should().HaveCount(1);
        attachments[0].FileName.Should().Be("test.txt");
        attachments[0].StoragePath.Should().Be("/files/test.txt");
        attachments[0].IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task UploadAsync_StorageFailure_ThrowsInvalidOperationException()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.StoreAsync(It.IsAny<Stream>(), "fail.txt", "text/plain", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new StorageResult("", "", false, "磁盘空间不足"));

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        var stream = new MemoryStream("test"u8.ToArray());

        // Act
        var act = () => sut.UploadAsync(stream, "fail.txt", "text/plain");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("文件存储失败: 磁盘空间不足");
    }

    [Fact]
    public async Task UploadAsync_ImageContentType_SetsWidthAndHeight()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.StoreAsync(It.IsAny<Stream>(), "photo.png", "image/png", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new StorageResult("/files/photo.png", "http://test/photo.png", true));

        var imageProcessor = new Mock<IImageProcessor>(MockBehavior.Loose);
        imageProcessor.Setup(p => p.GetDimensionsAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync((100, 50));

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            imageProcessor.Object,
            NullLogger<FileService>.Instance);

        var stream = new MemoryStream("fake image data"u8.ToArray());

        // Act
        var response = await sut.UploadAsync(stream, "photo.png", "image/png");

        // Assert
        imageProcessor.Verify(p => p.GetDimensionsAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);

        using var verifyCtx = CreateDbContext(dbName);
        var attachment = await verifyCtx.FileAttachments.FirstAsync();
        attachment.Width.Should().Be(100);
        attachment.Height.Should().Be(50);
    }

    [Fact]
    public async Task UploadAsync_NonImageContentType_DoesNotCallGetDimensions()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.StoreAsync(It.IsAny<Stream>(), "doc.pdf", "application/pdf", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new StorageResult("/files/doc.pdf", "http://test/doc.pdf", true));

        var imageProcessor = new Mock<IImageProcessor>(MockBehavior.Loose);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            imageProcessor.Object,
            NullLogger<FileService>.Instance);

        var stream = new MemoryStream("fake pdf"u8.ToArray());

        // Act
        await sut.UploadAsync(stream, "doc.pdf", "application/pdf");

        // Assert
        imageProcessor.Verify(p => p.GetDimensionsAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ──────────────────────────────────────────
    // UploadImageAsync
    // ──────────────────────────────────────────

    [Fact]
    public async Task UploadImageAsync_WithOptions_ProcessesAndUploadsImage()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.StoreAsync(It.IsAny<Stream>(), "photo.jpg", "image/jpeg", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new StorageResult("/files/photo.jpg", "http://test/photo.jpg", true));

        var processedStream = new MemoryStream("processed image"u8.ToArray());
        var imageProcessor = new Mock<IImageProcessor>(MockBehavior.Loose);
        imageProcessor.Setup(p => p.ProcessAsync(It.IsAny<Stream>(), It.IsAny<ImageProcessOptions>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new ImageProcessResult(processedStream, 200, 200, 15, true));

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            imageProcessor.Object,
            NullLogger<FileService>.Instance);

        var stream = new MemoryStream("original"u8.ToArray());
        var options = new ImageProcessOptions { Width = 200, Height = 200 };

        // Act
        var response = await sut.UploadImageAsync(stream, "photo.jpg", options);

        // Assert
        response.FileName.Should().Be("photo.jpg");
        response.AccessUrl.Should().Be("http://test/photo.jpg");
        imageProcessor.Verify(p => p.ProcessAsync(It.IsAny<Stream>(), options, It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(s => s.StoreAsync(It.IsAny<Stream>(), "photo.jpg", "image/jpeg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadImageAsync_GenerateThumbnail_StoresThumbnailAndUpdatesPath()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.StoreAsync(It.IsAny<Stream>(), "thumb.jpg", "image/jpeg", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new StorageResult("/files/thumb.jpg", "http://test/thumb.jpg", true));
        storage.Setup(s => s.StoreAsync(It.IsAny<Stream>(), "thumb_thumb.jpg", "image/jpeg", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new StorageResult("/files/thumb_thumb.jpg", "http://test/thumb_thumb.jpg", true));

        var processedStream = new MemoryStream("processed"u8.ToArray());
        var thumbnailStream = new MemoryStream("thumbnail"u8.ToArray());
        var imageProcessor = new Mock<IImageProcessor>(MockBehavior.Loose);
        imageProcessor.Setup(p => p.ProcessAsync(It.IsAny<Stream>(), It.IsAny<ImageProcessOptions>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new ImageProcessResult(processedStream, 800, 600, 20, true));
        imageProcessor.Setup(p => p.GenerateThumbnailAsync(It.IsAny<Stream>(), 200, 200, It.IsAny<CancellationToken>()))
                      .ReturnsAsync(thumbnailStream);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            imageProcessor.Object,
            NullLogger<FileService>.Instance);

        var stream = new MemoryStream("original image"u8.ToArray());
        var options = new ImageProcessOptions { GenerateThumbnail = true, ThumbnailWidth = 200, ThumbnailHeight = 200 };

        // Act
        var response = await sut.UploadImageAsync(stream, "thumb.jpg", options);

        // Assert
        imageProcessor.Verify(p => p.GenerateThumbnailAsync(It.IsAny<Stream>(), 200, 200, It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(s => s.StoreAsync(It.IsAny<Stream>(), "thumb_thumb.jpg", "image/jpeg", It.IsAny<CancellationToken>()), Times.Once);

        using var verifyCtx = CreateDbContext(dbName);
        var attachment = await verifyCtx.FileAttachments.FirstAsync();
        attachment.ThumbnailPath.Should().Be("/files/thumb_thumb.jpg");
    }

    [Fact]
    public async Task UploadImageAsync_ProcessFailure_ThrowsInvalidOperationException()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        var imageProcessor = new Mock<IImageProcessor>(MockBehavior.Loose);
        imageProcessor.Setup(p => p.ProcessAsync(It.IsAny<Stream>(), It.IsAny<ImageProcessOptions>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new ImageProcessResult(Stream.Null, 0, 0, 0, false, "不支持的图片格式"));

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            imageProcessor.Object,
            NullLogger<FileService>.Instance);

        var stream = new MemoryStream("bad image"u8.ToArray());
        var options = new ImageProcessOptions { Width = 100, Height = 100 };

        // Act
        var act = () => sut.UploadImageAsync(stream, "bad.jpg", options);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("图片处理失败: 不支持的图片格式");
    }

    [Fact]
    public async Task UploadImageAsync_NullOptions_BehavesLikeRegularUpload()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.StoreAsync(It.IsAny<Stream>(), "plain.jpg", "image/jpeg", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new StorageResult("/files/plain.jpg", "http://test/plain.jpg", true));

        var imageProcessor = new Mock<IImageProcessor>(MockBehavior.Loose);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            imageProcessor.Object,
            NullLogger<FileService>.Instance);

        var stream = new MemoryStream("raw image"u8.ToArray());

        // Act
        var response = await sut.UploadImageAsync(stream, "plain.jpg", null);

        // Assert
        response.FileName.Should().Be("plain.jpg");
        response.ContentType.Should().Be("image/jpeg");
        imageProcessor.Verify(p => p.ProcessAsync(It.IsAny<Stream>(), It.IsAny<ImageProcessOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        storage.Verify(s => s.StoreAsync(It.IsAny<Stream>(), "plain.jpg", "image/jpeg", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────────────────────────────────────────
    // DownloadAsync
    // ──────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_ExistingFile_ReturnsStreamAndMetadata()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString("N");
        var attachment = new TenE0FileAttachment
        {
            Id = fileId,
            FileName = "download.txt",
            StoragePath = "/files/download.txt",
            ContentType = "text/plain",
            StorageBackend = "Local",
            FileSize = 100
        };
        using (var seedCtx = CreateDbContext(dbName))
        {
            seedCtx.FileAttachments.Add(attachment);
            await seedCtx.SaveChangesAsync();
        }

        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        var retrieveStream = new MemoryStream("downloaded content"u8.ToArray());
        storage.Setup(s => s.RetrieveAsync("/files/download.txt", It.IsAny<CancellationToken>()))
               .ReturnsAsync(retrieveStream);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var (stream, metadata) = await sut.DownloadAsync(fileId);

        // Assert
        stream.Should().NotBeNull();
        metadata.Should().NotBeNull();
        metadata!.FileName.Should().Be("download.txt");
        metadata.StoragePath.Should().Be("/files/download.txt");
    }

    [Fact]
    public async Task DownloadAsync_MissingFile_ReturnsNullTuple()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var (stream, metadata) = await sut.DownloadAsync("nonexistent");

        // Assert
        stream.Should().BeNull();
        metadata.Should().BeNull();
    }

    [Fact]
    public async Task DownloadAsync_SoftDeletedFile_ReturnsNullTuple()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString("N");
        var attachment = new TenE0FileAttachment
        {
            Id = fileId,
            FileName = "deleted.txt",
            StoragePath = "/files/deleted.txt",
            ContentType = "text/plain",
            StorageBackend = "Local",
            FileSize = 50,
            IsDeleted = true
        };
        using (var seedCtx = CreateDbContext(dbName))
        {
            seedCtx.FileAttachments.Add(attachment);
            await seedCtx.SaveChangesAsync();
        }

        var storage = new Mock<IFileStorage>(MockBehavior.Loose);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var (stream, metadata) = await sut.DownloadAsync(fileId);

        // Assert
        stream.Should().BeNull();
        metadata.Should().BeNull();
    }

    // ──────────────────────────────────────────
    // DeleteAsync
    // ──────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_ExistingFile_SoftDeletesAndReturnsTrue()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString("N");
        var attachment = new TenE0FileAttachment
        {
            Id = fileId,
            FileName = "to-delete.txt",
            StoragePath = "/files/to-delete.txt",
            ContentType = "text/plain",
            StorageBackend = "Local",
            FileSize = 42
        };
        using (var seedCtx = CreateDbContext(dbName))
        {
            seedCtx.FileAttachments.Add(attachment);
            await seedCtx.SaveChangesAsync();
        }

        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.DeleteAsync("/files/to-delete.txt", It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var result = await sut.DeleteAsync(fileId);

        // Assert
        result.Should().BeTrue();

        using var verifyCtx = CreateDbContext(dbName);
        var deleted = await verifyCtx.FileAttachments.FindAsync(new object[] { fileId });
        deleted.Should().NotBeNull();
        deleted!.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_MissingFile_ReturnsFalse()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var result = await sut.DeleteAsync("nonexistent");

        // Assert
        result.Should().BeFalse();
        storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_AlreadyDeletedFile_ReturnsFalse()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString("N");
        var attachment = new TenE0FileAttachment
        {
            Id = fileId,
            FileName = "already-deleted.txt",
            StoragePath = "/files/already-deleted.txt",
            ContentType = "text/plain",
            StorageBackend = "Local",
            FileSize = 10,
            IsDeleted = true
        };
        using (var seedCtx = CreateDbContext(dbName))
        {
            seedCtx.FileAttachments.Add(attachment);
            await seedCtx.SaveChangesAsync();
        }

        var storage = new Mock<IFileStorage>(MockBehavior.Loose);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var result = await sut.DeleteAsync(fileId);

        // Assert
        result.Should().BeFalse();
        storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_StorageDeleteFailure_StillMarksDeleted()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString("N");
        var attachment = new TenE0FileAttachment
        {
            Id = fileId,
            FileName = "storage-fail.txt",
            StoragePath = "/files/storage-fail.txt",
            ContentType = "text/plain",
            StorageBackend = "Local",
            FileSize = 30
        };
        using (var seedCtx = CreateDbContext(dbName))
        {
            seedCtx.FileAttachments.Add(attachment);
            await seedCtx.SaveChangesAsync();
        }

        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.DeleteAsync("/files/storage-fail.txt", It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var logger = new Mock<ILogger<FileService>>();
        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            logger.Object);

        // Act
        var result = await sut.DeleteAsync(fileId);

        // Assert
        result.Should().BeTrue();

        using var verifyCtx = CreateDbContext(dbName);
        var deleted = await verifyCtx.FileAttachments.FindAsync(new object[] { fileId });
        deleted!.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_FileWithThumbnail_DeletesBothFromStorage()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString("N");
        var attachment = new TenE0FileAttachment
        {
            Id = fileId,
            FileName = "with-thumb.jpg",
            StoragePath = "/files/with-thumb.jpg",
            ContentType = "image/jpeg",
            StorageBackend = "Local",
            FileSize = 200,
            ThumbnailPath = "/files/thumb_with-thumb.jpg"
        };
        using (var seedCtx = CreateDbContext(dbName))
        {
            seedCtx.FileAttachments.Add(attachment);
            await seedCtx.SaveChangesAsync();
        }

        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.DeleteAsync("/files/with-thumb.jpg", It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);
        storage.Setup(s => s.DeleteAsync("/files/thumb_with-thumb.jpg", It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var result = await sut.DeleteAsync(fileId);

        // Assert
        result.Should().BeTrue();
        storage.Verify(s => s.DeleteAsync("/files/with-thumb.jpg", It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(s => s.DeleteAsync("/files/thumb_with-thumb.jpg", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────────────────────────────────────────
    // GetMetadataAsync
    // ──────────────────────────────────────────

    [Fact]
    public async Task GetMetadataAsync_ExistingFile_ReturnsAttachment()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString("N");
        var attachment = new TenE0FileAttachment
        {
            Id = fileId,
            FileName = "meta.txt",
            StoragePath = "/files/meta.txt",
            ContentType = "text/plain",
            StorageBackend = "Local",
            FileSize = 88
        };
        using (var seedCtx = CreateDbContext(dbName))
        {
            seedCtx.FileAttachments.Add(attachment);
            await seedCtx.SaveChangesAsync();
        }

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            Mock.Of<IFileStorage>(),
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var result = await sut.GetMetadataAsync(fileId);

        // Assert
        result.Should().NotBeNull();
        result!.FileName.Should().Be("meta.txt");
        result.FileSize.Should().Be(88);
    }

    [Fact]
    public async Task GetMetadataAsync_MissingFile_ReturnsNull()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            Mock.Of<IFileStorage>(),
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var result = await sut.GetMetadataAsync("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetMetadataAsync_SoftDeletedFile_ReturnsNull()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString("N");
        var attachment = new TenE0FileAttachment
        {
            Id = fileId,
            FileName = "deleted-meta.txt",
            StoragePath = "/files/deleted-meta.txt",
            ContentType = "text/plain",
            StorageBackend = "Local",
            FileSize = 33,
            IsDeleted = true
        };
        using (var seedCtx = CreateDbContext(dbName))
        {
            seedCtx.FileAttachments.Add(attachment);
            await seedCtx.SaveChangesAsync();
        }

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            Mock.Of<IFileStorage>(),
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var result = await sut.GetMetadataAsync(fileId);

        // Assert
        result.Should().BeNull();
    }

    // ──────────────────────────────────────────
    // GetAccessUrlAsync
    // ──────────────────────────────────────────

    [Fact]
    public async Task GetAccessUrlAsync_ExistingFile_ReturnsCorrectUrl()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString("N");
        var attachment = new TenE0FileAttachment
        {
            Id = fileId,
            FileName = "url-test.txt",
            StoragePath = "/files/url-test.txt",
            ContentType = "text/plain",
            StorageBackend = "Local",
            FileSize = 64
        };
        using (var seedCtx = CreateDbContext(dbName))
        {
            seedCtx.FileAttachments.Add(attachment);
            await seedCtx.SaveChangesAsync();
        }

        var storage = new Mock<IFileStorage>(MockBehavior.Loose);
        storage.Setup(s => s.GetAccessUrl("/files/url-test.txt")).Returns("https://cdn.example.com/files/url-test.txt");

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var url = await sut.GetAccessUrlAsync(fileId);

        // Assert
        url.Should().Be("https://cdn.example.com/files/url-test.txt");
    }

    [Fact]
    public async Task GetAccessUrlAsync_MissingFile_ReturnsNull()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var storage = new Mock<IFileStorage>(MockBehavior.Loose);

        var contextFactory = new Mock<IDbContextFactory<TenE0SystemDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(() => CreateDbContext(dbName));

        var sut = new FileService(
            contextFactory.Object,
            storage.Object,
            Mock.Of<IImageProcessor>(),
            NullLogger<FileService>.Instance);

        // Act
        var url = await sut.GetAccessUrlAsync("nonexistent");

        // Assert
        url.Should().BeNull();
    }
}
