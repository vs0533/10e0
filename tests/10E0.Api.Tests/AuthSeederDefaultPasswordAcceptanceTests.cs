using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TenE0.Api.Tests;

/// <summary>
/// BDD acceptance tests for issue #126 — <c>AuthSeeder</c> hardcodes the default
/// admin / alice password as the literal string <c>"111111"</c>. Anyone who
/// deploys the demo image inherits <c>admin / 111111</c> as a live credential.
/// The seeder must read the default password from configuration (e.g.
/// <c>Seed:DefaultPassword</c>) — and refuse to start when the value is missing
/// in non-development environments, mirroring the same fail-closed pattern as
/// <c>Jwt:SigningKey</c> in <c>Program.cs</c>.
///
/// Each scenario encodes a Given/When/Then business behavior. The tests are
/// intentionally RED today:
///   1. <c>AuthSeeder</c> still hardcodes <c>"111111"</c>.
///   2. There is no <c>Seed:DefaultPassword</c> configuration knob.
///   3. Starting the host without the configured default password still seeds
///      a working <c>admin / 111111</c> login — the exact leak #126 reports.
/// Once the fix lands (config-driven default + fail-closed validation), every
/// scenario below turns GREEN.
/// </summary>
[Trait("Category", "BDD")]
[Trait("Category", "Integration")]
// issue #193: WebApplicationFactory<Program> 在 host 启动抛异常时, factory 的 disposal
// 与异常传播存在 race —— 并行 / CI 资源紧张时, 真正的 fail-closed 异常会被掩盖成
// 无关的 ObjectDisposedException("IServiceProvider") / AggregateException。
// 把这个 class 单独放进一个禁用并行化的 collection, 消除与其它 WebApplicationFactory
// 测试的并行 race; 同时测试侧也做了 inner-exception 链解包 (见 UnwrapRootException)。
[Collection("AuthSeederFailClosed")]
public sealed class AuthSeederDefaultPasswordAcceptanceTests
{
    // ── 1) Hardcoded literal must disappear from the seeder source ──

    [Fact]
    public void GivenIssue126HardRule_WhenScanningAuthSeeder_ThenNoHardcodedDefaultPasswordLiteralExists()
    {
        // Arrange — locate AuthSeeder.cs from the repo root (mirrors AdminOutboxAuth test scan).
        var path = Path.Combine("src", "10E0.Api", "Seeders", "AuthSeeder.cs");
        var absolutePath = Path.Combine(FindRepoRoot(), path);
        File.Exists(absolutePath).Should().BeTrue(
            $"seeder source file `{path}` must exist for the scan");

        var content = File.ReadAllText(absolutePath);

        // Act — every occurrence of the leaked default password literal must be gone.
        // We check two flavors: the bare literal AND the literal wrapped in Hash(...)
        // / Verify(...) — both are the same bug.
        var containsBareLiteral = content.Contains("\"111111\"", StringComparison.Ordinal);
        var containsHashOfLiteral = Regex.IsMatch(
            content,
            @"\.(Hash|Verify)\(\s*""111111""\s*\)",
            RegexOptions.CultureInvariant);

        // Assert — source must no longer hardcode the demo credential.
        containsBareLiteral.Should().BeFalse(
            "AuthSeeder must not contain the literal \"111111\" — the default " +
            "password must come from configuration (Seed:DefaultPassword) or an " +
            "environment variable, never from a hardcoded string. " +
            "Issue #126: anyone who deploys the demo image inherits a live admin/111111 login.");
        containsHashOfLiteral.Should().BeFalse(
            "AuthSeeder must not call Hash(\"111111\") or Verify(\"111111\") — the " +
            "default password value must be read from configuration, not inlined as " +
            "a string literal at the call site.");
    }

    [Fact]
    public void GivenIssue126HardRule_WhenScanningAuthSeeder_ThenDefaultPasswordReadsFromConfiguration()
    {
        // Arrange — the fix introduces a Seed:DefaultPassword config section (or
        // equivalent). The seeder must resolve the value via IConfiguration, not
        // inline a constant. This test guards the *shape* of the fix: after the
        // fix, the source must mention the configuration key by name somewhere
        // observable (e.g. via GetValue / [FromKeyedServices] / options binding).
        var path = Path.Combine("src", "10E0.Api", "Seeders", "AuthSeeder.cs");
        var absolutePath = Path.Combine(FindRepoRoot(), path);
        File.Exists(absolutePath).Should().BeTrue();
        var content = File.ReadAllText(absolutePath);

        // Act — does the seeder reference the config key?
        var readsFromConfig =
            content.Contains("Seed:DefaultPassword", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Seed__DefaultPassword", StringComparison.OrdinalIgnoreCase)
            || content.Contains("DefaultPassword", StringComparison.Ordinal);

        // Assert
        readsFromConfig.Should().BeTrue(
            "after the #126 fix, AuthSeeder must read the default password from " +
            "configuration (e.g. Seed:DefaultPassword) instead of hardcoding " +
            "\"111111\". A missing config key in non-development environments " +
            "must fail-closed (InvalidOperationException) so a fresh prod deploy " +
            "can never come up with the published demo credential.");
    }

    // ── 2) dev environment: configured default password must actually work ──

    [Fact]
    public async Task GivenDevelopmentEnvironmentWithConfiguredDefaultPassword_WhenSeeding_ThenAdminCanLoginWithConfiguredPassword()
    {
        // Arrange — boot the host in Development with Seed:DefaultPassword set to a
        // non-default value (e.g. "rotate-me-now"). The seeder must hash THIS value,
        // not "111111", so admin can sign in only with the configured password.
        using var factory = new ConfigurableFactory(env: "Development", defaultPassword: "rotate-me-now");
        var client = factory.CreateClient();

        // Act
        var loginResp = await client.PostAsJsonAsync(
            "/auth/login", new { userCode = "admin", password = "rotate-me-now" });

        // Assert — login succeeds because the seeder honored the configuration.
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "after the #126 fix, the seeder must hash the Seed:DefaultPassword value " +
            "(not the historical literal \"111111\") so admin can sign in with " +
            "the configured password");
        var env = await loginResp.Content.ReadFromJsonAsync<LoginEnvelope>();
        env.Should().NotBeNull();
        env!.Success.Should().BeTrue();
        env.Data.Should().NotBeNull();
        env.Data!.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GivenDevelopmentEnvironmentWithConfiguredDefaultPassword_WhenLoggingInWithLegacyHardcodedPassword_ThenLoginIsRejected()
    {
        // Arrange — the historical demo password "111111" must NOT be a live
        // credential after the fix. Even in Development, an operator who only
        // read the old docs and tries the leaked password must be denied.
        using var factory = new ConfigurableFactory(env: "Development", defaultPassword: "rotate-me-now");
        var client = factory.CreateClient();

        // Act — attempt the historical leaked password.
        var loginResp = await client.PostAsJsonAsync(
            "/auth/login", new { userCode = "admin", password = "111111" });

        // Assert — login must fail. We accept any 4xx (401/400) since the exact
        // status depends on LoginCommand's mapping (auth vs validation), but it
        // must NOT be 200, and the success envelope must be false.
        loginResp.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "the historical literal \"111111\" must no longer be a valid admin " +
            "password after #126 is fixed — anyone deploying the demo image " +
            "should not inherit a live credential from the source code.");
        var raw = await loginResp.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var json = JsonDocument.Parse(raw).RootElement;
            if (json.TryGetProperty("success", out var success))
                success.GetBoolean().Should().BeFalse(
                    "the rejected login must report success=false in the ApiResult envelope");
        }
    }

    // ── 3) prod: missing Seed:DefaultPassword must fail-closed at startup ──

    [Fact]
    public async Task GivenProductionEnvironmentWithoutDefaultPasswordConfig_WhenStartingHost_ThenHostRefusesToBoot()
    {
        // Arrange — boot in Production with NO Seed:DefaultPassword supplied. The
        // fix must mirror Jwt:SigningKey's fail-closed pattern: refuse to start,
        // because booting with the historical literal would silently publish a
        // known credential. We model the expectation via the factory's
        // pre-startup hook: the IHost must surface an InvalidOperationException
        // (or any boot-time exception), NOT a successfully-running seeder.
        using var factory = new ConfigurableFactory(env: "Production", defaultPassword: null);

        // Act + Assert — building/starting the host must throw. We catch
        // broadly because the validator may use OptionsValidationException,
        // InvalidOperationException, or another startup-time guard.
        var act = async () =>
        {
            // CreateClient() forces the host to build + start (incl. seeders).
            // Without the config + with prod env, the validator must reject.
            var client = factory.CreateClient();
            await client.GetAsync("/");
        };

        var ex = await act.Should().ThrowAsync<Exception>(
            "the #126 fix must mirror the Jwt:SigningKey fail-closed pattern: a " +
            "Production environment without Seed:DefaultPassword must refuse to " +
            "start. Silently booting with the historical literal \"111111\" would " +
            "publish a known admin credential to the public internet.");

        // issue #193: WebApplicationFactory<Program> 在 host 启动抛异常时, factory 的
        // disposal 与异常传播存在 race —— CI 资源紧张时真正的 fail-closed 异常会被
        // 掩盖成 ObjectDisposedException("IServiceProvider") / AggregateException。
        // 我们关心的是「确有 fail-closed 异常」这一语义, 所以遍历 inner 链解包后再断言。
        var root = UnwrapRootException(ex.Which, "DefaultPassword");
        root.Message.Should().Contain(
            "DefaultPassword",
            "the fail-closed exception must name the Seed:DefaultPassword " +
            "configuration key so operators know exactly what to set, instead " +
            "of just bubbling up a generic 'something is wrong' message. " +
            "(issue #193: 容忍 ObjectDisposedException/AggregateException 包装, 解包到根因再断言)");
    }

    // ── 4) dev: missing config must still fail-closed (no silent "111111") ──

    [Fact]
    public async Task GivenDevelopmentEnvironmentWithoutDefaultPasswordConfig_WhenStartingHost_ThenHostRefusesToBoot()
    {
        // Arrange — even in Development, a missing Seed:DefaultPassword must
        // refuse to start. The historical behavior (silently using "111111") is
        // exactly the leak #126 reports; we cannot allow it in any environment.
        using var factory = new ConfigurableFactory(env: "Development", defaultPassword: null);

        var act = async () =>
        {
            var client = factory.CreateClient();
            await client.GetAsync("/");
        };

        var ex = await act.Should().ThrowAsync<Exception>(
            "the #126 fix must reject startup whenever Seed:DefaultPassword is " +
            "missing, regardless of environment — falling back to the historical " +
            "literal \"111111\" would defeat the entire point of the fix.");

        // 强化断言:异常必须明确提到 "DefaultPassword" 配置键,避免被其他无关的
        // 启动异常(如 DI 验证失败)误命中 —— 那不是 #126 期望的 fail-closed 语义。
        // issue #193: 容忍 disposal race 下的 ObjectDisposedException/AggregateException
        // 包装,遍历 inner 链解包到根因后再断言。
        var root = UnwrapRootException(ex.Which, "DefaultPassword");
        root.Message.Should().Contain(
            "DefaultPassword",
            "the fail-closed exception must name the Seed:DefaultPassword config " +
            "key so operators see exactly what to set, instead of an unrelated DI/IO " +
            "error masking the real cause");
    }

    // ── Helpers ────────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "10e0.slnx")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("must be able to locate the repository root via 10e0.slnx");
        return dir!.FullName;
    }

    /// <summary>
    /// Per-test isolated host that allows callers to choose:
    ///   * <c>env</c> — Development or Production (controls fail-closed gating).
    ///   * <c>defaultPassword</c> — value injected as <c>Seed:DefaultPassword</c>;
    ///     pass <c>null</c> to simulate "operator forgot to configure it".
    /// Each instance gets a unique InMemory database so seeders run clean.
    /// </summary>
    private sealed class ConfigurableFactory : WebApplicationFactory<Program>
    {
        private readonly string _env;
        private readonly string? _defaultPassword;
        private readonly string _dbName = $"issue126-{Guid.NewGuid():N}";

        public ConfigurableFactory(string env, string? defaultPassword)
        {
            _env = env;
            _defaultPassword = defaultPassword;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_env);

            // Inject Seed:DefaultPassword into configuration BEFORE the host
            // builds, so the seeder's IConfiguration.GetValue<...>("Seed:DefaultPassword")
            // sees our value (or sees it missing if _defaultPassword is null).
            // 必须显式清空 appsettings.json 的 dev 默认值，否则 "缺配置" 路径无法被触发。
            builder.ConfigureAppConfiguration((_, config) =>
            {
                // 不管 _defaultPassword 是否有值，都先清除 Seed 节点，避免 appsettings.json
                // 的 dev-only 默认值掩盖 "operator 忘了配" 的 fail-closed 场景。
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Seed:DefaultPassword"] = null!,
                });
                if (_defaultPassword is not null)
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Seed:DefaultPassword"] = _defaultPassword,
                    });
                }
            });

            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<DemoDbContext>))
                    .ToList();
                foreach (var d in existing) services.Remove(d);

                var dbName = _dbName;
                services.AddDbContextFactory<DemoDbContext>(opt =>
                    opt.UseInMemoryDatabase(dbName)
                       .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            });
        }
    }

    // ── Exception unwrapping ───────────────────────────────────

    /// <summary>
    /// 遍历异常链(自身 + InnerException + AggregateException.InnerExceptions),
    /// 返回第一个 message 含 <paramref name="keyword"/> 的异常; 找不到则返回 <paramref name="root"/>。
    ///
    /// issue #193: WebApplicationFactory<Program> 在 host 启动抛异常时, factory 的 disposal
    /// 与异常传播存在 race —— CI 资源紧张时, 真正的 fail-closed 异常 (message 含 "DefaultPassword")
    /// 会被掩盖成无关的 ObjectDisposedException("IServiceProvider") / AggregateException。
    /// 测试想断言的是「确有 fail-closed 异常被抛出」这一语义, 而不是「最外层异常的 message 长什么样」,
    /// 所以这里做一层容忍包装的解包。
    /// </summary>
    private static Exception UnwrapRootException(Exception root, string keyword)
    {
        var seen = new HashSet<Exception>();
        var queue = new Queue<Exception>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;   // 防自循环

            if (current.Message.Contains(keyword, StringComparison.Ordinal))
                return current;

            if (current.InnerException is not null)
                queue.Enqueue(current.InnerException);

            if (current is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                    queue.Enqueue(inner);
            }
        }

        return root;   // 未命中关键词, 返回原异常让调用方按原断言失败、暴露真实信息
    }

    // ── Wire DTOs ──────────────────────────────────────────────

    private sealed record AuthResponseDto(string AccessToken);

    private sealed record LoginEnvelope(bool Success, AuthResponseDto? Data);
}

/// <summary>
/// issue #193: 单独的 collection + <c>DisableParallelization</c>, 让
/// <see cref="AuthSeederDefaultPasswordAcceptanceTests"/> 不与其它
/// <c>WebApplicationFactory</c> 测试并行启动 host —— 消除 disposal race。
/// </summary>
[CollectionDefinition("AuthSeederFailClosed", DisableParallelization = true)]
public sealed class AuthSeederFailClosedCollection;
