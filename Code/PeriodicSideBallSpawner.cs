using Sandbox;

/// <summary>
/// Toutes les <see cref="IntervalSeconds"/> : spawn une balle sur le camp qui a le moins de joueurs au sol
/// (hors prison si <see cref="CountOnlyPlayersOnField"/>). Position aléatoire dans <see cref="RedSpawnVolume"/> / <see cref="BlueSpawnVolume"/>.
/// </summary>
[Title( "Periodic Side Ball Spawner" )]
public sealed class PeriodicSideBallSpawner : Component
{
    [Property] public GameObject BallPrefab { get; set; }
    [Property] public float IntervalSeconds { get; set; } = 60f;
    [Property] public BallSpawnVolume RedSpawnVolume { get; set; }
    [Property] public BallSpawnVolume BlueSpawnVolume { get; set; }
    [Property] public float SpawnHeightLift { get; set; } = 6f;
    /// <summary> Si vrai, les joueurs en prison ne comptent pas pour choisir le camp. </summary>
    [Property] public bool CountOnlyPlayersOnField { get; set; } = true;

    private TimeSince _sinceLastSpawn;

    protected override void OnStart()
    {
        _sinceLastSpawn = 0f;
    }

    protected override void OnUpdate()
    {
        if ( !BallPrefab.IsValid() || IntervalSeconds <= 0f )
            return;

        if ( Networking.IsActive && !Networking.IsHost )
            return;

        if ( _sinceLastSpawn < IntervalSeconds )
            return;

        _sinceLastSpawn = 0f;
        SpawnBallOnWeakerSide();
    }

    [Button]
    public void SpawnOneBallNow()
    {
        if ( !BallPrefab.IsValid() )
            return;

        if ( Networking.IsActive && !Networking.IsHost )
            return;

        SpawnBallOnWeakerSide();
    }

    private void SpawnBallOnWeakerSide()
    {
        if ( Networking.IsActive && !Networking.IsHost )
            return;

        var counts = CountTeamPlayersOnField();
        TeamId side;
        if ( counts.red < counts.blue )
            side = TeamId.Red;
        else if ( counts.blue < counts.red )
            side = TeamId.Blue;
        else
            side = Game.Random.Float( 0f, 1f ) < 0.5f ? TeamId.Red : TeamId.Blue;

        if ( !TryGetSpawnPosition( side, out var pos ) )
        {
            Log.Warning( $"[PeriodicSideBallSpawner] Assigne RedSpawnVolume / BlueSpawnVolume (manquant pour {side})." );
            return;
        }

        var clone = BallPrefab.Clone( pos, Rotation.Identity );
        if ( clone is null || !clone.IsValid() )
            return;

        // Ne pas laisser la balle enfant d'un GO en Snapshot (ex. GameMode) : le moteur ne traite
        // pas la physique / l'ownership comme un NetworkObject dédié → lancer sans mouvement.
        clone.Parent = null;

        clone.Enabled = true;

        if ( Networking.IsActive )
        {
            clone.NetworkMode = NetworkMode.Object;
            try
            {
                clone.NetworkSpawn();
            }
            catch ( System.Exception e )
            {
                Log.Error( $"[PeriodicSideBallSpawner] NetworkSpawn a echoue : {e.Message}" );
                clone.Destroy();
                return;
            }
        }

        // Après NetworkSpawn : remettre la vélocité au frame suivant pour ne pas annuler l'init réseau / ownership.
        var captured = clone;
        Invoke( 0f, () =>
        {
            if ( captured is null || !captured.IsValid() )
                return;
            var rb = captured.Components.Get<Rigidbody>() ?? captured.Components.GetInChildren<Rigidbody>( true );
            if ( rb is null )
                return;
            rb.Velocity = Vector3.Zero;
            rb.AngularVelocity = Vector3.Zero;
        } );

        Log.Info( $"[PeriodicSideBallSpawner] Balle spawn côté {side} à {pos} (rouge={counts.red}, bleu={counts.blue})." );
    }

    private bool TryGetSpawnPosition( TeamId side, out Vector3 pos )
    {
        pos = default;

        var vol = side == TeamId.Red ? RedSpawnVolume : BlueSpawnVolume;
        if ( vol is null || !vol.GameObject.IsValid() )
            return false;

        return vol.TryGetRandomWorldPoint( out pos, SpawnHeightLift );
    }

    private (int red, int blue) CountTeamPlayersOnField()
    {
        var red = 0;
        var blue = 0;

        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is null || pc.GameObject is null || !pc.GameObject.Enabled )
                continue;

            var tm = pc.Components.Get<TeamMember>() ?? pc.GameObject.Components.GetInChildren<TeamMember>( true );
            if ( tm is null )
                continue;

            if ( CountOnlyPlayersOnField )
            {
                var pbp = pc.Components.Get<PrisonBallPlayer>()
                    ?? pc.GameObject.Components.GetInChildren<PrisonBallPlayer>( true );
                if ( pbp is not null && pbp.InPrison )
                    continue;
            }

            if ( tm.Team == TeamId.Red )
                red++;
            else
                blue++;
        }

        return (red, blue);
    }
}
