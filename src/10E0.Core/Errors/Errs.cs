using TenE0.Core.Abstractions;

namespace TenE0.Core.Errors;

/// <summary>
/// IErrs 默认实现。线程不安全 — Scoped 生命周期内每个请求独立实例。
/// </summary>
internal sealed class Errs : IErrs
{
    private readonly List<ErrorEntry> _entries = [];

    public bool IsValid => _entries.Count == 0;

    public IReadOnlyList<ErrorEntry> Entries => _entries;

    public IReadOnlyList<string> Keys =>
        _entries.Where(e => e.Key is not null).Select(e => e.Key!).Distinct().ToList();

    public void Add(string message, string? key = null, string? code = null)
        => _entries.Add(new ErrorEntry(message, key, code));

    public string? GetFirstError() => _entries.Count == 0 ? null : _entries[0].Message;

    public void Clear() => _entries.Clear();
}
