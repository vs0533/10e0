using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Configuration.Storage;

/// <summary>
/// Configuration 模块（数据字典 + 系统参数）EF Core 表配置。
/// 约定与 <c>SequenceModelBuilderExtensions</c> / <c>MenuModelBuilderExtensions</c> 一致：
/// 每个字符串列显式声明 <c>HasMaxLength</c> + <c>IsRequired</c>，唯一性用 <c>HasIndex().IsUnique()</c>，
/// 不显式建表名、不显式声明 FK（仅索引约束）。
/// </summary>
public static class ConfigurationModelBuilderExtensions
{
    /// <summary>配置 Configuration 模块全部表（DictType / DictItem / SystemParameter）。</summary>
    public static ModelBuilder ConfigureTenE0ConfigurationTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0DictType>(b =>
        {
            b.Property(t => t.Code).HasMaxLength(64).IsRequired();
            b.HasIndex(t => t.Code).IsUnique();
            b.Property(t => t.Name).HasMaxLength(128).IsRequired();
            b.Property(t => t.Description).HasMaxLength(512);
        });

        mb.Entity<TenE0DictItem>(b =>
        {
            b.Property(i => i.DictTypeCode).HasMaxLength(64).IsRequired();
            b.HasIndex(i => i.DictTypeCode);

            b.Property(i => i.Label).HasMaxLength(128).IsRequired();
            b.Property(i => i.Value).HasMaxLength(128).IsRequired();
            // 同一字典类型下 Value 唯一；不同类型可复用同一 Value
            b.HasIndex(i => new { i.DictTypeCode, i.Value }).IsUnique();

            b.Property(i => i.ExtraJson).HasMaxLength(1024);
            b.Property(i => i.ParentItemValue).HasMaxLength(128);
            b.HasIndex(i => new { i.DictTypeCode, i.ParentItemValue });
        });

        mb.Entity<TenE0SystemParameter>(b =>
        {
            b.Property(p => p.Key).HasMaxLength(128).IsRequired();
            b.HasIndex(p => p.Key).IsUnique();

            b.Property(p => p.Value).HasMaxLength(1024).IsRequired();
            b.Property(p => p.ValueType).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(p => p.Description).HasMaxLength(512);
            b.Property(p => p.Group).HasMaxLength(64).IsRequired();
            b.HasIndex(p => p.Group);
        });

        return mb;
    }
}
