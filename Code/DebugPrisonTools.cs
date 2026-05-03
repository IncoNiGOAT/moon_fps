using Sandbox;

/// <summary>
/// Outil debug rapide pour tests prison/spawn.
/// A placer sur un objet debug (ex: GameMode ou DebugRoot).
/// </summary>
[Title( "Debug Prison Tools" )]
public sealed class DebugPrisonTools : Component
{
    [Property] public bool EnabledTools { get; set; } = true;
    [Property] public bool ShowHud { get; set; } = true;
    [Property] public bool TargetOwnedPlayerFirst { get; set; } = true;
    [Property] public GameObject ExplicitTargetRoot { get; set; }

    // Important: Input.Pressed lit des actions bindées (Project Settings > Input),
    // pas forcément les lettres clavier directes.
    [Property] public string KillAction { get; set; } = "Slot7";
    [Property] public string JailAction { get; set; } = "Slot8";
    [Property] public string FreeAction { get; set; } = "Slot9";

    protected override void OnUpdate()
    {
        if ( !EnabledTools )
            return;

        if ( Input.Pressed( KillAction ) )
            KillTarget();

        if ( Input.Pressed( JailAction ) )
            JailTarget();

        if ( Input.Pressed( FreeAction ) )
            FreeTarget();

        if ( ShowHud )
            DrawHud();
    }

    [Button]
    public void KillTarget()
    {
        // Pas de système de vie actuellement: "kill" = envoi direct en prison.
        JailTarget();
    }

    [Button]
    public void JailTarget()
    {
        if ( !TryGetTarget( out var player ) )
            return;

        player.Jail();
    }

    [Button]
    public void FreeTarget()
    {
        if ( !TryGetTarget( out var player ) )
            return;

        player.FreeToArena();
    }

    private bool TryGetTarget( out PrisonBallPlayer player )
    {
        player = null;

        if ( ExplicitTargetRoot is not null && ExplicitTargetRoot.IsValid() )
        {
            player = ExplicitTargetRoot.Components.Get<PrisonBallPlayer>()
                     ?? ExplicitTargetRoot.Components.GetInChildren<PrisonBallPlayer>( true );
            if ( player is not null )
                return true;
        }

        if ( TargetOwnedPlayerFirst )
        {
            foreach ( var p in Scene.GetAllComponents<PrisonBallPlayer>() )
            {
                if ( p is null )
                    continue;

                var pc = p.Components.Get<PlayerController>() ?? p.GameObject.Components.GetInChildren<PlayerController>( true );
                if ( pc is not null && pc.UseCameraControls )
                {
                    player = p;
                    return true;
                }
            }
        }

        foreach ( var p in Scene.GetAllComponents<PrisonBallPlayer>() )
        {
            if ( p is not null )
            {
                player = p;
                return true;
            }
        }

        return false;
    }

    private void DrawHud()
    {
        if ( Scene?.Camera is null )
            return;

        var hud = Scene.Camera.Hud;

        var status = TryGetTarget( out var target )
            ? $"Target: {(target.InPrison ? "IN PRISON" : "FREE")} | Team: {target.Team}"
            : "Target: none";

        var x = 20f;
        var y = Screen.Height - 120f;
        hud.DrawRect( new Rect( x - 8f, y - 8f, 460f, 80f ), new Color( 0f, 0f, 0f, 0.45f ) );
        hud.DrawText( new TextRendering.Scope( "[DEBUG] Prison Tools", new Color( 1f, 0.9f, 0.35f ), 18 ), new Vector2( x, y ) );
        hud.DrawText( new TextRendering.Scope( $"[{KillAction}] Kill->Prison  [{JailAction}] Prison  [{FreeAction}] Spawn libre", Color.White, 15 ), new Vector2( x, y + 24f ) );
        hud.DrawText( new TextRendering.Scope( status, new Color( 0.8f, 0.9f, 1f ), 14 ), new Vector2( x, y + 46f ) );
    }
}
