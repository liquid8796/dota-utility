using DotaUtility.Domain;

namespace DotaUtility.App.Services;

public interface ISessionLogger
{
    string LogPath { get; }

    void Log(string message);

    void LogSnapshot(VisibilitySnapshot snapshot);
}
