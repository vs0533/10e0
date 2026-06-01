using Microsoft.AspNetCore.Http;

namespace TenE0.Core.Json;

/// <summary>
/// HttpRequest 扩展方法，便于在 Minimal API 中提取 PostedProperties。
/// </summary>
public static class HttpRequestExtensions
{
    /// <summary>
    /// 从请求 Body 提取属性路径列表。
    /// 注意：会重置 Request.Body 位置，确保后续模型绑定仍可读取。
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetPostedPropertiesAsync(this HttpRequest request, CancellationToken ct = default)
    {
        if (!request.Body.CanSeek)
        {
            // 不可 Seek 的流，先缓冲（不用 using，因为要赋值给 request.Body 供后续读取）
            var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            var paths = await PostedBodyConvert.ExtractPathsAsync(buffer, ct);

            // 重置位置供后续读取
            buffer.Position = 0;
            request.Body = buffer;
            return paths;
        }

        request.Body.Position = 0;
        var result = await PostedBodyConvert.ExtractPathsAsync(request.Body, ct);
        request.Body.Position = 0;
        return result;
    }
}
