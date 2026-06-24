using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Core.Configuration;
using TenE0.Core.Configuration.Storage;
using TenE0.Core.Hosting;

namespace TenE0.Api.Seeders;

/// <summary>
/// 启动时初始化数据字典样例 + 系统参数（与 <see cref="SystemParameterRegistry"/> 同步）。
/// Order 400：在权限(100)/账号(200)/菜单(300) 之后。
/// </summary>
internal sealed class ConfigurationSeeder(
    IDbContextFactory<DemoDbContext> dcFactory,
    SystemParameterRegistry registry) : IDataSeeder
{
    public int Order => 400;

    public async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);

        await SeedDictAsync(dc, cancellationToken);
        await SeedSystemParametersAsync(dc, cancellationToken);

        await dc.SaveChangesAsync(cancellationToken);
    }

    // ============================================================
    // 数据字典
    // ============================================================
    private static async Task SeedDictAsync(DemoDbContext dc, CancellationToken ct)
    {
        if (await dc.DictTypes.AnyAsync(ct)) return; // 幂等

        dc.DictTypes.AddRange(
            new TenE0DictType { Code = "gender", Name = "性别", SortOrder = 1 },
            new TenE0DictType { Code = "id_type", Name = "证件类型", SortOrder = 2 },
            new TenE0DictType { Code = "order_status", Name = "订单状态", SortOrder = 3 });

        dc.DictItems.AddRange(
            // gender
            new TenE0DictItem { DictTypeCode = "gender", Label = "男", Value = "M", SortOrder = 1 },
            new TenE0DictItem { DictTypeCode = "gender", Label = "女", Value = "F", SortOrder = 2 },
            new TenE0DictItem { DictTypeCode = "gender", Label = "未知", Value = "U", SortOrder = 3 },
            // id_type
            new TenE0DictItem { DictTypeCode = "id_type", Label = "身份证", Value = "id_card", SortOrder = 1 },
            new TenE0DictItem { DictTypeCode = "id_type", Label = "护照", Value = "passport", SortOrder = 2 },
            new TenE0DictItem { DictTypeCode = "id_type", Label = "军官证", Value = "officer", SortOrder = 3 },
            // order_status
            new TenE0DictItem { DictTypeCode = "order_status", Label = "待付款", Value = "pending", SortOrder = 1 },
            new TenE0DictItem { DictTypeCode = "order_status", Label = "已付款", Value = "paid", SortOrder = 2 },
            new TenE0DictItem { DictTypeCode = "order_status", Label = "已发货", Value = "shipped", SortOrder = 3 },
            new TenE0DictItem { DictTypeCode = "order_status", Label = "已完成", Value = "completed", SortOrder = 4 },
            new TenE0DictItem { DictTypeCode = "order_status", Label = "已取消", Value = "cancelled", SortOrder = 5 });
    }

    // ============================================================
    // 系统参数：注册表里声明但 DB 缺失的 key 落库（含默认值）
    // ============================================================
    private async Task SeedSystemParametersAsync(DemoDbContext dc, CancellationToken ct)
    {
        if (await dc.SystemParameters.AnyAsync(ct)) return; // 幂等：已初始化则跳过（缺失补全另作管理工具）

        foreach (var def in registry.All)
        {
            dc.SystemParameters.Add(new TenE0SystemParameter
            {
                Key = def.Key,
                Value = def.DefaultValue,
                ValueType = def.ValueType,
                Description = def.Description,
                Group = def.Group,
                IsReadOnly = def.IsReadOnly,
            });
        }
    }
}
