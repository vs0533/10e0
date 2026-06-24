namespace TenE0.Core.ImportExport;

/// <summary>
/// 导入/导出文件格式。
/// </summary>
public enum ExportFormat
{
    /// <summary>Excel .xlsx（ClosedXML）。</summary>
    Xlsx,

    /// <summary>CSV（RFC 4180，手写）。</summary>
    Csv,
}

/// <summary>
/// 导入事务边界策略。
/// </summary>
public enum TransactionMode
{
    /// <summary>
    /// 非事务（默认）：每行独立写入，单行失败收集错误后继续后续行。
    /// 适合批量导入场景 —— 部分脏数据不打断整批。
    /// </summary>
    NonTransactional,

    /// <summary>
    /// 事务：所有行在同一事务内提交，任一行失败回滚全量。
    /// 适合"全成功或全不导入"的强一致性场景。
    /// </summary>
    Transactional,
}

/// <summary>
/// 导出可选项。
/// </summary>
public sealed record ExportOptions
{
    /// <summary>工作表名（Excel 专用，CSV 忽略）。默认 "Sheet1"。</summary>
    public string SheetName { get; init; } = "Sheet1";

    /// <summary>是否输出表头行。默认 true。</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>是否对敏感字段脱敏（注入 IExportFieldFilter 时生效）。默认 true。</summary>
    public bool MaskSensitive { get; init; } = true;

    /// <summary>导出文件的默认编码（CSV 专用）。默认 UTF-8 with BOM（Excel 友好）。</summary>
    public System.Text.Encoding Encoding { get; init; } = new System.Text.UTF8Encoding(true);
}

/// <summary>
/// 导入可选项。
/// </summary>
public sealed record ImportOptions
{
    /// <summary>表头所在行号（1-based）。默认 1。</summary>
    public int HeaderRow { get; init; } = 1;

    /// <summary>数据起始行号（1-based）。默认 2（紧跟表头）。</summary>
    public int DataStartRow { get; init; } = 2;

    /// <summary>是否忽略全空行。默认 true。</summary>
    public bool IgnoreBlankRows { get; init; } = true;

    /// <summary>导入文件编码（CSV 专用）。默认 UTF-8。</summary>
    public System.Text.Encoding Encoding { get; init; } = System.Text.Encoding.UTF8;

    /// <summary>事务模式。默认 <see cref="TransactionMode.NonTransactional"/>。</summary>
    public TransactionMode TransactionMode { get; init; } = TransactionMode.NonTransactional;
}

/// <summary>
/// 单行导入读取结果。
/// </summary>
/// <typeparam name="T">目标实体类型。</typeparam>
/// <param name="RowNumber">行号（1-based，对应原始文件）。</param>
/// <param name="Data">解析后的实体（解析失败时为 default）。</param>
/// <param name="Errors">本行解析错误（列级）。空列表表示无错。</param>
public sealed record ImportRow<T>(int RowNumber, T? Data, List<string> Errors)
{
    /// <summary>本行是否解析无错。</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>单行导入错误的程序化描述。</summary>
/// <param name="RowNumber">行号（1-based）。</param>
/// <param name="Errors">错误消息列表（一行可能多错）。</param>
/// <param name="Code">错误码（默认 <see cref="TenE0.Core.Abstractions.ErrorCodes.ImportRowError"/>，便于前端 i18n / 路由）。</param>
public sealed record RowError(int RowNumber, List<string> Errors, string Code = TenE0.Core.Abstractions.ErrorCodes.ImportRowError);

/// <summary>
/// 导入执行汇总结果。
/// </summary>
public sealed class ImportResult
{
    /// <summary>总行数（含失败）。</summary>
    public int Total { get; init; }

    /// <summary>成功写入行数。</summary>
    public int Success { get; init; }

    /// <summary>失败行数。</summary>
    public int Failed { get; init; }

    /// <summary>
    /// 失败行明细（只读）。暴露为 <see cref="IReadOnlyList{T}"/> 防止调用方
    /// 通过 <c>Add</c> 改写结果（原 <c>List</c> 可变，存在被污染风险）。
    /// </summary>
    public IReadOnlyList<RowError> Errors { get; init; } = Array.Empty<RowError>();

    /// <summary>事务模式：若任一行失败已触发整体回滚，标记为 true。</summary>
    public bool TransactionRolledBack { get; init; }
}

/// <summary>
/// 导出产出的流及其元信息。
///
/// <para>之所以包成 record 而非直接返回 Stream：大文件降级（xlsx→csv）时调用方必须感知
/// <see cref="Format"/> 才能设置正确的 Content-Type / 文件后缀。降级原因通过
/// <see cref="DowngradedReason"/> 透出，便于上层日志/提示。</para>
///
/// <para><b>资源所有权</b>：<see cref="Content"/> 由本对象持有，<see cref="IDisposable.Dispose"/>
/// 释放它。调用方（如 ASP.NET Core 端点）必须负责释放 —— 直接把 <see cref="Content"/> 传给
/// <c>Results.File(Stream)</c> 不会自动释放（ASP.NET Core 只读取不 dispose），
/// 应用 <c>HttpContext.Response.RegisterForDisposeAsync</c> 或 <c>using</c> 包裹。
/// 详见 <see cref="Dispose"/>。</para>
/// </summary>
public sealed record ExportStream(Stream Content, ExportFormat Format, string? DowngradedReason = null) : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// 释放 <see cref="Content"/>（典型为 MemoryStream / 临时文件流）。
    /// 幂等：多次调用安全。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Content?.Dispose();
    }
}

/// <summary>
/// 导入进度回调载体。配合 <c>IProgress&lt;ImportProgress&gt;</c> 让调用方感知进度。
/// </summary>
public sealed record ImportProgress(int Processed, int Success, int Failed, int? Total);
