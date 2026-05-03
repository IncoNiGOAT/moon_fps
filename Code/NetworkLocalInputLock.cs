using Sandbox;

/// <summary>
/// Multijoueur : n&apos;active clavier / souris / caméra que pour le <see cref="GameObject.Network.IsOwner"/>
/// du prefab joueur. Hors réseau : tout activé (solo / bots / éditeur).
/// </summary>
public sealed class NetworkLocalInputLock : Component
{
    [Property] public bool EnabledLock { get; set; } = true;

    protected override void OnUpdate()
    {
        if ( !EnabledLock )
            return;

        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is null )
                continue;

            if ( !Networking.IsActive )
            {
                pc.UseInputControls = true;
                pc.UseLookControls = true;
                pc.UseCameraControls = true;
                continue;
            }

            var owned = IsLocallyOwnedHierarchy( pc.GameObject );
            pc.UseInputControls = owned;
            pc.UseLookControls = owned;
            pc.UseCameraControls = owned;
        }
    }

    private static bool IsLocallyOwnedHierarchy( GameObject start )
    {
        if ( start is null || !start.IsValid() )
            return false;

        var go = start;
        while ( go is not null )
        {
            if ( go.Network.Active )
                return go.Network.IsOwner;

            go = go.Parent;
        }

        return false;
    }
}
