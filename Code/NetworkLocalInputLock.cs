using Sandbox;

/// <summary>
/// Multijoueur : n'active clavier / souris / caméra que sur le <see cref="PlayerController"/> dont la racine réseau
/// appartient au joueur local (<see cref="GameObject.Network.IsOwner"/>).
/// Mets ce composant sur le GameMode avec <see cref="MoonPlayerSpawner"/>.
/// </summary>
public sealed class NetworkLocalInputLock : Component
{
    [Property] public bool EnabledLock { get; set; } = true;

    protected override void OnUpdate()
    {
        if ( !EnabledLock )
            return;

        // Hors session (play solo / éditeur) : laisse LocalSoloInputGate + DebugPlayerSwitcher gérer les entrées.
        if ( !Networking.IsActive )
            return;

        // Tant qu'aucun perso n'est possédé localement, ne rien verrouiller (choix d'équipe MoonPlayerSpawner, lobby, etc.).
        var hasLocalOwnedPlayer = false;
        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is null )
                continue;

            if ( !TryGetNetworkRoot( pc.GameObject, out var root ) )
                continue;

            if ( root.Network.IsOwner )
            {
                hasLocalOwnedPlayer = true;
                break;
            }
        }

        if ( !hasLocalOwnedPlayer )
            return;

        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is null )
                continue;

            if ( !TryGetNetworkRoot( pc.GameObject, out var root ) )
            {
                pc.UseInputControls = false;
                pc.UseLookControls = false;
                pc.UseCameraControls = false;
                continue;
            }

            var own = root.Network.IsOwner;
            pc.UseInputControls = own;
            pc.UseLookControls = own;
            pc.UseCameraControls = own;
        }
    }

    private static bool TryGetNetworkRoot( GameObject start, out GameObject root )
    {
        var go = start;
        while ( go is not null )
        {
            if ( go.Network.Active )
            {
                root = go.Network.RootGameObject ?? go;
                return true;
            }

            go = go.Parent;
        }

        root = null;
        return false;
    }
}
