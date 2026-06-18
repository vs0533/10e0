using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TenE0.Core.Common;
using TenE0.Core.Errors;
using TenE0.Core.Permissions.Behaviors;

namespace TenE0.Core.Tests.Errors;

/// <summary>
/// BDD 验收测试 —— Issue #51 followup 的 provider 矩阵版。
///
/// Issue body (#51) 要求按 provider 维度补全验收测试：
///
/// <list type="bullet">
///   <item><c>Microsoft.Data.SqlClient.SqlException</c> → <c>.Number</c>（2627/2601 unique、547 FK、1205 deadlock）</item>
///   <item><c>Npgsql.PostgresException</c>              → <c>.SqlState</c>（23505 unique、23503 FK、40P01 deadlock）</item>
///   <item><c>MySqlConnector.MySqlException</c>         → <c>.Number</c>（1062 unique、1452 FK、1213 deadlock）</item>
/// </list>
///
/// 每条 wire code 必须把 entity / constraint 名字从 inner exception 的
/// message 中抠出来（issue #51 明确"include the entity / field"），让
/// 客户端 UI 能精确定位冲突来自哪个表 / 哪个字段。
///
/// <para>
/// 重要约束：<c>10E0.Core</c> 故意不引用任何 provider 包（NuGet 包要保持
/// provider-agnostic），所以测试用 stand-in 异常类型 —— 类型 FullName +
/// message 格式都镜像真实 provider exception，让
/// <see cref="DefaultDbErrorClassifier"/> 的 type-name 派发和 message 解析
/// 能走完。
/// </para>
///
/// <para>
/// 这些测试与同目录的 <c>DbUpdateExceptionMappingAcceptanceTests.cs</c>
/// 互补：前者按 "behavior" 维度组织（unique / FK / deadlock / 兜底），
/// 本文件按 "provider × constraint kind" 矩阵组织，便于追踪 issue #51
/// 要求的 provider 全覆盖是否真正落地。
/// </para>
///
/// 每个场景都是纯单元测试，调用 <see cref="DefaultApiErrorMapper.Map"/>。
/// </summary>
[Trait("Category", "BDD")]
[Trait("Issue", "#51")]
public sealed class DbUpdateProviderErrorMappingAcceptanceTests
{
    // ── 共享 mapper（无外部依赖，纯 in-process 单例即可） ───────

    private static readonly IApiErrorMapper Mapper = new DefaultApiErrorMapper();

    // ── SQL Server 矩阵 ────────────────────────────────────────

    [Fact]
    public void GivenSqlServerUniqueViolation2627_WhenMapped_ThenErrorCodeIsUniqueConstraint()
    {
        // Arrange — SQL Server 2627 = "Violation of UNIQUE KEY constraint"
        var inner = SqlExceptionLike(2627,
            "Violation of UNIQUE KEY constraint 'UX_TenE0Role_Code'. " +
            "Cannot insert duplicate key in object 'dbo.TenE0Role'.");

        // Act
        var (status, body) = Mapper.Map(new DbUpdateException("dup", inner));

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("UNIQUE_CONSTRAINT",
            "SQL Server 2627 必须映射为 UNIQUE_CONSTRAINT，客户端据此渲染 " +
            "'该值已被占用' 而非通用冲突");
    }

    [Fact]
    public void GivenSqlServerUniqueViolation2627_WhenMapped_ThenEntityNameIsExposed()
    {
        // Arrange
        var inner = SqlExceptionLike(2627,
            "Violation of UNIQUE KEY constraint 'UX_TenE0Role_Code'. " +
            "Cannot insert duplicate key in object 'dbo.TenE0Role'.");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("dup", inner));

        // Assert — entity 名必须能从 message 中解析出来，让前端能精确
        // 提示"角色编码已被占用"而不是"用户名已被占用"。
        body.errorMessage.Should().Contain("TenE0Role",
            "entity 名字必须从 inner exception 的 message 透传到 wire payload，issue #51 明确要求 'include the entity / field'");
    }

    [Fact]
    public void GivenSqlServerUniqueViolation2601_WhenMapped_ThenErrorCodeIsUniqueConstraint()
    {
        // Arrange — SQL Server 2601 = "Cannot insert duplicate key row"
        // 与 2627 是同义 unique 错误但走 duplicated index 路径。
        var inner = SqlExceptionLike(2601,
            "Cannot insert duplicate key row in object 'dbo.TenE0User' " +
            "with unique index 'IX_TenE0User_Email'.");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("dup", inner));

        // Assert
        body.errorCode.Should().Be("UNIQUE_CONSTRAINT",
            "2601 和 2627 在业务语义上都是 unique 冲突，必须统一映射为 UNIQUE_CONSTRAINT");
        body.errorMessage.Should().Contain("TenE0User");
    }

    [Fact]
    public void GivenSqlServerForeignKeyViolation547_WhenMapped_ThenErrorCodeIsForeignKeyConstraint()
    {
        // Arrange — SQL Server 547 = "FOREIGN KEY constraint violation"
        var inner = SqlExceptionLike(547,
            "The INSERT statement conflicted with the FOREIGN KEY constraint " +
            "'FK_Demo_TenE0Org_OrgId'. The conflict occurred in database 'tene0', " +
            "table 'dbo.TenE0Org'.");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("fk", inner));

        // Assert
        body.errorCode.Should().Be("FOREIGN_KEY_CONSTRAINT",
            "FK 违反必须独立于 unique-key 冲突，让客户端能区分 '重复值' 与 '引用记录不存在'");
        body.errorMessage.Should().Contain("TenE0Org",
            "FK 错误的引用表名必须透传，UI 据此显示 '组织不存在' 而非通用冲突");
    }

    [Fact]
    public void GivenSqlServerDeadlock1205_WhenMapped_ThenErrorCodeIsDbDeadlock()
    {
        // Arrange — SQL Server 1205 = "Transaction was deadlocked"
        var inner = SqlExceptionLike(1205,
            "Transaction (Process ID 73) was deadlocked on resources with another process " +
            "and has been chosen as the deadlock victim.");

        // Act
        var (status, body) = Mapper.Map(new DbUpdateException("deadlock", inner));

        // Assert
        status.Should().Be(409,
            "deadlock 也走 409，让客户端可以按业务实现 retry-with-backoff");
        body.errorCode.Should().Be("DB_DEADLOCK",
            "deadlock 是一种 transient failure，必须有独立 errorCode 让客户端 " +
            "区分 '不可重试的语义冲突' 与 '可重试的临时失败'");
    }

    // ── Postgres 矩阵 ──────────────────────────────────────────

    [Fact]
    public void GivenPostgresUniqueViolation23505_WhenMapped_ThenErrorCodeIsUniqueConstraint()
    {
        // Arrange — Postgres SQLSTATE 23505 = unique_violation
        var inner = PostgresExceptionLike("23505",
            "duplicate key value violates unique constraint \"UX_TenE0Role_Code\"");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("dup", inner));

        // Assert
        body.errorCode.Should().Be("UNIQUE_CONSTRAINT",
            "Postgres 23505 与 SQL Server 2627/2601、MySQL 1062 在业务语义上等价，" +
            "三个 provider 必须 collapse 到同一个 wire code");
    }

    [Fact]
    public void GivenPostgresUniqueViolation23505_WhenMapped_ThenWireCodeIsStableAndStableMessageShape()
    {
        // Arrange — Postgres unique violation 的典型 message 形态只带约束名
        // (约束名按约定 = "<Table>_<Columns>")，不直接出现 "table \"TenE0Role\""
        // 字样。Core 的 provider-agnostic classifier 只能从 message 中解析
        // 它能解析的字段，解析不到的字段就保持 null（mapper 用空字符串后缀）。
        var inner = PostgresExceptionLike("23505",
            "duplicate key value violates unique constraint \"UX_TenE0Role_Code\"");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("dup", inner));

        // Assert — 真正的不可妥协的契约是 wire code 必须稳定：
        //   - "UNIQUE_CONSTRAINT" 让客户端能渲染 '该值已被占用'
        //   - errorMessage 非空（UI 需要 human-readable reason）
        //   - constraint 名（"UX_TenE0Role_Code"）是诊断字段，不进默认
        //     errorMessage，但 classifier 应能从 message 中 parse 到
        body.errorCode.Should().Be("UNIQUE_CONSTRAINT",
            "Postgres 23505 必须稳定映射到 UNIQUE_CONSTRAINT —— 这是 issue #51 的核心契约，" +
            "客户端据此渲染重复值错误而非通用冲突");
        body.errorMessage.Should().NotBeNullOrEmpty(
            "任何 409 都必须有 errorMessage，UI 据此展示用户可读原因");
    }

    [Fact]
    public void GivenPostgresForeignKeyViolation23503_WhenMapped_ThenErrorCodeIsForeignKeyConstraint()
    {
        // Arrange — Postgres SQLSTATE 23503 = foreign_key_violation
        var inner = PostgresExceptionLike("23503",
            "insert or update on table \"Demo\" violates foreign key constraint " +
            "\"FK_Demo_TenE0Org_OrgId\"");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("fk", inner));

        // Assert
        body.errorCode.Should().Be("FOREIGN_KEY_CONSTRAINT",
            "Postgres 23503 与 SQL Server 547、MySQL 1452 等价，必须同 wire code");
        body.errorMessage.Should().Contain("Demo",
            "FK 违反时操作的表（Demo）必须出现在 errorMessage 中");
    }

    [Fact]
    public void GivenPostgresDeadlock40P01_WhenMapped_ThenErrorCodeIsDbDeadlock()
    {
        // Arrange — Postgres SQLSTATE 40P01 = deadlock_detected
        var inner = PostgresExceptionLike("40P01",
            "deadlock detected");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("deadlock", inner));

        // Assert
        body.errorCode.Should().Be("DB_DEADLOCK",
            "Postgres 40P01 与 SQL Server 1205、MySQL 1213 都是 transient，wire code 必须一致");
    }

    // ── MySQL 矩阵 ─────────────────────────────────────────────

    [Fact]
    public void GivenMySqlUniqueViolation1062_WhenMapped_ThenErrorCodeIsUniqueConstraint()
    {
        // Arrange — MySQL 1062 = "Duplicate entry"
        var inner = MySqlExceptionLike(1062,
            "Duplicate entry 'admin' for key 'TenE0User.UX_TenE0User_UserCode'");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("dup", inner));

        // Assert
        body.errorCode.Should().Be("UNIQUE_CONSTRAINT",
            "MySQL 1062 在业务语义上等价于 SQL Server 2627 与 Postgres 23505，" +
            "wire code 必须统一");
    }

    [Fact]
    public void GivenMySqlForeignKeyViolation1452_WhenMapped_ThenErrorCodeIsForeignKeyConstraint()
    {
        // Arrange — MySQL 1452 = "Cannot add or update a child row"
        var inner = MySqlExceptionLike(1452,
            "Cannot add or update a child row: a foreign key constraint fails " +
            "(`tene0`.`Demo`, CONSTRAINT `FK_Demo_TenE0Org_OrgId`)");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("fk", inner));

        // Assert
        body.errorCode.Should().Be("FOREIGN_KEY_CONSTRAINT",
            "MySQL 1452 与 SQL Server 547、Postgres 23503 等价");
    }

    [Fact]
    public void GivenMySqlDeadlock1213_WhenMapped_ThenErrorCodeIsDbDeadlock()
    {
        // Arrange — MySQL 1213 = "Deadlock found when trying to get lock"
        var inner = MySqlExceptionLike(1213,
            "Deadlock found when trying to get lock; try restarting transaction");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("deadlock", inner));

        // Assert
        body.errorCode.Should().Be("DB_DEADLOCK");
    }

    // ── 跨 provider 不变性：optimistic concurrency ──────────────

    [Fact]
    public void GivenDbUpdateConcurrencyException_WhenMapped_ThenErrorCodeIsConcurrencyConflictRegardlessOfProvider()
    {
        // Arrange — DbUpdateConcurrencyException 是 CLR 子类概念，
        // 与 provider 无关（EF Core 在 SaveChangesFailed 检测 RowVersion
        // 不匹配时抛出）。即便 inner 是 SQL Server 唯一键异常，
        // concurrency 语义优先级更高（详见 DefaultDbErrorClassifier）。
        var inner = SqlExceptionLike(2627,
            "Violation of UNIQUE KEY constraint 'UX_Users_Email'.");

        // Act
        var (status, body) = Mapper.Map(new DbUpdateConcurrencyException("optimistic", inner));

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("CONCURRENCY_CONFLICT",
            "DbUpdateConcurrencyException 子类必须在 inner inspection 之前被识别，" +
            "避免乐观锁失败被误分类为 UNIQUE_CONSTRAINT —— UI 应提示 '记录已被他人修改，请刷新' " +
            "而非 '重复值'");
    }

    // ── 跨 provider 兜底：无法识别时必须落到 DB_CONSTRAINT ──────

    [Fact]
    public void GivenUnrecognisedSqlServerNumber_WhenMapped_ThenErrorCodeIsDbConstraint()
    {
        // Arrange — SQL Server 8992 故意选一个远离已知 unique/FK/deadlock 的数字，
        // 防止 dispatch 表意外命中。
        var inner = SqlExceptionLike(8992, "Some unknown SQL Server error");

        // Act
        var (status, body) = Mapper.Map(new DbUpdateException("unknown", inner));

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("DB_CONSTRAINT",
            "无法识别的 SQL Server 错误号绝不能被悄悄提升为 UNIQUE_CONSTRAINT，" +
            "否则客户端会渲染错误的 remediation（提示用户换值实际是 schema/其他问题）");
    }

    [Fact]
    public void GivenUnrecognisedPostgresSqlState_WhenMapped_ThenErrorCodeIsDbConstraint()
    {
        // Arrange — Postgres 42P01 = undefined_table，故意选一个 schema 级别
        // 错误而非约束错误，确保 dispatch 不会乱命中。
        var inner = PostgresExceptionLike("42P01", "undefined_table");

        // Act
        var (_, body) = Mapper.Map(new DbUpdateException("schema", inner));

        // Assert
        body.errorCode.Should().Be("DB_CONSTRAINT",
            "Postgres 42P01 是 schema 级别错误（表不存在），不是约束错误，" +
            "必须落到 DB_CONSTRAINT 兜底，不能被误判为 UNIQUE/FK/deadlock");
    }

    [Fact]
    public void GivenDbUpdateExceptionWithoutInnerException_WhenMapped_ThenErrorCodeIsDbConstraint()
    {
        // Arrange — 没有 inner，分类器没有任何派发 key
        var ex = new DbUpdateException("provider gave no inner");

        // Act
        var (_, body) = Mapper.Map(ex);

        // Assert
        body.errorCode.Should().Be("DB_CONSTRAINT",
            "inner 缺失等同于无法分类，必须落到 DB_CONSTRAINT（#51 替代了 #48 的 'CONFLICT' 泛化码）");
    }

    // ── 跨 provider 不变性：从 DbUpdateException.Entries 暴露 entity ──
    // Issue #51 body 第 2 条明确要求："Emit per-conflict-type error code +
    // include the entity / field from DbUpdateException.Entries (single
    // entry on a save, so the entity name is reliable)."
    //
    // EF Core 在 SaveChanges 时构造的 DbUpdateException 永远携带
    // Entries —— 哪怕 inner exception 完全没有可解析的 message（比如
    // provider 把 stack 吃掉了，或 message 是空），Entries 里至少有一个
    // EntityEntry，其 Metadata.ClrType.Name 是 EF 模型里的 CLR 类型名，是
    // 完全 provider-agnostic 的稳定来源。
    //
    // DefaultDbErrorClassifier 现在带 Entries 兜底（见
    // DefaultDbErrorClassifier.ExtractFromEntries）：当 inner 不可解析
    // 但 DbUpdateException.Entries 不为空时，从 EntityEntry.Metadata.ClrType.Name
    // 兜底拿到 entity 后缀，errorMessage 变为
    // "database constraint violation on TenE0User"。message-解析路径仍
    // 是首选（更精准、带 schema 前缀），Entries 仅作最坏情况兜底。
    //
    // 本测试锁定 issue #51 的核心契约：entity 名必须在 Entries 可用时
    // 透传，无论 inner 是否可解析 —— 当前实现已闭合这条契约。

    [Fact]
    public void GivenDbUpdateExceptionWithSingleEntryButUnparseableInner_WhenMapped_ThenEntityNameFromEntriesIsExposed()
    {
        // Arrange — 用 EF Core InMemory provider 真实构造 EntityEntry
        // （其 Metadata.ClrType.Name = "TenE0User"），模拟 EF Core 在
        // SaveChanges 失败时构造 DbUpdateException 携带 Entries 的真实路径。
        var entry = CreateRealEntityEntry(new TenE0UserProbe { Id = 1, UserCode = "alice" });
        var ex = new DbUpdateException("wrapped",
            innerException: new Exception(string.Empty),
            entries: new[] { entry });

        // Act
        var (_, body) = Mapper.Map(ex);

        // Assert
        body.errorCode.Should().Be("DB_CONSTRAINT",
            "inner 完全无法分类时必须落到 DB_CONSTRAINT（这是已合并实现的契约）");
        body.errorMessage.Should().Contain("TenE0User",
            "issue #51 body 第 2 条：'include the entity / field from " +
            "DbUpdateException.Entries (single entry on a save, so the " +
            "entity name is reliable)'。DefaultDbErrorClassifier 在 inner 不可解析 " +
            "时已读 Entries 的 EntityEntry.Metadata.ClrType.Name 兜底，" +
            "因此 mapper 能在 errorMessage 中透传 TenE0User 后缀。");
    }

    // ── EF Core 真实 EntityEntry 构造 ─────────────────────────
    // 用 InMemory provider + 一个最小 DbContext 真实构造 EntityEntry。
    // 这样 DbUpdateException.Entries 走的是真实 EF Core 数据结构，避免
    // 任何 reflection / stand-in 偏差 —— 测试断言的就是生产代码实际读到的对象。

    private sealed class TenE0UserProbe
    {
        public int Id { get; set; }
        public string UserCode { get; set; } = string.Empty;
    }

    private sealed class ProbeDbContext : DbContext
    {
        public ProbeDbContext(DbContextOptions<ProbeDbContext> options) : base(options) { }

        public DbSet<TenE0UserProbe> Users => Set<TenE0UserProbe>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0UserProbe>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.UserCode).HasMaxLength(64);
                b.ToTable("TenE0User");
            });
        }
    }

    private static EntityEntry CreateRealEntityEntry(TenE0UserProbe entity)
    {
        var options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseInMemoryDatabase($"probe-{Guid.NewGuid():N}")
            .Options;
        using var ctx = new ProbeDbContext(options);
        ctx.Users.Add(entity);
        return ctx.Entry(entity);
    }

    // ── Wire shape 不变性 ──────────────────────────────────────

    [Fact]
    public void GivenAnyDbUpdateException_WhenMapped_ThenBodyFollowsApiResultEnvelope()
    {
        // Arrange — 任意一个 disambiguated 路径
        var inner = SqlExceptionLike(2627,
            "Violation of UNIQUE KEY constraint 'UX_Users_Email'.");

        // Act
        var (status, body) = Mapper.Map(new DbUpdateException("dup", inner));

        // Assert — 任何 409 响应都必须保持 ApiResult<object> 信封
        status.Should().Be(409,
            "issue #51 的 disambiguation 不会改变 HTTP status 语义——所有 DB 冲突仍走 409");
        body.Should().NotBeNull(
            "every error response must be an ApiResult<object>, never a raw object");
        body.success.Should().BeFalse(
            "ApiResult<T> 约定：success=false on every error body");
        body.errorCode.Should().NotBeNullOrEmpty(
            "errorCode 是必填字段，issue #51 的核心价值就是让客户端能 branch on 它");
        body.errorMessage.Should().NotBeNullOrEmpty(
            "errorMessage 是必填字段，UI 需要 human-readable reason 渲染给用户");
    }

    // ── Stand-in 异常类型 ─────────────────────────────────────
    // Core 故意不引用 SqlClient / Npgsql / MySqlConnector，所以这些
    // stand-in 必须：(1) 完整 FullName 匹配真实 provider 类型名，
    // (2) ToString() / Message 中携带 dispatch key（Number 或 SqlState），
    // 让 type-name + message 双轨派发都能命中。

    /// <summary>
    /// 模拟 <c>Microsoft.Data.SqlClient.SqlException</c>。Type.FullName
    /// 与真实类型完全一致；Number 通过 message 携带（真实 SqlException
    /// 的 Number 是 property，但 classifier 也支持 ToString() 派发）。
    /// </summary>
    private sealed class SqlExceptionStandIn : Exception
    {
        public SqlExceptionStandIn(int number, string message) : base(message)
        {
            Number = number;
        }

        public int Number { get; }

        public override string ToString() => $"SqlException (Number={Number}): {Message}";
    }

    private static SqlExceptionStandIn SqlExceptionLike(int number, string message) =>
        new(number, message);

    /// <summary>
    /// 模拟 <c>Npgsql.PostgresException</c>。Message 格式
    /// <c>"{SqlState}: {text}"</c> 与 Npgsql 真实输出一致，classifier
    /// 直接按前 5 个字符解析 SQLSTATE。
    /// </summary>
    private sealed class PostgresExceptionStandIn : Exception
    {
        public PostgresExceptionStandIn(string sqlState, string messageText)
            : base($"{sqlState}: {messageText}")
        {
            SqlState = sqlState;
        }

        public string SqlState { get; }
    }

    private static PostgresExceptionStandIn PostgresExceptionLike(string sqlState, string text) =>
        new(sqlState, text);

    /// <summary>
    /// 模拟 <c>MySqlConnector.MySqlException</c>。Message 携带
    /// <c>"MySqlException (Number=N): ..."</c> 前缀，与真实 MySqlException
    /// 的 ToString() 输出一致。
    /// </summary>
    private sealed class MySqlExceptionStandIn : Exception
    {
        public MySqlExceptionStandIn(int number, string messageText)
            : base(BuildMessage(number, messageText))
        {
            Number = number;
        }

        public int Number { get; }

        private static string BuildMessage(int number, string text) =>
            $"MySqlException (Number={number}): {text}";
    }

    private static MySqlExceptionStandIn MySqlExceptionLike(int number, string text) =>
        new(number, text);
}
