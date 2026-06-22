using Microsoft.Extensions.Options;
using TenE0.Core.Files.Storage;

namespace TenE0.Core.Tests.Files.Storage;

/// <summary>
/// 安全回归测试：LocalFileStorage 必须拒绝所有逃出 BasePath 沙箱的 storagePath。
/// 来源：issue #91 [P0][Security] LocalFileStorage 路径穿越漏洞。
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalFileStoragePathTraversalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalFileStorage _sut;

    public LocalFileStoragePathTraversalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"10e0-trav-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new LocalStorageOptions { BasePath = _tempDir, BaseUrl = "http://localhost/uploads" });
        _sut = new LocalFileStorage(TimeProvider.System, options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    public static IEnumerable<object[]> MaliciousPaths() => new[]
    {
        new object[] { "../escaped.txt" },
        new object[] { "../../etc/passwd" },
        new object[] { "subdir/../../escaped.txt" },
        new object[] { "/etc/passwd" },
        new object[] { "subdir/../../../escaped.txt" },
    };

    [Theory]
    [MemberData(nameof(MaliciousPaths))]
    public async Task RetrieveAsync_TraversalAttempt_ReturnsNull(string maliciousPath)
    {
        // Act
        var result = await _sut.RetrieveAsync(maliciousPath);

        // Assert：路径穿越必须被拒绝，返回 null，不抛异常
        result.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(MaliciousPaths))]
    public async Task DeleteAsync_TraversalAttempt_ReturnsFalseWithoutDeleting(string maliciousPath)
    {
        // Arrange：在 BasePath 外放置一个诱饵文件，验证攻击失败
        var parentDir = Directory.GetParent(_tempDir)!.FullName;
        var decoyPath = Path.Combine(parentDir, $"decoy-{Guid.NewGuid()}.txt");
        await File.WriteAllTextAsync(decoyPath, "must survive");

        try
        {
            // Act：用 maliciousPath 试图越界删除（即便诱饵不存在，攻击也不应有任何效果）
            var result = await _sut.DeleteAsync(maliciousPath);

            // Assert
            result.Should().BeFalse();
            File.Exists(decoyPath).Should().BeTrue("攻击者不得删除 BasePath 外的文件");
        }
        finally
        {
            if (File.Exists(decoyPath))
                File.Delete(decoyPath);
        }
    }

    [Theory]
    [MemberData(nameof(MaliciousPaths))]
    public async Task ExistsAsync_TraversalAttempt_ReturnsFalse(string maliciousPath)
    {
        // Act
        var result = await _sut.ExistsAsync(maliciousPath);

        // Assert：禁止探测 BasePath 外的文件存在性
        result.Should().BeFalse();
    }

    [Fact(Skip = "已知限制：路径前缀校验无法识别 symlink 穿越（symlink 本身是 BasePath 内合法文件，OS 在 open() 时才 dereference）；issue #91 followup")]
    public async Task RetrieveAsync_SymlinkEscapingBasePath_ReturnsNull()
    {
        // 占位：完整防御需在文件打开前调用 RealPath/lstat 比对，.NET 暂无跨平台 O_NOFOLLOW API。
        // 运营建议：BasePath 所在目录挂载时使用 `nosymfollow`（Linux），或在 BasePath 内定期
        // `find -type l -not -links 1` 扫描告警。
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ValidPathInsideBasePath_StillWorks()
    {
        // Arrange：通过正常路径写入
        using var stream = new MemoryStream("safe content"u8.ToArray());
        var storeResult = await _sut.StoreAsync(stream, "ok.txt", "text/plain");

        // Act & Assert：合法路径不受影响
        var retrieved = await _sut.RetrieveAsync(storeResult.StoragePath);
        retrieved.Should().NotBeNull();

        (await _sut.ExistsAsync(storeResult.StoragePath)).Should().BeTrue();
        (await _sut.DeleteAsync(storeResult.StoragePath)).Should().BeTrue();
    }
}
