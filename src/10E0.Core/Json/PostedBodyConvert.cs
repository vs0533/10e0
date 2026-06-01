using System.Text.Json;

namespace TenE0.Core.Json;

/// <summary>
/// JSON Body 属性路径提取器。
///
/// 用途：前端提交部分更新时，后端需要知道"客户端发了哪些字段"。
/// 本工具从原始 JSON 中提取所有属性路径，供 EntityService.PostedProperties 使用。
///
/// 示例输入：
/// {
///   "name": "张三",
///   "address": { "city": "北京", "zip": "100000" },
///   "tags": ["a", "b"]
/// }
///
/// 输出：
/// ["name", "address", "address.city", "address.zip", "tags"]
///
/// 注意：数组元素不展开路径（tags[0], tags[1] 等），只标记数组本身。
/// </summary>
public static class PostedBodyConvert
{
    /// <summary>
    /// 从 JSON 字符串提取属性路径列表。
    /// </summary>
    public static IReadOnlyList<string> ExtractPaths(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var paths = new List<string>();
            ExtractPathsRecursive(doc.RootElement, "", paths);
            return paths;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// 从 Stream 提取属性路径列表（用于 Request.Body）。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExtractPathsAsync(Stream stream, CancellationToken ct = default)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var paths = new List<string>();
            ExtractPathsRecursive(doc.RootElement, "", paths);
            return paths;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ExtractPathsRecursive(JsonElement element, string prefix, List<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var path = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                paths.Add(path);
                ExtractPathsRecursive(prop.Value, path, paths);
            }
        }
        // 数组不展开元素，只标记数组本身（已在父级添加）
        // 基本类型（string, number, bool, null）不递归
    }
}
