using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BannerBros.Network;
using LiteNetLib;

namespace BannerBros.Core;

/// <summary>
/// Manages save file transfer between host and clients for single authoritative campaign.
/// Handles chunked transfer of large save files over the network.
/// </summary>
public class SaveFileTransferManager
{
    private const int ChunkSize = 32 * 1024; // 32KB chunks

    private readonly SessionManager _sessionManager;

    // Client-side state for receiving save file
    private string? _pendingSaveFileName;
    private long _expectedTotalSize;
    private int _expectedTotalChunks;
    private string? _expectedChecksum;
    private readonly List<byte[]> _receivedChunks = new();
    private int _receivedChunkCount;

    public bool IsReceiving { get; private set; }
    public float ReceiveProgress => _expectedTotalChunks > 0 ? (float)_receivedChunkCount / _expectedTotalChunks : 0;

    public event Action<string>? OnSaveFileReady; // Fired when save is ready to load (path)
    public event Action<string>? OnTransferError;
    public event Action<float>? OnTransferProgress;

    public SaveFileTransferManager(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public void Initialize()
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null)
        {
            BannerBrosModule.LogMessage("[SaveTransfer] Initialize failed: NetworkManager is null");
            return;
        }

        BannerBrosModule.LogMessage($"[SaveTransfer] Initializing (IsHost: {networkManager.IsHost})");

        // Subscribe to save file transfer events
        networkManager.Messages.OnSaveFileRequestReceived += HandleSaveFileRequest;
        networkManager.Messages.OnSaveFileStartReceived += HandleSaveFileStart;
        networkManager.Messages.OnSaveFileChunkReceived += HandleSaveFileChunk;
        networkManager.Messages.OnSaveFileCompleteReceived += HandleSaveFileComplete;

        BannerBrosModule.LogMessage("[SaveTransfer] Event handlers registered");
    }

    public void Cleanup()
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null) return;

        networkManager.Messages.OnSaveFileRequestReceived -= HandleSaveFileRequest;
        networkManager.Messages.OnSaveFileStartReceived -= HandleSaveFileStart;
        networkManager.Messages.OnSaveFileChunkReceived -= HandleSaveFileChunk;
        networkManager.Messages.OnSaveFileCompleteReceived -= HandleSaveFileComplete;

        ResetReceiveState();
    }

    /// <summary>
    /// Client requests save file from host.
    /// </summary>
    public void RequestSaveFile(int playerId)
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null)
        {
            BannerBrosModule.LogMessage("[SaveTransfer] REQUEST FAILED: NetworkManager is null");
            return;
        }
        if (networkManager.IsHost)
        {
            BannerBrosModule.LogMessage("[SaveTransfer] REQUEST SKIPPED: We are the host");
            return;
        }

        BannerBrosModule.LogMessage($"[SaveTransfer] CLIENT -> HOST: Requesting save file (PlayerId: {playerId})");

        var packet = new SaveFileRequestPacket { PlayerId = playerId };
        networkManager.SendToServer(packet);

        BannerBrosModule.LogMessage("[SaveTransfer] Save file request packet sent");
    }

    /// <summary>
    /// Host handles request and sends save file.
    /// </summary>
    private void HandleSaveFileRequest(SaveFileRequestPacket packet, int peerId)
    {
        BannerBrosModule.LogMessage($"[SaveTransfer] HOST: Received save file request from peer {peerId} (PlayerId: {packet.PlayerId})");

        var networkManager = NetworkManager.Instance;
        if (networkManager == null)
        {
            BannerBrosModule.LogMessage("[SaveTransfer] HOST: NetworkManager is null!");
            return;
        }
        if (!networkManager.IsHost)
        {
            BannerBrosModule.LogMessage("[SaveTransfer] HOST: We are not the host, ignoring request");
            return;
        }

        try
        {
            // PAUSE THE GAME while client loads - this keeps worlds synced!
            // The client may need to manually load, which takes time
            BannerBrosModule.LogMessage("[SaveTransfer] HOST: *** PAUSING WHILE CLIENT LOADS ***");
            Patches.TimeControlPatches.IsWaitingForClientLoad = true;

            // Pause campaign time
            try
            {
                var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
                if (campaign != null)
                {
                    campaign.TimeControlMode = TaleWorlds.CampaignSystem.CampaignTimeControlMode.Stop;
                    campaign.SetTimeSpeed(0);
                }
            }
            catch { }

            // Auto-save before transferring to ensure client gets fresh state
            BannerBrosModule.LogMessage("[SaveTransfer] HOST: Auto-saving before transfer...");
            TriggerQuickSave();

            // Small delay to ensure save completes (save is async)
            System.Threading.Thread.Sleep(500);

            // Get current save file path
            var savePath = GetCurrentSavePath();
            BannerBrosModule.LogMessage($"[SaveTransfer] HOST: Found save file: {savePath ?? "NULL"}");

            if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath))
            {
                BannerBrosModule.LogMessage("[SaveTransfer] HOST ERROR: No save file found to transfer!");
                BannerBrosModule.LogMessage("[SaveTransfer] HOST: Please save your game manually, then have client reconnect");
                // Resume time since transfer failed
                Patches.TimeControlPatches.IsWaitingForClientLoad = false;
                return;
            }

            BannerBrosModule.LogMessage($"[SaveTransfer] HOST: Starting transfer of {savePath}");
            BannerBrosModule.LogMessage("[SaveTransfer] HOST: *** GAME PAUSED - Waiting for client to load save ***");
            SendSaveFile(peerId, savePath);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveTransfer] HOST ERROR: {ex.Message}");
            // Resume time on error
            Patches.TimeControlPatches.IsWaitingForClientLoad = false;
        }
    }

    /// <summary>
    /// Triggers a quicksave on the host to ensure fresh save for transfer.
    /// Waits for the save to complete before returning.
    /// </summary>
    private void TriggerQuickSave()
    {
        try
        {
            var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
            if (campaign == null)
            {
                BannerBrosModule.LogMessage("[SaveTransfer] Cannot quicksave: No active campaign");
                return;
            }

            // Get timestamp of most recent save before triggering new one
            var savesBefore = GetRecentSaveTimestamp();
            BannerBrosModule.LogMessage($"[SaveTransfer] Most recent save before quicksave: {savesBefore:HH:mm:ss}");

            bool triggered = false;

            // Try Campaign.Current.SaveHandler.QuickSaveCurrentGame() via reflection
            var saveHandler = campaign.GetType().GetProperty("SaveHandler")?.GetValue(campaign);
            if (saveHandler != null)
            {
                var quickSaveMethod = saveHandler.GetType().GetMethod("QuickSaveCurrentGame");
                if (quickSaveMethod != null)
                {
                    quickSaveMethod.Invoke(saveHandler, null);
                    BannerBrosModule.LogMessage("[SaveTransfer] Quicksave triggered via SaveHandler");
                    triggered = true;
                }
            }

            // Alternative: Try CampaignSaveSystemClass (older API)
            if (!triggered)
            {
                var saveSystemType = typeof(TaleWorlds.CampaignSystem.Campaign).Assembly
                    .GetType("TaleWorlds.CampaignSystem.CampaignSaveSystem");
                if (saveSystemType != null)
                {
                    var quickSaveMethod = saveSystemType.GetMethod("QuickSave",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (quickSaveMethod != null)
                    {
                        quickSaveMethod.Invoke(null, null);
                        BannerBrosModule.LogMessage("[SaveTransfer] Quicksave triggered via CampaignSaveSystem");
                        triggered = true;
                    }
                }
            }

            if (!triggered)
            {
                BannerBrosModule.LogMessage("[SaveTransfer] WARNING: Could not trigger quicksave - using existing save");
                return;
            }

            // Wait for the save to complete (poll for new file)
            BannerBrosModule.LogMessage("[SaveTransfer] Waiting for quicksave to complete...");
            for (int i = 0; i < 20; i++) // Wait up to 10 seconds
            {
                System.Threading.Thread.Sleep(500);
                var savesAfter = GetRecentSaveTimestamp();
                if (savesAfter > savesBefore)
                {
                    BannerBrosModule.LogMessage($"[SaveTransfer] Quicksave completed at {savesAfter:HH:mm:ss}");
                    return;
                }
            }

            BannerBrosModule.LogMessage("[SaveTransfer] WARNING: Quicksave may not have completed - proceeding anyway");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveTransfer] Quicksave error (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the timestamp of the most recently modified save file.
    /// </summary>
    private DateTime GetRecentSaveTimestamp()
    {
        try
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var possibleDirs = new[]
            {
                Path.Combine(documentsPath, "Mount and Blade II Bannerlord", "Game Saves", "Native"),
                Path.Combine(documentsPath, "Mount and Blade II Bannerlord", "Game Saves"),
            };

            DateTime newest = DateTime.MinValue;
            foreach (var dir in possibleDirs)
            {
                if (!Directory.Exists(dir)) continue;
                var files = Directory.GetFiles(dir, "*.sav");
                foreach (var file in files)
                {
                    var modified = File.GetLastWriteTime(file);
                    if (modified > newest) newest = modified;
                }
            }
            return newest;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Gets the path to the currently loaded save file.
    /// </summary>
    private string? GetCurrentSavePath()
    {
        try
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            BannerBrosModule.LogMessage($"[SaveTransfer] Documents path: {documentsPath}");

            // Try multiple possible save locations
            var possibleDirs = new[]
            {
                Path.Combine(documentsPath, "Mount and Blade II Bannerlord", "Game Saves", "Native"),
                Path.Combine(documentsPath, "Mount and Blade II Bannerlord", "Game Saves"),
                // Steam cloud saves location
                Path.Combine(documentsPath, "Mount and Blade II Bannerlord", "Game Saves", "SteamCloud"),
            };

            foreach (var savesDir in possibleDirs)
            {
                BannerBrosModule.LogMessage($"[SaveTransfer] Checking: {savesDir}");

                if (!Directory.Exists(savesDir))
                {
                    BannerBrosModule.LogMessage($"[SaveTransfer] Directory does not exist");
                    continue;
                }

                var saveFiles = Directory.GetFiles(savesDir, "*.sav");
                BannerBrosModule.LogMessage($"[SaveTransfer] Found {saveFiles.Length} .sav files");

                if (saveFiles.Length == 0)
                    continue;

                // Find the most recent save file
                string? mostRecent = null;
                DateTime mostRecentTime = DateTime.MinValue;

                foreach (var file in saveFiles)
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime > mostRecentTime)
                    {
                        mostRecentTime = info.LastWriteTime;
                        mostRecent = file;
                    }
                }

                if (mostRecent != null)
                {
                    var age = DateTime.Now - mostRecentTime;
                    BannerBrosModule.LogMessage($"[SaveTransfer] Most recent save: {Path.GetFileName(mostRecent)}");
                    BannerBrosModule.LogMessage($"[SaveTransfer] Last modified: {mostRecentTime} ({age.TotalMinutes:F0} minutes ago)");

                    // Warn if save is old
                    if (age.TotalMinutes > 30)
                    {
                        BannerBrosModule.LogMessage("[SaveTransfer] WARNING: Save file is over 30 minutes old!");
                        BannerBrosModule.LogMessage("[SaveTransfer] TIP: Save your game before clients join");
                    }

                    return mostRecent;
                }
            }

            BannerBrosModule.LogMessage("[SaveTransfer] ERROR: No save files found in any location!");
            BannerBrosModule.LogMessage("[SaveTransfer] Please save your game first, then have the client reconnect.");
            return null;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveTransfer] Error finding save file: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Host sends save file to a specific peer.
    /// </summary>
    private void SendSaveFile(int peerId, string savePath)
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null) return;

        try
        {
            var fileInfo = new FileInfo(savePath);
            var fileData = File.ReadAllBytes(savePath);
            var checksum = ComputeChecksum(fileData);
            var totalChunks = (int)Math.Ceiling((double)fileData.Length / ChunkSize);

            BannerBrosModule.LogMessage($"[SaveTransfer] HOST -> CLIENT: Sending {fileInfo.Name} ({fileData.Length} bytes, {totalChunks} chunks)");

            // Send start packet
            var startPacket = new SaveFileStartPacket
            {
                SaveFileName = fileInfo.Name,
                TotalSize = fileData.Length,
                TotalChunks = totalChunks,
                SaveChecksum = checksum
            };
            networkManager.SendTo(peerId, startPacket, DeliveryMethod.ReliableOrdered);

            // Send chunks
            for (int i = 0; i < totalChunks; i++)
            {
                var offset = i * ChunkSize;
                var length = Math.Min(ChunkSize, fileData.Length - offset);
                var chunkData = new byte[length];
                Array.Copy(fileData, offset, chunkData, 0, length);

                var chunkPacket = new SaveFileChunkPacket
                {
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    Data = chunkData,
                    DataLength = length
                };
                networkManager.SendTo(peerId, chunkPacket, DeliveryMethod.ReliableOrdered);
            }

            // Send complete packet
            var completePacket = new SaveFileCompletePacket
            {
                SaveFileName = fileInfo.Name,
                SaveChecksum = checksum
            };
            networkManager.SendTo(peerId, completePacket, DeliveryMethod.ReliableOrdered);

            BannerBrosModule.LogMessage("Save file transfer complete");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error sending save file: {ex.Message}");
        }
    }

    /// <summary>
    /// Client handles save file start.
    /// </summary>
    private void HandleSaveFileStart(SaveFileStartPacket packet)
    {
        BannerBrosModule.LogMessage($"[SaveTransfer] CLIENT: Received START - {packet.SaveFileName} ({packet.TotalSize} bytes, {packet.TotalChunks} chunks)");

        ResetReceiveState();

        _pendingSaveFileName = packet.SaveFileName;
        _expectedTotalSize = packet.TotalSize;
        _expectedTotalChunks = packet.TotalChunks;
        _expectedChecksum = packet.SaveChecksum;
        IsReceiving = true;

        // Pre-allocate list
        _receivedChunks.Clear();
        for (int i = 0; i < _expectedTotalChunks; i++)
        {
            _receivedChunks.Add(Array.Empty<byte>());
        }
    }

    /// <summary>
    /// Client handles save file chunk.
    /// </summary>
    private void HandleSaveFileChunk(SaveFileChunkPacket packet)
    {
        if (!IsReceiving || packet.ChunkIndex >= _receivedChunks.Count) return;

        _receivedChunks[packet.ChunkIndex] = packet.Data;
        _receivedChunkCount++;

        var progress = (float)_receivedChunkCount / _expectedTotalChunks;
        OnTransferProgress?.Invoke(progress);

        if (_receivedChunkCount % 10 == 0 || _receivedChunkCount == _expectedTotalChunks)
        {
            BannerBrosModule.LogMessage($"[SaveTransfer] CLIENT: Progress {_receivedChunkCount}/{_expectedTotalChunks} ({progress * 100:F0}%)");
        }
    }

    /// <summary>
    /// Client handles save file complete.
    /// </summary>
    private void HandleSaveFileComplete(SaveFileCompletePacket packet)
    {
        BannerBrosModule.LogMessage($"[SaveTransfer] CLIENT: Received COMPLETE signal for {packet.SaveFileName}");

        if (!IsReceiving)
        {
            BannerBrosModule.LogMessage("[SaveTransfer] CLIENT: WARNING - Not in receiving state!");
            return;
        }

        BannerBrosModule.LogMessage("[SaveTransfer] CLIENT: Verifying checksum...");

        try
        {
            // Combine all chunks
            var totalSize = 0;
            foreach (var chunk in _receivedChunks)
            {
                totalSize += chunk.Length;
            }

            var fileData = new byte[totalSize];
            var offset = 0;
            foreach (var chunk in _receivedChunks)
            {
                Array.Copy(chunk, 0, fileData, offset, chunk.Length);
                offset += chunk.Length;
            }

            // Verify checksum
            var checksum = ComputeChecksum(fileData);
            if (checksum != _expectedChecksum)
            {
                BannerBrosModule.LogMessage($"[SaveTransfer] CLIENT ERROR: Checksum mismatch! Expected: {_expectedChecksum}, Got: {checksum}");
                OnTransferError?.Invoke("Save file corrupted during transfer");
                ResetReceiveState();
                return;
            }

            BannerBrosModule.LogMessage("[SaveTransfer] CLIENT: Checksum verified OK");

            // Write to save directory - use the main Game Saves folder (not Native subfolder)
            // The game's internal save list scans Game Saves\, not Game Saves\Native\
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var savesDir = Path.Combine(documentsPath, "Mount and Blade II Bannerlord", "Game Saves");
            BannerBrosModule.LogMessage($"[SaveTransfer] CLIENT: Using save directory: {savesDir}");

            Directory.CreateDirectory(savesDir);

            // Use a special name to indicate this is a co-op save
            var coopSaveName = $"CoOp_{_pendingSaveFileName}";
            var savePath = Path.Combine(savesDir, coopSaveName);

            File.WriteAllBytes(savePath, fileData);

            BannerBrosModule.LogMessage($"[SaveTransfer] CLIENT: Save file written to: {savePath}");
            BannerBrosModule.LogMessage("[SaveTransfer] CLIENT: *** TRANSFER COMPLETE ***");
            BannerBrosModule.LogMessage("[SaveTransfer] CLIENT: Please load this save from the Load Game menu");

            // Notify that save is ready
            OnSaveFileReady?.Invoke(savePath);

            // Send confirmation to host
            var networkManager = NetworkManager.Instance;
            if (networkManager != null)
            {
                BannerBrosModule.LogMessage("[SaveTransfer] CLIENT -> HOST: Sending confirmation");
                var confirmPacket = new SaveFileReceivedPacket
                {
                    PlayerId = networkManager.LocalPeerId,
                    Success = true
                };
                networkManager.SendToServer(confirmPacket);
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveTransfer] CLIENT ERROR: {ex.Message}");
            OnTransferError?.Invoke($"Error processing save file: {ex.Message}");
        }
        finally
        {
            ResetReceiveState();
        }
    }

    private void ResetReceiveState()
    {
        IsReceiving = false;
        _pendingSaveFileName = null;
        _expectedTotalSize = 0;
        _expectedTotalChunks = 0;
        _expectedChecksum = null;
        _receivedChunks.Clear();
        _receivedChunkCount = 0;
    }

    private static string ComputeChecksum(byte[] data)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(data);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
