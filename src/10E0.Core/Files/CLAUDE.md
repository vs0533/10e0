# Files/ — 文件服务

文件上传/下载 + 图片处理，支持多种存储后端。

## 文件说明

| 文件 | 职责 |
|------|------|
| `IFileService.cs` / `FileService.cs` | 高级文件服务：上传、下载、删除、元数据管理 |
| `IFileStorage.cs` | 存储提供者接口：`SaveAsync`、`GetAsync`、`DeleteAsync`、`GetUrl` |
| `IImageProcessor.cs` / `ImageProcessor.cs` | 图片处理：缩放、裁剪、缩略图、水印。基于 SixLabors.ImageSharp |
| `Models.cs` | 文件元数据 DTO |
| `FilesExtensions.cs` | DI 注册扩展方法 |

## 子目录

| 目录 | 职责 |
|------|------|
| `Storage/` | 存储后端实现（Local / AWS S3 / Aliyun OSS）+ EF 映射 |

## 存储后端

| 实现 | 说明 |
|------|------|
| `LocalFileStorage` | 本地文件系统 |
| `AwsS3Storage` | AWS S3 |
| `AliyunOssStorage` | 阿里云 OSS |

通过 DI 切换，业务代码只依赖 `IFileStorage`。

## 新增功能

这是旧 E0.Core 中没有的模块，为重构新增。
