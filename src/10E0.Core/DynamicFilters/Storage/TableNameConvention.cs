using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.Extensions.Options;

namespace TenE0.Core.DynamicFilters.Storage;

/// <summary>
/// EF Core <see cref="IModelFinalizingConvention"/>：在模型 finalize 阶段
/// 将每个实体的物理表名替换为 <see cref="ITableNameProvider"/> 的解析结果，
/// 可选附加 <see cref="TableNameOptions.Schema"/>。
///
/// 在 <c>DbContext.OnConfiguring</c> / <c>AddDbContext</c> 中通过
/// <c>optionsBuilder.UseTableNameConvention(...)</c> 或
/// <c>ConfigureConventions</c> 注册。
/// </summary>
/// <remarks>
/// 仅影响 *未显式* 调用 <c>b.ToTable(...)</c> 的实体——若业务模型已经硬编码表名，
/// convention 跳过以保留向后兼容。Step 2/3 会将 <c>ConfigureTenE0XxxTables</c> 内部的
/// <c>b.ToTable("DataFilterRules")</c> 移除，让本 convention 接管。
/// </remarks>
public sealed class TableNameConvention(
    ITableNameProvider nameProvider,
    IOptions<TableNameOptions> options) : IModelFinalizingConvention
{
    private readonly TableNameOptions _options = options.Value;

    /// <inheritdoc />
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            // 仅处理 Relational 实体（有表映射）
            if (entityType.GetTableName() is null) continue;

            // 已显式指定表名（业务代码 b.ToTable(...) 调用）— 跳过，保留向后兼容
            var defaultTableName = entityType.GetDefaultTableName();
            var currentTableName = entityType.GetTableName();

            // Convention 不覆盖显式 ToTable；Step 2/3 会清掉框架表硬编码，那时本 convention 接管。
            if (currentTableName != defaultTableName) continue;

            var newName = nameProvider.GetTableName(entityType.ClrType);
            if (newName == currentTableName && string.IsNullOrEmpty(_options.Schema)) continue;

            var schema = string.IsNullOrEmpty(_options.Schema) ? entityType.GetSchema() : _options.Schema;
            entityType.SetTableName(newName);
            entityType.SetSchema(schema);
        }
    }
}
