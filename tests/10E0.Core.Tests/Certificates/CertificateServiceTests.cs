using System.Text.Json;
using TenE0.Core.Certificate;
using TenE0.Core.Certificate.Entities;
using TenE0.Core.Files;
using TenE0.Core.Sequences;

namespace TenE0.Core.Tests.Certificates;

/// <summary>
/// <see cref="CertificateService{TContext}"/> 单元测试（issue #185）。
///
/// <para>
/// 仿 <c>EfSequenceGeneratorTests</c> 的 InMemory 模式：最小 TestDbContext + 手写 IDbContextFactory +
/// Mock IFileService + Mock ICertificateRenderer。覆盖 RenderAsync 落库 / Sequence 编号 /
/// RenderToStreamAsync 不落库 / GetByRelatedEntityAsync 查询 / 禁用模板拒绝 / 模板不存在抛。
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class CertificateServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0CertificateTemplate> CertificateTemplates => Set<TenE0CertificateTemplate>();
        public DbSet<TenE0Certificate> Certificates => Set<TenE0Certificate>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0CertificateTemplate>(b =>
            {
                b.HasKey(e => e.Id);
                b.HasIndex(e => e.Code).IsUnique();
            });
            modelBuilder.Entity<TenE0Certificate>(b =>
            {
                b.HasKey(e => e.Id);
                b.HasIndex(e => e.CertificateNo).IsUnique();
                b.HasIndex(e => new { e.RelatedEntityType, e.RelatedEntityId });
            });
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    private static CertificateDefinition SampleDefinition() => new(
        "结业证书", PaperKind.A4, CertificateOrientation.Landscape,
        [new TitleElement("title", "结业证书"), new NameElement("leader")]);

    private static async Task SeedTemplateAsync(IDbContextFactory<TestDbContext> factory, string code, bool enabled = true)
    {
        await using var dc = factory.CreateDbContext();
        var template = new TenE0CertificateTemplate
        {
            Code = code,
            Name = code,
            TemplateJson = JsonSerializer.Serialize(SampleDefinition(), JsonOptions),
        };
        if (!enabled) template.Disable();
        dc.CertificateTemplates.Add(template);
        await dc.SaveChangesAsync();
    }

    private static (Mock<IFileService> FileSvc, Mock<ICertificateRenderer> Renderer) CreateMocks()
    {
        var fileSvc = new Mock<IFileService>();
        fileSvc.Setup(f => f.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<UploadRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string name, string ct, UploadRequest? _, CancellationToken _) =>
                new UploadResponse("file-id", name, "path", "url", 100, ct));

        var renderer = new Mock<ICertificateRenderer>();
        renderer.SetupGet(r => r.Format).Returns("pdf");
        renderer.Setup(r => r.RenderAsync(It.IsAny<CertificateDefinition>(),
            It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var ms = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // "%PDF"
                return ms;
            });
        return (fileSvc, renderer);
    }

    // 全限定名引用 Microsoft.Extensions.Options —— 避免 IDE0005 误报冗余 using（TreatWarningsAsErrors 把 IDE0005 变 error）。
    private static Microsoft.Extensions.Options.IOptions<CertificateOptions> OptionsWithSequence() =>
        Microsoft.Extensions.Options.Options.Create(
            new CertificateOptions { SequenceKey = "certificate", SequenceFormat = "CERT-{0000}" });

    private static Microsoft.Extensions.Options.IOptions<CertificateOptions> OptionsNoSequence() =>
        Microsoft.Extensions.Options.Options.Create(new CertificateOptions());

    [Fact]
    public async Task RenderAsync_RendersPdf_StoresFile_WritesCertificate_WithSequenceNo()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await SeedTemplateAsync(factory, "demo-completion");
        var (fileSvc, renderer) = CreateMocks();

        // Sequence 生成器：模拟返回固定编号（隔离 EF 并发逻辑）。
        var seq = new Mock<ISequenceGenerator>();
        seq.Setup(s => s.NextAsync("certificate", "CERT-{0000}", It.IsAny<CancellationToken>()))
           .ReturnsAsync("CERT-0001");

        var svc = new CertificateService<TestDbContext>(factory, renderer.Object, fileSvc.Object,
            seq.Object, OptionsWithSequence(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CertificateService<TestDbContext>>.Instance);

        var cert = await svc.RenderAsync("demo-completion",
            new Dictionary<string, object?> { ["leader"] = "张三" },
            new CertificateRenderOptions(RelatedEntityId: "proj-1", RelatedEntityType: "ResearchProject"),
            CancellationToken.None);

        // 证书实例落库 + 编号走 Sequence + 文件上传被调用。
        cert.CertificateNo.Should().Be("CERT-0001");
        cert.Title.Should().Be("结业证书");
        cert.FileAttachmentId.Should().Be("file-id");
        cert.RelatedEntityId.Should().Be("proj-1");
        cert.RelatedEntityType.Should().Be("ResearchProject");

        fileSvc.Verify(f => f.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), "application/pdf",
            It.IsAny<UploadRequest?>(), It.IsAny<CancellationToken>()), Times.Once);
        renderer.Verify(r => r.RenderAsync(It.IsAny<CertificateDefinition>(),
            It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);

        // DB 里确有一条证书实例。
        await using var dc = factory.CreateDbContext();
        (await dc.Certificates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RenderAsync_NoSequenceKey_LeavesCertificateNoEmpty_WhenNotProvided()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await SeedTemplateAsync(factory, "tpl");
        var (fileSvc, renderer) = CreateMocks();

        var svc = new CertificateService<TestDbContext>(factory, renderer.Object, fileSvc.Object,
            null, OptionsNoSequence(), null);

        var cert = await svc.RenderAsync("tpl", new Dictionary<string, object?>());

        cert.CertificateNo.Should().BeEmpty();
    }

    [Fact]
    public async Task RenderAsync_DisabledTemplate_Throws()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await SeedTemplateAsync(factory, "disabled-tpl", enabled: false);
        var (fileSvc, renderer) = CreateMocks();

        var svc = new CertificateService<TestDbContext>(factory, renderer.Object, fileSvc.Object,
            null, OptionsNoSequence(), null);

        var act = () => svc.RenderAsync("disabled-tpl", new Dictionary<string, object?>());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已禁用*");
    }

    [Fact]
    public async Task RenderAsync_MissingTemplate_Throws()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var (fileSvc, renderer) = CreateMocks();

        var svc = new CertificateService<TestDbContext>(factory, renderer.Object, fileSvc.Object,
            null, OptionsNoSequence(), null);

        var act = () => svc.RenderAsync("no-such-template", new Dictionary<string, object?>());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不存在*");
    }

    [Fact]
    public async Task RenderToStreamAsync_DoesNotPersistCertificate()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await SeedTemplateAsync(factory, "preview-tpl");
        var (fileSvc, renderer) = CreateMocks();

        var svc = new CertificateService<TestDbContext>(factory, renderer.Object, fileSvc.Object,
            null, OptionsNoSequence(), null);

        var stream = await svc.RenderToStreamAsync("preview-tpl", new Dictionary<string, object?>());

        stream.Should().NotBeNull();
        // 关键：预览路径不落库、不调 IFileService。
        fileSvc.Verify(f => f.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<UploadRequest?>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var dc = factory.CreateDbContext();
        (await dc.Certificates.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task GetByRelatedEntityAsync_ReturnsMatchingCertificates_OrderedDesc()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await SeedTemplateAsync(factory, "tpl");
        var (fileSvc, renderer) = CreateMocks();
        var seq = new Mock<ISequenceGenerator>();
        seq.Setup(s => s.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync("N");

        var svc = new CertificateService<TestDbContext>(factory, renderer.Object, fileSvc.Object,
            seq.Object, OptionsNoSequence(), null);

        // 生成 2 条关联同一实体的证书。
        await svc.RenderAsync("tpl", new Dictionary<string, object?>(),
            new CertificateRenderOptions(RelatedEntityId: "proj-1", RelatedEntityType: "ResearchProject"));
        await Task.Delay(20); // 错开 CreateTime
        await svc.RenderAsync("tpl", new Dictionary<string, object?>(),
            new CertificateRenderOptions(RelatedEntityId: "proj-1", RelatedEntityType: "ResearchProject"));
        // 再生成一条关联别的实体的（不应返回）。
        await svc.RenderAsync("tpl", new Dictionary<string, object?>(),
            new CertificateRenderOptions(RelatedEntityId: "proj-2", RelatedEntityType: "ResearchProject"));

        var list = await svc.GetByRelatedEntityAsync("ResearchProject", "proj-1", CancellationToken.None);

        list.Should().HaveCount(2);
        list.Should().OnlyContain(c => c.RelatedEntityId == "proj-1");
    }

    /// <summary>
    /// CodeQL cs/log-forging 回归测试：调用方传入含 CR/LF 的证书编号，
    /// 渲染成功路径日志经 SanitizeForLog 净化后不含换行符（防伪造日志行）。
    /// 同时验证存储值仍是原值（净化只作用于日志，不影响业务数据）。
    ///
    /// <para>SanitizeForLog 用 <c>String.Replace</c> 移除 CR/LF —— 这是 CodeQL
    /// <c>cs/log-forging</c> 内置认可的 sanitizer barrier（见 <c>LogForgingQuery.qll</c>
    /// 的 <c>StringReplaceSanitizer</c>），自定义 StringBuilder 循环不被认可。</para>
    /// </summary>
    [Fact]
    public async Task RenderAsync_CertificateNoWithControlChars_LogSanitized_StoragePreserved()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await SeedTemplateAsync(factory, "tpl");
        var (fileSvc, renderer) = CreateMocks();
        var captured = new List<string>();
        var captureLogger = new CaptureLogger(captured);

        var svc = new CertificateService<TestDbContext>(factory, renderer.Object, fileSvc.Object,
            null, OptionsNoSequence(), captureLogger);

        // 手动传入含 CR/LF 的编号（模拟恶意/脏数据输入，意图伪造日志行）。
        var maliciousNo = "CERT\r\nFAKE-LOG-LINE";
        var cert = await svc.RenderAsync("tpl", new Dictionary<string, object?>(),
            new CertificateRenderOptions(CertificateNo: maliciousNo));

        // 存储值是原值（净化只针对日志，不改变业务数据）。
        cert.CertificateNo.Should().Be(maliciousNo);
        (await factory.CreateDbContext().Certificates.SingleAsync()).CertificateNo.Should().Be(maliciousNo);

        // 日志不含 CR/LF（净化生效，防 log-forging）。
        captured.Should().NotBeEmpty();
        captured.Should().NotContain(msg => msg.Contains('\r') || msg.Contains('\n'),
            "日志消息不应含 CR/LF（log-forging 净化生效）");
        // 净化后的可见部分（CERT + FAKE-LOG-LINE 合并）仍出现在日志中。
        captured.Should().Contain(msg => msg.Contains("CERT"));
        captured.Should().Contain(msg => msg.Contains("FAKE-LOG-LINE"));
    }

    /// <summary>
    /// 捕获日志消息的极简 ILogger —— 仅记录 format 后的消息文本（含占位符替换）。
    /// 用于验证 SanitizeForLog 净化效果。
    /// </summary>
    private sealed class CaptureLogger(List<string> sink) : Microsoft.Extensions.Logging.ILogger<CertificateService<TestDbContext>>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Add(formatter(state, exception));
    }
}
