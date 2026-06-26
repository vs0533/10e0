namespace TenE0.Core.Abstractions;

/// <summary>
/// 全局业务错误码常量中心（#37 Part 2）。
///
/// 解决：当前业务错误码字符串（"AUTH_INVALID" / "AUTH_DISABLED" /
/// "TOKEN_INVALID" / "TOKEN_EXPIRED" / "TOKEN_REVOKED" / "NOT_FOUND" /
/// "FIELD_PERM" / "UNIQUE" / "UNIQUE_GROUP"）散落
/// <c>LoginCommandHandler</c> / <c>RefreshTokenCommandHandler</c> /
/// <c>EntityService</c> / <c>UniqueValidators</c> 4+ 文件，前端 i18n 困难。
///
/// <para>设计目标：</para>
/// <list type="bullet">
///   <item><term>唯一来源</term><description>所有错误码字符串收敛到本类，避免拼写漂移</description></item>
///   <item><term>前端 i18n</term><description>前端按 <c>code → message</c> 映射，Core 不参与 message 内容</description></item>
///   <item><term>可枚举</term><description>所有值遵循 <c>SCREAMING_SNAKE_CASE</c> 命名，方便前端枚举</description></item>
/// </list>
///
/// <para>迁移策略：本类为 <c>static class</c> + <c>const string</c>，业务方在 Step 2/3 把
/// <c>"AUTH_INVALID"</c> 等字面量替换为 <c>ErrorCodes.AuthInvalid</c>。本步只产出常量中心，
/// 不强制约束 handler 必须改写（handler/service 改动在 #37 下游步骤）。</para>
/// </summary>
public static class ErrorCodes
{
    /// <summary>登录凭据无效（用户名/密码错误、用户不存在）。默认 "AUTH_INVALID"。
    /// 散落源：<c>LoginCommandHandler.cs:35</c>。</summary>
    public const string AuthInvalid = "AUTH_INVALID";

    /// <summary>账号被禁用。默认 "AUTH_DISABLED"。
    /// 散落源：<c>LoginCommandHandler.cs:41</c> / <c>RefreshTokenCommandHandler.cs:86</c>。</summary>
    public const string AuthDisabled = "AUTH_DISABLED";

    /// <summary>Token 格式非法或签名校验失败。默认 "TOKEN_INVALID"。
    /// 散落源：<c>RefreshTokenCommandHandler.cs:52</c>。</summary>
    public const string TokenInvalid = "TOKEN_INVALID";

    /// <summary>Token 已过期。默认 "TOKEN_EXPIRED"。
    /// 散落源：<c>RefreshTokenCommandHandler.cs:79</c>。</summary>
    public const string TokenExpired = "TOKEN_EXPIRED";

    /// <summary>Token 已被吊销（刷新令牌表找不到 / 已注销）。默认 "TOKEN_REVOKED"。
    /// 散落源：<c>RefreshTokenCommandHandler.cs:73</c>。</summary>
    public const string TokenRevoked = "TOKEN_REVOKED";

    /// <summary>实体不存在。默认 "NOT_FOUND"。
    /// 散落源：<c>EntityService.cs:83,125</c>。</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>字段级权限不足（无 update / read 权限）。默认 "FIELD_PERM"。
    /// 散落源：<c>EntityService.cs:189</c>。</summary>
    public const string FieldPermission = "FIELD_PERM";

    /// <summary>唯一索引冲突。默认 "UNIQUE"。
    /// 散落源：<c>UniqueValidators.cs:39</c>（<c>FieldUniqueValidator</c>）。</summary>
    public const string Unique = "UNIQUE";

    /// <summary>复合唯一索引冲突（多字段组合）。默认 "UNIQUE_GROUP"。
    /// 散落源：<c>UniqueValidators.cs:115</c>（<c>GroupUniqueValidator</c>）。</summary>
    public const string UniqueGroup = "UNIQUE_GROUP";

    /// <summary>导入行级错误（类型转换失败 / 必填缺失 / 校验失败）。默认 "IMPORT_ROW"。
    /// 散落源：<c>ImportExecutor</c> / <c>ClosedXmlExcelImporter</c> / <c>CsvImporter</c>。</summary>
    public const string ImportRowError = "IMPORT_ROW";

    /// <summary>导入事务模式整体回滚（任一行失败已触发回滚全量）。默认 "IMPORT_ROLLBACK"。</summary>
    public const string ImportTransactionRolledback = "IMPORT_ROLLBACK";

    /// <summary>账号被锁定（登录失败次数过多）。默认 "AUTH_LOCKED"。issue #162。</summary>
    public const string AuthLocked = "AUTH_LOCKED";

    /// <summary>验证码无效 / 过期 / 不匹配。默认 "CAPTCHA_INVALID"。issue #162。</summary>
    public const string CaptchaInvalid = "CAPTCHA_INVALID";

    /// <summary>验证码必填但客户端未提供。默认 "CAPTCHA_REQUIRED"。issue #162。</summary>
    public const string CaptchaRequired = "CAPTCHA_REQUIRED";

    /// <summary>触发限流（429 Too Many Requests）。默认 "RATE_LIMITED"。issue #162。</summary>
    public const string RateLimited = "RATE_LIMITED";
}
