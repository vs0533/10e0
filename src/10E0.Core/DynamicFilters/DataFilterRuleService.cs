using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenE0.Core.DynamicFilters.Storage;

namespace TenE0.Core.DynamicFilters;

/// <summary>
/// IDataFilterRuleService 的 EF 实现。
/// 使用 ctx.Set&lt;TenE0DataFilterRule&gt;() 访问表，TContext 只需是 DbContext + 在 model 中注册该实体。
/// </summary>
public sealed class DataFilterRuleService<TContext>(
    IDbContextFactory<TContext> contextFactory,
    ILogger<DataFilterRuleService<TContext>> logger) : IDataFilterRuleService
    where TContext : DbContext
{
    public async Task<IReadOnlyList<TenE0DataFilterRule>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.Set<TenE0DataFilterRule>()
            .AsNoTracking()
            .OrderBy(r => r.EntityTypeName)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TenE0DataFilterRule>> GetByEntityAsync(string entityTypeName, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.Set<TenE0DataFilterRule>()
            .AsNoTracking()
            .Where(r => r.EntityTypeName == entityTypeName)
            .ToListAsync(ct);
    }

    public async Task<TenE0DataFilterRule?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.Set<TenE0DataFilterRule>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<TenE0DataFilterRule> CreateAsync(DataFilterRuleCreateRequest request, CancellationToken ct = default)
    {
        ValidateRuleJson(request.RuleJson);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var rule = new TenE0DataFilterRule
        {
            EntityTypeName = request.EntityTypeName,
            RuleJson = request.RuleJson,
            Description = request.Description,
            IsEnabled = request.IsEnabled,
        };

        ctx.Set<TenE0DataFilterRule>().Add(rule);
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("创建数据过滤规则: {Id} -> {Entity}", rule.Id, rule.EntityTypeName);
        return rule;
    }

    public async Task UpdateAsync(string id, DataFilterRuleUpdateRequest request, CancellationToken ct = default)
    {
        if (request.RuleJson is not null)
            ValidateRuleJson(request.RuleJson);

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var rule = await ctx.Set<TenE0DataFilterRule>().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new InvalidOperationException($"数据过滤规则不存在：{id}");

        if (request.RuleJson is not null)
            rule.RuleJson = request.RuleJson;
        if (request.Description is not null)
            rule.Description = request.Description;
        if (request.IsEnabled.HasValue)
            rule.IsEnabled = request.IsEnabled.Value;

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("更新数据过滤规则: {Id}", id);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var rule = await ctx.Set<TenE0DataFilterRule>().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new InvalidOperationException($"数据过滤规则不存在：{id}");

        ctx.Set<TenE0DataFilterRule>().Remove(rule);
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("删除数据过滤规则: {Id}", id);
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var rule = await ctx.Set<TenE0DataFilterRule>().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new InvalidOperationException($"数据过滤规则不存在：{id}");

        rule.IsEnabled = enabled;
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("{Action}数据过滤规则: {Id}", enabled ? "启用" : "禁用", id);
    }

    private static void ValidateRuleJson(string ruleJson)
    {
        try
        {
            JsonSerializer.Deserialize<ConditionRuleGroup>(ruleJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"RuleJson 不是合法的 ConditionRuleGroup JSON: {ex.Message}", ex);
        }
    }
}
