using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;

namespace TenE0.Core.EntityService.Validators;

/// <summary>
/// 唯一性验证器。
///
/// 与旧 IUnique 的差异：
/// - 不再依赖 E0Context（避免循环依赖）
/// - 接收 DbContext + IErrs，职责清晰
/// - async 方法，配合 EF Core 异步查询
/// </summary>
public interface IUniqueValidator
{
    /// <summary>
    /// 执行验证。验证失败时往 IErrs 添加错误条目。
    /// </summary>
    /// <param name="context">数据库上下文。</param>
    /// <param name="errs">错误收集器。</param>
    /// <param name="ignoreSelfId">true 时排除自身（更新场景），false 时不排除（创建场景）。</param>
    Task ValidateAsync(DbContext context, IErrs errs, bool ignoreSelfId, CancellationToken cancellationToken);
}
