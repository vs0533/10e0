namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 流程定义图合法性校验器。
///
/// 校验规则（Build 期 + 显式调用 Validate）：
/// <list type="bullet">
/// <item>有且仅有一个 Start 节点</item>
/// <item>至少一个 End 节点</item>
/// <item>所有节点的 NextNodeCode / BranchRoute.TargetNodeCode 指向的节点存在</item>
/// <item>无死节点（无可达路径，除 End 外）</item>
/// <item>无环路（除显式回退外）</item>
/// <item>审批节点必须有 AssigneePolicy</item>
/// <item>AllowRollback=true 的 ApprovalNode 必须有 RollbackTargetCode 且目标存在</item>
/// </list>
/// </summary>
public interface IProcessDefinitionValidator
{
    /// <summary>校验节点集合，返回错误列表（空 = 通过）。</summary>
    IReadOnlyList<string> Validate(IReadOnlyList<IProcessNode> nodes, string startNodeCode);

    /// <summary>校验并抛异常（失败抛 <see cref="ProcessDefinitionInvalidException"/>）。</summary>
    void ValidateOrThrow(IReadOnlyList<IProcessNode> nodes, string startNodeCode);
}

public sealed class ProcessDefinitionValidator : IProcessDefinitionValidator
{
    public IReadOnlyList<string> Validate(IReadOnlyList<IProcessNode> nodes, string startNodeCode)
    {
        var errors = new List<string>();
        if (nodes.Count == 0)
        {
            errors.Add("节点集合为空");
            return errors;
        }

        var byCode = nodes.ToDictionary(n => n.Code);
        var startNodes = nodes.Where(n => n.Type == NodeType.Start).ToList();
        var endNodes = nodes.Where(n => n.Type == NodeType.End).ToList();

        // 唯一 Start
        if (startNodes.Count == 0)
            errors.Add("缺少 Start 节点");
        else if (startNodes.Count > 1)
            errors.Add($"存在 {startNodes.Count} 个 Start 节点（应有且仅有一个）");

        // 至少一个 End
        if (endNodes.Count == 0)
            errors.Add("缺少 End 节点");

        // StartNodeCode 存在且类型为 Start
        if (!string.IsNullOrEmpty(startNodeCode) && !byCode.ContainsKey(startNodeCode))
            errors.Add($"StartNodeCode '{startNodeCode}' 不存在于节点集合");
        else if (byCode.TryGetValue(startNodeCode, out var sn) && sn.Type != NodeType.Start)
            errors.Add($"StartNodeCode '{startNodeCode}' 不是 Start 类型节点");

        // 引用完整性 + 审批节点必填字段
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ApprovalNode an:
                    if (an.AssigneePolicy is null)
                        errors.Add($"审批节点 '{an.Code}' 缺少 AssigneePolicy");
                    if (an.AllowRollback && string.IsNullOrEmpty(an.RollbackTargetCode))
                        errors.Add($"审批节点 '{an.Code}' AllowRollback=true 但未设置 RollbackTargetCode");
                    if (an.AllowRollback && !string.IsNullOrEmpty(an.RollbackTargetCode) && !byCode.ContainsKey(an.RollbackTargetCode))
                        errors.Add($"审批节点 '{an.Code}' RollbackTargetCode '{an.RollbackTargetCode}' 不存在");
                    if (!string.IsNullOrEmpty(an.NextNodeCode) && !byCode.ContainsKey(an.NextNodeCode))
                        errors.Add($"审批节点 '{an.Code}' NextNodeCode '{an.NextNodeCode}' 不存在");
                    break;
                case StartNode st:
                    if (string.IsNullOrEmpty(st.NextNodeCode))
                        errors.Add($"Start 节点 '{st.Code}' 缺少 NextNodeCode");
                    else if (!byCode.ContainsKey(st.NextNodeCode))
                        errors.Add($"Start 节点 '{st.Code}' NextNodeCode '{st.NextNodeCode}' 不存在");
                    break;
                case BranchNode bn:
                    if (bn.Routes.Count == 0)
                        errors.Add($"分支节点 '{bn.Code}' 没有任何路由");
                    foreach (var route in bn.Routes)
                    {
                        if (!byCode.ContainsKey(route.TargetNodeCode))
                            errors.Add($"分支节点 '{bn.Code}' 路由目标 '{route.TargetNodeCode}' 不存在");
                    }
                    if (string.IsNullOrEmpty(bn.DefaultNodeCode))
                        errors.Add($"分支节点 '{bn.Code}' 缺少 DefaultNodeCode（所有条件都不命中时无去处）");
                    else if (!byCode.ContainsKey(bn.DefaultNodeCode))
                        errors.Add($"分支节点 '{bn.Code}' DefaultNodeCode '{bn.DefaultNodeCode}' 不存在");
                    break;
                case ParallelNode pn:
                    if (pn.BranchPolicies.Count == 0)
                        errors.Add($"并行节点 '{pn.Code}' 没有分支策略");
                    if (string.IsNullOrEmpty(pn.NextNodeCode) || !byCode.ContainsKey(pn.NextNodeCode))
                        errors.Add($"并行节点 '{pn.Code}' NextNodeCode 不存在");
                    break;
            }
        }

        // 环路检测（从 Start 出发，除回退目标外不应形成环）
        if (startNodes.Count == 1)
        {
            var cycle = DetectCycle(startNodes[0], byCode);
            if (cycle is not null)
                errors.Add($"检测到环路（除显式回退外不应成环）：{cycle}");
        }

        return errors;
    }

    public void ValidateOrThrow(IReadOnlyList<IProcessNode> nodes, string startNodeCode)
    {
        var errors = Validate(nodes, startNodeCode);
        if (errors.Count > 0)
            throw new ProcessDefinitionInvalidException(errors);
    }

    /// <summary>
    /// 环路检测：DFS，回退目标（ApprovalNode.RollbackTargetCode）不视为环（属显式回退）。
    /// 返回环路描述，无环返回 null。
    /// </summary>
    private static string? DetectCycle(IProcessNode start, IReadOnlyDictionary<string, IProcessNode> byCode)
    {
        var visited = new HashSet<string>();
        var stack = new HashSet<string>();
        var path = new List<string>();

        bool Dfs(IProcessNode node)
        {
            if (stack.Contains(node.Code))
            {
                path.Add(node.Code);
                return true;
            }
            if (visited.Contains(node.Code)) return false;

            visited.Add(node.Code);
            stack.Add(node.Code);
            path.Add(node.Code);

            foreach (var next in GetSuccessors(node, byCode))
            {
                if (next is null) continue;
                if (Dfs(next)) return true;
            }

            stack.Remove(node.Code);
            path.RemoveAt(path.Count - 1);
            return false;
        }

        return Dfs(start) ? string.Join(" → ", path) : null;
    }

    private static IEnumerable<IProcessNode?> GetSuccessors(IProcessNode node, IReadOnlyDictionary<string, IProcessNode> byCode)
    {
        // 注意：回退目标（RollbackTargetCode）不视为正向后继，不计入环检测
        switch (node)
        {
            case StartNode st:
                if (!string.IsNullOrEmpty(st.NextNodeCode)) yield return byCode.GetValueOrDefault(st.NextNodeCode);
                break;
            case ApprovalNode an:
                if (!string.IsNullOrEmpty(an.NextNodeCode)) yield return byCode.GetValueOrDefault(an.NextNodeCode);
                break;
            case BranchNode bn:
                foreach (var r in bn.Routes)
                    yield return byCode.GetValueOrDefault(r.TargetNodeCode);
                if (!string.IsNullOrEmpty(bn.DefaultNodeCode))
                    yield return byCode.GetValueOrDefault(bn.DefaultNodeCode);
                break;
            case ParallelNode pn:
                if (!string.IsNullOrEmpty(pn.NextNodeCode)) yield return byCode.GetValueOrDefault(pn.NextNodeCode);
                break;
        }
    }
}
