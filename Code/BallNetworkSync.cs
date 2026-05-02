using Sandbox;

/// <summary>
/// Couche reseau "CS-AK style" pour la balle. A poser sur le meme GameObject que <see cref="BallPickup"/>.
///
/// Modele : <b>owner-driven</b>. Quand un joueur prend la balle, il en devient l'owner reseau
/// (<see cref="GameObject.NetworkAccessor.TakeOwnership"/>). L'owner simule TOUT en local :
/// position quand il la tient (suivi main), physique quand il la lance (rigidbody actif). Le
/// transform et la velocite sont sync via NetworkMode = Object → les autres clients voient le
/// resultat smooth (avec interpolation native).
///
/// Resultat : aucun delay, aucune prediction, aucun snap. Le owner a le meme feeling qu'en solo,
/// les autres voient la balle bouger via le sync (avec leur ping vers le owner, c'est inevitable).
/// Quand la balle se "calme" (rebonds max), l'owner drop l'ownership → l'host reprend la main pour
/// le prochain ramassage.
///
/// Evenements gameplay (jail / liberation) : detectes par l'owner via <see cref="BallPickup"/>,
/// puis broadcast a tous les clients via RPC pour qu'ils appliquent l'effet sur leur copie locale
/// du joueur cible.
/// </summary>
public sealed class BallNetworkSync : Component
{
    /// <summary>
    /// Etat reseau replique <b>depuis l'owner courant</b> (pas FromHost). Quand un client prend
    /// la balle, il devient owner et c'est lui qui pousse le state.
    /// </summary>
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

    protected override void OnStart()
    {
        _ball = Components.Get<BallPickup>();
        _rigidbody = Components.Get<Rigidbody>();

        if ( Networking.IsActive && GameObject.Network.Active )
        {
            // L'host configure : n'importe qui peut prendre l'ownership (pickup),
            // si l'owner se deconnecte l'host reprend la main.
            if ( Networking.IsHost )
            {
                GameObject.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
                GameObject.Network.SetOrphanedMode( NetworkOrphaned.Host );
            }
            GameObject.Network.Interpolation = true;
        }

        UpdateLocalPhysicsBasedOnOwnership();
        SnapshotLastSeen();

        // Si on join une session avec une balle deja tenue : applique l'etat localement.
        ApplyHolderFromNetwork();
        ApplyThrownFromNetwork();
    }

    protected override void OnUpdate()
    {
        if ( !Networking.IsActive )
            return;

        // Detecte les changements d'ownership (ex. un autre client a pris la balle pendant
        // qu'on l'avait optimistiquement pris). Met a jour l'inhibition de la physique.
        var weAreOwnerNow = GameObject.Network.IsOwner;
        if ( weAreOwnerNow != _lastSeenIsOwner )
        {
            _lastSeenIsOwner = weAreOwnerNow;
            UpdateLocalPhysicsBasedOnOwnership();
        }

        DetectStateChanges();
    }

    /// <summary>
    /// Owner = simule physique localement (rigidbody TOUJOURS actif, freeze velocite quand tenu).
    /// Proxy = laisse le sync moteur driver le transform (rigidbody desactive, zero conflit).
    ///
    /// Important : on n'utilise pas un toggle Enabled false→true pendant le throw, car dans s&box
    /// ca peut casser le setter Velocity au reactivation. On garde le rigidbody actif cote owner
    /// en permanence et on freeze la velocite pendant la tenue (cf. BallPickup.OnUpdate / ApplyPickupLocal).
    /// </summary>
    private void UpdateLocalPhysicsBasedOnOwnership()
    {
        if ( _ball is null )
            return;

        var weAreOwner = !Networking.IsActive || GameObject.Network.IsOwner;
        _ball.LocalPhysicsInhibited = !weAreOwner;

        if ( _rigidbody is null )
            return;

        if ( !weAreOwner )
        {
            // Proxy : kill toute physique locale, le sync moteur drive le transform.
            _rigidbody.Velocity = Vector3.Zero;
            _rigidbody.AngularVelocity = Vector3.Zero;
            _rigidbody.Enabled = false;
        }
        else
        {
            // Owner : assure que le rigidbody est actif (on simule). Si on freeze a 0,0,0
            // pendant la tenue, OnUpdate dans BallPickup s'en charge chaque frame.
            if ( !_rigidbody.Enabled )
                _rigidbody.Enabled = true;
        }
    }

    #region API appelee par BallPickup (pickup / throw)
    public bool RequestPickup( GameObject player, GameObject holdPoint )
    {
        if ( _ball is null || _ball.IsHeld )
            return false;

        if ( !Networking.IsActive )
            return _ball.ApplyPickupLocal( player, holdPoint );

        // CS-AK : on prend l'ownership de la balle. On devient le simulateur authoritative.
        // L'OwnerTransfer = Takeover dans la scene (ou force par l'host dans OnStart) le permet.
        if ( !GameObject.Network.IsOwner )
        {
            var taken = GameObject.Network.TakeOwnership();
            if ( !taken )
                return false;
        }

        // OPTIMISTE : TakeOwnership a succedee mais Network.IsOwner peut prendre 1 round-trip
        // pour devenir true cote local (latence reseau). On force directement l'etat "owner local"
        // pour que OnUpdate puisse driver la position de la balle vers la main *immediatement*,
        // sans attendre la confirmation reseau (sinon delai 1-2s a haut ping).
        _ball.LocalPhysicsInhibited = false;
        if ( _rigidbody is not null && !_rigidbody.Enabled )
            _rigidbody.Enabled = true;
        _lastSeenIsOwner = true;

        var ok = _ball.ApplyPickupLocal( player, holdPoint );
        if ( ok )
        {
            // [Sync] sans FromHost : on est owner, c'est nous qui poussons l'etat aux autres.
            NetHolder = player;
            NetIsThrown = false;
            NetLastThrower = null;
            NetStateVersion++;
            SnapshotLastSeen();
        }
        return ok;
    }

    public void RequestThrow( Vector3 direction, float? customForce, float? releaseWorldUpOverride, float? releaseLateralOverride )
    {
        if ( _ball is null || !_ball.IsHeld )
            return;

        if ( !Networking.IsActive )
        {
            _ball.ApplyThrowLocal( direction, customForce, releaseWorldUpOverride, releaseLateralOverride );
            return;
        }

        // On doit etre owner (pris l'ownership au pickup). Si race, on refuse.
        if ( !GameObject.Network.IsOwner )
            return;

        // ApplyThrowLocal active le rigidbody localement (LocalPhysicsInhibited = false car owner)
        // et applique la velocite. Le sync moteur de NetworkMode=Object envoie la position aux
        // autres clients en continu pendant le vol (smooth interpolation chez eux).
        _ball.ApplyThrowLocal( direction, customForce, releaseWorldUpOverride, releaseLateralOverride );

        NetHolder = null;
        NetIsThrown = true;
        NetLastThrower = _ball.LastThrower;
        NetStateVersion++;
        SnapshotLastSeen();
    }
    #endregion

    /// <summary>
    /// Appele par <see cref="BallPickup"/> apres un clean cote owner (rebonds max atteints).
    /// On notifie tous les autres clients du changement d'etat (rouge/bleu → blanc).
    ///
    /// IMPORTANT : on NE drop PAS l'ownership ici. La balle est encore en l'air, en train de
    /// rebondir physiquement ; lacher l'ownership transfererait la simulation a l'host avec
    /// une velocite a 0 (le proxy host avait zero la velocite) → la balle se figerait au mur.
    /// Le owner garde la main jusqu'au prochain pickup (Takeover) ou disconnect (NetworkOrphaned.Host).
    /// </summary>
    public void OnHostBallCleaned()
    {
        if ( !Networking.IsActive )
            return;

        if ( !GameObject.Network.IsOwner )
            return;

        NetIsThrown = false;
        NetLastThrower = null;
        NetStateVersion++;
        SnapshotLastSeen();
    }

    #region Reception du sync (proxies)
    private void DetectStateChanges()
    {
        // Si on est owner, c'est nous qui poussons : pas de detection a faire.
        if ( GameObject.Network.IsOwner )
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
        else
        {
            if ( ball.IsHeld )
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
        else
        {
            if ( ball.IsThrown )
                ball.ApplyCleanLocal();
        }
    }
    #endregion

    #region Evenements gameplay : broadcast jail
    /// <summary>
    /// Appele par <see cref="BallPickup"/> quand l'owner detecte une collision qui doit jailer
    /// un joueur. On replique l'evenement a tous les clients via RPC pour qu'ils appliquent
    /// l'effet sur leur copie locale du joueur cible (ragdoll prison + TP).
    /// </summary>
    public void BroadcastJailEvent( GameObject victimRoot, GameObject throwerRoot, Vector3 ballVelocity )
    {
        if ( victimRoot is null )
            return;

        if ( !Networking.IsActive )
        {
            // Solo : pas besoin de RPC, applique direct.
            ApplyJailLocal( victimRoot, throwerRoot, ballVelocity );
            return;
        }

        // Le owner (qui a detecte la collision) declenche le broadcast pour tous.
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

        // Anti-race "catch vs kill" : si la victime a deja attrape la balle localement (catch
        // optimiste via E + TakeOwnership) avant que ce broadcast jail n'arrive, on ignore le
        // jail. Le catch a gagne la course cote victime, c'est lui qui fait foi.
        if ( _ball is not null && _ball.IsHeld && ReferenceEquals( _ball.Holder, victimRoot ) )
            return;

        var victim = victimRoot.Components.Get<PrisonBallPlayer>()
            ?? victimRoot.Components.GetInChildren<PrisonBallPlayer>( true );
        if ( victim is null )
            return;

        if ( !victim.ShouldJailFromBall( throwerRoot ) )
            return;

        Vector3? jailKnock = null;
        if ( ballVelocity.Length > 8f )
        {
            jailKnock = ballVelocity;
        }
        else
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
    #endregion

    private void SnapshotLastSeen()
    {
        _lastSeenHolder = NetHolder;
        _lastSeenThrower = NetLastThrower;
        _lastSeenIsThrown = NetIsThrown;
        _lastSeenStateVersion = NetStateVersion;
        _lastSeenIsOwner = !Networking.IsActive || GameObject.Network.IsOwner;
    }
}
