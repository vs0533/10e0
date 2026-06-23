using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 流程定义存储抽象 — 隔离 EF Core，便于测试 mock。
/// 生产实现 <see cref="ProcessDefinitionStore{TContext}"/> 基于 <see cref="IDbContextFactory{TContext}"/>。
/// </summary>
public interface IProcessDefinitionStore
{
    /// <summary>取同 Code 下最新版本（IsLatest=true）。未找到返回 null。</summary>
    Task<TenE0ProcessDefinition?> GetLatestAsync(string code, CancellationToken ct = default);

    /// <summary>取指定 Code + Version。</summary>
    Task<TenE0ProcessDefinition?> GetAsync(string code, int version, CancellationToken ct = default);

    /// <summary>按 Id 取。</summary>
    Task<TenE0ProcessDefinition?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>取同 Code 下所有版本（按 Version 降序）。</summary>
    Task<IReadOnlyList<TenE0ProcessDefinition>> ListVersionsAsync(string code, CancellationToken ct = default);

    /// <summary>列出所有 Code 的最新版本（分页）。</summary>
    Task<IReadOnlyList<TenE0ProcessDefinition>> ListLatestAsync(int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// 发布新版本：把同 Code 旧版本的 IsLatest 置 false，插入新版本（Version = 旧 max + 1，IsLatest=true）。
    /// </summary>
    Task<TenE0ProcessDefinition> PublishAsync(TenE0ProcessDefinition definition, CancellationToken ct = default);

    /// <summary>禁用某版本（IsEnabled=false，不物理删除，存量实例仍可用）。</summary>
    Task DisableAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// 基于 EF Core 的流程定义存储。
/// </summary>
public sealed class ProcessDefinitionStore<TContext>(IDbContextFactory<TContext> contextFactory)
    : IProcessDefinitionStore
    where TContext : DbContext
{
    public async Task<TenE0ProcessDefinition?> GetLatestAsync(string code, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        return await dc.Set<TenE0ProcessDefinition>()
            .Where(d => d.Code == code && d.IsLatest && !d.IsSoftDelete)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<TenE0ProcessDefinition?> GetAsync(string code, int version, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        return await dc.Set<TenE0ProcessDefinition>()
            .Where(d => d.Code == code && d.Version == version && !d.IsSoftDelete)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<TenE0ProcessDefinition?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        return await dc.Set<TenE0ProcessDefinition>()
            .Where(d => d.Id == id && !d.IsSoftDelete)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<TenE0ProcessDefinition>> ListVersionsAsync(string code, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var list = await dc.Set<TenE0ProcessDefinition>()
            .Where(d => d.Code == code && !d.IsSoftDelete)
            .OrderByDescending(d => d.Version)
            .ToListAsync(ct);
        return list;
    }

    public async Task<IReadOnlyList<TenE0ProcessDefinition>> ListLatestAsync(int skip, int take, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var list = await dc.Set<TenE0ProcessDefinition>()
            .Where(d => d.IsLatest && !d.IsSoftDelete)
            .OrderByDescending(d => d.CreateTime)
            .Skip(skip).Take(take)
            .ToListAsync(ct);
        return list;
    }

    public async Task<TenE0ProcessDefinition> PublishAsync(TenE0ProcessDefinition definition, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        // 旧版本 IsLatest 置 false
        var previous = await dc.Set<TenE0ProcessDefinition>()
            .Where(d => d.Code == definition.Code && d.IsLatest)
            .ToListAsync(ct);
        foreach (var p in previous) p.IsLatest = false;

        // 新版本号
        var maxVersion = previous.Count > 0
            ? await dc.Set<TenE0ProcessDefinition>()
                .Where(d => d.Code == definition.Code)
                .MaxAsync(d => (int?)d.Version, ct) ?? 0
            : 0;
        definition.Version = maxVersion + 1;
        definition.IsLatest = true;

        dc.Set<TenE0ProcessDefinition>().Add(definition);
        await dc.SaveChangesAsync(ct);
        return definition;
    }

    public async Task DisableAsync(string id, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var def = await dc.Set<TenE0ProcessDefinition>()
            .Where(d => d.Id == id)
            .FirstOrDefaultAsync(ct);
        if (def is not null)
        {
            def.IsEnabled = false;
            await dc.SaveChangesAsync(ct);
        }
    }
}
