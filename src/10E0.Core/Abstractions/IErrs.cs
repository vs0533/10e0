namespace TenE0.Core.Abstractions;

/// <summary>
/// 错误收集器（请求作用域内）。
///
/// 与旧 E0Context.Err 的区别：
/// - 不再可被外部 set 覆盖（旧实现是 public 可写属性，导致状态可被随意替换）
/// - 通过 DI 独立注册为 Scoped，不归属于 E0Context
/// - 接口只暴露 Add/Get 等行为，状态封装在实现内
/// </summary>
public interface IErrs
{
    /// <summary>当前是否未出现任何错误（valid = true 表示无错）。</summary>
    bool IsValid { get; }

    /// <summary>所有错误条目（只读快照）。</summary>
    IReadOnlyList<ErrorEntry> Entries { get; }

    /// <summary>出错字段路径快照（用于前端表单字段绑定）。</summary>
    IReadOnlyList<string> Keys { get; }

    /// <summary>添加一条错误。</summary>
    void Add(string message, string? key = null, string? code = null);

    /// <summary>获取首条错误消息（无错时返回 null）。</summary>
    string? GetFirstError();

    /// <summary>清空所有错误。</summary>
    void Clear();
}

/// <summary>
/// 单条错误条目。
/// </summary>
/// <param name="Message">错误描述（面向用户）。</param>
/// <param name="Key">字段路径（可选，用于前端字段级提示）。</param>
/// <param name="Code">错误代码（可选，用于程序判断）。</param>
public record ErrorEntry(string Message, string? Key = null, string? Code = null);
