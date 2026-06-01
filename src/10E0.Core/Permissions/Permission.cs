namespace TenE0.Core.Permissions;

/// <summary>
/// 权限定义（代码层）。
///
/// 旧 E0 用 string ControllTag 标识权限，typo 编译期不可见、无 IDE 跳转。
/// 新版用 PermissionDefinition + 常量 key 模式，调用方传字符串 key 但 key 由静态常量提供。
/// </summary>
/// <param name="Key">唯一标识，约定 "{group}.{action}" 格式，例如 "demo.create"。</param>
/// <param name="DisplayName">人类可读名称（用于权限管理 UI）。</param>
/// <param name="Group">所属分组（用于 UI 分类显示）。</param>
/// <param name="Description">详细描述（可选）。</param>
public sealed record PermissionDefinition(
    string Key,
    string DisplayName,
    string? Group = null,
    string? Description = null);

/// <summary>
/// 权限定义提供者。每个业务模块实现一个，声明本模块的所有权限。
/// 系统启动时扫描注册，构建全局权限目录。
/// </summary>
public interface IPermissionProvider
{
    /// <summary>返回本模块定义的所有权限。</summary>
    IEnumerable<PermissionDefinition> Define();
}
