namespace TenE0.Core.Abstractions;

/// <summary>
/// 命令/查询的根接口。
///
/// 设计要点（对比旧 BaseCMD : IRequest&lt;TResult&gt;）：
/// - 不依赖 MediatR（避开 12.x+ 的商业许可问题）
/// - 不引入 CRUD 字段（旧 BaseCMD 把 Entity/PostedProp/Filter 等都堆在基类，污染所有命令）
/// - CRUD 专用命令在三期 EntityServer 模块用专门基类承载
/// </summary>
/// <typeparam name="TResult">命令执行结果类型。无返回值用 <see cref="Unit"/>。</typeparam>
public interface ICommand<out TResult>
{
}

/// <summary>
/// 查询接口。语义别名 — 与 <see cref="ICommand{TResult}"/> 在管道层行为一致，
/// 但单独的接口让代码更清晰地表达"读"vs"写"，便于将来添加读写分离行为。
/// </summary>
public interface IQuery<out TResult> : ICommand<TResult>
{
}

/// <summary>
/// 无返回值的占位类型。
///
/// 与 MediatR.Unit 等价：让 ICommand 体系只有一套泛型签名，避免维护 ICommand 和 ICommand&lt;T&gt; 两条平行接口。
/// </summary>
public readonly record struct Unit
{
    public static readonly Unit Value = new();
    public static Task<Unit> Task { get; } = System.Threading.Tasks.Task.FromResult(Value);
}
