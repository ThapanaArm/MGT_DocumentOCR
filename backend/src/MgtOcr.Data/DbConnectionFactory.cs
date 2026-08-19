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
        await conn.OpenAsync(ct);
        return conn;
    }
}
