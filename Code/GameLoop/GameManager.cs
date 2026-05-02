using Sandbox;

/// <summary>
/// Reste du template s&amp;box « GameLoop ». Si tu utilises <see cref="MoonPlayerSpawner"/>,
/// laisse <see cref="DisableAutoSpawn"/> à <c>true</c> (défaut) ou retire ce composant de la scène.
/// Sinon <see cref="OnActive"/> peut lever (PlayerPrefab / SpawnPoint non assignés).
/// </summary>
[Title( "Game Manager (template)" )]
public sealed class GameManager : Component, Component.INetworkListener
{
    [Property] public GameObject PlayerPrefab { get; set; }
    [Property] public GameObject SpawnPoint { get; set; }

    /// <summary>
    /// Quand <c>true</c>, ce composant n’essaie pas de spawn (évite le NullReference si prefab / point non câblés).
    /// Le spawn réseau est alors géré par <see cref="MoonPlayerSpawner"/> + <see cref="MoonPlayerSpawnerNetworkRelay"/>.
    /// </summary>
    [Property] public bool DisableAutoSpawn { get; set; } = true;

    public void OnActive( Connection channel )
    {
        if ( DisableAutoSpawn )
            return;

        if ( channel is null )
            return;

        if ( PlayerPrefab is null || !PlayerPrefab.IsValid() )
        {
            Log.Warning( "[GameManager] PlayerPrefab manquant ou invalide — spawn ignoré. Assigne-le ou active DisableAutoSpawn." );
            return;
        }

        if ( SpawnPoint is null || !SpawnPoint.IsValid() )
        {
            Log.Warning( "[GameManager] SpawnPoint manquant ou invalide — spawn ignoré." );
            return;
        }

        SpawnPlayer( channel );
    }

    private void SpawnPlayer( Connection connection )
    {
        var player = PlayerPrefab.Clone( SpawnPoint.WorldTransform );
        if ( player is null || !player.IsValid() )
        {
            Log.Warning( "[GameManager] Clone joueur invalide." );
            return;
        }

        player.NetworkSpawn( connection );
    }
}
