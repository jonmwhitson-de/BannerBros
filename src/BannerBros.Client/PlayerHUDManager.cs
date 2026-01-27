using TaleWorlds.Core;
using BannerBros.Core;
using BannerBros.Network;

namespace BannerBros.Client;

/// <summary>
/// Manages HUD elements for showing other players on the campaign map.
/// </summary>
public class PlayerHUDManager
{
    private readonly Dictionary<int, PlayerMapMarker> _playerMarkers = new();
    private float _updateTimer;
    private const float UPDATE_INTERVAL = 0.1f; // Update markers 10 times per second

    public void Update(float dt)
    {
        _updateTimer += dt;
        if (_updateTimer < UPDATE_INTERVAL) return;
        _updateTimer = 0;

        UpdatePlayerMarkers();
    }

    private void UpdatePlayerMarkers()
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return;

        var localPlayerId = module.PlayerManager.LocalPlayerId;

        // ToList() to avoid collection modified exception
        foreach (var player in module.PlayerManager.Players.Values.ToList())
        {
            // Don't show marker for local player
            if (player.NetworkId == localPlayerId) continue;

            if (!_playerMarkers.TryGetValue(player.NetworkId, out var marker))
            {
                marker = CreatePlayerMarker(player);
                _playerMarkers[player.NetworkId] = marker;
            }

            UpdateMarkerPosition(marker, player);
            UpdateMarkerAppearance(marker, player);
        }

        // Remove markers for disconnected players
        var disconnectedIds = _playerMarkers.Keys
            .Where(id => !module.PlayerManager.Players.ContainsKey(id))
            .ToList();

        foreach (var id in disconnectedIds)
        {
            RemovePlayerMarker(id);
        }
    }

    private PlayerMapMarker CreatePlayerMarker(CoopPlayer player)
    {
        // Create visual marker on campaign map
        // This integrates with Bannerlord's map party visualization

        return new PlayerMapMarker
        {
            PlayerId = player.NetworkId,
            PlayerName = player.Name,
            IsVisible = true
        };
    }

    private void UpdateMarkerPosition(PlayerMapMarker marker, CoopPlayer player)
    {
        marker.MapX = player.MapPositionX;
        marker.MapY = player.MapPositionY;

        // Update actual visual position in game
        // Implementation depends on Bannerlord's API
    }

    private void UpdateMarkerAppearance(PlayerMapMarker marker, CoopPlayer player)
    {
        // Update color/icon based on player state
        marker.State = player.State;

        // Colors:
        // - Green: Allied player on map
        // - Yellow: Player in dialogue/trade (protected)
        // - Red: Enemy faction player
        // - Blue: Player in battle
    }

    private void RemovePlayerMarker(int playerId)
    {
        if (_playerMarkers.TryGetValue(playerId, out var marker))
        {
            // Remove visual from game
            _playerMarkers.Remove(playerId);
        }
    }

    public void ShowConnectionStatus(bool connected, int ping)
    {
        // Show connection indicator in corner of screen
    }

    public void ShowPlayerTooltip(int playerId)
    {
        var module = BannerBrosModule.Instance;
        var player = module?.PlayerManager.GetPlayer(playerId);

        if (player != null)
        {
            // Show tooltip with player info
            // Name, Clan, Kingdom, Party Size, Current Activity
        }
    }
}

public class PlayerMapMarker
{
    public int PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public float MapX { get; set; }
    public float MapY { get; set; }
    public PlayerState State { get; set; }
    public bool IsVisible { get; set; }
}
