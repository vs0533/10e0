using System.Reflection;

namespace TenE0.Core.Tests.Abstractions;

/// <summary>
/// BDD acceptance tests for #37 — Part 2: <c>ErrorCodes</c> central registry.
///
/// 验证目标：
/// 1. 所有现有硬编码错误码字符串必须收敛到 ErrorCodes 常量
/// 2. 常量值与现有散落的字面量完全一致（避免破坏前端 i18n 映射）
/// 3. 常量集必须覆盖至少 Login / RefreshToken / EntityService / UniqueValidator 四类
/// 4. 错误码命名遵循 SCREAMING_SNAKE_CASE 约定（前端可枚举）
/// 5. 错误码全局唯一 — 没有重复定义
///
/// 失败模式：未实现前，业务方 grep "AUTH_INVALID" 等字符串定位错误源，需扫描 4 个文件；
/// 实现后，一个 ErrorCodes.AuthInvalid 即可定位。
///
/// 设计说明：本测试文件用反射访问 <c>ErrorCodes</c> 类型，
/// 而不是直接 <c>using</c> + <c>ErrorCodes.AuthInvalid</c> 静态调用 ——
/// 这样测试能编译通过，但在 #37 未实现时（ErrorCodes 类型不存在）运行时失败。
/// </summary>
[Trait("Category", "BDD")]
public sealed class ErrorCodesAcceptanceTests
{
    private static readonly Assembly CoreAssembly = typeof(TenE0.Core.Abstractions.IErrs).Assembly;

    private static Type? TryGetErrorCodesType() =>
        CoreAssembly.GetType("TenE0.Core.Abstractions.ErrorCodes", throwOnError: false);

    private static string? TryGetStringConstant(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
        if (field is null) return null;
        if (field.FieldType != typeof(string)) return null;
        if (!(field.IsLiteral || field.IsInitOnly)) return null;
        return field.GetRawConstantValue() as string;
    }

    private static IEnumerable<string> EnumerateStringConstants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    // ── ErrorCodes 类型必须存在 ──────────────────────────────

    [Fact]
    public void GivenCentralRegistry_WhenLoaded_ThenErrorCodesTypeExists()
    {
        // Arrange + Act
        var type = TryGetErrorCodesType();

        // Assert — #37 任务产出：必须新增 TenE0.Core.Abstractions.ErrorCodes 类
        type.Should().NotBeNull(
            "the #37 refactor must introduce a central TenE0.Core.Abstractions.ErrorCodes registry class");
    }

    // ── 认证错误码 ──────────────────────────────────────────

    [Fact]
    public void GivenErrorCodesRegistry_WhenReadingAuthInvalid_ThenValueMatchesLegacyLiteral()
    {
        // Arrange
        var type = TryGetErrorCodesType();
        type.Should().NotBeNull();
        var value = TryGetStringConstant(type!, "AuthInvalid");

        // Assert
        value.Should().NotBeNull("ErrorCodes.AuthInvalid must exist as a public const string field");
        value.Should().Be("AUTH_INVALID",
            "LoginCommandHandler.cs:35 hard-codes 'AUTH_INVALID' — central registry must keep the exact value");
    }

    [Fact]
    public void GivenErrorCodesRegistry_WhenReadingAuthDisabled_ThenValueMatchesLegacyLiteral()
    {
        var type = TryGetErrorCodesType()!;
        var value = TryGetStringConstant(type, "AuthDisabled");

        value.Should().NotBeNull();
        value.Should().Be("AUTH_DISABLED",
            "LoginCommandHandler.cs:41 / RefreshTokenCommandHandler.cs:86 hard-code 'AUTH_DISABLED'");
    }

    // ── Refresh token 错误码 ────────────────────────────────

    [Fact]
    public void GivenErrorCodesRegistry_WhenReadingTokenInvalid_ThenValueMatchesLegacyLiteral()
    {
        var type = TryGetErrorCodesType()!;
        var value = TryGetStringConstant(type, "TokenInvalid");

        value.Should().NotBeNull();
        value.Should().Be("TOKEN_INVALID",
            "RefreshTokenCommandHandler.cs:52 hard-codes 'TOKEN_INVALID'");
    }

    [Fact]
    public void GivenErrorCodesRegistry_WhenReadingTokenExpired_ThenValueMatchesLegacyLiteral()
    {
        var type = TryGetErrorCodesType()!;
        var value = TryGetStringConstant(type, "TokenExpired");

        value.Should().NotBeNull();
        value.Should().Be("TOKEN_EXPIRED",
            "RefreshTokenCommandHandler.cs:79 hard-codes 'TOKEN_EXPIRED'");
    }

    [Fact]
    public void GivenErrorCodesRegistry_WhenReadingTokenRevoked_ThenValueMatchesLegacyLiteral()
    {
        var type = TryGetErrorCodesType()!;
        var value = TryGetStringConstant(type, "TokenRevoked");

        value.Should().NotBeNull();
        value.Should().Be("TOKEN_REVOKED",
            "RefreshTokenCommandHandler.cs:73 hard-codes 'TOKEN_REVOKED'");
    }

    // ── EntityService 错误码 ────────────────────────────────

    [Fact]
    public void GivenErrorCodesRegistry_WhenReadingNotFound_ThenValueMatchesLegacyLiteral()
    {
        var type = TryGetErrorCodesType()!;
        var value = TryGetStringConstant(type, "NotFound");

        value.Should().NotBeNull();
        value.Should().Be("NOT_FOUND",
            "EntityService.cs:83,125 hard-codes 'NOT_FOUND'");
    }

    [Fact]
    public void GivenErrorCodesRegistry_WhenReadingFieldPerm_ThenValueMatchesLegacyLiteral()
    {
        var type = TryGetErrorCodesType()!;
        var value = TryGetStringConstant(type, "FieldPermission");

        value.Should().NotBeNull();
        value.Should().Be("FIELD_PERM",
            "EntityService.cs:189 hard-codes 'FIELD_PERM'");
    }

    // ── 唯一性校验错误码 ───────────────────────────────────

    [Fact]
    public void GivenErrorCodesRegistry_WhenReadingUnique_ThenValueMatchesLegacyLiteral()
    {
        var type = TryGetErrorCodesType()!;
        var value = TryGetStringConstant(type, "Unique");

        value.Should().NotBeNull();
        value.Should().Be("UNIQUE",
            "UniqueValidators.cs:39 hard-codes 'UNIQUE' (FieldUniqueValidator)");
    }

    [Fact]
    public void GivenErrorCodesRegistry_WhenReadingUniqueGroup_ThenValueMatchesLegacyLiteral()
    {
        var type = TryGetErrorCodesType()!;
        var value = TryGetStringConstant(type, "UniqueGroup");

        value.Should().NotBeNull();
        value.Should().Be("UNIQUE_GROUP",
            "UniqueValidators.cs:115 hard-codes 'UNIQUE_GROUP' (GroupUniqueValidator)");
    }

    // ── 命名约定：所有错误码必须遵循 SCREAMING_SNAKE_CASE ──

    [Theory]
    [InlineData("AuthInvalid")]
    [InlineData("AuthDisabled")]
    [InlineData("TokenInvalid")]
    [InlineData("TokenExpired")]
    [InlineData("TokenRevoked")]
    [InlineData("NotFound")]
    [InlineData("FieldPermission")]
    [InlineData("Unique")]
    [InlineData("UniqueGroup")]
    public void GivenErrorCodeConstant_WhenReflected_ThenValueIsScreamingSnakeCase(string propertyName)
    {
        // Arrange
        var type = TryGetErrorCodesType();
        type.Should().NotBeNull();
        var value = TryGetStringConstant(type!, propertyName);

        // Assert
        value.Should().NotBeNull($"ErrorCodes must expose a {propertyName} constant");
        value.Should().Match(v => v == v!.ToUpperInvariant(),
            "error codes must be UPPER_SNAKE_CASE so front-end i18n tables can enumerate them");
        value!.Replace("_", "").All(char.IsLetterOrDigit).Should().BeTrue(
            "error codes must only contain letters, digits and underscores");
    }

    // ── 全局唯一性 ─────────────────────────────────────────

    [Fact]
    public void GivenErrorCodesRegistry_WhenEnumeratingAllConstants_ThenValuesAreGloballyUnique()
    {
        // Arrange
        var type = TryGetErrorCodesType();
        type.Should().NotBeNull();
        var values = EnumerateStringConstants(type!).ToList();

        // Assert
        values.Should().NotBeEmpty("ErrorCodes must define at least the legacy codes");
        values.Should().OnlyHaveUniqueItems(
            "two distinct ErrorCodes members resolving to the same string value would corrupt i18n lookups");
    }

    // ── 现有字面量必须全部命中（防止遗漏） ────────────────

    [Theory]
    [InlineData("AUTH_INVALID")]
    [InlineData("AUTH_DISABLED")]
    [InlineData("TOKEN_INVALID")]
    [InlineData("TOKEN_EXPIRED")]
    [InlineData("TOKEN_REVOKED")]
    [InlineData("NOT_FOUND")]
    [InlineData("FIELD_PERM")]
    [InlineData("UNIQUE")]
    [InlineData("UNIQUE_GROUP")]
    public void GivenLegacyErrorCodeLiteral_WhenLookedUp_ThenExistsInRegistry(string legacyCode)
    {
        // Arrange
        var type = TryGetErrorCodesType();
        type.Should().NotBeNull();
        var allValues = EnumerateStringConstants(type!).ToHashSet();

        // Assert
        allValues.Should().Contain(legacyCode,
            $"legacy code '{legacyCode}' must be present in the central registry — otherwise the refactor regresses one of the call sites");
    }
}
