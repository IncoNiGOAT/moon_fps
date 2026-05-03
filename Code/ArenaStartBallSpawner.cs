using Sandbox;

/// <summary>
/// Au démarrage de l'arène : instancie plusieurs balles au centre (ou sur <see cref="CenterPoint"/>).
/// Après <see cref="Scene.Load"/> depuis le menu, <see cref="Networking.IsHost"/> peut rester faux quelques ticks :
/// on attend avant <see cref="GameObject.NetworkSpawn"/>, sinon les balles restent « locales » (plus de nom d'hôte sur l'entité).
/// </summary>
[Title( "Arena Start Ball Spawner" )]
public sealed class ArenaStartBallSpawner : Component
{
    [Property] public GameObject BallPrefab { get; set; }
    [Property] public int BallCount { get; set; } = 3;
    /// <summary> Si vide, utilise la position de l'objet qui porte ce composant. </summary>
    [Property] public GameObject CenterPoint { get; set; }
    [Property] public float HorizontalSpacing { get; set; } = 48f;
    [Property] public Vector3 SpreadDirection { get; set; } = new Vector3( 1f, 0f, 0f );
    [Property] public float SpawnHeightLift { get; set; } = 6f;
    [Property] public bool SpawnOnStart { get; set; } = true;
    [Property] public float SpawnDelaySeconds { get; set; } = 0.05f;

    /// <summary> Tentatives × <see cref="HostReadyRetryIntervalSeconds"/> pour obtenir <see cref="Networking.IsHost"/> (ex. après chargement de scène). </summary>
    [Property] public int HostReadyMaxRetries { get; set; } = 80;

    [Property] public float HostReadyRetryIntervalSeconds { get; set; } = 0.05f;

    /// <summary>
    /// En ligne : ne pas lancer <see cref="TryBeginSpawnFlow"/> au <see cref="OnStart"/> de la scène (souvent avant
    /// qu&apos;un joueur ait fini le handshake). Attend <see cref="StartNetworkSessionBallSpawningOnce"/> (appelé par
    /// <see cref="MoonPlayerSpawner"/> à la première <see cref="Component.INetworkListener.OnActive"/>), ou le fallback temps.
    /// </summary>
    [Property] public bool DeferSpawnUntilFirstNetworkActive { get; set; } = true;

    /// <summary> Si <see cref="DeferSpawnUntilFirstNetworkActive"/> et aucun <see cref="OnActive"/> : démarre quand même (sécurité). </summary>
    [Property] public float DeferredSpawnFallbackSeconds { get; set; } = 4f;

    private int _hostReadyRetries;
    private bool _spawnScheduledOrDone;
    private bool _sessionBallBootstrapStarted;

    protected override void OnStart()
    {
        if ( !SpawnOnStart || BallCount <= 0 || !BallPrefab.IsValid() )
            return;

        _spawnScheduledOrDone = false;
        _hostReadyRetries = 0;

        if ( Networking.IsActive && DeferSpawnUntilFirstNetworkActive )
        {
            var fb = DeferredSpawnFallbackSeconds <= 0f ? 4f : DeferredSpawnFallbackSeconds;
            Invoke( fb, StartNetworkSessionBallSpawningOnce );
            return;
        }

        Invoke( HostReadyRetryIntervalSeconds, TryBeginSpawnFlow );
    }

    /// <summary> Appelé par <see cref="MoonPlayerSpawner"/> dès qu&apos;une connexion est <see cref="Component.INetworkListener.OnActive"/>. </summary>
    public void StartNetworkSessionBallSpawningOnce()
    {
        if ( !SpawnOnStart || BallCount <= 0 || !BallPrefab.IsValid() )
            return;

        if ( !Networking.IsActive || !Networking.IsHost )
            return;

        if ( _sessionBallBootstrapStarted )
            return;

        _sessionBallBootstrapStarted = true;
        _spawnScheduledOrDone = false;
        _hostReadyRetries = 0;
        Invoke( HostReadyRetryIntervalSeconds, TryBeginSpawnFlow );
    }

    private void TryBeginSpawnFlow()
    {
        if ( _spawnScheduledOrDone )
            return;

        var online = Networking.IsActive || Networking.IsConnecting;

        if ( online )
        {
            if ( Networking.IsHost )
            {
                _spawnScheduledOrDone = true;
                ScheduleSpawnAfterDelay();
                return;
            }

            if ( _hostReadyRetries++ < HostReadyMaxRetries )
            {
                Invoke( HostReadyRetryIntervalSeconds, TryBeginSpawnFlow );
                return;
            }

            _spawnScheduledOrDone = true;
            if ( Networking.IsActive && !Networking.IsHost )
                Log.Warning( "[ArenaStartBallSpawner] Pas l'hote apres attente — pas de spawn (client ou timing)." );
            else
                Log.Warning( "[ArenaStartBallSpawner] Timeout sans devenir host — pas de spawn (evite balles hors reseau)." );
            return;
        }

        _spawnScheduledOrDone = true;
        ScheduleSpawnAfterDelay();
    }

    private void ScheduleSpawnAfterDelay()
    {
        var extra = SpawnDelaySeconds <= 0f ? 0f : SpawnDelaySeconds;
        if ( extra > 0f )
            Invoke( extra, SpawnBalls );
        else
            SpawnBalls();
    }

    [Button]
    public void SpawnBalls()
    {
        if ( !BallPrefab.IsValid() || BallCount <= 0 )
            return;

        if ( Networking.IsActive && !Networking.IsHost )
            return;

        var centerGo = CenterPoint is not null && CenterPoint.IsValid() ? CenterPoint : GameObject;
        var center = centerGo.WorldPosition;
        var mid = (BallCount - 1) * 0.5f;
        var up = Vector3.Up;
        var dir = PlanarSpreadDirection( SpreadDirection, up );

        for ( var i = 0; i < BallCount; i++ )
        {
            var pos = center + up * SpawnHeightLift + dir * (i - mid) * HorizontalSpacing;
            var clone = BallPrefab.Clone( pos, Rotation.Identity );
            if ( clone is null || !clone.IsValid() )
                continue;

            // Ne pas laisser la balle enfant d'un GO Snapshot (ex. GameMode) : physique / ownership incorrects.
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
                    Log.Error( $"[ArenaStartBallSpawner] NetworkSpawn a echoue : {e.Message}" );
                }
            }

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
        }

        Log.Info( $"[ArenaStartBallSpawner] {BallCount} balle(s). networking={Networking.IsActive} host={Networking.IsHost}" );
    }

    private static Vector3 PlanarSpreadDirection( Vector3 spreadHint, Vector3 worldUp )
    {
        var u = worldUp.Normal;
        var v = spreadHint;
        if ( v.LengthSquared < 0.0001f )
            v = Vector3.Right;

        var horizontal = v - u * Vector3.Dot( v, u );
        if ( horizontal.LengthSquared < 0.0001f )
        {
            var cross = Vector3.Cross( u, Vector3.Right );
            if ( cross.LengthSquared < 0.0001f )
                cross = Vector3.Cross( u, Vector3.Forward );

            horizontal = cross;
        }

        return horizontal.Normal;
    }
}
