using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.Certificate;
using TenE0.Core.Certificate.Entities;
using TenE0.Core.Hosting;

namespace TenE0.Api.Seeders;

/// <summary>
/// #185 启动时初始化证书 demo 模板（结业证书版式）。
/// Order 500：在权限/账号/菜单/配置 seeder 之后。
///
/// <para>
/// 幂等：模板已存在则跳过。模板 Code=<c>demo-completion</c>，含标题 / 项目编号 / 项目名称 /
/// 负责人 / 依托单位 / 签发日期 / 验真二维码 / 盖章位 / 签字位 全套元素，覆盖 R3 结业证书场景。
/// </para>
/// </summary>
internal sealed class CertificateSeeder(IDbContextFactory<DemoDbContext> dcFactory) : IDataSeeder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int Order => 500;

    public async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);

        // 幂等检查用 IgnoreQueryFilters —— 模板是 IMultiTenantEntity，seed 阶段在 root provider 跑，
        // 解析 ITenantContext (Scoped) 会抛 "Cannot resolve scoped service from root provider"。
        // 仿 StaticJobRegistrar 的系统级注册模式：seed 是全局/组织级共享，绕过 Tenant/SoftDelete 过滤器。
        if (await dc.CertificateTemplates
            .IgnoreQueryFilters()
            .AnyAsync(ct => ct.Code == "demo-completion", cancellationToken))
            return;

        var definition = new CertificateDefinition(
            Title: "结业证书",
            PaperKind: PaperKind.A4,
            Orientation: CertificateOrientation.Landscape,
            Elements:
            [
                new TitleElement("title", "结业证书"),
                new TextElement("projectNo", "项目编号"),
                new TextElement("projectName", "项目名称"),
                new NameElement("leader"),
                new TextElement("unit", "依托单位"),
                new DateElement("issueDate"),
                new QrCodeElement("qr", "https://verify.example.com/{certificateNo}"),
                new SealElement("seal", "签发单位"),
                new SignatureElement("signature")
            ]);

        dc.CertificateTemplates.Add(new TenE0CertificateTemplate
        {
            Code = "demo-completion",
            Name = "结业证书（demo 模板）",
            TemplateJson = JsonSerializer.Serialize(definition, JsonOptions),
            // IsEnabled 默认 true（实体构造期已设），无需显式赋值。
        });

        await dc.SaveChangesAsync(cancellationToken);
    }
}
