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
            // Get current save file path
            var savePath = GetCurrentSavePath();
            BannerBrosModule.LogMessage($"[SaveTransfer] HOST: Found save file: {savePath ?? "NULL"}");

            if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath))
            {
                BannerBrosModule.LogMessage("[SaveTransfer] HOST ERROR: No save file found to transfer!");
                return;
            }

            BannerBrosModule.LogMessage($"[SaveTransfer] HOST: Starting transfer of {savePath}");
            SendSaveFile(peerId, savePath);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveTransfer] HOST ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the path to the currently loaded save file.
    /// </summary>
    private string? GetCurrentSavePath()
    {
        try
        {
            // Bannerlord save files are typically in Documents/Mount and Blade II Bannerlord/Game Saves/Native
            var savesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord",
                "Game Saves",
                "Native"
            );

            if (!Directory.Exists(savesDir))
            {
                BannerBrosModule.LogMessage($"Save directory not found: {savesDir}");
                return null;
            }

            // Find the most recently modified save file
            // In a real implementation, we'd track which save is currently loaded
            var saveFiles = Directory.GetFiles(savesDir, "*.sav");
            if (saveFiles.Length == 0)
            {
                BannerBrosModule.LogMessage("No save files found");
                return null;
            }

            // For now, use the most recent save file
            // TODO: Track actual loaded save name
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

            return mostRecent;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error finding save file: {ex.Message}");
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

            // Write to temp file in save directory
            var savesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord",
                "Game Saves",
                "Native"
            );

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
