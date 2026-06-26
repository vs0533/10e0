namespace TenE0.Core.ImportExport;

/// <summary>
/// 导入模板生成器抽象。
///
/// <para>根据实体的列映射（attribute / fluent）生成空白导入模板：表头行 + 示例行 + 列级数据校验。
/// 前端引导用户按模板填写，减少导入期的类型/必填错误。</para>
/// <para>默认实现 <see cref="ClosedXml.ClosedXmlTemplateGenerator"/> 输出 .xlsx（ClosedXML 支持列校验）。</para>
/// </summary>
public interface IImportTemplateGenerator
{
    /// <summary>生成 <typeparamref name="T"/> 的导入模板，写入 <paramref name="output"/>。</summary>
    Task GenerateAsync<T>(
        Stream output,
        CancellationToken ct = default)
        where T : class, new();
}
