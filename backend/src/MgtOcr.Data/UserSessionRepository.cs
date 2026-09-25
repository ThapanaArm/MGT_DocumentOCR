using System.Text.RegularExpressions;
using MgtOcr.Core.Config;

namespace MgtOcr.Data;

// MGT_Datawarehouse.dbo.Ms_UserSession — the one browser/device each user may use PER SYSTEM
// (see sql/26_user_session.sql). OCR's rows are AppName = 'OCR'. "Newest device wins": ClaimAsync
// overwrites. Central table (next to Ms_User) so every company system can share one standard.
public partial class UserSessionRepository(Db db, AppConfig config)
{
    public const string AppName = "OCR";

    // Same rule as UserRepository: the database name is config, validated as a plain identifier.
    private string UserDb => SafeIdentifier().IsMatch(config.UserDatabase)
        ? config.UserDatabase
        : throw new InvalidOperationException($"Auth:UserDatabase is not a valid SQL identifier: '{config.UserDatabase}'");

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex SafeIdentifier();

    private static int Uid(string userId) => int.TryParse(userId, out var id)
        ? id
        : throw new InvalidOperationException($"UserID is not an int: '{userId}'");

    // Take over: this browser becomes the user's only allowed device for OCR.
    public Task<int> ClaimAsync(string userId, string sessionId, string? userAgent, string? ip) =>
        db.ExecuteAsync($"""
            MERGE {UserDb}.dbo.Ms_UserSession WITH (HOLDLOCK) AS t
            USING (SELECT @uid AS UserID, @app AS AppName) AS s ON t.UserID = s.UserID AND t.AppName = s.AppName
            WHEN MATCHED THEN UPDATE SET SessionId=@sessionId, UserAgent=@userAgent, IpAddress=@ip, ClaimedAt=SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT(UserID,AppName,SessionId,UserAgent,IpAddress) VALUES(@uid,@app,@sessionId,@userAgent,@ip);
            """, new { uid = Uid(userId), app = AppName, sessionId, userAgent = Trunc(userAgent, 400), ip = Trunc(ip, 64) });

    // The user's active OCR session id. No row yet (first request after this shipped) → the caller's
    // session is registered and returned, so nobody gets kicked by the rollout.
    public async Task<string> GetOrRegisterAsync(string userId, string sessionId, string? userAgent, string? ip)
    {
        await using var conn = await db.OpenAsync();
        return await Dapper.SqlMapper.ExecuteScalarAsync<string>(conn, $"""
            IF NOT EXISTS(SELECT 1 FROM {UserDb}.dbo.Ms_UserSession WITH (UPDLOCK, HOLDLOCK) WHERE UserID=@uid AND AppName=@app)
                INSERT {UserDb}.dbo.Ms_UserSession(UserID,AppName,SessionId,UserAgent,IpAddress) VALUES(@uid,@app,@sessionId,@userAgent,@ip);
            SELECT SessionId FROM {UserDb}.dbo.Ms_UserSession WHERE UserID=@uid AND AppName=@app;
            """, new { uid = Uid(userId), app = AppName, sessionId, userAgent = Trunc(userAgent, 400), ip = Trunc(ip, 64) }) ?? sessionId;
    }

    private static string? Trunc(string? s, int n) => s is null ? null : (s.Length <= n ? s : s[..n]);
}
