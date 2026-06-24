using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace TenE0.Api.Tests;

/// <summary>
/// 验收测试（issue #154）：导入导出端点的端到端行为。
///
/// <para>覆盖：</para>
/// <list type="bullet">
/// <item><c>GET /demo/export</c> 返回 xlsx 且 Content-Type 正确。</item>
/// <item><c>GET /demo/export-csv</c> 返回 text/csv。</item>
/// <item><c>GET /demo/import-template</c> 返回可加载的 xlsx。</item>
/// <item><c>POST /demo/import</c> 含错误行时返回 <c>ImportResult</c> 形状（Total/Success/Failed/Errors）。</item>
/// </list>
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
public sealed class ImportExportEndpointsAcceptanceTests
{
    [Fact]
    public async Task GivenSeededDemos_WhenGettingExport_ThenReturnsXlsxWithCorrectContentType()
    {
        using var factory = new ImportExportFactory();
        await factory.SeedDemoAsync("EXP-1", "导出行1");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/demo/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var ms = new MemoryStream(bytes);
        var act = () => new XLWorkbook(ms);
        act.Should().NotThrow("导出必须是合法 xlsx");
    }

    [Fact]
    public async Task GivenSeededDemos_WhenGettingExportCsv_ThenReturnsTextCsv()
    {
        using var factory = new ImportExportFactory();
        await factory.SeedDemoAsync("CSV-1", "CSV行");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/demo/export-csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("编码");
        content.Should().Contain("CSV行");
    }

    [Fact]
    public async Task GivenNoData_WhenGettingImportTemplate_ThenReturnsLoadableXlsx()
    {
        using var factory = new ImportExportFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/demo/import-template");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheets.First();

        // 模板表头应含 DemoEntity 声明的列
        var headers = Enumerable.Range(1, ws.LastColumnUsed()!.ColumnNumber())
            .Select(i => ws.Cell(1, i).GetValue<string>())
            .ToList();
        headers.Should().Contain("编码");
        headers.Should().Contain("名称");
    }

    [Fact]
    public async Task GivenXlsxWithInvalidRow_WhenPostingImport_ThenReturnsImportResultShape()
    {
        using var factory = new ImportExportFactory();
        var client = factory.CreateClient();

        // 构造一个 xlsx：表头 + 2 行（第 1 行有效，第 2 行缺必填"名称"）
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        ws.Cell(1, 1).Value = "编码";
        ws.Cell(1, 2).Value = "名称";
        ws.Cell(2, 1).Value = "IMP-1";
        ws.Cell(2, 2).Value = "有效行";
        ws.Cell(3, 1).Value = "IMP-2";
        ws.Cell(3, 2).Value = "";          // 名称必填 → 该行失败

        using var contentStream = new MemoryStream();
        wb.SaveAs(contentStream);
        contentStream.Position = 0;

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(contentStream.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(fileContent, "file", "import.xlsx");

        var response = await client.PostAsync("/demo/import", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ImportResultEnvelope>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Total.Should().Be(2);
        result.Data.Success.Should().Be(1);
        result.Data.Failed.Should().Be(1);
        result.Data.Errors.Should().HaveCount(1);
    }

    [Fact]
    public async Task GivenNoFile_WhenPostingImport_ThenReturns400()
    {
        using var factory = new ImportExportFactory();
        var client = factory.CreateClient();

        // 发一个合法 multipart 但不含文件部分 → form.Files 为空 → 端点返回 BadRequest
        using var form = new MultipartFormDataContent
        {
            { new StringContent("标记"), "marker" },
        };
        var response = await client.PostAsync("/demo/import", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Wire DTOs ──────────────────────────────────────────────

    private sealed record ImportResultEnvelope(bool Success, ImportResultData? Data, string? ErrorMessage);
    private sealed record ImportResultData(int Total, int Success, int Failed, List<RowErrorEnvelope> Errors, bool TransactionRolledBack);
    private sealed record RowErrorEnvelope(int RowNumber, List<string> Errors);

    // ── Factory ────────────────────────────────────────────────

    /// <summary>导入导出端点专用隔离 host（镜像 AdminAuditLogQueryAcceptanceTests.IsolatedFactory）。</summary>
    public sealed class ImportExportFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"ie154-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<DemoDbContext>))
                    .ToList();
                foreach (var d in existing) services.Remove(d);

                services.AddDbContextFactory<DemoDbContext>(opt =>
                    opt.UseInMemoryDatabase(_dbName)
                       .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            });
        }

        public async Task SeedDemoAsync(string code, string name)
        {
            using var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
            await using var ctx = await factory.CreateDbContextAsync();
            ctx.Demos.Add(new DemoEntity { Id = Guid.NewGuid().ToString("N"), Code = code, Name = name });
            await ctx.SaveChangesAsync();
        }
    }
}
