using Sandbox;

/// <summary>
/// Recharge la scène courante (redémarrage rapide en jeu). Action <c>Reload</c> par défaut.
/// </summary>
[Title( "Game Restart Hotkey" )]
public sealed class GameRestartHotkey : Component
{
    [Property] public bool EnabledHotkey { get; set; } = true;

    [Property] public string RestartAction { get; set; } = "Reload";

    [Property] public SceneFile OverrideScene { get; set; }

    [Property] public string OverrideScenePathFallback { get; set; }

    protected override void OnUpdate()
    {
        if ( !EnabledHotkey )
            return;

        var action = string.IsNullOrWhiteSpace( RestartAction ) ? "Reload" : RestartAction;
        if ( !Input.Pressed( action ) )
            return;

        TryRestart();
    }

    [Button( "Reset Game (now)" )]
    public void ResetNow() => TryRestart();

    private void TryRestart()
    {
        var scene = Scene;

        if ( OverrideScene is not null )
        {
            if ( scene.Load( OverrideScene ) )
                Log.Info( "[GameRestart] Scène rechargée (OverrideScene)." );
            return;
        }

        if ( !string.IsNullOrWhiteSpace( OverrideScenePathFallback ) )
        {
            if ( scene.LoadFromFile( OverrideScenePathFallback ) )
                Log.Info( $"[GameRestart] Scène rechargée depuis {OverrideScenePathFallback}." );
            return;
        }

        if ( scene.Source is SceneFile sf )
        {
            if ( scene.Load( sf ) )
                Log.Info( "[GameRestart] Scène courante rechargée." );
            return;
        }

        var path = scene.Source?.ResourcePath;
        if ( !string.IsNullOrWhiteSpace( path ) && scene.LoadFromFile( path ) )
        {
            Log.Info( $"[GameRestart] Scène rechargée depuis {path}." );
            return;
        }

        Log.Warning( "[GameRestart] Impossible de recharger : pas de SceneFile ni ResourcePath pour cette scène." );
    }
}
