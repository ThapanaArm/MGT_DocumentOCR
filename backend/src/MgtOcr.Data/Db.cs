using System.ComponentModel;
using System.Linq;
using Dapper;
using Microsoft.Data.SqlClient;

namespace MgtOcr.Data;

// Thin data helpers (query/query_one/execute/insert/ping). READ calls auto-retry on transient
// failures (connection drop / timeout / throttling) because the SQL Server is reached over a public
// IP and the odd blip is normal; WRITE calls are NOT auto-retried, to avoid a double INSERT/UPDATE
// when a command actually ran but the connection dropped before its acknowledgement.
public class Db(DbConnectionFactory factory)
{
    // SQL error numbers worth retrying: -2 = timeout; the rest are connection-reset / failover /
    // throttling codes. A transport-level connect failure surfaces as a Win32Exception (handled below).
    private static readonly HashSet<int> TransientErrors =
    [
        -2, 20, 64, 121, 233, 1205, 10053, 10054, 10060,
        10928, 10929, 40197, 40501, 40613, 49918, 49919, 49920, 4060, 40143, 40540,
    ];

    private static bool IsTransient(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is Win32Exception) return true;        // "host failed to respond" — transport level
            if (e is TimeoutException) return true;
            if (e is SqlException sql &&
                sql.Errors.Cast<SqlError>().Any(er => TransientErrors.Contains(er.Number)))
                return true;
        }
        return false;
    }

    private static async Task<T> RetryReadAsync<T>(Func<Task<T>> op, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try { return await op(); }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                // short backoff, then a fresh connection on the next attempt
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
            }
        }
    }

    public Task<IEnumerable<dynamic>> QueryAsync(string sql, object? param = null, CancellationToken ct = default) =>
        RetryReadAsync(async () =>
        {
            await using var conn = await factory.OpenAsync(ct);
            return await conn.QueryAsync(sql, param);
        }, ct);

    public Task<dynamic?> QueryOneAsync(string sql, object? param = null, CancellationToken ct = default) =>
        RetryReadAsync<dynamic?>(async () =>
        {
            await using var conn = await factory.OpenAsync(ct);
            return await conn.QueryFirstOrDefaultAsync(sql, param);
        }, ct);

    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync(sql, param);
    }

    // Mirrors insert_returning_id(): caller's SQL ends with "; SELECT SCOPE_IDENTITY();".
    public async Task<int> InsertReturningIdAsync(string sql, object? param = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<decimal>(sql, param);
        return (int)id;
    }

    public Task<(string Db, string Usr, string Srv)> PingAsync(CancellationToken ct = default) =>
        RetryReadAsync(async () =>
        {
            await using var conn = await factory.OpenAsync(ct);
            var row = await conn.QueryFirstAsync(
                "SELECT DB_NAME() AS db, SUSER_NAME() AS usr, @@SERVERNAME AS srv");
            return ((string)row.db, (string)row.usr, (string)row.srv);
        }, ct);

    // For multi-statement transactions that must commit together — caller owns the connection, so
    // this is not auto-retried.
    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default) => await factory.OpenAsync(ct);
}
