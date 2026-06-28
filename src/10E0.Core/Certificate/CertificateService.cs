using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.Certificate.Entities;
using TenE0.Core.Files;
using TenE0.Core.Sequences;

namespace TenE0.Core.Certificate;

/// <summary>
/// <see cref="ICertificateService"/> 的默认实现（issue #185）。
///
/// <para>
/// 泛型化设计：TContext 仅需是 <see cref="DbContext"/>，实体 <c>TenE0CertificateTemplate</c> /
/// <c>TenE0Certificate</c> 通过 <see cref="CertificateModelBuilderExtensions.ConfigureTenE0CertificateTables"/>
/// 在 DbContext 的 OnModelCreating 中注册即可（继承 <c>TenE0SystemDbContext</c> 的 DbContext 已自动完成）。
/// </para>
///
/// <para>
/// <b>依赖</b>：
/// <list type="bullet">
/// <item><see cref="IDbContextFactory{TContext}"/>：短作用域 DbContext（不长期持有）。</item>
/// <item><see cref="ICertificateRenderer"/>：渲染器抽象。默认 <c>NullCertificateRenderer</c>（占位抛异常），
/// 引用独立包 <c>TenE0.Core.Certificate</c> 后 Replace 为 <c>PdfCertificateRenderer</c>。</item>
/// <item><see cref="IFileService"/>：渲染产物 PDF 落库（复用 <c>TenE0FileAttachment</c>）。</item>
/// <item><see cref="ISequenceGenerator"/>：证书编号流水号（可选，<c>SequenceKey</c> 配置时使用）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>安全性</b>：数据绑定走 <see cref="DataBinder"/>（scheme 白名单 + 不执行任意代码 + 结构化模板）。
/// </para>
/// </summary>
public sealed class CertificateService<TContext>(
    IDbContextFactory<TContext> contextFactory,
    ICertificateRenderer renderer,
    IFileService fileService,
    ISequenceGenerator? sequenceGenerator,
    IOptions<CertificateOptions> certificateOptions,
    ILogger<CertificateService<TContext>>? logger = null) : ICertificateService
    where TContext : DbContext
{
    // Web defaults：camelCase + 不区分属性大小写，与 SystemParameterStore / 端点 JsonOptions 对齐。
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<TenE0Certificate> RenderAsync(
        string templateCode,
        IReadOnlyDictionary<string, object?> data,
        CertificateRenderOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentNullException.ThrowIfNull(data);
        var opts = certificateOptions.Value;
        var renderOpts = options ?? new CertificateRenderOptions();

        // 1. 加载模板 + 反序列化 DSL
        var (definition, template) = await LoadTemplateAsync(templateCode, ct);

        // 2. 证书编号：显式传入 > Sequence 自动生成 > 留空
        var certificateNo = renderOpts.CertificateNo;
        if (string.IsNullOrWhiteSpace(certificateNo) && !string.IsNullOrWhiteSpace(opts.SequenceKey))
        {
            // SequenceKey 配置时走流水号生成器（防并发重复，详见 docs/15）。
            if (sequenceGenerator is null)
                throw new InvalidOperationException(
                    $"CertificateOptions.SequenceKey='{opts.SequenceKey}' 已配置，但 ISequenceGenerator 未注册。" +
                    "请确保 opt.Sequences=true（默认开）或手动注册 AddTenE0Sequences<TContext>()。");
            certificateNo = await sequenceGenerator.NextAsync(opts.SequenceKey, opts.SequenceFormat, ct);
        }
        certificateNo ??= string.Empty;

        // 3. 把 certificateNo 注入 data 副本（供 QrCodeElement 的 {certificateNo} 占位符替换）
        var boundData = BindWithCertificateNo(data, certificateNo);

        // 4. 渲染（渲染器负责实际布局；DataBinder 已校验 scheme）
        var stream = await renderer.RenderAsync(definition, boundData, ct);

        // 5. 落库 PDF → IFileService（复用 TenE0FileAttachment）
        var category = string.IsNullOrWhiteSpace(renderOpts.Category) ? opts.StorageCategory : renderOpts.Category;
        var fileName = $"{templateCode}-{certificateNo}.pdf";
        var contentType = renderer.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf" : "application/octet-stream";
        UploadResponse upload;
        try
        {
            stream.Position = 0;
            upload = await fileService.UploadAsync(stream, fileName, contentType, new UploadRequest
            {
                Category = category,
                RelatedEntityId = renderOpts.RelatedEntityId,
                RelatedEntityType = renderOpts.RelatedEntityType,
            }, ct);
        }
        finally
        {
            await stream.DisposeAsync();
        }

        // 6. 写证书实例（含快照）
        var entity = new TenE0Certificate
        {
            TemplateCode = template.Code,
            Title = definition.Title,
            CertificateNo = certificateNo,
            DataJson = JsonSerializer.Serialize(boundData, JsonOptions),
            FileAttachmentId = upload.Id,
            RelatedEntityId = renderOpts.RelatedEntityId,
            RelatedEntityType = renderOpts.RelatedEntityType,
        };

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        ctx.Set<TenE0Certificate>().Add(entity);
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // 证书编号唯一索引兜底（Sequence 并发冲突或手动传入重复编号）。
            logger?.LogError(ex,
                "证书编号唯一冲突：CertificateNo={No} TemplateCode={Template}", certificateNo, templateCode);
            throw new InvalidOperationException(
                $"证书编号 '{certificateNo}' 已存在（唯一冲突）。若用 Sequence 自动生成，请重试；" +
                "若手动传入请改编号。", ex);
        }

        logger?.LogInformation(
            "证书已生成：Id={Id} No={No} Template={Template} FileAttachment={FileId}",
            entity.Id, certificateNo, templateCode, upload.Id);

        return entity;
    }

    /// <inheritdoc />
    public async Task<Stream> RenderToStreamAsync(
        string templateCode,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentNullException.ThrowIfNull(data);

        var (definition, _) = await LoadTemplateAsync(templateCode, ct);
        // 预览路径无 certificateNo —— 二维码 {certificateNo} 占位符留空（DataBinder 不校验含占位符的 URL）。
        return await renderer.RenderAsync(definition, data, ct);
    }

    /// <inheritdoc />
    public async Task<List<TenE0Certificate>> GetByRelatedEntityAsync(
        string relatedEntityType, string relatedEntityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relatedEntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(relatedEntityId);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        // 软删除 / 租户 Named Query Filter 由 BaseDataContext 自动附加（见 docs/20）。
        return await ctx.Set<TenE0Certificate>()
            .AsNoTracking()
            .Where(c => c.RelatedEntityType == relatedEntityType && c.RelatedEntityId == relatedEntityId)
            .OrderByDescending(c => c.CreateTime)
            .ToListAsync(ct);
    }

    private async Task<(CertificateDefinition Definition, TenE0CertificateTemplate Template)> LoadTemplateAsync(
        string templateCode, CancellationToken ct)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        // 软删除 / 租户 Named Query Filter 自动附加。
        var template = await ctx.Set<TenE0CertificateTemplate>()
            .FirstOrDefaultAsync(t => t.Code == templateCode, ct);
        if (template is null)
            throw new InvalidOperationException($"证书模板不存在：Code='{templateCode}'。");
        if (!template.IsEnabled)
            throw new InvalidOperationException($"证书模板已禁用：Code='{templateCode}'。");

        CertificateDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<CertificateDefinition>(template.TemplateJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"证书模板 DSL 反序列化失败：Code='{templateCode}'。TemplateJson 不是合法的 CertificateDefinition。", ex);
        }
        if (definition is null)
            throw new InvalidOperationException($"证书模板 DSL 为空：Code='{templateCode}'。");

        return (definition, template);
    }

    /// <summary>
    /// 把 certificateNo 注入 data 副本，并替换 QrCodeElement 的 {certificateNo} 占位符。
    /// 返回新字典（不修改入参，保持幂等）。
    /// </summary>
    private static IReadOnlyDictionary<string, object?> BindWithCertificateNo(
        IReadOnlyDictionary<string, object?> data, string certificateNo)
    {
        // 副本 + 补 certificateNo key（供 DataBinder 用 {certificateNo} 时查询）。
        var copy = new Dictionary<string, object?>(data)
        {
            ["certificateNo"] = certificateNo,
        };
        return copy;
    }

    /// <summary>
    /// 粗判唯一约束冲突（跨 provider：SQL Server 2627/2601，Postgres 23505，SQLite 19/2067）。
    /// 精确判断由 EF Core 抛出后业务层处理；此处只做兜底友好提示。
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;
}
