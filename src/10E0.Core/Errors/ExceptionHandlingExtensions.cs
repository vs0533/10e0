using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TenE0.Core.Errors;

/// <summary>
/// DI / pipeline helpers for the centralized exception handling layer
/// introduced in issue #39.
///
/// Typical Program.cs wiring:
/// <code>
/// // Service registration
/// builder.Services.AddTenE0ExceptionHandler();
///
/// // Pipeline
/// var app = builder.Build();
/// app.UseExceptionHandler();   // dispatches to registered IExceptionHandler
/// </code>
/// </summary>
public static class ExceptionHandlingExtensions
{
    /// <summary>
    /// Register <see cref="IApiErrorMapper"/> (the default
    /// <see cref="DefaultApiErrorMapper"/>) and the
    /// <see cref="TenE0ExceptionHandler"/> in DI.
    ///
    /// Call this BEFORE <c>builder.Build()</c>, then add
    /// <c>app.UseExceptionHandler()</c> at the start of the pipeline
    /// (before any endpoint mapping) so unhandled exceptions are caught.
    /// </summary>
    public static IServiceCollection AddTenE0ExceptionHandler(this IServiceCollection services)
    {
        // Mapper is pure + stateless → singleton.
        services.TryAddSingleton<IApiErrorMapper, DefaultApiErrorMapper>();

        // The handler resolves scoped services (ILogger) per request.
        // Use TryAddEnumerable so multiple test fixtures can stack extra
        // handlers without colliding.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionHandler, TenE0ExceptionHandler>());

        return services;
    }
}
