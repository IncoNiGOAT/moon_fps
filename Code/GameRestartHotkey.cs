using Sandbox;

/// <summary>
/// Recharge la scène courante (redémarrage rapide en jeu).
/// Par défaut utilise l&apos;action <c>Reload</c> du projet (touche <c>R</c> dans Input.config).
///
/// <para>
/// <b>Multijoueur :</b> seul l&apos;hôte peut déclencher le reset. La commande est ensuite broadcastée
/// à tous les clients via <see cref="Rpc.Broadcast"/> pour qu&apos;ils rechargent leur scène locale
/// (Scene.Load n&apos;étant pas réseau-aware en lui-même).
/// </para>
/// </summary>
[Title( "Game Restart Hotkey" )]
public sealed class GameRestartHotkey : Component
{
    [Property] public bool EnabledHotkey { get; set; } = true;

    /// <summary> Nom d&apos;une entrée dans Project Settings &gt; Input (ex. <c>Reload</c>, <c>Slot4</c>).</summary>
    [Property] public string RestartAction { get; set; } = "Reload";

    /// <summary> Si défini, charge cette scène au lieu de la scène courante.</summary>
    [Property] public SceneFile OverrideScene { get; set; }

    /// <summary> Sinon chemin .scene (ex. <c>Assets/Arena.scene</c>) si <see cref="OverrideScene"/> est vide.</summary>
    [Property] public string OverrideScenePathFallback { get; set; }

    protected override void OnUpdate()
    {
        if ( !EnabledHotkey )
            return;

        var action = string.IsNullOrWhiteSpace( RestartAction ) ? "Reload" : RestartAction;
        if ( !Input.Pressed( action ) )
            return;

        // Multi : seul l'host peut declencher, et on broadcast a tous les clients
        // (Scene.Load reste une operation locale, on appelle TryRestart sur chaque machine).
        if ( Networking.IsActive )
        {
            if ( !Networking.IsHost )
            {
                Log.Info( "[GameRestart] Reload ignore : seul l'host peut declencher en multi." );
                return;
            }

            BroadcastRestartRpc();
            return;
        }

        TryRestart();
    }

    [Button( "Reset Game (now)" )]
    public void ResetNow()
    {
        if ( Networking.IsActive )
        {
            if ( !Networking.IsHost )
            {
                Log.Info( "[GameRestart] Reset bouton ignore : seul l'host peut declencher en multi." );
                return;
            }

            BroadcastRestartRpc();
            return;
        }

        TryRestart();
    }

    [Rpc.Broadcast]
    private void BroadcastRestartRpc()
    {
        TryRestart();
    }

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
