using Sandbox;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Balle au prisonnier : une instance dans la scène (ex. sur un GameObject "GameMode").
/// Branche <see cref="ArenaSpawns"/> (spawns terrain) et les listes de spawns prison par équipe.
/// </summary>
public sealed class PrisonBallRules : Component
{
    [Property] public TeamSpawnManager ArenaSpawns { get; set; }
    [Property] public List<GameObject> RedPrisonSpawns { get; set; } = new();
    [Property] public List<GameObject> BluePrisonSpawns { get; set; } = new();
    [Property] public bool FreezePlayersOnGameOver { get; set; } = true;
    [Property] public Color HudWinColor { get; set; } = new Color( 1f, 0.92f, 0.35f, 0.95f );

    public bool IsGameOver { get; private set; }
    public TeamId? WinningTeam { get; private set; }

    private readonly HashSet<PrisonBallPlayer> _players = new();
    private readonly List<GameObject> _redPrisonBag = new();
    private readonly List<GameObject> _bluePrisonBag = new();
    private bool _frozen;

    public void Register( PrisonBallPlayer player )
    {
        if ( player is null )
            return;

        _players.Add( player );
    }

    public void Unregister( PrisonBallPlayer player )
    {
        if ( player is null )
            return;

        _players.Remove( player );
    }

    /// <summary> Point sur le terrain pour une équipe (file + mélange comme les spawns classiques). </summary>
    public bool TryGetArenaSpawn( TeamId team, out GameObject spawn )
    {
        return TryGetArenaSpawn( team, -1, out spawn );
    }

    /// <param name="arenaSlotIndex"> Si &gt;= 0 et spawns par slot activés, même point que le spawn de départ pour ce joueur. </param>
    public bool TryGetArenaSpawn( TeamId team, int arenaSlotIndex, out GameObject spawn )
    {
        spawn = null;
        EnsureArenaSpawns();

        if ( ArenaSpawns is null )
            return false;

        if ( ArenaSpawns.UseSlotBasedArenaSpawns && arenaSlotIndex >= 0 )
        {
            spawn = ArenaSpawns.GetArenaSpawnForSlot( team, arenaSlotIndex );
            if ( spawn is not null )
                return true;
        }

        spawn = ArenaSpawns.GetSpawnPoint( team );
        return spawn is not null;
    }

    /// <summary> Point en prison pour l'équipe du prisonnier. </summary>
    public bool TryGetPrisonSpawn( TeamId team, out GameObject spawn )
    {
        spawn = null;
        var source = team == TeamId.Red ? RedPrisonSpawns : BluePrisonSpawns;
        var bag = team == TeamId.Red ? _redPrisonBag : _bluePrisonBag;

        bag.RemoveAll( x => x is null || !source.Contains( x ) );

        if ( bag.Count == 0 )
        {
            foreach ( var p in source )
            {
                if ( p is not null )
                    bag.Add( p );
            }

            Shuffle( bag );
        }

        if ( bag.Count == 0 )
            return false;

        spawn = bag[0];
        bag.RemoveAt( 0 );
        return true;
    }

    public void CheckWinCondition()
    {
        if ( IsGameOver )
            return;

        var red = _players.Where( p => p is not null && p.Team == TeamId.Red ).ToList();
        var blue = _players.Where( p => p is not null && p.Team == TeamId.Blue ).ToList();

        if ( red.Count > 0 && red.All( p => p.InPrison ) )
        {
            EndGame( TeamId.Blue );
            return;
        }

        if ( blue.Count > 0 && blue.All( p => p.InPrison ) )
        {
            EndGame( TeamId.Red );
        }
    }

    protected override void OnUpdate()
    {
        if ( IsGameOver )
            DrawGameOverHud();
    }

    private void EndGame( TeamId winner )
    {
        IsGameOver = true;
        WinningTeam = winner;

        if ( FreezePlayersOnGameOver && !_frozen )
        {
            _frozen = true;
            foreach ( var player in _players )
            {
                if ( player is null )
                    continue;

                var pc = player.Components.Get<PlayerController>();
                if ( pc is not null )
                    pc.UseInputControls = false;
            }
        }
    }

    private void DrawGameOverHud()
    {
        if ( Scene.Camera is null || WinningTeam is null )
            return;

        var hud = Scene.Camera.Hud;
        var label = WinningTeam == TeamId.Red ? "LES ROUGES GAGNENT" : "LES BLEUS GAGNENT";
        hud.DrawText( new TextRendering.Scope( label, HudWinColor, 42 ), new Vector2( Screen.Width * 0.5f - 220f, Screen.Height * 0.35f ) );
    }

    private void EnsureArenaSpawns()
    {
        if ( ArenaSpawns is not null )
            return;

        ArenaSpawns = Scene.GetAllComponents<TeamSpawnManager>().FirstOrDefault();
    }

    private static void Shuffle( List<GameObject> list )
    {
        for ( var i = list.Count - 1; i > 0; i-- )
        {
            var j = Game.Random.Int( 0, i );
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
