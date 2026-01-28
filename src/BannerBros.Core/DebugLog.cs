using System;
using System.IO;

namespace BannerBros.Core;

/// <summary>
/// Debug file logger that writes immediately to disk (survives crashes).
/// All BannerBros logging goes here for easy debugging.
/// </summary>
public static class DebugLog
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Mount and Blade II Bannerlord", "Configs");

    private static readonly string ServerLogPath = Path.Combine(LogDir, "BannerBros_Server.log");
    private static readonly string ClientLogPath = Path.Combine(LogDir, "BannerBros_Client.log");

    private static bool _isHost = false;
    private static bool _initialized = false;

    /// <summary>
    /// Initialize the logger. Call this when session starts.
    /// </summary>
    public static void Initialize(bool isHost)
    {
        _isHost = isHost;
        _initialized = true;

        try
        {
            Directory.CreateDirectory(LogDir);

            // Clear old log on new session
            var path = isHost ? ServerLogPath : ClientLogPath;
            File.WriteAllText(path, $"=== BannerBros {(isHost ? "SERVER" : "CLIENT")} Log Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        }
        catch { }
    }

    /// <summary>
    /// Log a message to the appropriate file (server or client).
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            var path = _isHost ? ServerLogPath : ClientLogPath;
            var prefix = _isHost ? "[SERVER]" : "[CLIENT]";
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {prefix} {message}\n";

            // Ensure directory exists
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(path, line);
        }
        catch { }
    }

    /// <summary>
    /// Log to server file specifically.
    /// </summary>
    public static void LogServer(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [SERVER] {message}\n";
            File.AppendAllText(ServerLogPath, line);
        }
        catch { }
    }

    /// <summary>
    /// Log to client file specifically.
    /// </summary>
    public static void LogClient(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [CLIENT] {message}\n";
            File.AppendAllText(ClientLogPath, line);
        }
        catch { }
    }
}
