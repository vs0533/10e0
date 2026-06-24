namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 流程定义校验失败异常 — 携带所有校验问题列表，便于一次反馈全部问题。
/// </summary>
public sealed class ProcessDefinitionInvalidException : InvalidOperationException
{
    /// <summary>所有校验问题（字段路径 + 描述）。</summary>
    public IReadOnlyList<string> Errors { get; }

    public ProcessDefinitionInvalidException(IReadOnlyList<string> errors)
        : base($"流程定义校验失败（{errors.Count} 个问题）：\n- {string.Join("\n- ", errors)}")
    {
        Errors = errors;
    }
}
