using System;
using System.IO;

namespace BannerBros.Core;

/// <summary>
/// Debug file logger that writes immediately to disk (survives crashes).
/// All BannerBros logging goes here for easy debugging.
/// Supports streaming client logs to server when enabled.
/// </summary>
public static class DebugLog
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Mount and Blade II Bannerlord", "Configs");

    private static readonly string ServerLogPath = Path.Combine(LogDir, "BannerBros_Server.log");
    private static readonly string ClientLogPath = Path.Combine(LogDir, "BannerBros_Client.log");
    private static readonly string RemoteClientLogPath = Path.Combine(LogDir, "BannerBros_ClientRemote.log");

    private static bool _isHost = false;
    private static bool _initialized = false;
    private static bool _streamToServer = false;
    private static int _localPlayerId = 0;
    private static string _localPlayerName = "Client";

    /// <summary>
    /// Callback to send log message to server. Set by BannerBrosModule when streaming is enabled.
    /// </summary>
    public static Action<string, int, string>? SendToServerCallback { get; set; }

    /// <summary>
    /// Initialize the logger. Call this when session starts.
    /// </summary>
    public static void Initialize(bool isHost, bool streamToServer = false, int playerId = 0, string playerName = "Client")
    {
        _isHost = isHost;
        _initialized = true;
        _streamToServer = streamToServer && !isHost; // Only clients stream to server
        _localPlayerId = playerId;
        _localPlayerName = playerName;

        try
        {
            Directory.CreateDirectory(LogDir);

            // Clear old log on new session
            var path = isHost ? ServerLogPath : ClientLogPath;
            File.WriteAllText(path, $"=== BannerBros {(isHost ? "SERVER" : "CLIENT")} Log Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");

            // Clear remote client log on server
            if (isHost)
            {
                File.WriteAllText(RemoteClientLogPath, $"=== BannerBros CLIENT REMOTE Log Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            }

            if (_streamToServer)
            {
                Log("Debug log streaming to server ENABLED");
            }
        }
        catch { }
    }

    /// <summary>
    /// Enable or disable streaming to server (client only).
    /// </summary>
    public static void SetStreamToServer(bool enabled, int playerId, string playerName)
    {
        _streamToServer = enabled && !_isHost;
        _localPlayerId = playerId;
        _localPlayerName = playerName;

        if (_streamToServer)
        {
            Log("Debug log streaming to server ENABLED");
        }
    }

    /// <summary>
    /// Log a message to the appropriate file (server or client).
    /// If streaming is enabled (client), also sends to server.
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

            // Stream to server if enabled
            if (_streamToServer && SendToServerCallback != null)
            {
                try
                {
                    SendToServerCallback(message, _localPlayerId, _localPlayerName);
                }
                catch { } // Don't let network errors break logging
            }
        }
        catch { }
    }

    /// <summary>
    /// Called on server when receiving a log message from a client.
    /// Writes to BannerBros_ClientRemote.log.
    /// </summary>
    public static void LogRemoteClient(string message, int playerId, string playerName, DateTime timestamp)
    {
        if (!_isHost) return; // Only server receives remote logs

        try
        {
            Directory.CreateDirectory(LogDir);
            var line = $"[{timestamp:HH:mm:ss.fff}] [CLIENT:{playerId}:{playerName}] {message}\n";
            File.AppendAllText(RemoteClientLogPath, line);
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
