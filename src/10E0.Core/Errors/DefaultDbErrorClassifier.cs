using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TenE0.Core.Errors;

/// <summary>
/// Provider-agnostic <see cref="IDbErrorClassifier"/> that detects the
/// inner provider exception by <c>Type.FullName</c> and pulls the
/// error number / SQLSTATE out of a convention-blessed message format.
///
/// Why type-name dispatch (and not, say, hard package references)?
/// <c>TenE0.Core</c> deliberately stays EF-provider-agnostic so a single
/// build ships against SQL Server, Postgres, and MySQL. The convention
/// the test stand-ins (and the real provider exceptions) follow:
///
/// <list type="bullet">
///   <item>
///     <c>Microsoft.Data.SqlClient.SqlException</c> — message format
///     <c>"SqlException (Number={n}): {provider message}"</c>.
///     Lookup table:
///     <list type="bullet">
///       <item>2627, 2601 → <see cref="DbErrorKind.UniqueKey"/></item>
///       <item>547       → <see cref="DbErrorKind.ForeignKey"/></item>
///       <item>1205      → <see cref="DbErrorKind.Deadlock"/></item>
///     </list>
///   </item>
///   <item>
///     <c>Npgsql.PostgresException</c> — message format
///     <c>"{SqlState}: {provider message}"</c>. Lookup:
///     <list type="bullet">
///       <item>23505 → <see cref="DbErrorKind.UniqueKey"/></item>
///       <item>23503 → <see cref="DbErrorKind.ForeignKey"/></item>
///       <item>40P01 → <see cref="DbErrorKind.Deadlock"/></item>
///     </list>
///   </item>
///   <item>
///     <c>MySqlConnector.MySqlException</c> — message format
///     <c>"MySqlException (Number={n}): {provider message}"</c>:
///     <list type="bullet">
///       <item>1062 → <see cref="DbErrorKind.UniqueKey"/></item>
///       <item>1452 → <see cref="DbErrorKind.ForeignKey"/></item>
///       <item>1213 → <see cref="DbErrorKind.Deadlock"/></item>
///     </list>
///   </item>
/// </list>
///
/// <see cref="DbUpdateConcurrencyException"/> (a subclass of
/// <see cref="DbUpdateException"/>) is detected by CLR type and
/// short-circuits the inner-inspection path.
/// </summary>
public sealed class DefaultDbErrorClassifier : IDbErrorClassifier
{
    // Provider exception type names — kept as constants so a code-search
    // for "SqlException" lands here, not scattered through the mapper.
    private const string SqlServerExceptionType = "Microsoft.Data.SqlClient.SqlException";
    private const string PostgresExceptionType = "Npgsql.PostgresException";
    private const string MySqlExceptionType = "MySqlConnector.MySqlException";

    public DbErrorClassification Classify(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 1. Optimistic-concurrency is a CLR-level concept — check the
        //    subclass before going near the inner exception. Otherwise
        //    a concurrency failure wrapped with a generic inner would
        //    get misclassified as an unknown DB error.
        if (exception is DbUpdateConcurrencyException)
        {
            return new DbErrorClassification(DbErrorKind.Concurrency);
        }

        var inner = exception.InnerException;
        if (inner is null)
        {
            return DbErrorClassification.Unknown();
        }

        // 2. Dispatch on inner.GetType().FullName — stays provider-agnostic
        //    at compile time, matches at runtime against whatever
        //    Microsoft.Data.SqlClient / Npgsql / MySqlConnector the host
        //    happens to load.
        var typeName = inner.GetType().FullName;
        if (typeName == SqlServerExceptionType) return ClassifyBySqlServerNumber(inner.Message);
        if (typeName == PostgresExceptionType) return ClassifyByPostgresSqlState(inner.Message);
        if (typeName == MySqlExceptionType) return ClassifyByMySqlNumber(inner.Message);

        // 3. Fallback: provider-shaped messages can be detected by the
        //    message prefix even when the inner exception is a stand-in
        //    (e.g. unit-test proxies that don't carry the real provider
        //    type). The prefix is a stable convention the test stand-ins
        //    use, so dispatching on it keeps the seam DB-agnostic without
        //    requiring a hard package reference. If the message shape is
        //    unrecognised we return Other — never throw.
        //
        //    We also probe inner.ToString() because some stand-ins
        //    (mirroring the real SqlException where Number is a property
        //    rather than a message field) surface the prefix on ToString
        //    only. ToString() for the base Exception is also fine to
        //    call — it never throws and is cheap to construct.
        var messageProbe = inner.Message;
        var toStringProbe = inner.ToString();
        if (HasSqlServerPrefix(messageProbe) || HasSqlServerPrefix(toStringProbe))
            return ClassifyBySqlServerNumber(PickProbeWithPrefix(messageProbe, toStringProbe, "SqlException (Number="));
        if (HasMySqlPrefix(messageProbe) || HasMySqlPrefix(toStringProbe))
            return ClassifyByMySqlNumber(PickProbeWithPrefix(messageProbe, toStringProbe, "MySqlException (Number="));
        if (ExtractPostgresSqlState(messageProbe) is not null)
            return ClassifyByPostgresSqlState(messageProbe);
        if (ExtractPostgresSqlState(toStringProbe) is not null)
            return ClassifyByPostgresSqlState(toStringProbe);

        return DbErrorClassification.Unknown();
    }

    private static bool HasSqlServerPrefix(string text) =>
        text.StartsWith("SqlException (Number=", StringComparison.Ordinal);

    private static bool HasMySqlPrefix(string text) =>
        text.StartsWith("MySqlException (Number=", StringComparison.Ordinal);

    /// <summary>
    /// Returns whichever of the two probes actually starts with
    /// <paramref name="prefix"/>. Lets the downstream extractors read
    /// the number/entity from the same string the dispatch key was found
    /// in — necessary when a stand-in surfaces the prefix on
    /// <c>ToString()</c> only.
    /// </summary>
    private static string PickProbeWithPrefix(string message, string toString, string prefix) =>
        message.StartsWith(prefix, StringComparison.Ordinal) ? message : toString;

    private static DbErrorClassification ClassifyBySqlServerNumber(string message)
    {
        var number = ExtractNumberPrefix(message, "SqlException (Number=");
        if (number is null)
        {
            return DbErrorClassification.Unknown();
        }

        return number switch
        {
            2627 or 2601 => new DbErrorClassification(
                DbErrorKind.UniqueKey,
                EntityName: ExtractSqlServerObjectName(message),
                ConstraintName: ExtractSqlServerConstraintName(message)),
            547 => new DbErrorClassification(
                DbErrorKind.ForeignKey,
                EntityName: ExtractSqlServerObjectName(message),
                ConstraintName: ExtractSqlServerConstraintName(message)),
            1205 => new DbErrorClassification(DbErrorKind.Deadlock),
            _ => DbErrorClassification.Unknown(),
        };
    }

    private static DbErrorClassification ClassifyByPostgresSqlState(string message)
    {
        var sqlState = ExtractPostgresSqlState(message);
        if (sqlState is null)
        {
            return DbErrorClassification.Unknown();
        }

        return sqlState switch
        {
            "23505" => new DbErrorClassification(
                DbErrorKind.UniqueKey,
                EntityName: ExtractPostgresObjectName(message),
                ConstraintName: ExtractPostgresConstraintName(message)),
            "23503" => new DbErrorClassification(
                DbErrorKind.ForeignKey,
                EntityName: ExtractPostgresObjectName(message),
                ConstraintName: ExtractPostgresConstraintName(message)),
            "40P01" => new DbErrorClassification(DbErrorKind.Deadlock),
            _ => DbErrorClassification.Unknown(),
        };
    }

    private static DbErrorClassification ClassifyByMySqlNumber(string message)
    {
        var number = ExtractNumberPrefix(message, "MySqlException (Number=");
        if (number is null)
        {
            return DbErrorClassification.Unknown();
        }

        return number switch
        {
            1062 => new DbErrorClassification(
                DbErrorKind.UniqueKey,
                EntityName: ExtractMySqlTableName(message),
                ConstraintName: ExtractMySqlKeyName(message)),
            1452 => new DbErrorClassification(
                DbErrorKind.ForeignKey,
                EntityName: ExtractMySqlTableName(message),
                ConstraintName: ExtractMySqlKeyName(message)),
            1213 => new DbErrorClassification(DbErrorKind.Deadlock),
            _ => DbErrorClassification.Unknown(),
        };
    }

    // ── Parsing helpers ─────────────────────────────────────────
    // Each provider's message format is stable; we extract the bits the
    // API client cares about (entity / constraint name) with the cheapest
    // possible string ops. Failures are non-fatal: missing data just
    // yields null on the corresponding field.

    private static int? ExtractNumberPrefix(string message, string marker)
    {
        var idx = message.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + marker.Length;
        var end = message.IndexOf(')', start);
        if (end < 0) return null;
        return int.TryParse(message.AsSpan(start, end - start), out var n) ? n : null;
    }

    private static string? ExtractPostgresSqlState(string message)
    {
        // Postgres stand-ins and the real Npgsql.PostgresException both
        // format the message as "{SqlState}: {text}" — the SQLSTATE is
        // always 5 characters, terminated by a colon.
        var colon = message.IndexOf(':');
        if (colon != 5) return null;
        return message[..5];
    }

    private static string? ExtractSqlServerObjectName(string message)
    {
        // "Cannot insert duplicate key row in object 'dbo.TenE0User' ..."
        // or "The INSERT statement conflicted with the FOREIGN KEY constraint 'FK_...'
        //  ... in database '...', table 'dbo.TenE0Org'."
        const string objectMarker = "object '";
        var idx = message.IndexOf(objectMarker, StringComparison.Ordinal);
        if (idx < 0)
        {
            const string tableMarker = "table '";
            idx = message.IndexOf(tableMarker, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += tableMarker.Length;
        }
        else
        {
            idx += objectMarker.Length;
        }

        var end = message.IndexOf('\'', idx);
        return end < 0 ? null : message[idx..end];
    }

    private static string? ExtractSqlServerConstraintName(string message)
    {
        const string marker = "constraint '";
        var idx = message.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += marker.Length;
        var end = message.IndexOf('\'', idx);
        return end < 0 ? null : message[idx..end];
    }

    private static string? ExtractPostgresObjectName(string message)
    {
        // "... table \"Demo\" ..." or "... table \"TenE0User\" ..."
        const string marker = "table \"";
        var idx = message.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += marker.Length;
        var end = message.IndexOf('"', idx);
        return end < 0 ? null : message[idx..end];
    }

    private static string? ExtractPostgresConstraintName(string message)
    {
        const string marker = "constraint \"";
        var idx = message.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += marker.Length;
        var end = message.IndexOf('"', idx);
        return end < 0 ? null : message[idx..end];
    }

    private static string? ExtractMySqlTableName(string message)
    {
        // "`tene0`.`Demo`" or "Duplicate entry 'x' for key 'TenE0User.UX_...'"
        const string tableMarker = "`.`";
        var idx = message.IndexOf(tableMarker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var end = message.IndexOf('`', idx + tableMarker.Length);
        if (end < 0) return null;
        var start = message.LastIndexOf('`', idx) + 1;
        return message[start..end];
    }

    private static string? ExtractMySqlKeyName(string message)
    {
        const string marker = "for key '";
        var idx = message.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += marker.Length;
        var end = message.IndexOf('\'', idx);
        if (end < 0) return null;
        // "TenE0User.UX_TenE0User_UserCode" → keep the part after the dot.
        var raw = message[idx..end];
        var dot = raw.IndexOf('.');
        return dot < 0 ? raw : raw[(dot + 1)..];
    }
}

/// <summary>
/// DI helpers for the DB-error classification layer introduced in #51.
/// Kept in its own static so host applications that need a custom
/// classifier (e.g. one that uses a real Npgsql package reference) can
/// <c>services.Replace(ServiceDescriptor.Singleton&lt;IDbErrorClassifier,
/// MyClassifier&gt;())</c> after calling this extension.
/// </summary>
public static class DbErrorClassificationExtensions
{
    /// <summary>
    /// Register the default <see cref="DefaultDbErrorClassifier"/> as a
    /// singleton (the classifier is pure / stateless). Call this from
    /// <c>Program.cs</c> alongside <c>AddTenE0ExceptionHandler</c>; the
    /// mapper added in the same step takes the classifier through
    /// constructor injection in a later PR.
    /// </summary>
    public static IServiceCollection AddTenE0DbErrorClassifier(this IServiceCollection services)
    {
        services.TryAddSingleton<IDbErrorClassifier, DefaultDbErrorClassifier>();
        return services;
    }
}
