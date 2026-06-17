namespace TenE0.Core.Cqrs.Behaviors;

/// <summary>
/// 内置 Pipeline Behavior 的标准 Order 常量。
///
/// #41 引入 — 让跨插件/跨团队的 behavior 可以声明"我必须在 X 之后、Y 之前"，
/// 而不再依赖"靠注册顺序"这种约定俗成。
///
/// 数值约定：**数字越大 = 越外层**（最先进入、最先退出）。
/// 与 issue #41 的示例一致：Logging(200) = 最外层，Permission(50) = 最内层。
///
/// 典型管道（外 → 内）：
/// <code>
///   [Logging 200] → [Transaction 100] → [Permission 50] → Handler
/// </code>
///
/// 用户自定义 behavior 推荐用 0~1000 区间；保留 1xxx+ 给框架未来扩展。
/// </summary>
public static class BuiltInBehaviorOrders
{
    /// <summary>LoggingBehavior 顺序：最外层，捕获所有内部行为抛出的异常/耗时。</summary>
    public const int Logging = 200;

    /// <summary>TransactionBehavior 顺序：中间层，权限失败要能触发事务回滚 → 必须包住 Permission。</summary>
    public const int Transaction = 100;

    /// <summary>PermissionBehavior 顺序：最内层，最贴近 handler，避免"先开事务后鉴权"的回滚浪费。</summary>
    public const int Permission = 50;
}
