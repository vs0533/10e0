using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth;
using TenE0.Core.DependencyInjection;

namespace TenE0.Core.Tests.DependencyInjection;

[Trait("Category", "Unit")]
public sealed class AppModuleExtensionsTests
{
    private interface IModuleMarker { }

    private sealed class FirstModule : IAppModule
    {
        public int Order => 100;
        public bool ConfigureServicesCalled { get; private set; }
        public bool MapEndpointsCalled { get; private set; }
        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            ConfigureServicesCalled = true;
            services.AddSingleton<IModuleMarker>(new FirstMarker());
        }
        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            MapEndpointsCalled = true;
            endpoints.MapGet("/first", () => Results.Ok("first"));
        }
    }

    private sealed class FirstMarker : IModuleMarker { }

    private sealed class SecondModule : IAppModule
    {
        public int Order => 200;
        public bool MapEndpointsCalled { get; private set; }
        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            MapEndpointsCalled = true;
            endpoints.MapGet("/second", () => Results.Ok("second"));
        }
    }

    private sealed class ThrowingModule : IAppModule
    {
        public int Order => 50;
        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints)
            => throw new InvalidOperationException("boom");
    }

    private sealed class StubUserInfoLoader : IUserInfoLoader
    {
        public ValueTask<ICurrentUserInfo?> LoadAsync(string userCode, UserType userType, CancellationToken cancellationToken)
            => ValueTask.FromResult<ICurrentUserInfo?>(null);
        public string Serialize(ICurrentUserInfo info) => string.Empty;
        public ICurrentUserInfo? Deserialize(string payload, UserType userType) => null;
    }

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    [Fact]
    public void AddAppModule_GenericConstraint_RegistersModuleAndInvokesConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddAppModule<FirstModule>(EmptyConfig());

        services.Should().Contain(s => s.ServiceType == typeof(IAppModule));
        services.Should().Contain(s => s.ServiceType == typeof(IModuleMarker));
    }

    [Fact]
    public void MapAppModules_InvokesMapEndpointsInOrderAscending()
    {
        var first = new FirstModule();
        var second = new SecondModule();
        var services = new ServiceCollection();
        services.AddSingleton<IAppModule>(second);
        services.AddSingleton<IAppModule>(first);

        var app = BuildApp(services);

        app.MapAppModules();

        first.MapEndpointsCalled.Should().BeTrue();
        second.MapEndpointsCalled.Should().BeTrue();
    }

    [Fact]
    public void MapAppModules_PropagatesException_WhenModuleThrows()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppModule>(new ThrowingModule());

        var app = BuildApp(services);

        var act = () => app.MapAppModules();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("boom");
    }

    [Fact]
    public void MapAppModules_ReturnsEndpointRouteBuilder()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppModule>(new FirstModule());

        var app = BuildApp(services);

        var result = app.MapAppModules();

        result.Should().BeSameAs(app);
    }

    [Fact]
    public void AddTenE0Core_RegistersNullUserInfoLoaderAsDefault()
    {
        var services = new ServiceCollection();

        services.AddTenE0Core();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var loader = scope.ServiceProvider.GetService<IUserInfoLoader>();

        loader.Should().NotBeNull();
        loader.Should().BeOfType<NullUserInfoLoader>();
    }

    [Fact]
    public void AddTenE0Core_TryAddScoped_DoesNotOverrideExistingRegistration()
    {
        var services = new ServiceCollection();
        var replacement = new StubUserInfoLoader();

        services.AddScoped<IUserInfoLoader>(_ => replacement);
        services.AddTenE0Core();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<IUserInfoLoader>();

        loader.Should().BeSameAs(replacement);
    }

    private static WebApplication BuildApp(IServiceCollection services)
    {
        var builder = WebApplication.CreateBuilder();
        foreach (var s in services) builder.Services.Add(s);
        return builder.Build();
    }
}
