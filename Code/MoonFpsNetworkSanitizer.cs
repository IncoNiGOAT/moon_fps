using Sandbox;

/// <summary>
/// Si un <c>GameManager</c> template (ex. ancienne référence <c>facepunch.sandbox</c>) est encore présent dans la scène,
/// il implémente <see cref="Component.INetworkListener"/> et peut entrer en conflit avec <see cref="MoonPlayerSpawner"/>.
/// On le désactive pour éviter un double spawn ; le spawn réseau doit passer par Moon Player Spawner.
/// <para>
/// Ne pas utiliser <see cref="Component.Destroy"/> ici : le template peut partager un GameObject avec d’autres systèmes.
/// </para>
/// </summary>
public static class MoonFpsNetworkSanitizer
{
    public static bool IsTemplateGameManagerListener( Component c )
    {
        if ( c is null || c is not Component.INetworkListener || c is MoonPlayerSpawner )
            return false;

        return c.GetType().Name == "GameManager";
    }

    /// <summary> Parcourt la scène donnée et, si différente, <see cref="Game.ActiveScene"/>. </summary>
    public static void DisableTemplateGameManagers( Scene scene )
    {
        SanitizeScene( scene );

        var active = Game.ActiveScene;
        if ( active is not null && !ReferenceEquals( active, scene ) )
            SanitizeScene( active );
    }

    private static void SanitizeScene( Scene scene )
    {
        if ( scene is null )
            return;

        foreach ( var c in scene.GetAllComponents<Component>() )
        {
            if ( c is null || !c.Enabled )
                continue;

            if ( !IsTemplateGameManagerListener( c ) )
                continue;

            Log.Warning( $"[MoonFPS] GameManager (template) désactivé sur « {c.GameObject.Name} » ({c.GetType().FullName}) — spawn : Moon Player Spawner." );
            c.Enabled = false;
        }
    }
}
