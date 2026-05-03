using Sandbox;

/// <summary>
/// Multijoueur : props / cubes prison (<see cref="ThrowablePropPickup"/>). Même schéma que <see cref="BallNetworkSync"/>.
/// </summary>
public sealed class ThrowablePropNetworkSync : Component, Component.INetworkSpawn
{
    [Sync] public GameObject NetHolder { get; set; }
    [Sync] public int NetStateVersion { get; set; }
    [Sync] public bool NetLastActionWasThrow { get; set; }
    [Sync] public Vector3 NetThrowDirection { get; set; }
    [Sync] public float NetThrowForce { get; set; }

    private ThrowablePropPickup _prop;
    private Rigidbody _rigidbody;

    private GameObject _lastSeenHolder;
    private int _lastSeenStateVersion;
    private bool _lastSeenIsOwner;
    private bool _optimisticOwnership;

    private bool _interpolationConfigured;
    private bool _takeoverRulesConfigured;
    private bool _networkSpawnBootstrapComplete;

    private void EnsureRefs()
    {
        _prop ??= Components.Get<ThrowablePropPickup>() ?? Components.GetInChildren<ThrowablePropPickup>( true );
        _rigidbody ??= Components.Get<Rigidbody>() ?? Components.GetInChildren<Rigidbody>( true );
    }

    protected override void OnAwake() => EnsureRefs();

    private void TryConfigureNetworkedPropRules()
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

    private void RunNetworkSpawnBootstrap()
    {
        EnsureRefs();
        if ( _prop is null )
            return;

        if ( !Networking.IsActive || !GameObject.Network.Active )
            return;

        TryConfigureNetworkedPropRules();
        UpdateLocalPhysicsBasedOnOwnership();
        SnapshotLastSeen();
        ApplyHolderFromNetwork();

        if ( _interpolationConfigured && _takeoverRulesConfigured )
            _networkSpawnBootstrapComplete = true;
    }

    protected override void OnStart()
    {
        EnsureRefs();

        if ( !Networking.IsActive )
        {
            TryConfigureNetworkedPropRules();
            UpdateLocalPhysicsBasedOnOwnership();
            SnapshotLastSeen();
            ApplyHolderFromNetwork();
            _networkSpawnBootstrapComplete = true;
            return;
        }

        Invoke( 0f, RunNetworkSpawnBootstrap );
        Invoke( 0.05f, RunNetworkSpawnBootstrap );
        Invoke( 0.15f, RunNetworkSpawnBootstrap );
    }

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

        TryConfigureNetworkedPropRules();

        var weAreOwnerNow = GameObject.Network.IsOwner;
        if ( weAreOwnerNow )
            _optimisticOwnership = false;

        var effectiveOwner = weAreOwnerNow || _optimisticOwnership;
        if ( effectiveOwner != _lastSeenIsOwner )
        {
            _lastSeenIsOwner = effectiveOwner;
            UpdateLocalPhysicsBasedOnOwnership();
        }

        DetectStateChanges();
    }

    private void UpdateLocalPhysicsBasedOnOwnership()
    {
        if ( _prop is null )
            return;

        var weAreOwner = !Networking.IsActive
            || GameObject.Network.IsOwner
            || _optimisticOwnership
            || (Networking.IsHost && !GameObject.Network.IsProxy);
        _prop.LocalPhysicsInhibited = !weAreOwner;

        if ( _rigidbody is null )
            return;

        if ( !weAreOwner )
        {
            _rigidbody.Velocity = Vector3.Zero;
            _rigidbody.AngularVelocity = Vector3.Zero;
            _rigidbody.Enabled = false;
        }
        else if ( !_prop.IsHeld && !_rigidbody.Enabled )
            _rigidbody.Enabled = true;
    }

    public bool RequestPickup( GameObject player )
    {
        EnsureRefs();
        if ( _prop is null || _prop.IsHeld )
            return false;

        if ( !Networking.IsActive )
            return _prop.ApplyPickupLocal( player );

        if ( !GameObject.Network.IsOwner )
        {
            if ( !GameObject.Network.TakeOwnership() )
                return false;
            _optimisticOwnership = true;
        }

        _prop.LocalPhysicsInhibited = false;
        var ok = _prop.ApplyPickupLocal( player );
        if ( ok )
        {
            NetLastActionWasThrow = false;
            NetHolder = player;
            NetStateVersion++;
            SnapshotLastSeen();
        }
        else
            _optimisticOwnership = false;

        return ok;
    }

    public bool RequestThrow( Vector3 direction, float? customForce, float? releaseWorldUpOverride )
    {
        EnsureRefs();
        if ( _prop is null || !_prop.IsHeld )
            return false;

        if ( !Networking.IsActive )
        {
            _prop.ApplyThrowLocal( direction, customForce, releaseWorldUpOverride );
            return true;
        }

        if ( !GameObject.Network.IsOwner && !_optimisticOwnership )
        {
            if ( !GameObject.Network.TakeOwnership() )
                return false;
            _optimisticOwnership = true;
        }

        var force = customForce ?? _prop.ThrowForce;
        var dir = direction.Length > 0.001f ? direction.Normal : Vector3.Forward;
        NetLastActionWasThrow = true;
        NetThrowDirection = dir;
        NetThrowForce = force;
        _prop.ApplyThrowLocal( direction, customForce, releaseWorldUpOverride );
        NetHolder = null;
        NetStateVersion++;
        SnapshotLastSeen();
        return true;
    }

    /// <summary> Libération (ex. knockdown prison) : aligne les proxies. </summary>
    public void RequestForceReleaseFromHolder()
    {
        EnsureRefs();
        if ( _prop is null || !_prop.IsHeld )
            return;

        if ( !Networking.IsActive )
        {
            _prop.ApplyForceReleaseLocal();
            return;
        }

        if ( !GameObject.Network.IsOwner && !_optimisticOwnership )
        {
            if ( !GameObject.Network.TakeOwnership() )
            {
                _prop.ApplyForceReleaseLocal();
                return;
            }

            _optimisticOwnership = true;
        }

        NetLastActionWasThrow = false;
        _prop.ApplyForceReleaseLocal();
        NetHolder = null;
        NetStateVersion++;
        SnapshotLastSeen();
    }

    private void DetectStateChanges()
    {
        if ( GameObject.Network.IsOwner || _optimisticOwnership )
        {
            SnapshotLastSeen();
            return;
        }

        var versionChanged = NetStateVersion != _lastSeenStateVersion;
        var holderChanged = !ReferenceEquals( NetHolder, _lastSeenHolder );

        if ( !versionChanged && !holderChanged )
            return;

        ApplyHolderFromNetwork();

        SnapshotLastSeen();
    }

    private void ApplyHolderFromNetwork()
    {
        var newHolder = NetHolder;
        var prop = _prop;
        if ( prop is null )
            return;

        if ( newHolder is not null && newHolder.IsValid() )
        {
            if ( prop.IsHeld && ReferenceEquals( prop.Holder, newHolder ) )
                return;

            if ( prop.IsHeld )
                prop.ApplyForceReleaseLocal();

            prop.ApplyPickupLocal( newHolder );
            return;
        }

        if ( !prop.IsHeld )
            return;

        if ( NetLastActionWasThrow )
        {
            var dir = NetThrowDirection.LengthSquared > 0.0001f ? NetThrowDirection.Normal : Vector3.Forward;
            var forceArg = NetThrowForce > 0.001f ? NetThrowForce : (float?)null;
            prop.ApplyThrowLocal( dir, forceArg, null );
            return;
        }

        prop.ApplyForceReleaseLocal();
    }

    private void SnapshotLastSeen()
    {
        _lastSeenHolder = NetHolder;
        _lastSeenStateVersion = NetStateVersion;
        _lastSeenIsOwner = !Networking.IsActive || GameObject.Network.IsOwner || _optimisticOwnership;
    }
}
