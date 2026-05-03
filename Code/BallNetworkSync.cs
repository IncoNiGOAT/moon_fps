using Sandbox;

/// <summary>
/// Multijoueur : la balle est un objet réseau. Le client qui la porte prend <see cref="GameObject.NetworkAccessor.TakeOwnership"/>
/// et simule localement (tenue + lancer) ; les autres reçoivent transform / état via sync.
/// </summary>
public sealed class BallNetworkSync : Component, Component.INetworkSpawn
{
    [Sync] public GameObject NetHolder { get; set; }
    [Sync] public GameObject NetLastThrower { get; set; }
    [Sync] public bool NetIsThrown { get; set; }
    [Sync] public int NetStateVersion { get; set; }

    private BallPickup _ball;
    private Rigidbody _rigidbody;

    private GameObject _lastSeenHolder;
    private GameObject _lastSeenThrower;
    private bool _lastSeenIsThrown;
    private int _lastSeenStateVersion;
    private bool _lastSeenIsOwner;
    private bool _optimisticBallOwnership;

    private bool _interpolationConfigured;
    private bool _takeoverRulesConfigured;

    /// <summary> True quand interpolation + takeover sont en place (évite de spammer <see cref="RunNetworkSpawnBootstrap"/>). </summary>
    private bool _networkSpawnBootstrapComplete;

    private void EnsureRefs()
    {
        _ball ??= Components.Get<BallPickup>() ?? Components.GetInChildren<BallPickup>( true );
        _rigidbody ??= Components.Get<Rigidbody>() ?? Components.GetInChildren<Rigidbody>( true );
    }

    protected override void OnAwake()
    {
        EnsureRefs();
    }

    /// <summary>
    /// Après <c>Clone</c> + <c>NetworkSpawn()</c>, <see cref="GameObject.NetworkAccessor.Active"/> peut encore être faux dans <see cref="OnStart"/>.
    /// On réessaie chaque frame jusqu'à appliquer Takeover / interpolation côté hôte.
    /// </summary>
    private void TryConfigureNetworkedBallRules()
    {
        if ( !Networking.IsActive || !GameObject.Network.Active )
            return;

        var changed = false;

        if ( !_interpolationConfigured )
        {
            GameObject.Network.Interpolation = true;
            _interpolationConfigured = true;
            changed = true;
        }

        // Takeover doit exister sur chaque machine (pas seulement l’hôte), sinon TakeOwnership au pickup/lancer
        // peut rester sans effet sur les balles spawnées plus tard en session.
        if ( !_takeoverRulesConfigured )
        {
            GameObject.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
            if ( Networking.IsHost )
                GameObject.Network.SetOrphanedMode( NetworkOrphaned.Host );
            _takeoverRulesConfigured = true;
            changed = true;
        }

        if ( changed )
        {
            UpdateLocalPhysicsBasedOnOwnership();
            SnapshotLastSeen();
        }
    }

    protected override void OnStart()
    {
        EnsureRefs();

        if ( !Networking.IsActive )
        {
            TryConfigureNetworkedBallRules();
            UpdateLocalPhysicsBasedOnOwnership();
            SnapshotLastSeen();
            ApplyHolderFromNetwork();
            ApplyThrownFromNetwork();
            _networkSpawnBootstrapComplete = true;
            return;
        }

        // En ligne : ordre indéterminé vs <see cref="INetworkSpawn"/> / autres composants — plusieurs passes.
        Invoke( 0f, RunNetworkSpawnBootstrap );
        Invoke( 0.05f, RunNetworkSpawnBootstrap );
        Invoke( 0.15f, RunNetworkSpawnBootstrap );
    }

    /// <summary>
    /// Réapplique config + état sync ; peut tourner avant que <see cref="BallPickup"/> soit prêt si appelé trop tôt
    /// (d&apos;où les <see cref="Component.Invoke"/> et le repli <see cref="OnUpdate"/>).
    /// </summary>
    private void RunNetworkSpawnBootstrap()
    {
        EnsureRefs();
        if ( _ball is null )
            return;

        if ( !Networking.IsActive || !GameObject.Network.Active )
            return;

        TryConfigureNetworkedBallRules();
        UpdateLocalPhysicsBasedOnOwnership();
        SnapshotLastSeen();
        ApplyHolderFromNetwork();
        ApplyThrownFromNetwork();

        if ( _interpolationConfigured && _takeoverRulesConfigured )
            _networkSpawnBootstrapComplete = true;
    }

    /// <summary>
    /// Quand l&apos;objet est spawné sur le réseau (join en cours de partie, balles après chargement, etc.).
    /// </summary>
    public void OnNetworkSpawn( Connection connection )
    {
        _interpolationConfigured = false;
        _takeoverRulesConfigured = false;
        _networkSpawnBootstrapComplete = false;

        Invoke( 0f, RunNetworkSpawnBootstrap );
        Invoke( 0.05f, RunNetworkSpawnBootstrap );
        Invoke( 0.15f, RunNetworkSpawnBootstrap );
    }

    protected override void OnUpdate()
    {
        if ( !Networking.IsActive )
            return;

        if ( GameObject.Network.Active && !_networkSpawnBootstrapComplete )
            RunNetworkSpawnBootstrap();

        TryConfigureNetworkedBallRules();

        var weAreOwnerNow = GameObject.Network.IsOwner;
        if ( weAreOwnerNow )
            _optimisticBallOwnership = false;

        var effectiveOwner = weAreOwnerNow || _optimisticBallOwnership;
        if ( effectiveOwner != _lastSeenIsOwner )
        {
            _lastSeenIsOwner = effectiveOwner;
            UpdateLocalPhysicsBasedOnOwnership();
        }

        DetectStateChanges();
    }

    private void UpdateLocalPhysicsBasedOnOwnership()
    {
        if ( _ball is null )
            return;

        // IsOwner peut rester faux sur l’hôte pour une balle « orpheline » alors que l’hôte la simule
        // (voir doc IsProxy : non-proxy = simulé localement, ex. balle sans owner côté serveur).
        var weAreOwner = !Networking.IsActive
            || GameObject.Network.IsOwner
            || _optimisticBallOwnership
            || (Networking.IsHost && !GameObject.Network.IsProxy);
        _ball.LocalPhysicsInhibited = !weAreOwner;

        if ( _rigidbody is null )
            return;

        if ( !weAreOwner )
        {
            _rigidbody.Velocity = Vector3.Zero;
            _rigidbody.AngularVelocity = Vector3.Zero;
            _rigidbody.Enabled = false;
        }
        else if ( !_rigidbody.Enabled )
        {
            _rigidbody.Enabled = true;
        }
    }

    public bool RequestPickup( GameObject player, GameObject holdPoint )
    {
        EnsureRefs();
        if ( _ball is null || _ball.IsHeld )
            return false;

        if ( !Networking.IsActive )
            return _ball.ApplyPickupLocal( player, holdPoint );

        if ( !GameObject.Network.IsOwner )
        {
            if ( !GameObject.Network.TakeOwnership() )
                return false;
            _optimisticBallOwnership = true;
        }

        _ball.LocalPhysicsInhibited = false;
        if ( _rigidbody is not null && !_rigidbody.Enabled )
            _rigidbody.Enabled = true;
        _lastSeenIsOwner = true;

        var ok = _ball.ApplyPickupLocal( player, holdPoint );
        if ( ok )
        {
            NetHolder = player;
            NetIsThrown = false;
            NetLastThrower = null;
            NetStateVersion++;
            SnapshotLastSeen();
        }
        else
            _optimisticBallOwnership = false;

        return ok;
    }

    /// <returns> Faux si le lancer n’a pas pu s’appliquer (ex. <see cref="GameObject.NetworkAccessor.TakeOwnership"/> refusé). </returns>
    public bool RequestThrow( Vector3 direction, float? customForce, float? releaseWorldUpOverride, float? releaseLateralOverride )
    {
        EnsureRefs();
        if ( _ball is null || !_ball.IsHeld )
            return false;

        if ( !Networking.IsActive )
        {
            _ball.ApplyThrowLocal( direction, customForce, releaseWorldUpOverride, releaseLateralOverride );
            return true;
        }

        if ( !GameObject.Network.IsOwner && !_optimisticBallOwnership )
        {
            if ( !GameObject.Network.TakeOwnership() )
                return false;
            _optimisticBallOwnership = true;
            _ball.LocalPhysicsInhibited = false;
            if ( _rigidbody is not null && !_rigidbody.Enabled )
                _rigidbody.Enabled = true;
            _lastSeenIsOwner = true;
        }

        _ball.ApplyThrowLocal( direction, customForce, releaseWorldUpOverride, releaseLateralOverride );
        // Ne pas remettre _optimisticBallOwnership à false ici : IsOwner peut rester faux 1+ frames après
        // TakeOwnership → sinon OnUpdate remet LocalPhysicsInhibited et annule la vélocité du lancer.

        NetHolder = null;
        NetIsThrown = true;
        NetLastThrower = _ball.LastThrower;
        NetStateVersion++;
        SnapshotLastSeen();
        return true;
    }

    public void OnHostBallCleaned()
    {
        if ( !Networking.IsActive || !GameObject.Network.IsOwner )
            return;

        NetIsThrown = false;
        NetLastThrower = null;
        NetStateVersion++;
        SnapshotLastSeen();
    }

    private void DetectStateChanges()
    {
        // Même logique que effectiveOwner : évite qu’un ack réseau écrase le lancer local pendant la fenêtre TakeOwnership.
        if ( GameObject.Network.IsOwner || _optimisticBallOwnership )
        {
            SnapshotLastSeen();
            return;
        }

        var versionChanged = NetStateVersion != _lastSeenStateVersion;
        var holderChanged = !ReferenceEquals( NetHolder, _lastSeenHolder );
        var thrownChanged = NetIsThrown != _lastSeenIsThrown;
        var throwerChanged = !ReferenceEquals( NetLastThrower, _lastSeenThrower );

        if ( !versionChanged && !holderChanged && !thrownChanged && !throwerChanged )
            return;

        if ( holderChanged || versionChanged )
            ApplyHolderFromNetwork();

        if ( thrownChanged || throwerChanged || versionChanged )
            ApplyThrownFromNetwork();

        SnapshotLastSeen();
    }

    private void ApplyHolderFromNetwork()
    {
        var newHolder = NetHolder;
        var ball = _ball;
        if ( ball is null )
            return;

        if ( newHolder is not null && newHolder.IsValid() )
        {
            if ( ball.IsHeld && ReferenceEquals( ball.Holder, newHolder ) )
                return;

            if ( ball.IsHeld )
                ball.ApplyForceReleaseFromNetwork();

            var carrier = newHolder.Components.Get<BallCarrier>() ?? newHolder.Components.GetInChildren<BallCarrier>( true );
            var holdPoint = carrier?.HoldPoint ?? newHolder;
            ball.ApplyPickupLocal( newHolder, holdPoint );
        }
        else if ( ball.IsHeld )
        {
            ball.ApplyForceReleaseFromNetwork();
        }
    }

    private void ApplyThrownFromNetwork()
    {
        var ball = _ball;
        if ( ball is null )
            return;

        if ( NetIsThrown )
        {
            if ( !ball.IsThrown || !ReferenceEquals( ball.LastThrower, NetLastThrower ) )
                ball.ApplyThrowAckFromNetwork( NetLastThrower );
        }
        else if ( ball.IsThrown )
        {
            ball.ApplyCleanLocal();
        }
    }

    public void BroadcastJailEvent( GameObject victimRoot, GameObject throwerRoot, Vector3 ballVelocity )
    {
        if ( victimRoot is null )
            return;

        if ( !Networking.IsActive )
        {
            ApplyJailLocal( victimRoot, throwerRoot, ballVelocity );
            return;
        }

        if ( !GameObject.Network.IsOwner )
            return;

        BroadcastJailRpc( victimRoot, throwerRoot, ballVelocity );
    }

    [Rpc.Broadcast]
    private void BroadcastJailRpc( GameObject victimRoot, GameObject throwerRoot, Vector3 ballVelocity )
    {
        ApplyJailLocal( victimRoot, throwerRoot, ballVelocity );
    }

    private void ApplyJailLocal( GameObject victimRoot, GameObject throwerRoot, Vector3 ballVelocity )
    {
        if ( victimRoot is null || !victimRoot.IsValid() )
            return;

        if ( _ball is not null && _ball.IsHeld && ReferenceEquals( _ball.Holder, victimRoot ) )
            return;

        var victim = victimRoot.Components.Get<PrisonBallPlayer>()
            ?? victimRoot.Components.GetInChildren<PrisonBallPlayer>( true );
        if ( victim is null )
            return;

        if ( !victim.ShouldJailFromBall( throwerRoot ) )
            return;

        Vector3? jailKnock = ballVelocity.Length > 8f ? ballVelocity : null;
        if ( jailKnock is null )
        {
            var push = ( victim.GameObject.WorldPosition - WorldPosition ).WithZ( 0f );
            if ( push.Length > 0.1f )
                jailKnock = push.Normal * 400f;
        }

        victim.Jail( jailKnock );

        if ( throwerRoot is not null && throwerRoot.IsValid() )
        {
            var throwerPlayer = throwerRoot.Components.Get<PrisonBallPlayer>()
                ?? throwerRoot.Components.GetInChildren<PrisonBallPlayer>( true );
            if ( throwerPlayer is not null && throwerPlayer != victim && throwerPlayer.InPrison )
                throwerPlayer.FreeToArena();
        }
    }

    private void SnapshotLastSeen()
    {
        _lastSeenHolder = NetHolder;
        _lastSeenThrower = NetLastThrower;
        _lastSeenIsThrown = NetIsThrown;
        _lastSeenStateVersion = NetStateVersion;
        _lastSeenIsOwner = !Networking.IsActive
            || GameObject.Network.IsOwner
            || _optimisticBallOwnership;
    }
}
