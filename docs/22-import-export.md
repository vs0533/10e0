# 22 — 导入导出（Import / Export）

通用 Excel/CSV 导入导出模块。Excel 走 [ClosedXML](https://github.com/ClosedXML/ClosedXML)（MIT 许可），CSV 手写 RFC 4180（不引入 CsvHelper）。导入可直接走 `IEntityService.CreateAsync`（复用唯一性 / 权限 / 流水号校验），导出可直接接 `DynamicWhere` 查询。

---

## 架构总览

```
IExcelExporter / IExcelImporter      ICsvExporter / ICsvImporter
        │                                    │
ClosedXmlExcelExporter              CsvExporter
ClosedXmlExcelImporter              CsvImporter
        │                                    │
        └──────── MappingResolver ◄──────────┘   (attribute / fluent → 统一 ColumnMap)
                       │
              [ImportColumn] / [ExportColumn]
              [ImportIgnore] / [ExportIgnore]
              ImportMapping<T>（fluent API）

IImportTemplateGenerator ─ ClosedXmlTemplateGenerator（表头 + 示例行 + 列校验）
IExportFieldFilter       ─ ExportFieldFilter（默认包装 IAuditFieldFilter）

ImportExecutor ─ 读流 → IEntityService.CreateAsync → 收集错误（事务 / 非事务）
```

所有代码位于 `TenE0.Core.ImportExport`，DI 扩展在 `TenE0.Core.DependencyInjection.ImportExportExtensions`。

---

## 快速开始

```csharp
// Program.cs
builder.Services.AddTenE0ImportExport();   // 无 <TContext>，纯流处理
```

```csharp
// 实体：声明列映射
public class Order
{
    [ImportColumn("订单号", Required = true)] [ExportColumn("订单号", Order = 1)]
    public string Code { get; set; } = "";

    [ImportColumn("金额")] [ExportColumn("金额", Order = 2, Format = "N2")]
    public decimal Amount { get; set; }

    [ExportColumn("下单时间", Order = 3, Format = "yyyy-MM-dd HH:mm")]
    public DateTimeOffset CreateTime { get; set; }

    [ImportIgnore] [ExportIgnore]
    public string SecretKey { get; set; } = "";
}
```

```csharp
// 导出：接 IQueryable，走 DynamicWhere，超阈值自动降级 CSV
var query = db.Orders.AsNoTracking().DynamicWhere(request.Where);
var export = await exporter.ExportAsync(query, new ExportOptions { SheetName = "订单" });
return export.Format == ExportFormat.Csv
    ? Results.File(export.Content, "text/csv", "orders.csv")
    : Results.File(export.Content, "...spreadsheetml.sheet", "orders.xlsx");

// 导入：IFormFile → ImportExecutor（走 EntityService 校验链）
await using var stream = file.OpenReadStream();
var result = await executor.ExecuteAsync<AppDbContext, Order>(
    factory, stream, ExportFormat.Xlsx, ct: ct);
// result.Total / result.Success / result.Failed / result.Errors

// 模板下载
await generator.GenerateAsync<Order>(stream, ct);
```

---

## 列映射

映射由 `MappingResolver` 解析（反射结果进程级缓存，对齐 `EntityService.SequenceFieldCache`）：

| Attribute | 作用 |
|-----------|------|
| `[ImportColumn("姓名")]` | 参与导入，源文件表头列名 |
| `[ExportColumn("姓名", Order = 1, Format = "N2")]` | 参与导出，列名 / 顺序 / 格式 |
| `[ImportIgnore]` | 不参与导入 |
| `[ExportIgnore]` | 不参与导出 |

**判定规则**：
- **Importable** = 有 `[ImportColumn]` 且未 `[ImportIgnore]`
- **Exportable** = 有 `[ExportColumn]` 且未 `[ExportIgnore]`
- 无任何标记的属性不进映射（避免 `Password` 等未声明字段意外泄露）

**Fluent API**（DTO 转换 / 列名与属性不一致 / 运行时动态映射）：

```csharp
var mapping = ImportMapping<Order>.Create(b => b
    .Map(x => x.Code).ToColumn("编码").Required()
    .Map(x => x.CreateTime).ToColumn("创建时间")
        .WithFormat("yyyy-MM-dd").ExportOnly());

// fluent 声明的属性覆盖 attribute，其余回退 attribute
var columns = MappingResolver.Resolve<Order>(mapping);
```

---

## 导出

### `IExcelExporter`

```csharp
Task<ExportStream> ExportAsync<T>(IEnumerable<T> data, ExportOptions?, CancellationToken);
Task<ExportStream> ExportAsync<T>(IQueryable<T> query, ExportOptions?, CancellationToken) where T : class;
```

- `IEnumerable<T>` 重载：内存数据集一次性写入
- `IQueryable<T>` 重载（典型 EF Core `DbSet`）：
  - 先 `CountAsync` 取总数
  - **超 `ImportExportOptions.LargeExportThreshold`（默认 10w）自动降级 CSV** —— `ExportStream.Format` 标记 `Csv`，`DowngradedReason` 附原因
  - 未降级时按 `ExportBatchSize`（默认 5000）分页 `ToListAsync` 逐批写入，避免一次性 `ToList` 内存爆炸
  - 超 `MaxExportRows`（默认 10w）抛 `InvalidOperationException` 兜底

`ExportStream`（而非裸 `Stream`）—— 降级时调用方据 `Format` 设置正确 Content-Type：

```csharp
public sealed record ExportStream(Stream Content, ExportFormat Format, string? DowngradedReason = null);
```

### 敏感字段脱敏

`IExportFieldFilter`（**独立于**审计模块的 `IAuditFieldFilter`）—— 导出语义自洽，业务方可单独覆盖：

```csharp
public interface IExportFieldFilter
{
    bool ShouldMask(string propertyName);
    object? Mask(string propertyName, object? value);
}
```

默认实现 `ExportFieldFilter` 包装 `IAuditFieldFilter`（若审计模块已注册）；未启用审计时直通不脱敏。导出前对每个值过滤，`ExportOptions.MaskSensitive = false` 可关闭。

---

## 导入

### `IExcelImporter` / `ICsvImporter`

```csharp
IAsyncEnumerable<ImportRow<T>> ReadAsync<T>(Stream, ImportOptions?, CancellationToken) where T : class, new();
```

逐行流式读取（`IAsyncEnumerable`），行级解析错误（类型转换失败 / 必填缺失）收集进 `ImportRow.Errors`，**不抛断流** —— 让调用方决定收集错误继续还是整体回滚。列匹配按表头文本，不依赖列顺序。

### `ImportExecutor`（核心协同）

把读取的行通过 `IEntityService.CreateAsync` 落库，**复用唯一性 / 权限 / 流水号校验**（核心价值：导入与正常创建走同一条校验链）：

```csharp
Task<ImportResult> ExecuteAsync<TContext, TEntity>(
    IDbContextFactory<TContext> contextFactory,
    Stream input,
    ExportFormat format,
    ImportOptions? options = null,
    EntityWriteOptions? writeOptions = null,
    IProgress<ImportProgress>? progress = null,
    CancellationToken ct = default);
```

| 事务模式 | 行为 |
|----------|------|
| **非事务**（默认） | 每行用**新建** DbContext 独立写入，失败行收集错误后继续；每行处理后清空 `IErrs` 避免错误累积。适合批量导入 |
| **事务** | 所有行共享同一 DbContext + 事务，任一行失败回滚全量；`ImportResult.TransactionRolledBack` 标记 |

不绑定 DbContext 类型 —— `IDbContextFactory` 由调用方传入。

---

## CSV（RFC 4180）

手写状态机，不依赖 CsvHelper：

- **写入**（`CsvWriter`）：含逗号 / 双引号 / CR / LF 的字段用双引号包裹，内部双引号翻倍；行分隔 CRLF
- **读取**（`CsvReader`）：支持引号包裹字段、引号内转义双引号、引号内 CR/LF、CRLF 与 LF 行分隔

---

## 模板生成

`IImportTemplateGenerator.GenerateAsync<T>` —— 根据列映射生成空白导入模板：

- 表头行（列名）
- 一行示例（按属性类型推断占位值：string→"示例"、int→0、DateTime→"2024-01-01"…）
- 必填列加粗 + 黄色底色，数字列附"必须为数字"数据校验

前端引导用户按模板填写，减少导入期的类型 / 必填错误。

---

## DI 注册

```csharp
public static IServiceCollection AddTenE0ImportExport(
    this IServiceCollection services,
    Action<ImportExportOptions>? configure = null);
```

注册：`IExcelExporter/Importer`（ClosedXML）、`ICsvExporter/Importer`（RFC 4180）、`IImportTemplateGenerator`、`IExportFieldFilter`、`ImportExecutor`。**无 `<TContext>` 泛型** —— 纯流处理，`ImportExecutor` 在端点接收 `IDbContextFactory<TContext>`。

---

## 配置（`ImportExportOptions`）

| 属性 | 默认 | 说明 |
|------|------|------|
| `ExportBatchSize` | 5000 | 导出流式分批大小（行/批） |
| `LargeExportThreshold` | 100000 | 超此行数自动降级 CSV |
| `MaxExportRows` | 100000 | 导出行数上限（兜底） |

---

## 错误码（`ErrorCodes`）

| 常量 | 值 | 含义 |
|------|----|------|
| `ImportRowError` | `IMPORT_ROW` | 导入行级错误（类型转换失败 / 必填缺失 / 校验失败） |
| `ImportTransactionRolledback` | `IMPORT_ROLLBACK` | 事务模式整体回滚 |
