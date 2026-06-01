# Files/Storage/ — 文件存储后端实现

## 文件说明

| 文件 | 职责 |
|------|------|
| `LocalFileStorage.cs` | 本地文件系统存储：文件保存到配置目录，URL 返回相对路径 |
| `AwsS3Storage.cs` | AWS S3 存储：使用 AWS SDK 上传/下载，支持预签名 URL |
| `AliyunOssStorage.cs` | 阿里云 OSS 存储：使用阿里云 SDK，支持预签名 URL |
| `FileModelBuilderExtensions.cs` | `TenE0FileAttachment` 实体的 EF Core 表映射 |

## TenE0FileAttachment 实体

框架自有表，记录文件元数据：文件名、大小、MIME 类型、存储路径、存储类型、关联业务实体等。
