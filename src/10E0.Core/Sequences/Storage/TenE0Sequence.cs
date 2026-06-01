using TenE0.Core.Entities;

namespace TenE0.Core.Sequences.Storage;

/// <summary>
/// 流水号序列存储表。每个序列 key 对应一条记录。
/// 重置策略（日重置/月重置/年重置/永不重置）由当前 bucket 决定 — 不同 bucket 自动归零。
///
/// 例：
///   Key="order", Format="ORD-{yyyyMMdd}-{0000}"
///   2026-05-18 当天: 第 1 次取号 → ORD-20260518-0001，第 2 次 → ORD-20260518-0002
///   2026-05-19: 自动重置 → ORD-20260519-0001
/// </summary>
public sealed class TenE0Sequence : BaseEntity
{
    /// <summary>序列业务 key（如 "order" / "course_code" / "application_no"）。同时作为唯一索引。</summary>
    public required string SequenceKey { get; set; }

    /// <summary>当前 bucket — 用于判断是否要归零。值由格式串里的日期 token 渲染得出，无日期 token 时固定为 "_"。</summary>
    public required string CurrentBucket { get; set; }

    /// <summary>当前 bucket 已发放到的序号（下一次发放时 +1）。</summary>
    public long CurrentNumber { get; set; }
}
