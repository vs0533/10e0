using TenE0.Core.Abstractions;
using TenE0.Core.Workflow.Runtime;

namespace TenE0.Api.Endpoints;

/// <summary>
/// #159 业务端点 — 普通用户发起/审批/查询流程。
/// 所有端点要求认证（参考 DemoEndpoints 模式，用 ICurrentUserContext 取当前用户）。
/// </summary>
internal static class WorkflowEndpoints
{
    public static WebApplication MapWorkflowEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/workflow").RequireAuthorization();

        // ----------------- 发起流程 -----------------
        g.MapPost("/start", async (
            StartProcessRequestDto dto,
            IProcessRuntimeService runtime,
            ICurrentUserContext user,
            CancellationToken ct) =>
        {
            var req = new StartProcessRequest
            {
                DefinitionCode = dto.DefinitionCode,
                BusinessKey = dto.BusinessKey,
                EntityType = dto.EntityType,
                EntityId = dto.EntityId,
                Initiator = user.UserCode!,
                InitiatorOrgId = dto.InitiatorOrgId,
                Title = dto.Title,
                SummaryJson = dto.SummaryJson,
                BusinessData = dto.BusinessData ?? new Dictionary<string, object?>(),
            };
            var result = await runtime.StartAsync(req, ct);
            return Results.Ok(result);
        });

        // ----------------- 执行审批操作 -----------------
        g.MapPost("/{instanceId}/actions", async (
            string instanceId,
            ExecuteActionDto dto,
            IProcessRuntimeService runtime,
            ICurrentUserContext user,
            CancellationToken ct) =>
        {
            var req = new ExecuteActionRequest
            {
                InstanceId = instanceId,
                Action = dto.Action,
                Actor = user.UserCode!,
                Comment = dto.Comment,
                DelegateTo = dto.DelegateTo,
                AddSigners = dto.AddSigners,
                RollbackToNodeCode = dto.RollbackToNodeCode,
            };
            var result = await runtime.ExecuteActionAsync(req, ct);
            return Results.Ok(result);
        });

        // ----------------- 撤销流程 -----------------
        g.MapPost("/{instanceId}/cancel", async (
            string instanceId,
            CancelDto dto,
            IProcessRuntimeService runtime,
            ICurrentUserContext user,
            CancellationToken ct) =>
        {
            await runtime.CancelAsync(instanceId, user.UserCode!, dto.Reason, ct);
            return Results.Ok(new { ok = true });
        });

        // ----------------- 我的待办 -----------------
        g.MapGet("/tasks/pending", async (
            ITaskService tasks,
            ICurrentUserContext user,
            int page = 1,
            int pageSize = 20,
            CancellationToken ct = default) =>
        {
            var result = await tasks.GetMyPendingTasksAsync(user.UserCode!, new WorkflowPagedQuery(page, pageSize), ct);
            return Results.Ok(result);
        });

        // ----------------- 我发起的 -----------------
        g.MapGet("/initiated", async (
            ITaskService tasks,
            ICurrentUserContext user,
            int page = 1,
            int pageSize = 20,
            CancellationToken ct = default) =>
        {
            var result = await tasks.GetMyInitiatedAsync(user.UserCode!, new WorkflowPagedQuery(page, pageSize), ct);
            return Results.Ok(result);
        });

        // ----------------- 实例详情 -----------------
        g.MapGet("/{instanceId}", async (
            string instanceId,
            ITaskService tasks,
            CancellationToken ct) =>
        {
            var result = await tasks.GetInstanceAsync(instanceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // ----------------- 审批历史 -----------------
        g.MapGet("/{instanceId}/history", async (
            string instanceId,
            ITaskService tasks,
            CancellationToken ct) =>
        {
            var result = await tasks.GetInstanceHistoryAsync(instanceId, ct);
            return Results.Ok(result);
        });

        return app;
    }

    // ---- DTO ----

    public sealed class StartProcessRequestDto
    {
        public required string DefinitionCode { get; set; }
        public required string BusinessKey { get; set; }
        public required string EntityType { get; set; }
        public required string EntityId { get; set; }
        public string? InitiatorOrgId { get; set; }
        public string? Title { get; set; }
        public string? SummaryJson { get; set; }
        public Dictionary<string, object?>? BusinessData { get; set; }
    }

    public sealed class ExecuteActionDto
    {
        public ProcessActionKind Action { get; set; }
        public string? Comment { get; set; }
        public string? DelegateTo { get; set; }
        public IReadOnlyList<string>? AddSigners { get; set; }
        public string? RollbackToNodeCode { get; set; }
    }

    public sealed class CancelDto
    {
        public string? Reason { get; set; }
    }
}
