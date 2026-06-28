using TenE0.Core.Permissions;

namespace TenE0.Api.Domain;

/// <summary>
/// 权限 key 常量定义 — Demo 业务用。
/// </summary>
internal static class DemoPermissions
{
    public const string View = "demo.view";
    public const string Create = "demo.create";
    public const string Update = "demo.update";
    public const string Delete = "demo.delete";
    public const string ManageSalary = "demo.field.salary";  // 字段级权限
    public const string Admin = "perm.admin";                 // 后台管理权限

    // #185 证书模块
    public const string CertificateView = "certificate.view";
    public const string CertificateRender = "certificate.render";
}

/// <summary>
/// 权限元数据 — 提供给 PermissionCatalog，供管理后台展示。
/// </summary>
internal sealed class DemoPermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> Define() =>
    [
        new(DemoPermissions.View,         "查看 Demo",          "demo"),
        new(DemoPermissions.Create,       "创建 Demo",          "demo"),
        new(DemoPermissions.Update,       "更新 Demo",          "demo"),
        new(DemoPermissions.Delete,       "删除 Demo",          "demo"),
        new(DemoPermissions.ManageSalary, "维护 Demo 薪资字段", "demo"),
        new(DemoPermissions.Admin,        "权限后台",           "system"),
        new(DemoPermissions.CertificateView,   "查看证书", "certificate"),
        new(DemoPermissions.CertificateRender, "生成证书", "certificate"),
    ];
}
