using System.Linq.Expressions;
using System.Reflection;

namespace TenE0.Core.ImportExport.Mapping;

/// <summary>
/// Fluent 映射接口 —— 描述某实体类型的列映射，优先级高于 attribute。
///
/// <para>典型用法（DTO 转换 / 列名与实体属性不一致 / 运行时动态映射）：</para>
/// <code>
/// var mapping = ImportMapping&lt;DemoEntity&gt;.Create(b => b
///     .Map(x => x.Code).ToColumn("编码").ImportOnly()
///     .Map(x => x.Name).ToColumn("名称").Required()
///     .Map(x => x.CreateTime).ToColumn("创建时间").WithFormat("yyyy-MM-dd").ExportOnly());
/// </code>
/// </summary>
public interface IImportMapping
{
    /// <summary>映射的目标实体类型。</summary>
    Type EntityType { get; }

    /// <summary>解析后的列映射（已合并 fluent 声明）。</summary>
    IReadOnlyList<ColumnMap> Columns { get; }
}

/// <summary>
/// 单属性的 fluent 配置器，链式调用累积列映射参数。
/// </summary>
public sealed class ColumnConfig<T, TProperty>
{
    private readonly ImportMapping<T> _owner;
    private readonly PropertyInfo _property;

    private string? _columnName;
    private int _exportOrder = int.MaxValue;
    private string? _format;
    private bool _importable = true;
    private bool _exportable = true;
    private bool _required;

    internal ColumnConfig(ImportMapping<T> owner, PropertyInfo property)
    {
        _owner = owner;
        _property = property;
    }

    /// <summary>绑定到列名（表头名）。</summary>
    public ColumnConfig<T, TProperty> ToColumn(string columnName)
    {
        _columnName = columnName;
        return this;
    }

    /// <summary>设置导出顺序（升序）。</summary>
    public ColumnConfig<T, TProperty> WithOrder(int order)
    {
        _exportOrder = order;
        return this;
    }

    /// <summary>设置值格式串。</summary>
    public ColumnConfig<T, TProperty> WithFormat(string format)
    {
        _format = format;
        return this;
    }

    /// <summary>仅参与导入。</summary>
    public ColumnConfig<T, TProperty> ImportOnly()
    {
        _importable = true;
        _exportable = false;
        return this;
    }

    /// <summary>仅参与导出。</summary>
    public ColumnConfig<T, TProperty> ExportOnly()
    {
        _importable = false;
        _exportable = true;
        return this;
    }

    /// <summary>标记为必填。</summary>
    public ColumnConfig<T, TProperty> Required()
    {
        _required = true;
        return this;
    }

    /// <summary>提交本列配置到 owner，并返回 owner 以继续链式配置其它列。</summary>
    public ImportMapping<T> Map<TNextProperty>(Expression<Func<T, TNextProperty>> selector)
    {
        Commit();
        return _owner.Map(selector);
    }

    /// <summary>隐式转回 owner（支持链尾不再显式调 End()）。</summary>
    public static implicit operator ImportMapping<T>(ColumnConfig<T, TProperty> config)
    {
        config.Commit();
        return config._owner;
    }

    internal void Commit()
    {
        if (_owner.Contains(_property)) return; // 已提交过（重复 Commit 幂等）

        _owner.AddColumn(new ColumnMap
        {
            Property = _property,
            ColumnName = _columnName ?? _property.Name,
            ExportOrder = _exportOrder,
            Format = _format,
            Importable = _importable,
            Exportable = _exportable,
            Required = _required,
        });
    }
}

/// <summary>
/// 实体 <typeparamref name="T"/> 的 fluent 映射构造器。
/// </summary>
public sealed class ImportMapping<T> : IImportMapping
{
    private readonly List<ColumnMap> _columns = [];

    private ImportMapping() { }

    /// <summary>实体类型。</summary>
    public Type EntityType => typeof(T);

    /// <summary>已声明的列映射。</summary>
    public IReadOnlyList<ColumnMap> Columns => _columns;

    /// <summary>开始构造一个 <typeparamref name="T"/> 的映射。</summary>
    public static ImportMapping<T> Create() => new();

    /// <summary>从配置委托构造（最常用入口）。</summary>
    public static ImportMapping<T> Create(Func<ImportMapping<T>, ImportMapping<T>> configure)
    {
        var mapping = new ImportMapping<T>();
        configure(mapping);
        return mapping;
    }

    /// <summary>声明一个属性的列映射，返回链式配置器。</summary>
    public ColumnConfig<T, TProperty> Map<TProperty>(Expression<Func<T, TProperty>> selector)
    {
        var property = ResolveProperty(selector);
        return new ColumnConfig<T, TProperty>(this, property);
    }

    internal void AddColumn(ColumnMap map) => _columns.Add(map);
    internal bool Contains(PropertyInfo property) => _columns.Any(c => c.Property == property);

    private static PropertyInfo ResolveProperty<TProperty>(Expression<Func<T, TProperty>> selector)
    {
        if (selector.Body is not MemberExpression { Member: PropertyInfo pi })
            throw new ArgumentException(
                $"映射表达式必须是属性访问：{selector}",
                nameof(selector));
        return pi;
    }
}
