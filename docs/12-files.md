# 12 — 文件服务（File Service）

文件上传/下载/图片处理模块，支持三种存储后端（本地文件系统、阿里云 OSS、AWS S3），可通过 DI 一键切换。图片处理基于 SixLabors.ImageSharp。

---

## 架构总览

```
IFileService          ← 高层服务接口
    │
FileService           ← 实现类：哈希、持久化、缩略图
    ├── IFileStorage        ← 存储抽象层
    │     ├── LocalFileStorage    (本地磁盘)
    │     ├── AliyunOssStorage    (阿里云 OSS)
    │     └── AwsS3Storage        (AWS S3)
    ├── IImageProcessor     ← 图片处理
    │     └── ImageProcessor      (SixLabors.ImageSharp)
    └── TenE0FileAttachment ← EF Core 元数据实体
```

所有代码位于 `TenE0.Core.Files`，存储实现在 `TenE0.Core.Files.Storage`，DI 扩展在 `TenE0.Core.DependencyInjection`。

---

## IFileService 接口

```csharp
public interface IFileService
{
    Task<UploadResponse> UploadAsync(Stream stream, string fileName,
        string contentType, UploadRequest? request = null,
        CancellationToken ct = default);

    Task<UploadResponse> UploadImageAsync(Stream stream, string fileName,
        ImageProcessOptions? options = null, UploadRequest? request = null,
        CancellationToken ct = default);

    Task<(Stream? Stream, TenE0FileAttachment? Metadata)> DownloadAsync(
        string fileId, CancellationToken ct = default);

    Task<bool> DeleteAsync(string fileId, CancellationToken ct = default);

    Task<TenE0FileAttachment?> GetMetadataAsync(string fileId,
        CancellationToken ct = default);

    Task<string?> GetAccessUrlAsync(string fileId,
        CancellationToken ct = default);
}
```

| 方法 | 说明 |
|------|------|
| `UploadAsync` | 通用文件上传，返回元数据 |
| `UploadImageAsync` | 图片上传，可选缩放/水印/缩略图 |
| `DownloadAsync` | 下载文件（文件流 + 元数据） |
| `DeleteAsync` | 软删除（标记 `IsDeleted = true`） |
| `GetMetadataAsync` | 查询元数据（忽略已删除） |
| `GetAccessUrlAsync` | 获取文件访问 URL |

---

## IFileStorage 接口与 StorageResult

```csharp
public interface IFileStorage
{
    Task<StorageResult> StoreAsync(Stream stream, string fileName,
        string contentType, CancellationToken ct = default);
    Task<Stream?> RetrieveAsync(string storagePath, CancellationToken ct = default);
    Task<bool> DeleteAsync(string storagePath, CancellationToken ct = default);
    string GetAccessUrl(string storagePath);
    Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default);
}

public record StorageResult(
    string StoragePath,   // 存储后端相对路径
    string AccessUrl,     // 可访问 URL
    bool Success,
    string? ErrorMessage = null
);
```

### 存储后端对比

| 后端 | 类 | 路径策略 | 访问 URL |
|------|----|----------|----------|
| 本地文件系统 | `LocalFileStorage` | `{yyyy}/{MM}/{guid}{ext}` | `{BaseUrl}/{相对路径}` |
| 阿里云 OSS | `AliyunOssStorage` | 同上 | `https://{Bucket}.{Endpoint}/{key}` |
| AWS S3 | `AwsS3Storage` | 同上 | `https://{Bucket}.s3.{Region}.amazonaws.com/{key}` |

### 配置类

```csharp
public class LocalStorageOptions
{
    public string BasePath { get; set; } = "uploads";   // 本地目录
    public string BaseUrl { get; set; } = "/uploads";    // URL 前缀
}
```

`AliyunOssOptions`：`Endpoint`、`AccessKeyId`、`AccessKeySecret`、`BucketName`
`AwsS3Options`：`AccessKey`、`SecretKey`、`Region`（默认 `us-east-1`）、`BucketName`

---

## 图片处理（SixLabors.ImageSharp）

### IImageProcessor 接口

```csharp
public interface IImageProcessor
{
    Task<ImageProcessResult> ProcessAsync(Stream inputStream,
        ImageProcessOptions options, CancellationToken ct = default);

    Task<Stream> GenerateThumbnailAsync(Stream inputStream,
        int width, int height, CancellationToken ct = default);

    Task<(int Width, int Height)> GetDimensionsAsync(
        Stream inputStream, CancellationToken ct = default);
}

public record ImageProcessResult(
    Stream ProcessedStream, int Width, int Height,
    long FileSize, bool Success, string? ErrorMessage = null
);
```

### ImageProcessOptions

```csharp
public class ImageProcessOptions
{
    public int? Width { get; set; }          // 目标宽度（null=原宽）
    public int? Height { get; set; }         // 目标高度（null=原高）
    public string? WatermarkText { get; set; } // 水印文字
    public int Quality { get; set; } = 85;   // JPEG 质量 (1-100)
    public bool GenerateThumbnail { get; set; } // 是否生成缩略图
    public int ThumbnailWidth { get; set; } = 200;
    public int ThumbnailHeight { get; set; } = 200;
}
```

### ImageProcessor 处理流程

1. **缩放**：`Image.LoadAsync<Rgba32>` → `Mutate(x => x.Resize(...))`，使用 `ResizeMode.Max` 等比缩放
2. **水印**：Arial 24px 字体，白色 50% 透明度，右下角定位
3. **输出编码**：`JpegEncoder` + 配置的 `Quality`
4. **缩略图**：`ResizeMode.Crop` 居中裁剪，JPEG 质量 80

---

## 上传流程（UploadAsync）

```
调用方
  │
  ▼
IFileStorage.StoreAsync()      ── 写入存储后端
  │
  ▼
SHA256.ComputeHashAsync()      ── 文件内容哈希（十六进制）
  │
  ▼  (如为图片)
IImageProcessor.GetDimensionsAsync()  ── 提取宽高
  │
  ▼
构造 TenE0FileAttachment 实体
  │
  ▼
DbContext.FileAttachments.Add()
  │
  ▼
SaveChangesAsync()             ── 持久化元数据
```

图片上传（`UploadImageAsync`）额外步骤：

1. 若有 `options`，先调 `ProcessAsync` 处理图片流
2. 用处理后的流调 `UploadAsync`
3. 若 `options.GenerateThumbnail == true`，生成缩略图并存储

---

## TenE0FileAttachment 实体

```csharp
public class TenE0FileAttachment : BaseEntity  // Id = Guid.NewGuid().ToString("N")
{
    public required string FileName { get; set; }     // 原文件名
    public required string StoragePath { get; set; }   // 存储相对路径
    public required string ContentType { get; set; }   // MIME 类型
    public long FileSize { get; set; }                 // 字节数
    public required string StorageBackend { get; set; }// Local/AliyunOss/AwsS3
    public string? Category { get; set; }              // 业务分类
    public string? FileHash { get; set; }              // SHA256 哈希
    public int? Width { get; set; }                    // 图片宽度
    public int? Height { get; set; }                   // 图片高度
    public string? ThumbnailPath { get; set; }         // 缩略图路径
    public string? RelatedEntityId { get; set; }       // 关联实体 ID
    public string? RelatedEntityType { get; set; }     // 关联实体类型
    public bool IsDeleted { get; set; }                // 软删除
    public DateTimeOffset? CreateTime { get; set; }
    public string? CreateBy { get; set; }
}
```

表名 `FileAttachments`，字段 `HasMaxLength` 约束，在 `Category`、`RelatedEntityId`、`RelatedEntityType`、`IsDeleted` 上建了索引。

---

## DTO 模型

```csharp
public record UploadResponse(string Id, string FileName,
    string StoragePath, string AccessUrl, long FileSize, string ContentType);

public record FileResponse(string Id, string FileName, string ContentType,
    long FileSize, string AccessUrl, string? ThumbnailUrl,
    int? Width, int? Height, string? Category, DateTimeOffset? CreateTime);
```

---

## API 端点（Minimal API）

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/files/upload` | 通用文件上传 |
| POST | `/files/upload/image` | 图片上传（支持处理） |
| GET | `/files/{id}` | 下载文件 |
| DELETE | `/files/{id}` | 删除文件 |
| GET | `/files/{id}/metadata` | 获取元数据 |

```csharp
// 通用上传
app.MapPost("/files/upload", async (IFormFile file, IFileService fileSvc,
    IErrs errs, CancellationToken ct) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "文件不能为空" });

    using var stream = file.OpenReadStream();
    var response = await fileSvc.UploadAsync(stream, file.FileName,
        file.ContentType, ct: ct);
    return Results.Ok(response);
});

// 图片上传
app.MapPost("/files/upload/image", async (IFormFile file, IFileService fileSvc,
    IErrs errs, int? width, int? height, bool generateThumbnail,
    int quality, string? watermarkText, CancellationToken ct) =>
{
    if (!file.ContentType.StartsWith("image/"))
        return Results.BadRequest(new { error = "只能上传图片文件" });

    var options = new ImageProcessOptions
    {
        Width = width, Height = height,
        GenerateThumbnail = generateThumbnail,
        Quality = quality > 0 ? quality : 85,
        WatermarkText = watermarkText
    };

    using var stream = file.OpenReadStream();
    var response = await fileSvc.UploadImageAsync(stream, file.FileName,
        options, ct: ct);
    return Results.Ok(response);
});

// 下载
app.MapGet("/files/{id}", async (string id, IFileService fileSvc,
    CancellationToken ct) =>
{
    var (stream, metadata) = await fileSvc.DownloadAsync(id, ct);
    if (stream == null || metadata == null)
        return Results.NotFound();
    return Results.File(stream, metadata.ContentType, metadata.FileName);
});

// 删除
app.MapDelete("/files/{id}", async (string id, IFileService fileSvc,
    CancellationToken ct) =>
{
    var deleted = await fileSvc.DeleteAsync(id, ct);
    return deleted ? Results.Ok(new { message = "删除成功" })
                   : Results.NotFound();
});

// 元数据
app.MapGet("/files/{id}/metadata", async (string id, IFileService fileSvc,
    CancellationToken ct) =>
{
    var metadata = await fileSvc.GetMetadataAsync(id, ct);
    if (metadata == null) return Results.NotFound();

    var accessUrl = await fileSvc.GetAccessUrlAsync(id, ct);
    return Results.Ok(new FileResponse(
        metadata.Id, metadata.FileName, metadata.ContentType,
        metadata.FileSize, accessUrl!,
        metadata.ThumbnailPath != null ? $"{accessUrl}/thumb" : null,
        metadata.Width, metadata.Height,
        metadata.Category, metadata.CreateTime
    ));
});
```

---

## DI 注册

所有方法统一注册 `IFileService`（Scoped）、`IImageProcessor`（Scoped）和对应的 `IFileStorage`（Scoped）。

```csharp
// 本地文件系统
services.AddTenE0Files(options =>
{
    options.BasePath = "uploads";
    options.BaseUrl = "/uploads";
});

// 阿里云 OSS
services.AddTenE0FilesWithAliyunOss(options =>
{
    options.Endpoint = "oss-cn-hangzhou.aliyuncs.com";
    options.BucketName = "my-bucket";
    // AccessKeyId / AccessKeySecret 从配置读取
});

// AWS S3
services.AddTenE0FilesWithAwsS3(options =>
{
    options.Region = "ap-northeast-1";
    options.BucketName = "my-bucket";
});
```

---

## 依赖包

| 包 | 版本 | 用途 |
|----|------|------|
| SixLabors.ImageSharp | 3.1.12 | 图片编解码与处理 |
| SixLabors.ImageSharp.Drawing | 2.1.6 | 水印文字绘制 |
| Aliyun.OSS.SDK.NetCore | 2.13.0 | 阿里云 OSS SDK |
| AWSSDK.S3 | 3.7.305.7 | AWS S3 SDK |

---

## 测试覆盖

`10E0.Core.Tests` 中包含 49 个文件服务测试用例：

- **FileServiceTests**（14 用例）：上传/下载/删除/元数据全流程覆盖
- **ImageProcessorTests**（11 用例）：缩放、水印、缩略图、尺寸提取
- **LocalFileStorageTests**（9 用例）：存储/读取/删除/URL 生成
- **FilesModelsTests**（13 用例）：所有 DTO 和实体构造
- **FilesExtensionsTests**（2 用例）：DI 注册验证

云存储（AliyunOss / AwsS3）因需外部 SDK 集成环境而不含单元测试。
