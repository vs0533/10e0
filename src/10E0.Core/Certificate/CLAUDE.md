# Certificate/ — 证书生成模块

模板 DSL + 默认 PDF 渲染器 + IFileService 集成的证书 / 结业证 / 发证内容生成（issue #185）。

## 设计取舍

- **拆独立包 `10E0.Core.Certificate`**（仿 RabbitMq/Kafka）：PDF 渲染依赖（PDFsharp + QRCoder）是重依赖。主包 `10E0.Core` 零 PDF 依赖，只放抽象（`ICertificateService` / `ICertificateRenderer` / 实体 / DSL）。渲染器实现 `PdfCertificateRenderer` 在独立包，业务方按需引用 + `AddTenE0PdfCertificateRenderer()` Replace 占位渲染器。
- **占位渲染器 `NullCertificateRenderer`**：主包注册它（渲染时抛「请引用独立包」明确异常）。未引用独立包时启动不崩，只有真正渲染才报错 —— 与 RabbitMq/Kafka 的 Replace 模式一致。
- **PDF 库选型：PDFsharp 6.2 而非 QuestPDF**：issue #185 原提议 QuestPDF，但 QuestPDF 已于 2026-06-10 切 Community License v2.0（非 OSI 认可 —— 源码可见商业许可，营收 > $1M 需付费）。本框架一贯纯 MIT 偏好（见 `10E0.Core.csproj` 的 ClosedXML/Cronos 注释、RabbitMq 拆包理由）。PDFsharp（empira 官方，真 MIT，2005-2026，无营收门槛）契合。
- **二维码不嵌入 PNG**：PDFsharp Core build 不支持 PNG 解码（仅 JPEG/Skia）。`PdfCertificateRenderer.DrawQrCode` 直接读 QRCoder `ModuleMatrix`（`List<BitArray>`）+ 用 `XGraphics.DrawRectangle` 画黑白方块 —— 零图像格式依赖，跨平台稳定，且不依赖 System.Drawing（QRCoder `QRCode` 类才需要）。
- **证书不走 `IEntityService`**：证书不是聚合（无业务方法 / 无领域事件 / 无需唯一性校验链），直接走 `ICertificateService`（已封装渲染 → IFileService 落库 → 写实例全流程）。
- **模板 DSL 是结构化对象，非脚本**：不接受用户提供的可执行模板字符串（安全考量 #2）。数据绑定不解析表达式（安全考量 #1）。二维码 URL scheme 白名单（仅 http/https，安全考量 #3）。

## 文件说明

| 文件 | 职责 |
|------|------|
| `CertificateDefinition.cs` | 模板定义根 record：Title/PaperKind/Orientation/Elements/Styles；多态序列化 |
| `CertificateElement.cs` | 抽象元素 record + 9 个 sealed 子类型（Title/Text/Name/Date/QrCode/Image/Seal/Signature/Line） |
| `CertificateStyles.cs` | 全局样式（字号/颜色/边距/间距） |
| `PaperKind.cs` | 纸张枚举（A4/A5/Letter）+ 方向枚举（Portrait/Landscape） |
| `CertificateOptions.cs` | 运行参数：DefaultFont/StorageCategory/SequenceKey/SequenceFormat |
| `CertificateRenderOptions.cs` | 渲染元数据 record：CertificateNo/RelatedEntityId/RelatedEntityType/Category |
| `ICertificateService.cs` | 服务契约（RenderAsync/RenderToStreamAsync/GetByRelatedEntityAsync） |
| `ICertificateRenderer.cs` | 渲染器抽象 + `NullCertificateRenderer` 占位（Format + RenderAsync） |
| `CertificateService.cs` | `CertificateService<TContext>`：模板加载 → Sequence 编号 → 渲染 → IFileService 落库 → 写实例 |
| `DataBinder.cs` | 数据绑定器（取值 + 格式化 + URL scheme 白名单校验），纯逻辑无副作用 |
| `Entities/TenE0CertificateTemplate.cs` | 模板实体（`AuditedEntity` + `IMultiTenantEntity`）：Code/Name/TemplateJson/IsEnabled/TenantId |
| `Entities/TenE0Certificate.cs` | 证书实例实体：TemplateCode/Title/CertificateNo/DataJson/FileAttachmentId/Related* |
| `Entities/CertificateModelBuilderExtensions.cs` | `ConfigureTenE0CertificateTables()` 表映射 + 列长常量（权威源） |

## 子目录

| 目录 | 职责 |
|------|------|
| `Entities/` | EF Core 实体 + ModelBuilder 扩展 |

## 跨模块依赖

- **Files**：证书 PDF 存 `IFileService`（复用 `TenE0FileAttachment`，`Category`=`certificate`）。`opt.Files = true`。
- **Sequences**：证书编号走 `ISequenceGenerator`（`CertificateOptions.SequenceKey` 配置时）。`opt.Sequences` 默认开。
- **TenE0SystemDbContext**：`ConfigureTenE0CertificateTables()` 自动建表，2 个 DbSet。

## 渲染器替换（业务方自定义）

```csharp
// Replace 默认 PDFsharp 实现为自定义（图片 / Word / SVG 渲染器）
services.Replace(ServiceDescriptor.Scoped<ICertificateRenderer, MyWordCertificateRenderer>());
```

## 选型变更记录

| 时间 | 变更 | 原因 |
|------|------|------|
| 2026-06-28 | PDF 库 QuestPDF → **PDFsharp 6.2** | QuestPDF 2026-06-10 切 Community License v2.0（非 MIT，营收门槛）。框架纯 MIT 偏好。PDFsharp 真 MIT（empira，无门槛） |

## 测试

- `tests/10E0.Core.Tests/Certificates/`：主包测试（DSL / 服务 / DI），InMemory + Mock IFileService/Renderer。
- `tests/10E0.Core.Certificate.Tests/`：独立包测试（`PdfCertificateRenderer`），验证输出合法 PDF（`%PDF-` 魔数）+ 二维码 + URL scheme 集成。

详见 `docs/29-certificate.md`。
