namespace TenE0.Api.Endpoints;

internal static class HealthEndpoints
{
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => new { name = "10E0.Api", status = "ok" });
        return app;
    }
}
