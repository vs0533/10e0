using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Common;

namespace TenE0.Core.Security.RateLimiting;

/// <summary>
/// 429 响应格式化（issue #162）。
///
/// <para>
/// 与 <c>ForbiddenResponseWriter</c> / <c>TenE0ExceptionHandler</c> 风格一致 ——
/// 把 429 写成统一 <see cref="ApiResult{T}"/> 信封，前端用同一 DTO 反序列化。
/// 同时附 <c>Retry-After</c> 头（秒级整数），让客户端知道等多久重试。
/// </para>
///
/// <para>
/// 注册：<c>AddTenE0RateLimiting</c> 把 <see cref="OnRejectedAsync"/> 挂到
/// <c>RateLimiterOptions.OnRejected</c>。
/// </para>
/// </summary>
public static class RateLimitResponseWriter
{
    /// <summary>
    /// 默认 Retry-After（秒）。内置 limiter 在 OnRejected 阶段不直接暴露 retry 时间，
    /// 用一个保守的 60s 让客户端不要立即重试刷爆。
    /// </summary>
    public const int DefaultRetryAfterSeconds = 60;

    /// <summary>
    /// <c>RateLimiterOptions.OnRejected</c> 回调：写 429 + ApiResult JSON + Retry-After 头。
    /// 签名匹配 <c>Func&lt;OnRejectedContext, CancellationToken, ValueTask&gt;</c>。
    /// </summary>
    public static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        // review #6：优先从 lease 取真实剩余重试时间；limiter 未提供时退回保守默认 60s。
        var retryAfter = DefaultRetryAfterSeconds;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
        {
            var seconds = (int)Math.Ceiling(retry.TotalSeconds);
            if (seconds > 0) retryAfter = seconds;
        }
        http.Response.Headers.RetryAfter = retryAfter.ToString();

        // 用 host 的 JsonOptions（与 ForbiddenResponseWriter / ExceptionHandler 同一序列化器来源）
        var jsonOptions = http.RequestServices
            .GetService<IOptions<JsonOptions>>()?.Value;

        var body = ApiResult<object>.Fail(
            "请求过于频繁，请稍后再试。",
            code: "RATE_LIMITED");

        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            body,
            jsonOptions?.SerializerOptions,
            cancellationToken: cancellationToken);
    }
}
