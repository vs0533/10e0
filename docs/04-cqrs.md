# CQRS — 自建命令分发器

10E0 用自建 `ICommandDispatcher` 替代 MediatR，避免 12.x+ 商业许可风险。核心设计：**单一 `ICommand<TResult>` 接口体系 + Pipeline Behavior 管道 + Wrapper 缓存机制**。

> **命名空间**：接口在 `TenE0.Core.Abstractions`，实现在 `TenE0.Core.Cqrs`，DI 扩展在 `TenE0.Core.DependencyInjection`。

---

## 1. 核心接口

```csharp
// 命令根接口 — 不依赖 MediatR
public interface ICommand<out TResult> { }

// 查询语义别名 — 与 Command 管道行为一致，预留读写分离扩展点
public interface IQuery<out TResult> : ICommand<TResult> { }

// 无返回值的占位类型（等价 MediatR.Unit）
public readonly record struct Unit
{
    public static readonly Unit Value = new();
    public static Task<Unit> Task { get; } = System.Threading.Tasks.Task.FromResult(Value);
}

// 命令处理器 — 纯接口，通过构造函数注入所需依赖
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

// 命令分发器 — 替代 IMediator.Send，单一职责（不掺杂事件广播）
public interface ICommandDispatcher
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
}
```

分发器内部使用 **Wrapper 模式 + 静态 `ConcurrentDictionary` 缓存**，每个命令类型首次分发时构造泛型 wrapper 实例并缓存，后续命中缓存零反射热路径开销。

---

## 2. 三种命令定义模式

### 2.1 简单 CRUD — 委托 EntityService

封装创建/更新/删除的样板代码，业务逻辑薄：

```csharp
[RequirePermission(DemoPermissions.Create)]
internal sealed record CreateDemoCommand(string Name, string? OrgId, decimal? Salary) : ICommand<string>;

internal sealed class CreateDemoCommandHandler(IDbContextFactory<DemoDbContext> dcFactory, IEntityService entitySvc)
    : ICommandHandler<CreateDemoCommand, string>
{
    public async Task<string> HandleAsync(CreateDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        var demo = new DemoEntity { Name = command.Name, OrgId = command.OrgId, Salary = command.Salary };
        await entitySvc.CreateAsync(dc, demo, new EntityWriteOptions
        {
            UniqueValidators = [Unique.Field(demo, x => x.Name)],
            FieldPermissions = command.Salary.HasValue ? DemoFieldPermissions.Map : null,
        }, ct);
        return demo.Id;
    }
}
```

### 2.2 纯业务命令 — 直接操作 AggregateRoot

不依赖 EntityService，通过聚合方法触发领域事件：

```csharp
[RequirePermission(DemoPermissions.Update)]
internal sealed record PublishDemoCommand(string Id) : ICommand<bool>;

internal sealed class PublishDemoCommandHandler(
    IDbContextFactory<DemoDbContext> dcFactory, ICurrentUserContext currentUser, IErrs errs)
    : ICommandHandler<PublishDemoCommand, bool>
{
    public async Task<bool> HandleAsync(PublishDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        var demo = await dc.Demos.FirstOrDefaultAsync(d => d.Id == command.Id, ct);
        if (demo is null) { errs.Add("Demo 不存在", code: "NOT_FOUND"); return false; }

        demo.Publish(currentUser.UserCode ?? "anonymous");  // 聚合方法触发 DemoPublishedEvent
        await dc.SaveChangesAsync(ct);  // 业务状态 + OutboxMessage 同事务原子提交
                                         // 注：此处无需 ITransactional 标记，SaveChangesAsync 本身是原子操作
        return true;
    }
}
```

### 2.3 查询 — 实现 IQuery

用 `IQuery<TResult>` 语义别名表达"只读"意图，管道行为与 Command 一致：

```csharp
[RequirePermission(DemoPermissions.View)]
internal sealed record ListDemosQuery : IQuery<List<DemoView>>;

internal sealed class ListDemosQueryHandler(IDbContextFactory<DemoDbContext> dcFactory)
    : ICommandHandler<ListDemosQuery, List<DemoView>>
{
    public async Task<List<DemoView>> HandleAsync(ListDemosQuery query, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        return await dc.Demos
            .Select(d => new DemoView(d.Id, d.Code, d.Name, d.OrgId, d.Salary, d.CreateTime))
            .ToListAsync(ct);
    }
}
```

---

## 3. API 端点使用

`ICommandDispatcher` 直接从 Minimal API 端点参数注入：

```csharp
app.MapPost("/demo", async (CreateDemoDto dto, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
{
    try
    {
        var id = await dispatcher.SendAsync(new CreateDemoCommand(dto.Name, dto.OrgId, dto.Salary), ct);
        return errs.IsValid
            ? Results.Ok(new { id })
            : Results.BadRequest(new { error = errs.GetFirstError() });
    }
    catch (PermissionDeniedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
});

app.MapGet("/demo", async (ICommandDispatcher dispatcher, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await dispatcher.SendAsync(new ListDemosQuery(), ct));
    }
    catch (PermissionDeniedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
});
```

---

## 4. 管道行为链

命令进入后按 **DI 注册顺序** 逆序包裹，先注册的在外层（最先进入、最后退出），与 ASP.NET Core 中间件一致：

```
命令进入 → [Logging] → [Transaction] → [Permission] → Handler
               ↑            ↑              ↑
         外层 behavior   中层 behavior   内层 behavior
```

行为接口定义：

```csharp
public delegate Task<TResult> CommandHandlerDelegate<TResult>(CancellationToken cancellationToken);

public interface IPipelineBehavior<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken cancellationToken);
}
```

| 行为 | 注册时机 | 职责 |
|------|----------|------|
| `LoggingBehavior` | `AddTenE0Cqrs` 自动注册 | 记录命令开始/结束/异常 + 耗时 |
| `TransactionBehavior` | `AddTenE0TransactionBehavior<TContext>()` 单独注册 | 对 `ITransactional` 命令包裹数据库事务 |
| `PermissionBehavior` | `AddTenE0Permissions` 自动注册 | 检查 `[RequirePermission]` 属性，无权限抛 `PermissionDeniedException` |

---

## 5. TransactionBehavior — 事务与 Savepoint

命令实现 `ITransactional` 标记接口即被 `TransactionBehavior` 包裹：

```csharp
public interface ITransactional { }

// 标记事务命令
internal sealed record CreateOrderCommand(string CustomerCode, decimal Amount) : ICommand<string>, ITransactional;
```

事务行为处理两种场景：

```csharp
if (database.CurrentTransaction is null)
{
    // 无外层事务 → 开新事务，异常回滚
    await using var tx = await database.BeginTransactionAsync(ct);
    var result = await next(ct);
    await tx.CommitAsync(ct);
    return result;
}
else
{
    // 已有外层事务 → 用 Savepoint 实现嵌套语义
    var savepoint = $"sp_{Guid.NewGuid():N}";
    await currentTx.CreateSavepointAsync(savepoint, ct);
    try
    {
        var result = await next(ct);
        await currentTx.ReleaseSavepointAsync(savepoint, ct);
        return result;
    }
    catch
    {
        await currentTx.RollbackToSavepointAsync(savepoint, ct);  // 只回滚内层
        throw;
    }
}
```

Savepoint 机制修复了旧 `CommandManager.Dispatch()` 的嵌套事务 Bug：内层回滚不再破坏外层。

---

## 6. 自定义管道行为

实现 `IPipelineBehavior<TCommand, TResult>` 并注册即可插入横切逻辑：

```csharp
public sealed class ValidationBehavior<TCommand, TResult>(IValidator<TCommand> validator)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken ct)
    {
        var errors = await validator.ValidateAsync(command, ct);
        if (errors.Any())
            throw new ValidationException(errors);

        return await next(ct);  // 验证通过，进入下一环节
    }
}
```

注册顺序决定执行位置：

```csharp
// 在 LoggingBehavior 之后、TransactionBehavior 之前执行
services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
```

---

## 7. DI 注册

```csharp
// 核心：注册分发器 + LoggingBehavior + 扫描程序集注册所有 ICommandHandler
builder.Services.AddTenE0Cqrs(typeof(Program).Assembly);

// 事务行为单独注册（需要指定 DbContext 类型）
builder.Services.AddTenE0TransactionBehavior<DemoDbContext>();

// 权限行为由 AddTenE0Permissions / AddTenE0Identity 自动注册
builder.Services.AddTenE0Identity<AppUser, DemoDbContext>(opt => { ... });
```

`RegisterHandlersFromAssembly` 内部反射扫描所有实现了 `ICommandHandler<,>` 的非抽象类，以 `Scoped` 生命周期注册到 DI（即每个 HTTP 请求一个实例，与 `ICommandDispatcher` 生命周期一致）。多个程序集可同时传入，`Distinct()` 去重。
