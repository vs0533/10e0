# 29. 证书生成模块（模板 DSL + PDF 渲染器）

证书 / 结业证 / 发证内容生成 —— 把「模板 + 数据」渲染为正式文档（默认 PDF），存入 `IFileService`，可追溯 / 可重生成。

适用场景：科研项目结题证书、职称证书、结业证、批复文件、检验报告 —— 任何「按模板 + 数据 → 渲染成正式文档 → 存储 → 下载」的需求。

> 本模块由 R3 真实项目（淄博市卫生健康科研项目与科研资源登记平台）验证暴露的框架增强驱动，issue #185。

---

## 29.1 为什么是独立 NuGet 包

PDF 渲染依赖（PDFsharp + QRCoder）是重依赖。如果把它们塞进 `TenE0.Core` 主包，所有用户（哪怕只想要证书的「模板管理 / 数据绑定」能力而不渲染 PDF）都会被迫引入这些依赖。因此框架把渲染器拆成独立包，**按需引用**：

```bash
# 只在你需要 PDF 渲染的项目里加
dotnet add package TenE0.Core.Certificate
```

| 包 | 含 | 依赖 |
|----|----|------|
| `TenE0.Core` | 证书抽象（`ICertificateService` / `ICertificateRenderer` / 实体 / 模板 DSL）| 零 PDF 依赖 |
| `TenE0.Core.Certificate` | `PdfCertificateRenderer`（默认 PDF 渲染器）| `PdfSharp` 6.2（MIT）+ `QRCoder` 1.8（MIT）|

> **契约**：主包注册一个 `NullCertificateRenderer` 占位（渲染时抛「请引用独立包」明确异常）。引用独立包后调 `AddTenE0PdfCertificateRenderer()` Replace 为 PDFsharp 渲染器。未引用独立包时启动不崩，只有真正渲染才报错 —— 与 RabbitMq/Kafka 的 Replace 模式一致。

---

## 29.2 三行接入

```csharp
// Program.cs
builder.Services.AddTenE0All<AppDbContext>(builder.Configuration, opt =>
{
    opt.Files = true;            // 证书 PDF 存 IFileService，必须启用
    opt.Sequences = true;        // 证书编号走流水号（默认开）
    opt.Certificate = true;      // 证书模块（注册 ICertificateService + 占位渲染器）
    opt.CertificateOptions = cert =>
    {
        cert.SequenceKey = "certificate";
        cert.SequenceFormat = "CERT-{yyyyMMdd}-{0000}";
    };
});

// 替换占位渲染器为 PDFsharp 实现（需引用 TenE0.Core.Certificate 包）
builder.Services.AddTenE0PdfCertificateRenderer();
```

`AddTenE0Certificate<TContext>()` 一次性完成：
1. 注册 `ICertificateService` → `CertificateService<TContext>`（渲染 / 落库 / 查询）
2. 注册 `ICertificateRenderer` → `NullCertificateRenderer`（占位，由独立包 Replace）

`AddTenE0PdfCertificateRenderer()` 把渲染器 **Replace** 为 `PdfCertificateRenderer`（PDFsharp）。

---

## 29.3 模板 DSL（声明式元素）

证书模板用结构化 C# 对象（`CertificateDefinition`）描述，**不是脚本，不接受用户输入字符串**（安全考量 #2）。序列化为 JSON 存 `TenE0CertificateTemplate.TemplateJson`。

### 元素速查

| 元素 | 用途 | 关键字段 |
|------|------|---------|
| `TitleElement` | 顶部大字标题（居中） | `Text`（命中数据时被覆盖） |
| `TextElement` | 带标签的文本行（如"项目编号：PRD-001"） | `Label` / `Placeholder` |
| `NameElement` | 突出姓名（字号略大 / 加粗） | `Label`（默认"姓名"） |
| `DateElement` | 签发日期（按 `Format` 格式化） | `Label` / `Format`（默认 yyyy-MM-dd） |
| `QrCodeElement` | 二维码（验真场景） | `UrlPlaceholder`（支持 `{certificateNo}` 占位） |
| `ImageElement` | 嵌入图片（徽标 / 头像） | `Width` / `Height`（pt） |
| `SealElement` | 盖章位（带边框方框 + 单位名） | `Label` |
| `SignatureElement` | 签字位（下划线 + 标签） | `Label` |
| `LineElement` | 分隔线 | `Color` |

### 定义模板（代码）

```csharp
using TenE0.Core.Certificate;

var def = new CertificateDefinition(
    Title: "科研项目结题证书",
    PaperKind: PaperKind.A4,
    Orientation: CertificateOrientation.Landscape,
    Elements:
    [
        new TitleElement("title", "科研项目结题证书"),
        new TextElement("projectNo", "项目编号"),
        new TextElement("projectName", "项目名称"),
        new NameElement("leader"),
        new TextElement("unit", "依托单位"),
        new DateElement("issueDate"),
        new QrCodeElement("qr", "https://verify.example.com/{certificateNo}"),
        new SealElement("seal", "淄博市卫生健康委员会"),
        new SignatureElement("signature")
    ]);
```

### 定义模板（JSON，存 DB）

模板 DSL 用 `System.Text.Json` 多态序列化（`[JsonDerivedType]` 声明 discriminant），反序列化自动回填正确子类型：

```json
{
  "title": "结业证书",
  "paperKind": "A4",
  "orientation": "Landscape",
  "elements": [
    { "$type": "title", "key": "title", "text": "结业证书" },
    { "$type": "name", "key": "leader" },
    { "$type": "qrcode", "key": "qr", "urlPlaceholder": "https://verify.example.com/{certificateNo}" }
  ]
}
```

---

## 29.4 数据绑定

渲染时传一个 `IReadOnlyDictionary<string, object?>`，key 与元素 `Key` 匹配，命中即用字典值覆盖元素默认：

```csharp
var data = new Dictionary<string, object?>
{
    ["projectNo"] = project.Code,
    ["projectName"] = project.Name,
    ["leader"] = project.LeaderName,
    ["unit"] = project.UnitName,
    ["issueDate"] = DateTimeOffset.UtcNow,
    ["qr"] = $"https://verify.example.com/{project.Code}",
};
var cert = await certificateSvc.RenderAsync("research-completion", data, ...);
```

**安全**（issue 安全考量 #1）：
- 字典值仅作字符串 / 数字 / 日期 / 图片占位符替换，**不解析为表达式**。
- 模板 DSL 是结构化对象，**不接受**用户提供的可执行模板字符串。
- **二维码 URL scheme 白名单**（安全考量 #3）：`QrCodeElement` 的 URL 渲染前校验仅 http/https，拒绝 `javascript:` / `file:` / `data:` 等危险 scheme（防扫码终端触发非预期行为）。

---

## 29.5 ICertificateService 接口

```csharp
public interface ICertificateService
{
    // 渲染 + 落库：PDF 存 IFileService + 写证书实例 + 可选走 Sequence 生成编号
    Task<TenE0Certificate> RenderAsync(string templateCode,
        IReadOnlyDictionary<string, object?> data,
        CertificateRenderOptions? options = null, CancellationToken ct = default);

    // 仅渲染到流，不落库（即时预览 / 下载）
    Task<Stream> RenderToStreamAsync(string templateCode,
        IReadOnlyDictionary<string, object?> data, CancellationToken ct = default);

    // 按业务实体查询已生成证书
    Task<List<TenE0Certificate>> GetByRelatedEntityAsync(
        string relatedEntityType, string relatedEntityId, CancellationToken ct = default);
}

public sealed record CertificateRenderOptions(
    string? CertificateNo = null,        // 显式编号；不传走 Sequence（若配置）
    string? RelatedEntityId = null,
    string? RelatedEntityType = null,
    string? Category = "certificate");   // 存储分类（写 TenE0FileAttachment.Category）
```

| 方法 | 说明 |
|------|------|
| `RenderAsync` | 渲染 PDF → `IFileService.UploadAsync` 落库 → 写 `TenE0Certificate` 实例 → 返回实例（`FileAttachmentId` 指向 PDF） |
| `RenderToStreamAsync` | 仅渲染到流，不落库（不调 IFileService、不写证书实例） |
| `GetByRelatedEntityAsync` | 按 (RelatedEntityType, RelatedEntityId) 查已生成证书，按 CreateTime 倒序 |

### 渲染流程（RenderAsync）

```
LoadTemplateAsync(templateCode)     ── 从 DB 加载模板，校验 IsEnabled，反序列化 DSL
        │
        ▼
Sequence.NextAsync(SequenceKey)     ── 证书编号（未显式传入 + SequenceKey 配置时）
        │
        ▼
ICertificateRenderer.RenderAsync    ── 渲染 PDF 流（PdfCertificateRenderer / 自定义）
        │
        ▼
IFileService.UploadAsync            ── PDF 落库（Category=certificate, RelatedEntityId）
        │
        ▼
写 TenE0Certificate 实体            ── 快照（Title/CertificateNo/DataJson）+ FileAttachmentId
```

---

## 29.6 与 IFileService 集成

证书渲染产物 PDF 通过 `IFileService.UploadAsync` 存储，复用 `TenE0FileAttachment`：

- `Category` = `CertificateOptions.StorageCategory`（默认 `certificate`，可在 `CertificateRenderOptions` 覆盖）
- `RelatedEntityId` / `RelatedEntityType` = 渲染时传入的业务实体（如项目 Id + `ResearchProject`）
- 下载走标准 `IFileService.DownloadAsync(fileAttachmentId)`

因此证书模块**依赖 Files 模块**（`opt.Files = true`）。`TenE0Certificate.FileAttachmentId` 指向 PDF 文件元数据。

---

## 29.7 证书编号（Sequence 集成）

`CertificateOptions.SequenceKey` 配置后，`RenderAsync` 自动调 `ISequenceGenerator.NextAsync(SequenceKey, SequenceFormat)` 生成编号（防并发重复，详见 [15. 流水号](15-sequences.md)）：

```csharp
opt.CertificateOptions = cert =>
{
    cert.SequenceKey = "certificate";
    cert.SequenceFormat = "CERT-{yyyyMMdd}-{0000}";  // 同 [Sequence] 语法
};
```

不配 `SequenceKey` 时编号留空，业务方需在 `CertificateRenderOptions.CertificateNo` 显式传入（表上有唯一索引兜底）。

---

## 29.8 渲染器抽象（可替换）

```csharp
public interface ICertificateRenderer
{
    string Format { get; }   // "pdf" / "png" / "docx"
    Task<Stream> RenderAsync(CertificateDefinition definition,
        IReadOnlyDictionary<string, object?> data, CancellationToken ct = default);
}
```

业务方可自定义实现（图片渲染器 / Word 渲染器 / SVG 渲染器），通过 `services.Replace(...)` 覆盖默认注册：

```csharp
// 自定义渲染器覆盖默认 PDFsharp 实现
services.Replace(ServiceDescriptor.Scoped<ICertificateRenderer, MyWordCertificateRenderer>());
```

默认 `PdfCertificateRenderer`（在 `TenE0.Core.Certificate` 包）基于 **PDFsharp 6.2**（真 MIT，跨平台 Core build），用 `XGraphics` 流式布局，二维码直接绘制矩阵（零图像格式依赖）。

> ⚠️ **PDF 库选型说明**：issue #185 原提议 QuestPDF，但 QuestPDF 已于 2026-06-10 切换为 **Community License v2.0**（非 OSI 认可的开源许可 —— 源码可见商业许可，营收超 $1M 需付费专业/企业版）。本框架一贯偏好纯 MIT 依赖（见 `10E0.Core.csproj` 的 ClosedXML/Cronos 注释），故改用 **PDFsharp**（empira 官方，真 MIT，2005-2026，无营收门槛，商业免费）。

---

## 29.9 实体

### TenE0CertificateTemplate（模板）

```csharp
public sealed class TenE0CertificateTemplate : AuditedEntity, IMultiTenantEntity
{
    public string Code { get; set; } = "";           // 业务编码，唯一
    public string Name { get; set; } = "";           // 显示名
    public string TemplateJson { get; set; } = "";   // CertificateDefinition 序列化 JSON
    public bool IsEnabled { get; private set; } = true;  // Enable()/Disable() 切换
    public string TenantId { get; set; } = "";
}
```

### TenE0Certificate（证书实例）

```csharp
public sealed class TenE0Certificate : AuditedEntity, IMultiTenantEntity
{
    public string TemplateCode { get; set; } = "";    // 冗余字段，便于查询
    public string Title { get; set; } = "";           // 快照（生成当时的标题）
    public string CertificateNo { get; set; } = "";   // 证书编号（可走 [Sequence]）
    public string DataJson { get; set; } = "";        // 生成时数据快照
    public string? FileAttachmentId { get; set; }     // 指向 TenE0FileAttachment.Id
    public string? RelatedEntityId { get; set; }
    public string? RelatedEntityType { get; set; }
    public string TenantId { get; set; } = "";
}
```

两者均实现 `IMultiTenantEntity`（自动租户 Named Query Filter，见 [20. 多租户](20-multi-tenancy.md)）。继承 `TenE0SystemDbContext` 的业务 DbContext 自动获得证书表（EnsureCreated/Migrate 自动建表）。

---

## 29.10 配置项（CertificateOptions）

```csharp
public sealed class CertificateOptions
{
    public string DefaultFont { get; set; } = "Microsoft YaHei";  // 中文字体，回退系统默认
    public string StorageCategory { get; set; } = "certificate";  // TenE0FileAttachment.Category
    public string? SequenceKey { get; set; }                      // 证书编号流水号 key（null 不自动生成）
    public string SequenceFormat { get; set; } = "CERT-{yyyyMMdd}-{0000}";
}
```

也可从 `appsettings.json` 绑定（`AddTenE0PdfCertificateRenderer` 重载支持）：

```csharp
builder.Services.AddTenE0PdfCertificateRenderer(builder.Configuration.GetSection("Certificate"));
```

```json
{
  "Certificate": { "DefaultFont": "SimSun", "SequenceKey": "certificate" }
}
```

> 生产环境若证书含中文，**必须预装中文字体**（如 Microsoft YaHei / SimSun / Noto Sans CJK）。运行环境无中文字体时回退方块。Linux 容器通常需显式安装 `fonts-noto-cjk`。

---

## 29.11 R3 结业证书实战示例

```csharp
// R3 业务层：科研项目结题审核通过后生成结业证书
public async Task<string> IssueCompletionCertificateAsync(ResearchProject project, CancellationToken ct)
{
    var data = new Dictionary<string, object?>
    {
        ["projectNo"] = project.Code,
        ["projectName"] = project.Name,
        ["leader"] = project.LeaderName,
        ["unit"] = project.UnitName,
        ["issueDate"] = DateTimeOffset.UtcNow,
    };
    var cert = await _certificateSvc.RenderAsync(
        "research-completion",   // 模板 code（DB 里预置）
        data,
        new CertificateRenderOptions(
            RelatedEntityId: project.Id,
            RelatedEntityType: "ResearchProject"),
        ct);
    return cert.Id;  // 证书 Id，前端凭此下载 / 查阅
}
```

---

## 29.12 端点（Minimal API）

范本见 `src/10E0.Api/Endpoints/CertificateEndpoints.cs`：

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/demo/certificates/render` | 渲染证书（模板 code + 数据 → PDF 落库 + 证书实例） |
| GET | `/demo/certificates/by-entity/{type}/{id}` | 按业务实体查询已生成证书 |

```csharp
app.MapPost("/demo/certificates/render", async (
    RenderCertificateDto dto, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
{
    var cert = await dispatcher.SendAsync(
        new RenderCertificateCommand(dto.TemplateCode, dto.Data, options), ct);
    return errs.IsValid
        ? ApiResultResult.Api(ApiResult<object>.Ok(cert))
        : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
});
```

> **证书不走 `IEntityService`** —— 证书不是聚合（无业务方法 / 无领域事件 / 无需唯一性校验链），直接走 `ICertificateService`（已封装渲染 → 落库全流程）。

---

## 29.13 安全考量

| # | 风险 | 缓解 |
|---|------|------|
| 1 | 数据绑定执行任意代码 | `data` 字典值仅作字符串/数字/日期/图片占位符替换，**不解析为表达式** |
| 2 | 模板注入 | 模板 DSL 是结构化对象，**不接受**用户提供的可执行模板字符串 |
| 3 | 二维码危险 URL | `QrCodeElement` 的 URL 渲染前校验 scheme 白名单（仅 http/https），拒绝 javascript:/file:/data: |
| 4 | 跨租户泄露 | `TenE0CertificateTemplate` / `TenE0Certificate` 实现 `IMultiTenantEntity`，自动走租户 Named Query Filter |
| 5 | 编号并发重复 | 走框架 `ISequenceGenerator`（乐观并发 + 唯一索引兜底） |

---

## 29.14 依赖包

| 包 | 版本 | 用途 | 许可 |
|----|------|------|------|
| PdfSharp | 6.2.4 | PDF 生成（Core build，跨平台）| **MIT**（empira，2005-2026，无营收门槛） |
| QRCoder | 1.8.0 | 二维码矩阵生成 | MIT（Shane32 维护） |

> ⚠️ **不**用 QuestPDF：2026-06-10 起切 Community License v2.0（非 MIT，源码可见商业许可）。PDFsharp 真 MIT，与框架许可证偏好一致。

---

## 29.15 测试覆盖

- **CertificateDefinitionTests**（10 例）：DSL 序列化往返、各元素类型、数据绑定、URL scheme 白名单（javascript:/file:/data: 拒绝）
- **CertificateServiceTests**（6 例）：RenderAsync 落库 + Sequence 编号、RenderToStreamAsync 不落库、GetByRelatedEntityAsync、禁用模板拒绝、模板不存在抛
- **CertificateExtensionsTests**（3 例）：DI 注册验证、占位渲染器抛异常、聚合选项开关默认 false
- **PdfCertificateRendererTests**（13 例）：各元素渲染为合法 PDF（`%PDF-` 魔数）、全纸张/方向组合、二维码、base64 图片、自定义样式、URL scheme 集成

详见 `tests/10E0.Core.Tests/Certificates/` 与 `tests/10E0.Core.Certificate.Tests/`。
