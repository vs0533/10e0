using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Workflow.Definitions;
using TenE0.Core.Workflow.Runtime;

namespace TenE0.Core.Tests.Workflow.Runtime;

/// <summary>
/// #159 流程运行时端到端测试。
///
/// 覆盖：启动 / Approve（Single/Countersign/OrSign）/ Reject / Delegate / AddSigner / Rollback / 完成事件。
/// 用真实 InMemory DbContext + 真实 Engine/Service 装配（仅 IAssigneeDirectory 用内存假实现）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProcessRuntimeTests
{
    /// <summary>测试用 DbContext — 包含定义表 + 运行时表。</summary>
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0ProcessDefinition> ProcessDefinitions => Set<TenE0ProcessDefinition>();
        public DbSet<TenE0ProcessInstance> ProcessInstances => Set<TenE0ProcessInstance>();
        public DbSet<TenE0ProcessTask> ProcessTasks => Set<TenE0ProcessTask>();
        public DbSet<TenE0ProcessHistory> ProcessHistories => Set<TenE0ProcessHistory>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.ConfigureTenE0WorkflowDefinitionTables();
            mb.ConfigureTenE0WorkflowRuntimeTables();
        }

        protected override void OnConfiguring(DbContextOptionsBuilder b)
        {
            base.OnConfiguring(b);
            b.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private sealed class TestFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    /// <summary>内存版审批人目录（测试可控）。</summary>
    private sealed class FakeDirectory : IAssigneeDirectory
    {
        public List<string> RoleUsers { get; set; } = [];
        public string? ManagerOrg { get; set; }
        public List<string> ManagerOrgMembers { get; set; } = [];

        public Task<IReadOnlyList<string>> GetUsersByRoleAsync(string roleCode, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(RoleUsers);

        public Task<string?> GetManagerOrgIdAsync(string orgId, int level, CancellationToken ct = default)
            => Task.FromResult(ManagerOrg);

        public Task<IReadOnlyList<string>> GetOrgMembersAsync(string orgId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(ManagerOrgMembers);
    }

    /// <summary>装配一个完整的运行时栈。</summary>
    private sealed class Stack
    {
        public TestFactory Factory { get; }
        public IProcessDefinitionStore Store { get; }
        public IWorkflowEngine Engine { get; }
        public IProcessRuntimeService Runtime { get; }
        public ITaskService Tasks { get; }
        public FakeDirectory Directory { get; }
        public string DbName { get; }

        public Stack(string dbName, List<string>? roleUsers = null)
        {
            DbName = dbName;
            var options = new DbContextOptionsBuilder<TestDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            Factory = new TestFactory(options);
            Directory = new FakeDirectory { RoleUsers = roleUsers ?? [] };
            Store = new ProcessDefinitionStore<TestDbContext>(Factory);
            var resolvers = new IAssigneeResolver[]
            {
                new RoleAssigneeResolver(Directory),
                new ManagerAssigneeResolver(Directory),
                new UserAssigneeResolver(),
                new ExpressionAssigneeResolver(Directory),
            };
            Engine = new WorkflowEngine<TestDbContext>(Factory, resolvers, eventDispatcher: null, TimeProvider.System);
            var handlers = new IProcessActionHandler[]
            {
                new ApproveActionHandler<TestDbContext>(Factory, Engine, TimeProvider.System),
                new RejectActionHandler<TestDbContext>(Factory, TimeProvider.System),
                new DelegateActionHandler<TestDbContext>(Factory, TimeProvider.System),
                new AddSignerActionHandler<TestDbContext>(Factory, TimeProvider.System),
                new RollbackActionHandler<TestDbContext>(Factory, Engine),
            };
            Runtime = new ProcessRuntimeService<TestDbContext>(Factory, Store, Engine, handlers, eventDispatcher: null, TimeProvider.System);
            Tasks = new TaskService<TestDbContext>(Factory);
        }

        public async Task<string> SeedDefinitionAsync(IProcessNode[] nodes, string startCode)
        {
            var json = ProcessNodeSerializer.SerializeNodes(nodes);
            var def = new TenE0ProcessDefinition
            {
                Code = "test-flow",
                Name = "测试流程",
                StartNodeCode = startCode,
                NodesJson = json,
                EdgesJson = "[]",
                TenantId = "t1",
            };
            var published = await Store.PublishAsync(def);
            return published.Id;
        }
    }

    private static StartNode Start(string next) => new() { Code = "start", Name = "开始", NextNodeCode = next };
    private static EndNode End() => new() { Code = "end", Name = "结束" };

    // ================================================================
    // 启动流程
    // ================================================================

    [Fact]
    public async Task StartAsync_CreatesInstanceAndInitialTasks()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1"]);
        var nodes = new IProcessNode[]
        {
            Start("approve"),
            new ApprovalNode { Code = "approve", Name = "审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        var dto = await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow",
            BusinessKey = "BIZ-1",
            EntityType = "Expense",
            EntityId = "e1",
            Initiator = "u0",
        });

        dto.Status.Should().Be(ProcessStatus.Running);
        dto.CurrentNodeCode.Should().Be("approve");

        var pending = await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery());
        pending.Items.Should().ContainSingle();
        pending.Items[0].NodeCode.Should().Be("approve");
    }

    [Fact]
    public async Task StartAsync_UnknownDefinition_Throws()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"));

        var act = () => stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "missing", BusinessKey = "b", EntityType = "T", EntityId = "e", Initiator = "u",
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing*");
    }

    // ================================================================
    // Approve - Single 模式（任一通过即推进）
    // ================================================================

    [Fact]
    public async Task Approve_SingleMode_AnyApproveAdvancesToNext()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1", "u2"]);
        var nodes = new IProcessNode[]
        {
            Start("a"),
            new ApprovalNode { Code = "a", Name = "审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });

        // u1 通过 → 节点应推进到 end
        var result = await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items[0].InstanceId,
            Action = ProcessActionKind.Approve,
            Actor = "u1",
            Comment = "ok",
        });

        result.InstanceStatus.Should().Be(ProcessStatus.Approved, "Single 模式任一通过即完成");
    }

    // ================================================================
    // Approve - Countersign 模式（全部通过才推进）
    // ================================================================

    [Fact]
    public async Task Approve_CountersignMode_AllMustApprove()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1", "u2"]);
        var nodes = new IProcessNode[]
        {
            Start("a"),
            new ApprovalNode { Code = "a", Name = "会签", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Countersign, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });
        var instanceId = (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items[0].InstanceId;

        // u1 通过 → 不应推进（Countersign 需全部）
        var r1 = await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = instanceId, Action = ProcessActionKind.Approve, Actor = "u1", Comment = "ok",
        });
        r1.InstanceStatus.Should().Be(ProcessStatus.Running);

        // u2 通过 → 全部完成，推进
        var r2 = await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = instanceId, Action = ProcessActionKind.Approve, Actor = "u2", Comment = "ok",
        });
        r2.InstanceStatus.Should().Be(ProcessStatus.Approved);
    }

    // ================================================================
    // Reject
    // ================================================================

    [Fact]
    public async Task Reject_TerminatesInstanceAndVoidsSiblingTasks()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1", "u2"]);
        var nodes = new IProcessNode[]
        {
            Start("a"),
            new ApprovalNode { Code = "a", Name = "审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Countersign, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });
        var instanceId = (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items[0].InstanceId;

        var result = await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = instanceId, Action = ProcessActionKind.Reject, Actor = "u1", Comment = "no",
        });

        result.InstanceStatus.Should().Be(ProcessStatus.Rejected);

        // u2 的任务应被作废
        var u2Pending = await stack.Tasks.GetMyPendingTasksAsync("u2", new WorkflowPagedQuery());
        u2Pending.Items.Should().BeEmpty("驳回后同节点其他任务作废");
    }

    // ================================================================
    // Delegate
    // ================================================================

    [Fact]
    public async Task Delegate_CreatesTaskForDelegateeAndMarksOriginalDelegated()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1"]);
        var nodes = new IProcessNode[]
        {
            Start("a"),
            new ApprovalNode { Code = "a", Name = "审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, AllowDelegate = true, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });
        var instanceId = (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items[0].InstanceId;

        var result = await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = instanceId, Action = ProcessActionKind.Delegate, Actor = "u1", DelegateTo = "u9", Comment = "请代审",
        });

        result.NewTaskAssignees.Should().Contain("u9");
        // u1 的待办清空，u9 出现待办
        (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items.Should().BeEmpty();
        (await stack.Tasks.GetMyPendingTasksAsync("u9", new WorkflowPagedQuery())).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Delegate_NotAllowed_Throws()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1"]);
        var nodes = new IProcessNode[]
        {
            Start("a"),
            new ApprovalNode { Code = "a", Name = "审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, AllowDelegate = false, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });
        var instanceId = (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items[0].InstanceId;

        var act = () => stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = instanceId, Action = ProcessActionKind.Delegate, Actor = "u1", DelegateTo = "u9",
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不允许委派*");
    }

    // ================================================================
    // AddSigner
    // ================================================================

    [Fact]
    public async Task AddSigner_AppendsTasksForNewSigners()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1"]);
        var nodes = new IProcessNode[]
        {
            Start("a"),
            new ApprovalNode { Code = "a", Name = "会签", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Countersign, AllowAddSigner = true, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });
        var instanceId = (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items[0].InstanceId;

        await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = instanceId, Action = ProcessActionKind.AddSigner, Actor = "u1", AddSigners = ["u7", "u8"],
        });

        (await stack.Tasks.GetMyPendingTasksAsync("u7", new WorkflowPagedQuery())).Items.Should().ContainSingle();
        (await stack.Tasks.GetMyPendingTasksAsync("u8", new WorkflowPagedQuery())).Items.Should().ContainSingle();
    }

    // ================================================================
    // Rollback
    // ================================================================

    [Fact]
    public async Task Rollback_VoidsCurrentAndRegeneratesTargetNodeTasks()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1", "u2"]);
        var nodes = new IProcessNode[]
        {
            Start("a1"),
            new ApprovalNode { Code = "a1", Name = "一审", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, NextNodeCode = "a2" },
            new ApprovalNode
            {
                Code = "a2", Name = "二审", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single,
                AllowRollback = true, RollbackTargetCode = "a1", NextNodeCode = "end",
            },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });
        // 推进到 a2：u1 审批 a1 通过
        var instanceId = (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items[0].InstanceId;
        await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = instanceId, Action = ProcessActionKind.Approve, Actor = "u1",
        });

        // 现在 a2 节点，回退到 a1
        var result = await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = instanceId, Action = ProcessActionKind.Rollback, Actor = "u1", RollbackToNodeCode = "a1", Comment = "打回",
        });

        result.NextNodeCode.Should().Be("a1");
        // a1 重新有待办
        (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items.Should().ContainSingle(t => t.NodeCode == "a1");
    }

    // ================================================================
    // Cancel
    // ================================================================

    [Fact]
    public async Task Cancel_OnlyInitiator_CanCancel()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1"]);
        var nodes = new IProcessNode[]
        {
            Start("a"),
            new ApprovalNode { Code = "a", Name = "审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        var dto = await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });

        // 非发起人撤销 → 抛
        await stack.Runtime.Invoking(s => s.CancelAsync(dto.Id, "u99", reason: null))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*仅发起人*");

        // 发起人撤销 → 成功
        await stack.Runtime.CancelAsync(dto.Id, "u0", reason: "不需要了");

        var fetched = await stack.Tasks.GetInstanceAsync(dto.Id);
        fetched!.Status.Should().Be(ProcessStatus.Cancelled);
    }

    // ================================================================
    // 非审批人不能操作
    // ================================================================

    [Fact]
    public async Task Approve_NonAssignee_Throws()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1"]);
        var nodes = new IProcessNode[]
        {
            Start("a"),
            new ApprovalNode { Code = "a", Name = "审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });
        var instanceId = (await stack.Tasks.GetMyPendingTasksAsync("u1", new WorkflowPagedQuery())).Items[0].InstanceId;

        var act = () => stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = instanceId, Action = ProcessActionKind.Approve, Actor = "u999", // 非审批人
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*没有待处理任务*");
    }

    // ================================================================
    // 历史记录
    // ================================================================

    [Fact]
    public async Task History_RecordsAllActions()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1"]);
        var nodes = new IProcessNode[]
        {
            Start("a"),
            new ApprovalNode { Code = "a", Name = "审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        var dto = await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
        });
        await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = dto.Id, Action = ProcessActionKind.Approve, Actor = "u1", Comment = "approve",
        });

        var history = await stack.Tasks.GetInstanceHistoryAsync(dto.Id);

        history.Should().Contain(h => h.Action == "Start");
        history.Should().Contain(h => h.Action == "Approve" && h.Actor == "u1");
    }

    // ================================================================
    // 多节点顺序推进 + 分支路由
    // ================================================================

    [Fact]
    public async Task Branch_RoutesByCondition_AmountGreaterThanThreshold()
    {
        var stack = new Stack(Guid.NewGuid().ToString("N"), roleUsers: ["u1", "u2"]);
        var nodes = new IProcessNode[]
        {
            Start("manager"),
            new ApprovalNode { Code = "manager", Name = "主管审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, NextNodeCode = "amount-check" },
            new BranchNode
            {
                Code = "amount-check", Name = "金额判断",
                Routes = [new BranchRoute { TargetNodeCode = "director", Condition = new ConditionRuleGroup { Rules = [new ConditionRule { Field = "Amount", Op = "gt", Value = "10000" }] } }],
                DefaultNodeCode = "end",
            },
            new ApprovalNode { Code = "director", Name = "总监审批", AssigneePolicy = AssigneePolicy.Role("r1"), Mode = ApprovalMode.Single, NextNodeCode = "end" },
            End(),
        };
        await stack.SeedDefinitionAsync(nodes, "start");

        // Amount=20000 → 走 director 分支
        var dto = await stack.Runtime.StartAsync(new StartProcessRequest
        {
            DefinitionCode = "test-flow", BusinessKey = "B", EntityType = "T", EntityId = "e", Initiator = "u0",
            SummaryJson = """{"Amount":20000}""",
        });

        // 主管审批通过 → 应路由到 director（而非 end）
        await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = dto.Id, Action = ProcessActionKind.Approve, Actor = "u1",
        });

        var fetched = await stack.Tasks.GetInstanceAsync(dto.Id);
        fetched!.CurrentNodeCode.Should().Be("director");

        // director 审批通过 → 完成
        var r2 = await stack.Runtime.ExecuteActionAsync(new ExecuteActionRequest
        {
            InstanceId = dto.Id, Action = ProcessActionKind.Approve, Actor = "u1",
        });
        r2.InstanceStatus.Should().Be(ProcessStatus.Approved);
    }
}
