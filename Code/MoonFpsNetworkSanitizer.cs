using Sandbox;

/// <summary>
/// Désactive un <c>GameManager</c> template tiers qui implémente <see cref="Component.INetworkListener"/>
/// en parallèle de <see cref="MoonPlayerSpawner"/> (conflit de spawn).
/// </summary>
public static class MoonFpsNetworkSanitizer
{
    public static void DisableTemplateGameManagers( Scene scene )
    {
        if ( scene is null )
            return;

        foreach ( var c in scene.GetAllComponents<Component>() )
        {
            if ( c is null || !c.Enabled || c is MoonPlayerSpawner )
                continue;

            if ( c is not Component.INetworkListener )
                continue;

            if ( c.GetType().Name != "GameManager" )
                continue;

            Log.Warning( $"[MoonFPS] GameManager template désactivé sur « {c.GameObject.Name} » — spawn : Moon Player Spawner." );
            c.Enabled = false;
        }
    }
}
