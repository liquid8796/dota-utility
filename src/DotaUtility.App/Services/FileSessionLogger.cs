using System.Globalization;
using System.IO;
using DotaUtility.Domain;

namespace DotaUtility.App.Services;

public sealed class FileSessionLogger : ISessionLogger
{
    private readonly object _gate = new();
    private string? _lastSnapshotSignature;

    public FileSessionLogger(string? logPath = null)
    {
        LogPath = logPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DotaUtility",
            "logs",
            "session.log");

        var directory = Path.GetDirectoryName(LogPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string LogPath { get; }

    public void Log(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {Sanitize(message)}");
    }

    public void LogSnapshot(VisibilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var signature = string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.State}|{snapshot.IsAttached}|{snapshot.ProcessId}|{snapshot.LocalTeam}|{snapshot.EnemyTeam}|{snapshot.RawVisibilityMask}|{snapshot.HeroAddress}|{snapshot.Detail}");

        lock (_gate)
        {
            if (string.Equals(signature, _lastSnapshotSignature, StringComparison.Ordinal))
            {
                return;
            }

            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | state={snapshot.State} attached={snapshot.IsAttached} pid={snapshot.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-"} local={snapshot.LocalTeam?.ToString(CultureInfo.InvariantCulture) ?? "-"} enemy={snapshot.EnemyTeam?.ToString(CultureInfo.InvariantCulture) ?? "-"} mask={FormatMask(snapshot.RawVisibilityMask)} hero={FormatAddress(snapshot.HeroAddress)} | {Sanitize(snapshot.Detail)}{Environment.NewLine}");
            // A failed write must not mark this state as logged: retry on the
            // next identical snapshot once storage becomes available.
            _lastSnapshotSignature = signature;
        }
    }

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }

    // Keep an untrusted exception/diagnostic message from injecting extra log
    // records or terminal control sequences into the human-readable log.
    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                destination[index] = char.IsControl(source[index]) ? ' ' : source[index];
            }
        });
    }

    private static string FormatMask(int? mask) => mask is int value ? $"0x{unchecked((uint)value):X8}" : "-";

    private static string FormatAddress(ulong? address) => address is ulong value ? $"0x{value:X16}" : "-";
}
