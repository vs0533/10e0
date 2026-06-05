using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Behaviors;
using TenE0.Core.Permissions.DataFilter;
using TenE0.Core.Permissions.Management;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.Tests.Permissions;

[Trait("Category", "Unit")]
public sealed class PermissionsExtensionsTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
    }

    private sealed class FakePermissionProvider : IPermissionProvider
    {
        public IEnumerable<PermissionDefinition> Define() =>
        [
            new PermissionDefinition("demo.view", "Demo View", "demo"),
            new PermissionDefinition("demo.create", "Demo Create", "demo"),
        ];
    }

    private sealed class OtherFakePermissionProvider : IPermissionProvider
    {
        public IEnumerable<PermissionDefinition> Define() =>
        [
            new PermissionDefinition("user.view", "User View", "user"),
        ];
    }

    private sealed class FakeEntityFilterContributor : IEntityFilterContributor
    {
        public Type EntityType => typeof(TestDbContext);
        public LambdaExpression? BuildFilter(TenE0.Core.DataContext.BaseDataContext context) => null;
    }

    private sealed class SecondFakeFilterContributor : IEntityFilterContributor
    {
        public Type EntityType => typeof(TestDbContext);
        public LambdaExpression? BuildFilter(TenE0.Core.DataContext.BaseDataContext context) => null;
    }

    private sealed class StubCurrentUserContext : ICurrentUserContext
    {
        public bool IsAuthenticated => false;
        public string? UserCode => null;
        public UserType UserType => UserType.Person;
        public IReadOnlyList<string> RoleIds => [];
        public ValueTask<ICurrentUserInfo?> GetUserInfoAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ICurrentUserInfo?>(null);
    }

    private sealed class StubPermissionStore : IPermissionStore
    {
        public Task<IReadOnlySet<string>> GetGrantedPermissionsAsync(
            IReadOnlyCollection<string> roleIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    // ────────────────────────────────────────────────────────────────────
    // AddTenE0Permissions
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddTenE0Permissions_RegistersEvaluatorCacheCatalog()
    {
        var services = new ServiceCollection();
        services.AddTenE0Permissions();

        services.Should().Contain(s => s.ServiceType == typeof(IPermissionEvaluator));
        services.Should().Contain(s => s.ServiceType == typeof(IPermissionCache));
        services.Should().Contain(s => s.ServiceType == typeof(PermissionCatalog));
    }

    [Fact]
    public void AddTenE0Permissions_ReplacesDefaultDataAccessPolicy_WithSuperUserPolicy()
    {
        var services = new ServiceCollection();
        // Seed with a default (Core-style) implementation first
        services.AddScoped<IDataAccessPolicy, TenE0.Core.Abstractions.DefaultDataAccessPolicy>();
        services.AddTenE0Permissions();

        // After AddTenE0Permissions, IDataAccessPolicy should be replaced (SuperUserDataAccessPolicy)
        var policyDescriptors = services.Where(s => s.ServiceType == typeof(IDataAccessPolicy)).ToList();
        policyDescriptors.Should().HaveCount(1, "Replace should overwrite the default registration");
        policyDescriptors[0].ImplementationType.Should().NotBe(typeof(TenE0.Core.Abstractions.DefaultDataAccessPolicy));
        policyDescriptors[0].ImplementationType!.Name.Should().Be(nameof(SuperUserDataAccessPolicy));
    }

    [Fact]
    public void AddTenE0Permissions_RegistersOptions_WhenConfigureNull()
    {
        var services = new ServiceCollection();
        services.AddTenE0Permissions(); // no configure delegate

        // AddOptions<PermissionsOptions>() registers the closed IOptions<PermissionsOptions> via OptionsManager<>.
        // Verifying via resolution is the most reliable check.
        using var sp = services.BuildServiceProvider();
        var act = () => sp.GetRequiredService<IOptions<PermissionsOptions>>().Value;

        act.Should().NotThrow();
    }

    [Fact]
    public void AddTenE0Permissions_RunsConfigureDelegate_WhenProvided()
    {
        var services = new ServiceCollection();
        services.AddTenE0Permissions(opt =>
        {
            opt.SuperUserRoles.Add("super_admin");
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<PermissionsOptions>>().Value;

        opts.SuperUserRoles.Should().Contain("super_admin");
    }

    [Fact]
    public void AddTenE0Permissions_RegistersPipelineBehavior_AsOpenGeneric()
    {
        var services = new ServiceCollection();
        services.AddTenE0Permissions();

        var descriptor = services.SingleOrDefault(s =>
            s.ServiceType == typeof(IPipelineBehavior<,>) &&
            s.ImplementationType == typeof(PermissionBehavior<,>));

        descriptor.Should().NotBeNull("PermissionBehavior<,> must be registered as an open-generic IPipelineBehavior<,>");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddTenE0Permissions_BuildsScopedEvaluator()
    {
        var services = new ServiceCollection();
        // Register all dependencies that PermissionEvaluator needs:
        // ICurrentUserContext, IPermissionStore, IPermissionCache, IOptions<PermissionsOptions>.
        services.AddSingleton<ICurrentUserContext>(new StubCurrentUserContext());
        services.AddSingleton<IDistributedCache, MemoryDistributedCache>();
        services.AddSingleton<IPermissionStore>(new StubPermissionStore());
        services.AddTenE0Permissions();
        using var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var evaluator = scope.ServiceProvider.GetService<IPermissionEvaluator>();

        evaluator.Should().NotBeNull();
    }

    [Fact]
    public void AddTenE0Permissions_SingletonCatalog_ResolvedAcrossScopes()
    {
        var services = new ServiceCollection();
        services.AddTenE0Permissions();
        using var sp = services.BuildServiceProvider();

        var c1 = sp.GetRequiredService<PermissionCatalog>();
        var c2 = sp.GetRequiredService<PermissionCatalog>();

        c1.Should().BeSameAs(c2, "PermissionCatalog should be Singleton");
    }

    [Fact]
    public void AddTenE0Permissions_CalledTwice_DoesNotDuplicateSingletons()
    {
        var services = new ServiceCollection();
        services.AddTenE0Permissions();
        services.AddTenE0Permissions(); // second call

        services.Count(s => s.ServiceType == typeof(PermissionCatalog)).Should().Be(1,
            "PermissionCatalog registration should not be duplicated (TryAddSingleton)");
        services.Count(s => s.ServiceType == typeof(IPermissionEvaluator)).Should().Be(1,
            "IPermissionEvaluator registration should not be duplicated (TryAddScoped)");
    }

    // ────────────────────────────────────────────────────────────────────
    // AddTenE0PermissionStorage
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddTenE0PermissionStorage_RegistersEfStore_AsClosedGeneric()
    {
        var services = new ServiceCollection();
        services.AddTenE0PermissionStorage<TestDbContext>();

        // EfPermissionStore<TestDbContext> is the implementation
        var descriptor = services.FirstOrDefault(s =>
            s.ServiceType == typeof(IPermissionStore) &&
            s.ImplementationType == typeof(EfPermissionStore<TestDbContext>));

        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddTenE0PermissionStorage_RegistersPermissionGrantService_AsScoped()
    {
        var services = new ServiceCollection();
        services.AddTenE0PermissionStorage<TestDbContext>();

        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IPermissionGrantService));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(PermissionGrantService<TestDbContext>));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddTenE0PermissionStorage_ReplacesExistingStore_InsteadOfAppending()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPermissionStore, EfPermissionStore<TestDbContext>>();
        services.AddTenE0PermissionStorage<TestDbContext>();

        services.Count(s => s.ServiceType == typeof(IPermissionStore)).Should().Be(1,
            "Replace should overwrite the previous IPermissionStore registration");
    }

    // ────────────────────────────────────────────────────────────────────
    // AddTenE0PermissionsFromAssembly
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddTenE0PermissionsFromAssembly_RegistersPermissionProviders_AsSingletons()
    {
        var services = new ServiceCollection();
        services.AddTenE0PermissionsFromAssembly(typeof(PermissionsExtensionsTests).Assembly);

        var providerDescriptors = services
            .Where(s => s.ServiceType == typeof(IPermissionProvider))
            .ToList();

        providerDescriptors.Should().Contain(d => d.ImplementationType == typeof(FakePermissionProvider));
        providerDescriptors.Should().Contain(d => d.ImplementationType == typeof(OtherFakePermissionProvider));
        providerDescriptors.Should().AllSatisfy(d => d.Lifetime.Should().Be(ServiceLifetime.Singleton));
    }

    [Fact]
    public void AddTenE0PermissionsFromAssembly_RegistersEntityFilterContributors_AsScoped()
    {
        var services = new ServiceCollection();
        services.AddTenE0PermissionsFromAssembly(typeof(PermissionsExtensionsTests).Assembly);

        var contributorDescriptors = services
            .Where(s => s.ServiceType == typeof(IEntityFilterContributor))
            .ToList();

        contributorDescriptors.Should().Contain(d => d.ImplementationType == typeof(FakeEntityFilterContributor));
        contributorDescriptors.Should().Contain(d => d.ImplementationType == typeof(SecondFakeFilterContributor));
        contributorDescriptors.Should().AllSatisfy(d => d.Lifetime.Should().Be(ServiceLifetime.Scoped));
    }

    [Fact]
    public void AddTenE0PermissionsFromAssembly_SkipsAbstractAndInterfaces()
    {
        var services = new ServiceCollection();
        services.AddTenE0PermissionsFromAssembly(typeof(PermissionsExtensionsTests).Assembly);

        // The interfaces themselves are abstract; the test types above are concrete.
        services.Where(s => s.ServiceType == typeof(IPermissionProvider))
            .Select(d => d.ImplementationType)
            .Should().NotContainNulls();
        services.Should().NotContain(s => s.ImplementationType != null && s.ImplementationType.IsAbstract);
        services.Should().NotContain(s => s.ImplementationType != null && s.ImplementationType.IsInterface);
    }

    [Fact]
    public void AddTenE0PermissionsFromAssembly_ReturnSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddTenE0PermissionsFromAssembly(typeof(PermissionsExtensionsTests).Assembly);

        result.Should().BeSameAs(services, "fluent API must return the same instance");
    }
}
