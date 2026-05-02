using Sandbox;

/// <summary>
/// Outil debug solo: permet de basculer le joueur contrôlé pendant le playtest.
/// Ajoute ce composant sur un GameObject "Debug".
/// </summary>
public sealed class DebugPlayerSwitcher : Component
{
    [Property] public string SwitchAction { get; set; } = "Slot0";
    [Property] public bool AutoFindAllPlayers { get; set; } = true;
    [Property] public List<GameObject> PlayerRoots { get; set; } = new();
    [Property] public bool ShowHud { get; set; } = true;

    private readonly List<PlayerController> _players = new();
    private int _activeIndex = -1;

    protected override void OnStart()
    {
        RefreshPlayers();
    }

    protected override void OnUpdate()
    {
        if ( Input.Pressed( SwitchAction ) )
        {
            SwitchToNextPlayer();
        }

        if ( ShowHud )
        {
            DrawHud();
        }
    }

    [Button]
    public void RefreshPlayers()
    {
        _players.Clear();

        if ( AutoFindAllPlayers )
        {
            foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
            {
                if ( pc is not null && !_players.Contains( pc ) )
                    _players.Add( pc );
            }
        }
        else
        {
            foreach ( var root in PlayerRoots )
            {
                if ( root is null )
                    continue;

                var pc = root.Components.Get<PlayerController>() ?? root.Components.GetInChildren<PlayerController>( true );
                if ( pc is not null && !_players.Contains( pc ) )
                    _players.Add( pc );
            }
        }

        if ( _players.Count == 0 )
        {
            _activeIndex = -1;
            return;
        }

        var foundActive = -1;
        for ( var i = 0; i < _players.Count; i++ )
        {
            if ( _players[i].UseInputControls )
            {
                foundActive = i;
                break;
            }
        }

        SetActivePlayer( foundActive >= 0 ? foundActive : 0 );
    }

    [Button]
    public void SwitchToNextPlayer()
    {
        if ( _players.Count == 0 )
        {
            RefreshPlayers();
            if ( _players.Count == 0 )
                return;
        }

        var next = (_activeIndex + 1) % _players.Count;
        SetActivePlayer( next );
    }

    private void SetActivePlayer( int index )
    {
        if ( index < 0 || index >= _players.Count )
            return;

        _activeIndex = index;

        for ( var i = 0; i < _players.Count; i++ )
        {
            var player = _players[i];
            if ( player is null )
                continue;

            var isActive = i == _activeIndex;
            player.UseInputControls = isActive;
            player.UseLookControls = isActive;
            player.UseCameraControls = isActive;
        }

        // Même référence que MoonPlayerSpawner.PinOfflineHumanAsActive : si Apply() est rappelé, le bon perso reste actif.
        var scene = Game.ActiveScene;
        if ( scene is not null )
        {
            var root = _players[_activeIndex]?.GameObject;
            if ( root is not null && root.IsValid() )
            {
                foreach ( var gate in scene.GetAllComponents<LocalSoloInputGate>() )
                {
                    if ( gate is not null )
                        gate.ActivePlayerRoot = root;
                }
            }
        }
    }

    private void DrawHud()
    {
        if ( Scene.Camera is null )
            return;

        var hud = Scene.Camera.Hud;
        var action = string.IsNullOrWhiteSpace( SwitchAction ) ? "Slot0" : SwitchAction;

        var label = _players.Count == 0
            ? $"No PlayerController found ({action})"
            : $"Active Player: {_activeIndex + 1}/{_players.Count}  |  Switch: {action}";

        var x = 20f;
        var y = 20f;
        hud.DrawRect( new Rect( x - 8f, y - 6f, 420f, 32f ), new Color( 0f, 0f, 0f, 0.45f ) );
        hud.DrawText( new TextRendering.Scope( label, Color.White, 16 ), new Vector2( x, y ) );
    }
}
