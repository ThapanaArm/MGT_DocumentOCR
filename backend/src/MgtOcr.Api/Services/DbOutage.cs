namespace MgtOcr.Api.Services;

// Shared by the background workers (compress / archive / cleanup). When SQL Server can't be reached
// (network drop, server restart: SqlException "transport-level error", Win32 10060 timeout), every
// worker used to log a full ERROR + stack trace on every poll (compression every 10 s), flooding
// the console while the real problem was simply "database unavailable". This classifies that case,
// logs ONE warning when the outage starts and ONE info when it recovers, and backs the poll off
// (10 s -> 20 s -> ... capped at 5 min) instead of hammering a server that is down.
// Any other exception is a real bug and is still logged as an error with its stack trace.
public sealed class DbOutage(string worker, ILogger log)
{
    private int _failures;

    public static bool IsDbUnavailable(Exception e)
    {
        for (var x = e; x != null; x = x.InnerException)
        {
            var name = x.GetType().FullName ?? "";
            if (name is "Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException") return true;
            if (x is System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception or TimeoutException) return true;
        }
        return false;
    }

    /// <summary>Handle a batch failure. Returns the extra delay to wait before the next poll.</summary>
    public TimeSpan Failed(Exception e, string what)
    {
        if (!IsDbUnavailable(e))
        {
            log.LogError(e, "{What} failed", what);
            return TimeSpan.Zero;
        }
        _failures++;
        if (_failures == 1)
            log.LogWarning("{Worker}: database unavailable ({Msg}) - pausing and retrying with back-off",
                worker, e.GetBaseException().Message);
        var seconds = Math.Min(300, 10 * Math.Pow(2, Math.Min(_failures - 1, 5)));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Call after a batch that reached the database successfully.</summary>
    public void Succeeded()
    {
        if (_failures > 0)
            log.LogInformation("{Worker}: database reachable again after {N} failed attempt(s)", worker, _failures);
        _failures = 0;
    }
}
