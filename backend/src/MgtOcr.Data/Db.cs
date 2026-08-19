using Dapper;
using Microsoft.Data.SqlClient;

namespace MgtOcr.Data;

// Ported from app/db.py's query/query_one/execute/insert_returning_id/ping helpers.
// Python's cur.execute(sql, *params) unpacks a tuple positionally against `?` placeholders;
// Dapper does the same against `@p0`/named parameters, so callers pass a DynamicParameters
// or anonymous object instead of a positional tuple — call sites get translated at the same time.
public class Db(DbConnectionFactory factory)
{
    public async Task<IEnumerable<dynamic>> QueryAsync(string sql, object? param = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync(sql, param);
    }

    public async Task<dynamic?> QueryOneAsync(string sql, object? param = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync(sql, param);
    }

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

    public async Task<(string Db, string Usr, string Srv)> PingAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QueryFirstAsync(
            "SELECT DB_NAME() AS db, SUSER_NAME() AS usr, @@SERVERNAME AS srv");
        return ((string)row.db, (string)row.usr, (string)row.srv);
    }

    // For multi-statement transactions (map_document, post_document equivalents) that need
    // several statements to commit together — mirrors Python's `with db.conn() as cx:` blocks.
    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default) => await factory.OpenAsync(ct);
}
