using Microsoft.AspNetCore.Http;
using TenE0.Core.Abstractions;
using TenE0.Core.Files;

namespace TenE0.Api.Endpoints;

internal static class FileEndpoints
{
    public static WebApplication MapFileEndpoints(this WebApplication app)
    {
        app.MapPost("/files/upload", async (IFormFile file, IFileService fileSvc, IErrs errs, CancellationToken ct) =>
        {
            if (file == null || file.Length == 0)
            {
                errs.Add("文件不能为空", "file", "FILE_EMPTY");
                return Results.BadRequest(new { error = "文件不能为空" });
            }

            using var stream = file.OpenReadStream();
            var response = await fileSvc.UploadAsync(stream, file.FileName, file.ContentType, ct: ct);

            return Results.Ok(response);
        })
        .WithName("UploadFile")
        .WithDescription("上传文件");

        app.MapPost("/files/upload/image", async (IFormFile file, IFileService fileSvc, IErrs errs,
            int? width, int? height, bool generateThumbnail, int quality, string? watermarkText, CancellationToken ct) =>
        {
            if (file == null || file.Length == 0)
            {
                errs.Add("文件不能为空", "file", "FILE_EMPTY");
                return Results.BadRequest(new { error = "文件不能为空" });
            }

            if (!file.ContentType.StartsWith("image/"))
            {
                errs.Add("只能上传图片文件", "file", "NOT_IMAGE");
                return Results.BadRequest(new { error = "只能上传图片文件" });
            }

            var options = new ImageProcessOptions
            {
                Width = width,
                Height = height,
                GenerateThumbnail = generateThumbnail,
                Quality = quality > 0 ? quality : 85,
                WatermarkText = watermarkText
            };

            using var stream = file.OpenReadStream();
            var response = await fileSvc.UploadImageAsync(stream, file.FileName, options, ct: ct);

            return Results.Ok(response);
        })
        .WithName("UploadImage")
        .WithDescription("上传图片（支持处理选项）");

        app.MapGet("/files/{id}", async (string id, IFileService fileSvc, CancellationToken ct) =>
        {
            var (stream, metadata) = await fileSvc.DownloadAsync(id, ct);
            if (stream == null || metadata == null)
            {
                return Results.NotFound(new { error = "文件不存在" });
            }

            return Results.File(stream, metadata.ContentType, metadata.FileName);
        })
        .WithName("DownloadFile")
        .WithDescription("下载文件");

        app.MapDelete("/files/{id}", async (string id, IFileService fileSvc, CancellationToken ct) =>
        {
            var deleted = await fileSvc.DeleteAsync(id, ct);
            if (!deleted)
            {
                return Results.NotFound(new { error = "文件不存在或已删除" });
            }

            return Results.Ok(new { message = "删除成功" });
        })
        .WithName("DeleteFile")
        .WithDescription("删除文件");

        app.MapGet("/files/{id}/metadata", async (string id, IFileService fileSvc, CancellationToken ct) =>
        {
            var metadata = await fileSvc.GetMetadataAsync(id, ct);
            if (metadata == null)
            {
                return Results.NotFound(new { error = "文件不存在" });
            }

            var accessUrl = await fileSvc.GetAccessUrlAsync(id, ct);

            return Results.Ok(new FileResponse(
                metadata.Id,
                metadata.FileName,
                metadata.ContentType,
                metadata.FileSize,
                accessUrl!,
                metadata.ThumbnailPath != null ? $"{accessUrl}/thumb" : null,
                metadata.Width,
                metadata.Height,
                metadata.Category,
                metadata.CreateTime
            ));
        })
        .WithName("GetFileMetadata")
        .WithDescription("获取文件元数据");

        return app;
    }
}
