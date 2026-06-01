using TenE0.Core.Abstractions;

namespace TenE0.Core.Common;

/// <summary>
/// 统一 API 响应结构。
///
/// 旧 ApiResult 的字段命名（success/errorCode/errorMessage 全小写）继承下来，
/// 已是项目历史约定，前端依赖此格式，新版保持向后兼容。
/// </summary>
/// <typeparam name="T">业务数据类型。</typeparam>
public record ApiResult<T>
{
    public bool success { get; init; }
    public T? data { get; init; }
    public string? errorCode { get; init; }
    public string? errorMessage { get; init; }

    /// <summary>表单字段错误绑定（前端按此定位出错字段）。</summary>
    public string[]? nameBound { get; init; }

    /// <summary>提示类型（0=静音, 1=警告, 2=错误, 4=通知, 9=页面）。</summary>
    public int showType { get; init; }

    public string? traceId { get; init; }

    public static ApiResult<T> Ok(T data) => new() { success = true, data = data };

    public static ApiResult<T> Fail(string message, string? code = null, string[]? nameBound = null) =>
        new()
        {
            success = false,
            errorMessage = message,
            errorCode = code,
            nameBound = nameBound,
            showType = 2
        };

    /// <summary>从 IErrs 构造失败响应。</summary>
    public static ApiResult<T> FromErrs(IErrs errs) =>
        Fail(errs.GetFirstError() ?? "操作失败", nameBound: errs.Keys.ToArray());
}
