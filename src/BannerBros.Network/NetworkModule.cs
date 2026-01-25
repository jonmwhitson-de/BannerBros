using System;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerBros.Network;

/// <summary>
/// Entry point for the BannerBros networking subsystem.
/// </summary>
public class NetworkModule : MBSubModuleBase
{
    public static NetworkModule? Instance { get; private set; }

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        Instance = this;

        // DISABLED: Testing if NetworkManager init causes crash
        // NetworkManager.Initialize();
    }

    protected override void OnSubModuleUnloaded()
    {
        base.OnSubModuleUnloaded();

        // Only shutdown if it was initialized
        // NetworkManager.Shutdown();
        Instance = null;
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);

        // DISABLED: Isolating crash cause
        // NetworkManager.Instance?.Update(dt);
    }
}
