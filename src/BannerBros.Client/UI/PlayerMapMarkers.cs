using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using BannerBros.Core;

namespace BannerBros.Client.UI;

/// <summary>
/// Manages visual markers for co-op players on the campaign map.
/// Shows player positions, names, and status indicators.
/// </summary>
public class PlayerMapMarkers
{
    private readonly Dictionary<int, PlayerMarkerEntity> _markers = new();
    private Scene? _mapScene;
    private bool _initialized;

    public void Initialize(Scene mapScene)
    {
        _mapScene = mapScene;
        _initialized = true;
    }

    public void Cleanup()
    {
        foreach (var marker in _markers.Values)
        {
            marker.Dispose();
        }
        _markers.Clear();
        _initialized = false;
        _mapScene = null;
    }

    public void Update(float dt)
    {
        if (!_initialized || _mapScene == null) return;

        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true)
        {
            // Cleanup markers if disconnected
            if (_markers.Count > 0) Cleanup();
            return;
        }

        var localPlayerId = module.PlayerManager.LocalPlayerId;

        // Update or create markers for each player
        foreach (var player in module.PlayerManager.Players.Values)
        {
            // Skip local player
            if (player.NetworkId == localPlayerId) continue;

            if (!_markers.TryGetValue(player.NetworkId, out var marker))
            {
                marker = CreateMarker(player);
                _markers[player.NetworkId] = marker;
            }

            UpdateMarker(marker, player);
        }

        // Remove markers for disconnected players
        var toRemove = _markers.Keys
            .Where(id => !module.PlayerManager.Players.ContainsKey(id))
            .ToList();

        foreach (var id in toRemove)
        {
            _markers[id].Dispose();
            _markers.Remove(id);
        }
    }

    private PlayerMarkerEntity CreateMarker(CoopPlayer player)
    {
        var marker = new PlayerMarkerEntity
        {
            PlayerId = player.NetworkId,
            PlayerName = player.Name
        };

        // Create the visual entity
        // In Bannerlord, this would create a 3D marker on the map
        // The exact implementation depends on the game's scene system
        if (_mapScene != null)
        {
            // For now, we'll use a simple approach
            // A full implementation would create a proper mesh entity
            marker.IsCreated = true;
        }

        return marker;
    }

    private void UpdateMarker(PlayerMarkerEntity marker, CoopPlayer player)
    {
        marker.PlayerName = player.Name;

        // Update position
        var position = new Vec3(player.MapPositionX, player.MapPositionY, 0);

        // Get terrain height at position
        if (_mapScene != null)
        {
            float height = 0;
            _mapScene.GetHeightAtPoint(position.AsVec2, BodyFlags.CommonCollisionExcludeFlags, ref height);
            position.z = height + 2f; // Float above terrain
        }

        marker.Position = position;
        marker.State = player.State;

        // Update color based on state/faction
        marker.Color = GetMarkerColor(player);
    }

    private uint GetMarkerColor(CoopPlayer player)
    {
        // Colors in ARGB format
        return player.State switch
        {
            PlayerState.InBattle => 0xFF4444FF,     // Blue - in battle
            PlayerState.InDialogue => 0xFFFFFF00,   // Yellow - protected (dialogue)
            PlayerState.InTrade => 0xFFFFFF00,      // Yellow - protected (trade)
            PlayerState.InTown => 0xFF00FF00,       // Green - in town
            PlayerState.InVillage => 0xFF00FF00,    // Green - in village
            PlayerState.InCastle => 0xFF00FF00,     // Green - in castle
            _ => 0xFFFFFFFF                          // White - normal
        };
    }
}

/// <summary>
/// Represents a visual marker entity on the map.
/// </summary>
public class PlayerMarkerEntity : IDisposable
{
    public int PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public Vec3 Position { get; set; }
    public PlayerState State { get; set; }
    public uint Color { get; set; } = 0xFFFFFFFF;
    public bool IsCreated { get; set; }
    public bool IsVisible { get; set; } = true;

    // Game engine references would go here
    // GameEntity? _entity;
    // MetaMesh? _mesh;

    public void Dispose()
    {
        // Cleanup game engine resources
        // _entity?.Remove(0);
        IsCreated = false;
    }
}

/// <summary>
/// Alternative approach: Use Bannerlord's party banner system
/// to show player parties distinctly on the map.
/// </summary>
public static class PlayerPartyVisuals
{
    private static readonly Dictionary<string, bool> _customizedParties = new();

    /// <summary>
    /// Customizes the visual appearance of a co-op player's party on the map.
    /// </summary>
    public static void SetupPlayerPartyVisuals(MobileParty party, CoopPlayer player)
    {
        if (party == null || _customizedParties.ContainsKey(party.StringId)) return;

        // Add custom visual indicator
        // This could be a special banner, glow effect, or name tag
        _customizedParties[party.StringId] = true;

        // The actual visual customization depends on modifying the party's
        // PartyVisual component or using Bannerlord's visual effect system
    }

    /// <summary>
    /// Removes custom visuals from a party (when player disconnects).
    /// </summary>
    public static void ClearPlayerPartyVisuals(string partyId)
    {
        _customizedParties.Remove(partyId);
    }

    /// <summary>
    /// Gets display name for a party, adding [COOP] tag for player parties.
    /// </summary>
    public static string GetPartyDisplayName(MobileParty party)
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return party.Name.ToString();

        // Check if this party belongs to a co-op player
        foreach (var player in module.PlayerManager.Players.Values)
        {
            if (player.PartyId == party.StringId)
            {
                return $"[COOP] {player.Name}";
            }
        }

        return party.Name.ToString();
    }
}
