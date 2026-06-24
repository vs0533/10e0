using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;

namespace TenE0.Core.ImportExport;

/// <summary>
/// 通用导入执行器 —— 把读取出的行通过 <see cref="IEntityService.CreateAsync"/> 落库，
/// 复用唯一性 / 权限 / 流水号校验（核心价值：导入与正常创建走同一条校验链）。
///
/// <para><b>非事务模式</b>（<see cref="TransactionMode.NonTransactional"/>，默认）：每行用<b>新建</b>
/// DbContext 独立写入，失败行收集错误后继续；每行处理后清空 <see cref="IErrs"/>，避免错误累积污染下一行。
/// 适合批量导入 —— 部分脏数据不打断整批。</para>
///
/// <para><b>事务模式</b>（<see cref="TransactionMode.Transactional"/>）：所有行共享同一 DbContext + 事务，
/// 任一行失败回滚全量；<see cref="ImportResult.TransactionRolledBack"/> 标记是否已回滚。</para>
///
/// <para>不绑定 DbContext 类型 —— <see cref="IDbContextFactory"/> 由调用方传入，本类纯流处理。</para>
/// </summary>
public sealed class ImportExecutor(
    IExcelImporter excelImporter,
    ICsvImporter csvImporter,
    IEntityService entityService,
    IErrs errs)
{
    /// <summary>
    /// 执行导入：读取流 → 逐行 CreateAsync → 收集错误。
    /// </summary>
    /// <typeparam name="TContext">DbContext 类型（由 <paramref name="contextFactory"/> 决定）。</typeparam>
    /// <typeparam name="TEntity">目标实体类型（须实现 <see cref="IBaseEntity"/>）。</typeparam>
    /// <param name="contextFactory">DbContext 工厂（非事务模式每行新建 context；事务模式共用一个）。</param>
    /// <param name="input">导入文件流。</param>
    /// <param name="format">导入格式。</param>
    /// <param name="options">导入选项（含事务模式）。</param>
    /// <param name="writeOptions">传给 <see cref="IEntityService.CreateAsync"/> 的写选项（唯一性/权限等）。</param>
    /// <param name="progress">可选进度回调。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ImportResult> ExecuteAsync<TContext, TEntity>(
        IDbContextFactory<TContext> contextFactory,
        Stream input,
        ExportFormat format,
        ImportOptions? options = null,
        EntityWriteOptions? writeOptions = null,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
        where TContext : DbContext
        where TEntity : class, IBaseEntity, new()
    {
        options ??= new ImportOptions();

        var rows = ReadRows<TEntity>(input, format, options, ct);
        var errors = new List<RowError>();
        var total = 0;
        var success = 0;

        if (options.TransactionMode == TransactionMode.Transactional)
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            await foreach (var row in rows.WithCancellation(ct))
            {
                total++;
                errs.Clear();

                if (!row.IsValid)
                {
                    errors.Add(new RowError(row.RowNumber, row.Errors));
                    continue;
                }

                var ok = await entityService.CreateAsync<TEntity>(context, row.Data!, writeOptions, ct);
                if (ok && errs.IsValid)
                {
                    success++;
                    // 已 SaveChanges 的实体仍被 ChangeTracker 持有（Unchanged 状态）。
                    // 事务模式下跨行复用同一 context，10w+ 行导入会让 ChangeTracker 累积海量引用
                    // （GC 压力 + 长事务锁 + 事务日志暴涨）。每行清理释放已持久化实体的追踪。
                    context.ChangeTracker.Clear();
                }
                else
                {
                    // 收集 errs 里的校验失败 + 已存在的行解析错误
                    var rowErrors = new List<string>();
                    if (row.Errors.Count > 0) rowErrors.AddRange(row.Errors);
                    rowErrors.AddRange(errs.Entries.Select(e => e.Message));
                    errors.Add(new RowError(row.RowNumber, rowErrors));
                }

                progress?.Report(new ImportProgress(total, success, errors.Count, null));
            }

            // 事务模式：任一失败即整体回滚
            if (errors.Count > 0)
            {
                await transaction.RollbackAsync(ct);
                return new ImportResult
                {
                    Total = total,
                    Success = 0,
                    Failed = total,
                    Errors = errors,
                    TransactionRolledBack = true,
                };
            }

            await transaction.CommitAsync(ct);
            return new ImportResult
            {
                Total = total,
                Success = success,
                Failed = 0,
                Errors = errors,
            };
        }

        // 非事务模式：每行独立 context
        await foreach (var row in rows.WithCancellation(ct))
        {
            total++;
            errs.Clear();

            if (!row.IsValid)
            {
                errors.Add(new RowError(row.RowNumber, row.Errors));
                progress?.Report(new ImportProgress(total, success, errors.Count, null));
                continue;
            }

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var ok = await entityService.CreateAsync<TEntity>(context, row.Data!, writeOptions, ct);
            if (ok && errs.IsValid)
            {
                success++;
            }
            else
            {
                var rowErrors = new List<string>();
                if (row.Errors.Count > 0) rowErrors.AddRange(row.Errors);
                rowErrors.AddRange(errs.Entries.Select(e => e.Message));
                errors.Add(new RowError(row.RowNumber, rowErrors));
            }

            progress?.Report(new ImportProgress(total, success, errors.Count, null));
        }

        return new ImportResult
        {
            Total = total,
            Success = success,
            Failed = errors.Count,
            Errors = errors,
        };
    }

    private async IAsyncEnumerable<ImportRow<TEntity>> ReadRows<TEntity>(
        Stream input,
        ExportFormat format,
        ImportOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        where TEntity : class, new()
    {
        IAsyncEnumerable<ImportRow<TEntity>> rows = format switch
        {
            ExportFormat.Xlsx => excelImporter.ReadAsync<TEntity>(input, options, ct),
            ExportFormat.Csv => csvImporter.ReadAsync<TEntity>(input, options, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的导入格式"),
        };

        await foreach (var row in rows.WithCancellation(ct))
            yield return row;
    }
}
