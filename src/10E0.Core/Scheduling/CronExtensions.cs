using Cronos;

namespace TenE0.Core.Scheduling;

/// <summary>
/// Cron 表达式解析与下次执行时间计算（issue #164）。
///
/// <para>
/// 封装 <c>Cronos</c> 库，把 <c>CronExpression.Parse</c> 抛出的 <see cref="CronFormatException"/>
/// 转成 <see cref="ArgumentException"/>（带任务上下文），让调用方无需 catch Cronos 专属异常。
/// </para>
///
/// <para>
/// <b>Cron 格式约定</b>：用 6 字段（含秒）格式 <c>sec min hour day month dayOfWeek</c>，
/// 与 Quartz 的 7 字段（含年 + 可选秒）对齐。典型示例：
/// <list type="bullet">
/// <item><c>"0 0 9 * * ?"</c> — 每天 9:00（<c>?</c> 表示日/周互斥，Cronos 也接受 <c>*</c>）</item>
/// <item><c>"0 0 0 1 * ?"</c> — 每月 1 号 0 点</item>
/// <item><c>"0 0 2 ? * MON"</c> — 每周一 2:00</item>
/// </list>
/// </para>
/// </summary>
public static class CronExtensions
{
    /// <summary>
    /// 解析 Cron 表达式为 <see cref="CronExpression"/>（6 字段含秒）。
    /// 表达式非法时抛 <see cref="ArgumentException"/>（带原表达式）。
    /// </summary>
    /// <param name="expression">Cron 表达式。</param>
    /// <param name="jobCode">任务编码（仅用于异常消息上下文，可空）。</param>
    public static CronExpression Parse(string expression, string? jobCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        try
        {
            // IncludeSeconds: 支持 6 字段表达式（含秒），兼容 Quartz 风格 "0 0 9 * * ?"
            return CronExpression.Parse(expression, CronFormat.IncludeSeconds);
        }
        catch (CronFormatException ex)
        {
            var ctx = string.IsNullOrEmpty(jobCode) ? string.Empty : $" (任务 {jobCode})";
            throw new ArgumentException($"无效的 Cron 表达式 '{expression}'{ctx}：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 计算给定 Cron 表达式在 <paramref name="from"/> 之后的下一次执行时间。
    /// 表达式非法时抛 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="expression">Cron 表达式。</param>
    /// <param name="from">基准时间（通常是「现在」）。</param>
    /// <param name="timeZone">时区；默认 <see cref="TimeZoneInfo.Utc"/> 避免夏令时坑。</param>
    /// <param name="jobCode">任务编码（仅用于异常消息上下文，可空）。</param>
    /// <returns>下次执行时间；若表达式在未来不会触发（如固定日期已过）则返回 <c>null</c>。</returns>
    public static DateTimeOffset? GetNextOccurrence(
        string expression,
        DateTimeOffset from,
        TimeZoneInfo? timeZone = null,
        string? jobCode = null)
    {
        var cron = Parse(expression, jobCode);
        var tz = timeZone ?? TimeZoneInfo.Utc;
        return cron.GetNextOccurrence(from, tz);
    }

    /// <summary>
    /// 校验 Cron 表达式是否合法（不抛异常）。供 Admin API 创建/修改动态任务时做前置校验。
    /// </summary>
    /// <returns><c>true</c> 合法；<c>false</c> 非法。</returns>
    public static bool IsValid(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }
        try
        {
            CronExpression.Parse(expression, CronFormat.IncludeSeconds);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }
}
