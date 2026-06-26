namespace TenE0.Core.Workflow.Runtime;

/// <summary>流程实例状态。</summary>
public enum ProcessStatus
{
    /// <summary>运行中。</summary>
    Running,
    /// <summary>全部审批通过。</summary>
    Approved,
    /// <summary>被驳回。</summary>
    Rejected,
    /// <summary>发起人撤销。</summary>
    Cancelled,
    /// <summary>超时终止。</summary>
    TimedOut,
}

/// <summary>任务状态。</summary>
public enum ProcessTaskStatus
{
    /// <summary>待处理。</summary>
    Pending,
    /// <summary>审批通过。</summary>
    Approved,
    /// <summary>驳回。</summary>
    Rejected,
    /// <summary>已委派（原 Task 被新 Task 接替）。</summary>
    Delegated,
    /// <summary>超时自动处理。</summary>
    Timeout,
    /// <summary>因回退 / 撤销作废。</summary>
    Voided,
}

/// <summary>审批操作种类（运行时执行入口）。</summary>
public enum ProcessActionKind
{
    Approve,
    Reject,
    Delegate,
    AddSigner,
    Rollback,
}
