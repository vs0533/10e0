using TenE0.Core.Entities;

namespace TenE0.Core.Files;

/// <summary>
/// 存储后端类型
/// </summary>
public enum StorageBackend
{
    Local,
    AliyunOss,
    AwsS3
}

/// <summary>
/// 文件上传请求选项
/// </summary>
public class UploadRequest
{
    /// <summary>存储后端（默认本地）</summary>
    public StorageBackend Backend { get; set; } = StorageBackend.Local;

    /// <summary>业务分类（如 avatar、document）</summary>
    public string? Category { get; set; }

    /// <summary>关联实体 Id</summary>
    public string? RelatedEntityId { get; set; }

    /// <summary>关联实体类型名称</summary>
    public string? RelatedEntityType { get; set; }
}

/// <summary>
/// 文件上传响应
/// </summary>
public record UploadResponse(
    string Id,
    string FileName,
    string StoragePath,
    string AccessUrl,
    long FileSize,
    string ContentType
);

/// <summary>
/// 图片处理选项
/// </summary>
public class ImageProcessOptions
{
    /// <summary>目标宽度（null 则保持原宽）</summary>
    public int? Width { get; set; }

    /// <summary>目标高度（null 则保持原高）</summary>
    public int? Height { get; set; }

    /// <summary>水印文字（null 则不添加水印）</summary>
    public string? WatermarkText { get; set; }

    /// <summary>压缩质量 1-100（默认 85）</summary>
    public int Quality { get; set; } = 85;

    /// <summary>是否生成缩略图</summary>
    public bool GenerateThumbnail { get; set; }

    /// <summary>缩略图宽度（默认 200）</summary>
    public int ThumbnailWidth { get; set; } = 200;

    /// <summary>缩略图高度（默认 200）</summary>
    public int ThumbnailHeight { get; set; } = 200;
}

/// <summary>
/// 文件元数据 API 响应
/// </summary>
public record FileResponse(
    string Id,
    string FileName,
    string ContentType,
    long FileSize,
    string AccessUrl,
    string? ThumbnailUrl,
    int? Width,
    int? Height,
    string? Category,
    DateTimeOffset? CreateTime
);

/// <summary>
/// 文件附件实体 — 记录已上传文件的元数据
/// </summary>
public class TenE0FileAttachment : BaseEntity
{
    public required string FileName { get; set; }
    public required string StoragePath { get; set; }
    public required string ContentType { get; set; }
    public long FileSize { get; set; }
    public required string StorageBackend { get; set; }
    public string? Category { get; set; }
    public string? FileHash { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? RelatedEntityId { get; set; }
    public string? RelatedEntityType { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? CreateTime { get; set; }
    public string? CreateBy { get; set; }
}
