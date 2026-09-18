using Microsoft.Data.SqlClient;
using MgtOcr.Core.Config;

namespace MgtOcr.Data;

// Ported from app/db.py. Python opens a fresh pyodbc connection per operation and relies on
// pyodbc.pooling; Microsoft.Data.SqlClient pools connections per connection-string automatically
// under the hood, so the same "open per operation, let the pool absorb the cost" pattern is safe here.
public class DbConnectionFactory(AppConfig config)
{
    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(config.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Microsoft.Data.SqlClient quirk: when the *connect timeout* elapses on the async path,
            // OpenAsync throws TaskCanceledException instead of SqlException -2. Nothing downstream
            // recognises that as transient, so a plain "can't reach the SQL Server" surfaced as a
            // bare "A task was canceled." and skipped the read-retry entirely. Translate it into the
            // TimeoutException it actually is, so Db.RetryReadAsync retries and the message is honest.
            await conn.DisposeAsync();
            throw new TimeoutException(
                $"Timed out connecting to SQL Server '{conn.DataSource}' " +
                $"(Connect Timeout {conn.ConnectionTimeout}s). The server may be unreachable, " +
                "asleep, or blocked by a firewall.");
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
        return conn;
    }
}
