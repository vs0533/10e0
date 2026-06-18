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
        // #51: the DbUpdateException disambiguator must be in DI before
        // the mapper resolves it. DefaultDbErrorClassifier is provider-
        // agnostic and stateless → singleton. Hosts that need a custom
        // classifier (e.g. one that reads Npgsql.PostgresException
        // directly) can call services.Replace(...) after this method.
        services.AddTenE0DbErrorClassifier();

        // Mapper depends on IDbErrorClassifier; use a factory so DI
        // resolves the (potentially replaced) classifier instance rather
        // than constructing DefaultApiErrorMapper with a parameterless
        // ctor and silently bypassing the registered classifier.
        services.TryAddSingleton<IApiErrorMapper>(sp =>
            new DefaultApiErrorMapper(sp.GetRequiredService<IDbErrorClassifier>()));

        // ILogger<T> is a singleton and the handler itself has no per-request
        // state, so the handler is registered as singleton too. TryAddEnumerable
        // keeps registration idempotent so multiple test fixtures can stack
        // extra IExceptionHandler implementations without colliding.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionHandler, TenE0ExceptionHandler>());

        return services;
    }
}
