# ImportExport 模块

通用 Excel/CSV 导入导出（issue #154）。Excel 走 ClosedXML（MIT），CSV 手写 RFC 4180（不引入 CsvHelper）。

## 职责

- 统一抽象：`IExcelImporter` / `IExcelExporter` / `ICsvImporter` / `ICsvExporter` / `IImportTemplateGenerator`
- 声明式映射：attribute（`[ImportColumn]` / `[ExportColumn]` / `[ImportIgnore]` / `[ExportIgnore]`）+ fluent API（`ImportMapping<T>`）
- 框架协同：导入走 `IEntityService.CreateAsync`（复用唯一性 / 权限 / 流水号校验）；导出接 `DynamicWhere` 查询
- 大文件：分页流式加载 + 超阈值自动降级 CSV

## 设计决策

- **ClosedXML 而非 EPPlus**：MIT 许可，无商业限制（EPPlus 社区版有非商业条款）
- **不引入 CsvHelper**：RFC 4180 用状态机手写足够（`CsvReader` / `CsvWriter`），避免多一个依赖
- **导入事务边界**：默认非事务 + 收集错误（批量场景部分失败不打断），`TransactionMode.Transactional` 切换全成功或全回滚
- **Importable / Exportable 判定**：有 `[ImportColumn]` 且未 `[ImportIgnore]` 才可导入；有 `[ExportColumn]` 且未 `[ExportIgnore]` 才可导出。无任何标记的属性不进映射（避免 Password 等未声明字段泄露）
- **`IExportFieldFilter` 独立于 `IAuditFieldFilter`**：导出脱敏语义自洽，业务方可单独覆盖而不影响审计落库。默认实现包装 `IAuditFieldFilter`（审计模块未注册时直通）
- **`ExportStream` 而非裸 `Stream`**：大文件降级时调用方必须感知 `Format` 才能设置正确 Content-Type；`DowngradedReason` 透出降级原因
- **DI 无 `<TContext>`**：纯流处理；`ImportExecutor` 在端点接收 `IDbContextFactory<TContext>`

## 注意事项

- **非事务模式每行新建 context**：避免 tracked 实体污染 + errs 每行 Clear 防止错误累积
- **反射缓存**：`MappingResolver` 用 `ConcurrentDictionary<Type, ...>` 缓存 attribute 解析（对齐 `EntityService.SequenceFieldCache`）；fluent mapping 按调用传入不缓存
- **CSV 行分隔固定 CRLF**（RFC 4180 §2.2）；读取兼容 CRLF / LF
- **大数据量降级阈值**：默认 10w 行 → CSV；超 `MaxExportRows`（默认 10w）抛异常兜底，调用方应缩小查询范围
- **InMemory 不支持事务**：`ImportExecutor` 事务测试需抑制 `TransactionIgnoredWarning`（与 `FileServiceTests` 一致）；真实事务行为在 SQLite/SqlServer 才生效
- **不在本模块范围**：WebSocket 进度推送（依赖 #155，本次仅 `IProgress<ImportProgress>` 同步回调）、异步任务化导入（同步执行）

## 关键文件

- `Abstractions/` — 接口 + 契约（`ImportRow` / `ImportResult` / `ExportStream` / `ExportOptions` / `ImportOptions` / `TransactionMode`）+ `IExportFieldFilter` / `ExportFieldFilter`
- `Mapping/` — attribute + fluent API + `MappingResolver`（attribute/fluent 合并 + 反射缓存）
- `ClosedXml/` — `ClosedXmlExcelExporter` / `ClosedXmlExcelImporter` / `ClosedXmlTemplateGenerator`
- `Csv/` — `CsvWriter`（RFC 4180 写）/ `CsvReader`（状态机读）/ `CsvExporter` / `CsvImporter`
- `ImportExecutor.cs` — 通用导入执行器（事务 / 非事务 + 进度）
- `ImportExportOptions.cs` — 配置（批次 / 阈值）
- `DependencyInjection/ImportExportExtensions.cs` — `AddTenE0ImportExport`
