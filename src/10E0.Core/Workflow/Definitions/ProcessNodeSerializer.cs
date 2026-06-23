using System.Text.Json;

namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 流程节点图的 JSON 序列化 / 反序列化工具。
///
/// 节点用 <see cref="IProcessNode"/> 多态序列化（discriminator = $nodeType）。
/// 单独抽出来便于 Builder / Store / 测试统一复用序列化选项。
/// </summary>
public static class ProcessNodeSerializer
{
    /// <summary>统一序列化选项（多态 + 驼峰容错）。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    /// <summary>序列化节点集合为 JSON。</summary>
    public static string SerializeNodes(IEnumerable<IProcessNode> nodes)
        => JsonSerializer.Serialize(nodes, Options);

    /// <summary>反序列化 JSON 为节点集合。</summary>
    public static IReadOnlyList<IProcessNode> DeserializeNodes(string json)
        => JsonSerializer.Deserialize<List<IProcessNode>>(json, Options) ?? [];
}
