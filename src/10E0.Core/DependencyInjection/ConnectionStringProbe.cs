using System.Diagnostics.CodeAnalysis;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 连接串 → <see cref="DatabaseProvider"/> 启发式探测器（issue #160）。
///
/// <para>
/// Core 本身不引用 SqlServer / Npgsql / Sqlite 包 —— provider 的 <c>UseXxx</c> 扩展方法
/// 必须在 app 层调用。本类只负责"连接串长什么样"，把结果交回给调用方让其挂上对应 provider，
/// 让框架保持 provider-agnostic（与 Microsoft 自身的 <c>AddDbContext</c> 设计一致）。
/// </para>
///
/// <para><b>探测规则</b>（顺序敏感，命中即返回）：</para>
/// <list type="bullet">
/// <item><c>Server=</c> / <c>Data Source=</c> + 含 <c>;</c> 分号分隔的多段（典型 SQL Server ADO.NET 串）→ <see cref="DatabaseProvider.SqlServer"/></item>
/// <item><c>Host=</c> + <c>Port=5432</c>，或 <c>UserID=</c> 风格（Npgsql）→ <see cref="DatabaseProvider.PostgreSQL"/></item>
/// <item><c>Data Source=</c> + <c>.db</c> / <c>.sqlite</c>，或含 <c>Mode=Memory</c> → <see cref="DatabaseProvider.SQLite"/></item>
/// </list>
/// <para>均不命中时抛 <see cref="InvalidOperationException"/>，提示显式传 <see cref="DatabaseProvider"/>。</para>
/// </summary>
public static class ConnectionStringProbe
{
    /// <summary>
    /// 按启发式规则探测连接串对应的 <see cref="DatabaseProvider"/>。
    /// </summary>
    /// <param name="connectionString">待探测的连接串。</param>
    /// <param name="provider">探测结果。</param>
    /// <returns>能否识别。识别失败时 <paramref name="provider"/> 为 <c>null</c>。</returns>
    public static bool TryDetect(string? connectionString, [NotNullWhen(true)] out DatabaseProvider? provider)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            provider = null;
            return false;
        }

        // 用.OrdinalIgnoreCase 的 key=value 解析：连接串约定分号分隔，去空格。
        var dict = ParseKeyValue(connectionString);

        // PostgreSQL：标准 Npgsql 串常用 Port=5432，且 UserID=（区别于 SQL Server 的 User ID=）。
        if (dict.TryGetValue("host", out _) &&
            (dict.TryGetValue("port", out var port) && port == "5432"))
        {
            provider = DatabaseProvider.PostgreSQL;
            return true;
        }
        if (dict.ContainsKey("userid") && !dict.ContainsKey("user id"))
        {
            provider = DatabaseProvider.PostgreSQL;
            return true;
        }

        // SQLite：Data Source 指向文件或内存。
        if (dict.TryGetValue("data source", out var ds))
        {
            if (ds.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                ds.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                ds.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase) ||
                ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                provider = DatabaseProvider.SQLite;
                return true;
            }
        }

        // SQL Server：Server / Data Source + Database 是经典 ADO.NET 串。
        if (dict.ContainsKey("server") || dict.ContainsKey("data source"))
        {
            provider = DatabaseProvider.SqlServer;
            return true;
        }

        provider = null;
        return false;
    }

    /// <summary>
    /// 探测连接串；失败时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <exception cref="InvalidOperationException">无法识别 provider。</exception>
    public static DatabaseProvider Detect(string? connectionString)
    {
        if (TryDetect(connectionString, out var p))
            return p.Value;

        throw new InvalidOperationException(
            "无法从连接串自动探测 database provider，请显式传入 DatabaseProvider（如 opt.Provider = DatabaseProvider.PostgreSQL），" +
            "或用 AddTenE0DataContext<TContext>(connectionString, provider) 重载指定。" +
            $"连接串片段：{Snippet(connectionString)}");
    }

    private static string Snippet(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "<empty>";
        // 仅暴露前缀 + 是否含凭证的提示，避免把整串（可能含密码）写进异常消息。
        var i = s.IndexOf('=');
        return i < 0 ? s : s[..Math.Min(s.Length, i + 8)] + "***";
    }

    private static Dictionary<string, string> ParseKeyValue(string connectionString)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in connectionString.Split(';'))
        {
            var span = raw.AsSpan().Trim();
            if (span.IsEmpty) continue;
            var eq = span.IndexOf('=');
            if (eq <= 0) continue;
            var key = span[..eq].Trim().ToString();
            var val = span[(eq + 1)..].Trim().ToString();
            // 保留首个：典型连接串无重复键；重复时业务侧应显式传 provider。
            dict.TryAdd(key, val);
        }
        return dict;
    }
}
