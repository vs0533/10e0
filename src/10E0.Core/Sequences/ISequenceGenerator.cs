namespace TenE0.Core.Sequences;

/// <summary>
/// 流水号生成器。
///
/// 设计要点：
/// - 格式串支持自由组合：固定文本 + 日期 token + 序号 token
/// - 序号 token 用 N 个 0 表示宽度（{0000} = 4 位补零）
/// - 日期 token 用 .NET 标准日期格式（{yyyyMMdd}、{yyyyMM}、{yyyy} 等）
/// - bucket 自动从格式串里的日期 token 推导 → 日/月/年自动归零
///
/// 例：
///   "ORD-{yyyyMMdd}-{0000}"  → ORD-20260518-0001（日重置，4 位序号）
///   "MD{yyyyMM}{000000}"     → MD2026050001（月重置，6 位序号）
///   "USR{00000000}"          → USR00000001（无日期 → 永不重置，8 位序号）
///   "INV-{yyyy}-{0000}"      → INV-2026-0001（年重置）
/// </summary>
public interface ISequenceGenerator
{
    /// <summary>
    /// 按格式串生成下一个流水号。
    /// </summary>
    /// <param name="sequenceKey">序列业务 key，区分不同序列空间（如 "order" / "course_code"）。</param>
    /// <param name="format">格式串，含日期 token 和序号 token。</param>
    Task<string> NextAsync(string sequenceKey, string format, CancellationToken cancellationToken = default);
}
