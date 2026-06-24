using TenE0.Core.Configuration;
using TenE0.Core.Configuration.Storage;

namespace TenE0.Api.Seeders;

/// <summary>
/// Demo 项目声明的系统参数定义（Issue #153 决策点 2：仅预定义 key 可被 Admin 改值）。
///
/// <para>
/// 这些定义在 <c>Program.cs</c> 注册为 <see cref="ISystemParameterDefinition"/>，启动期由
/// <see cref="SystemParameterRegistry"/> 收集供 <c>SystemParameterStore</c> 校验；
/// <see cref="ConfigurationSeeder"/> 据此把缺失 key 落库（含默认值），保证 DB 与注册表一致。
/// </para>
/// </summary>
public static class SystemParameterDefinitions
{
    /// <summary>全部定义实例，供 <c>Program.cs</c> 批量注册到 <see cref="ISystemParameterDefinition"/>。</summary>
    public static readonly ISystemParameterDefinition[] All =
    [
        new PasswordMinLength(),
        new SessionTimeoutMinutes(),
        new UploadMaxSizeMb(),
        new RealnameRequired(),
        new SystemInstallationId(),
    ];

    /// <summary>密码最小长度。</summary>
    public sealed record PasswordMinLength : ISystemParameterDefinition
    {
        public string Key => "password.min_length";
        public string DefaultValue => "8";
        public ParameterValueType ValueType => ParameterValueType.Int;
        public string Group => "Security";
        public string? Description => "密码最小长度";
        public bool IsReadOnly => false;
    }

    /// <summary>会话超时（分钟）。</summary>
    public sealed record SessionTimeoutMinutes : ISystemParameterDefinition
    {
        public string Key => "session.timeout_minutes";
        public string DefaultValue => "30";
        public ParameterValueType ValueType => ParameterValueType.Int;
        public string Group => "Security";
        public string? Description => "会话超时（分钟）";
        public bool IsReadOnly => false;
    }

    /// <summary>上传文件大小上限（MB）。</summary>
    public sealed record UploadMaxSizeMb : ISystemParameterDefinition
    {
        public string Key => "upload.max_size_mb";
        public string DefaultValue => "10";
        public ParameterValueType ValueType => ParameterValueType.Int;
        public string Group => "File";
        public string? Description => "上传文件大小上限（MB）";
        public bool IsReadOnly => false;
    }

    /// <summary>是否开启实名认证（业务开关）。</summary>
    public sealed record RealnameRequired : ISystemParameterDefinition
    {
        public string Key => "business.realname_required";
        public string DefaultValue => "false";
        public ParameterValueType ValueType => ParameterValueType.Bool;
        public string Group => "Business";
        public string? Description => "是否开启实名认证";
        public bool IsReadOnly => false;
    }

    /// <summary>安装时锁定的系统标识（只读，演示 IsReadOnly）。</summary>
    public sealed record SystemInstallationId : ISystemParameterDefinition
    {
        public string Key => "system.installation_id";
        public string DefaultValue => "10e0-demo";
        public ParameterValueType ValueType => ParameterValueType.String;
        public string Group => "System";
        public string? Description => "安装实例标识（只读）";
        public bool IsReadOnly => true;
    }
}
