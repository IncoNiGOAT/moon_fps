using Sandbox;
using System.Collections.Generic;

/// <summary>
/// Ramassage / lancer. Prefab : Network Object, Owner Transfer <b>Fixed</b>, Orphaned <b>Host</b>, Always Transmit si dispo.
/// Vol : tout le monde applique la même pose au RPC ; physique + vélocité uniquement hôte ; interpolation réseau reste active (ne pas spammer ClearInterpolation).
/// </summary>
public sealed class BallPickup : Component, Component.ICollisionListener
{
    [Property] public float ThrowForce { get; set; } = 1200f;

    /// <summary> Décale le point de départ le long de la direction de lancer (unités). </summary>
    [Property] public float ThrowSpawnOffset { get; set; } = 26f;

    /// <summary>
    /// Décalage monde +Z au <b>lâcher uniquement</b> (pas en main). Compense une main basse sans anim de visée.
    /// </summary>
    [Property] public float ThrowReleaseWorldUpOffset { get; set; } = 0f;

    /// <summary>
    /// Décalage gauche / droite au lâcher (unités monde), perpendiculaire au tir dans le plan horizontal. Positif = droite.
    /// </summary>
    [Property] public float ThrowReleaseLateralOffset { get; set; } = 0f;

    [Property] public float ThrowerGraceTime { get; set; } = 0.15f;
    [Property] public int MaxKillBounces { get; set; } = 1;

    /// <summary> Rebonds en vol : incrémenté sur l’hôte, répliqué (teinte / passes / clients &amp; join). </summary>
    [Sync( SyncFlags.FromHost )] public int NetThrowSurfaceHits { get; set; }

    [Property] public Color NeutralTint { get; set; } = Color.White;
    [Property] public Color AttackTintRed { get; set; } = new Color( 1f, 0.25f, 0.25f );
    [Property] public Color AttackTintBlue { get; set; } = new Color( 0.25f, 0.45f, 1f );

    /// <summary>
    /// Si vrai (défaut), les <see cref="ParticleEffect"/> sous la balle simulent en <b>espace local</b> :
    /// la lueur suit la balle au lieu de laisser des particules « figées » en monde au lancer.
    /// </summary>
    [Property] public bool ParticlesFollowBallTransform { get; set; } = true;

    public bool IsHeld { get; private set; }
    public bool IsThrown { get; private set; }
    public GameObject Holder { get; private set; }
    public GameObject LastThrower { get; private set; }

    /// <summary> Vitesse linéaire juste après le dernier <see cref="Throw"/> (pour <see cref="BallAttackTrailDriver"/>). </summary>
    public float LastThrowSpeed { get; private set; }

    /// <summary> Teinte actuelle mesh / FX (même valeur que pour le trail). </summary>
    public Color CurrentVisualTint { get; private set; } = Color.White;

    private Rigidbody _rigidbody;
    private GameObject _holdPoint;
    private SkinnedModelRenderer _holderRenderer;
    private readonly List<Collider> _disabledColliders = new();
    private TimeSince _timeSinceThrow;
    private int _throwBounceCount;
    /// <summary> Citizen : <c>hand_R</c> ; Mixamo / autres rigs : noms alternatifs en secours. </summary>
    private static readonly string[] RightHandBoneNames =
    {
        "hand_R",
        "hand_r",
        "ValveBiped.Bip01_R_Hand",
        "right_hand",
        "r_hand",
        "RightHand",
        "mixamorig:RightHand",
        "R_Hand",
        "Hand_R",
        "Bip01 R Hand",
    };
    [Property] public float HandForwardOffset { get; set; } = 6f;
    [Property] public float HandRightOffset { get; set; } = 2f;

    /// <summary> Offset main en port (axe main.up). Pour visée trop basse au tir, préférer <see cref="ThrowReleaseWorldUpOffset"/>. </summary>
    [Property] public float HandUpOffset { get; set; } = -2f;
    private readonly List<ModelRenderer> _modelRenderers = new();
    private readonly List<SkinnedModelRenderer> _skinnedRenderers = new();
    private readonly List<ParticleEffect> _ballParticleEffects = new();
    private bool _devAttackFlightPreview;
    private GameObject _devFakeThrowerForTint;
    private TeamId? _devForcedAttackTintTeam;
    private float _devSimulatedLinearSpeedForTrail;
    private float _devPreviewMotionTimeScale = 1f;

    /// <summary> Vitesse linéaire simulée (scène dev) pour le multiplicateur « vitesse » du <see cref="BallAttackTrailDriver"/> ; 0 en jeu réel. </summary>
    public float DevSimulatedLinearSpeedForTrail => _devSimulatedLinearSpeedForTrail;

    /// <summary> Facteur de ralenti / accélération du mouvement preview (1 = normal) ; le driver trail l’utilise pour <see cref="TrailRenderer.PointDistance"/>. </summary>
    public float DevPreviewMotionTimeScale => _devPreviewMotionTimeScale;

    /// <summary>
    /// Scènes dev (ex. <c>ball_fx_dev</c>) : simule un vol attaque pour trail + teintes sans arène.
    /// Désactive avec <c>active: false</c> avant build si tu laisses le composant sur un prefab.
    /// </summary>
    /// <param name="fakeLastThrowSpeedForTrail">Si &gt; 0 pendant <paramref name="active"/>, alimente <see cref="LastThrowSpeed"/> pour le multiplicateur « puissance » du trail.</param>
    /// <param name="devSimulatedLinearSpeedForTrail">Vitesse linéaire (u/s) pour le multiplicateur « vitesse » (ex. tangente d’orbite) ; ignorée hors preview.</param>
    /// <param name="devPreviewMotionTimeScale">Multiplie le déplacement d’oscillation preview ; 1 = inchangé. Compense <see cref="TrailRenderer.PointDistance"/> dans <see cref="BallAttackTrailDriver"/>.</param>
    public void SetDevAttackFlightPreview( bool active, GameObject fakeThrowerForTeamTint = null, TeamId? forcedAttackTintTeam = null, float fakeLastThrowSpeedForTrail = 0f, float devSimulatedLinearSpeedForTrail = 0f, float devPreviewMotionTimeScale = 1f )
    {
        _devAttackFlightPreview = active;
        _devFakeThrowerForTint = fakeThrowerForTeamTint;
        _devForcedAttackTintTeam = forcedAttackTintTeam;
        if ( !active )
        {
            _devSimulatedLinearSpeedForTrail = 0f;
            _devPreviewMotionTimeScale = 1f;
        }
        else
        {
            _devSimulatedLinearSpeedForTrail = devSimulatedLinearSpeedForTrail;
            _devPreviewMotionTimeScale = devPreviewMotionTimeScale.Clamp( 0.05f, 2f );
        }

        if ( active && fakeLastThrowSpeedForTrail > 0f )
            LastThrowSpeed = fakeLastThrowSpeedForTrail;

        RefreshBallTint();
    }

    /// <summary> Vol attaque réel ou preview dev : le trail peut s’activer. </summary>
    public bool IsAttackFlightVisual()
    {
        return (IsThrown && LastThrower is not null && LastThrower.IsValid()) || _devAttackFlightPreview;
    }

    protected override void OnStart()
    {
        _rigidbody = Components.Get<Rigidbody>();
        EnsureBallContinuousCollision();
        CacheBallRenderers();
        ApplyParticlesFollowBallTransform();
        RefreshBallTint();

        if ( Networking.IsActive && !GameObject.Network.Active )
        {
            Log.Warning(
                $"[BallPickup] « {GameObject.Name} » : Network désactivé — ramassage / lancer ne se synchroniseront pas. Active Network sur le prefab balle." );
        }
        else if ( GameObject.Network.Active )
        {
            GameObject.Network.Interpolation = true;
            // Évite Destroy si le porteur quitte ; l’hôte reprend la balle (aligné vol = simu hôte).
            GameObject.Network.SetOrphanedMode( NetworkOrphaned.Host );
        }
    }

    /// <summary>
    /// Vitesses de lancer élevées + murs fins = tunneling sans CCD ; indispensable pour que la balle ne traverse pas les <see cref="InvisibleBarrier"/>.
    /// </summary>
    private void EnsureBallContinuousCollision()
    {
        if ( _rigidbody is null || !_rigidbody.IsValid() )
            return;

        _rigidbody.EnhancedCcd = true;
    }

    protected override void OnUpdate()
    {
        SyncThrowSurfaceStateFromHost();

        if ( !IsHeld )
            return;

        // Sinon les proxies écrasent la pose chaque frame → conflit avec la synchro réseau / saccades.
        if ( Networking.IsActive && GameObject.Network.Active && !GameObject.Network.IsOwner )
            return;

        if ( TryGetRightHandTransform( out var handTx ) )
        {
            WorldPosition = handTx.Position
                + handTx.Rotation.Forward * HandForwardOffset
                + handTx.Rotation.Right * HandRightOffset
                + handTx.Rotation.Up * HandUpOffset;
            WorldRotation = handTx.Rotation;
            return;
        }

        if ( _holdPoint is not null )
        {
            WorldPosition = _holdPoint.WorldPosition;
            WorldRotation = _holdPoint.WorldRotation;
        }
    }

    /// <summary>
    /// Balle encore dangereuse pour ce joueur : lancée par un adversaire (teinte attaque) et pas encore &quot;nettoyée&quot; par les rebonds.
    /// </summary>
    public bool IsLiveEnemyBallFor( GameObject pickerRoot )
    {
        if ( !IsThrown || LastThrower is null || !LastThrower.IsValid() || pickerRoot is null || !pickerRoot.IsValid() )
            return false;

        return GetTeamOnRoot( LastThrower ) != GetTeamOnRoot( pickerRoot );
    }

    /// <summary>
    /// Passe allié &quot;propre&quot; : même équipe, autre joueur, et pas plus de <paramref name="maxBouncesSinceThrow"/> rebonds depuis le lancer.
    /// </summary>
    public bool IsCleanAllyPassFor( GameObject pickerRoot, int maxBouncesSinceThrow = 0 )
    {
        if ( !IsThrown || LastThrower is null || !LastThrower.IsValid() || pickerRoot is null || !pickerRoot.IsValid() )
            return false;

        var maxB = maxBouncesSinceThrow < 0 ? 0 : maxBouncesSinceThrow;
        if ( EffectiveThrowSurfaceHitCount() > maxB )
            return false;

        if ( GetTeamOnRoot( LastThrower ) != GetTeamOnRoot( pickerRoot ) )
            return false;

        return !IsSamePlayerRoot( LastThrower, pickerRoot );
    }

    public static bool IsSamePlayerRoot( GameObject a, GameObject b )
    {
        if ( a is null || b is null || !a.IsValid() || !b.IsValid() )
            return false;

        if ( ReferenceEquals( a, b ) )
            return true;

        if ( TryGetNetworkRoot( a, out var na ) && TryGetNetworkRoot( b, out var nb ) && na is not null && nb is not null && ReferenceEquals( na, nb ) )
            return true;

        if ( IsSameOrChildOf( a, b ) || IsSameOrChildOf( b, a ) )
            return true;

        return false;
    }

    public bool PickUp( GameObject player, GameObject holdPoint )
    {
        if ( IsHeld || holdPoint is null || player is null || !player.IsValid() )
            return false;

        if ( Networking.IsActive )
        {
            BroadcastPickUp( player, holdPoint );
            return true;
        }

        return ApplyPickUpState( player, holdPoint );
    }

    [Rpc.Broadcast]
    private void BroadcastPickUp( GameObject player, GameObject holdPoint )
    {
        ApplyPickUpState( player, holdPoint );
    }

    private bool ApplyPickUpState( GameObject player, GameObject holdPoint )
    {
        if ( IsHeld || holdPoint is null || player is null || !player.IsValid() || !holdPoint.IsValid() )
            return false;

        IsHeld = true;
        IsThrown = false;
        LastThrowSpeed = 0f;
        _throwBounceCount = 0;
        if ( Networking.IsActive && Networking.IsHost )
            NetThrowSurfaceHits = 0;

        Holder = player;
        _holdPoint = holdPoint;
        _holderRenderer = ResolveHolderBodySkinnedRenderer( player );

        SetCollidersEnabled( false );

        if ( _rigidbody is not null )
        {
            _rigidbody.Velocity = Vector3.Zero;
            _rigidbody.AngularVelocity = Vector3.Zero;
            _rigidbody.Enabled = false;
        }

        GameObject.Parent = null;

        RefreshBallTint();
        RegisterHolderCarrierHeldBall( player );

        if ( Networking.IsActive && Networking.IsHost && GameObject.Network.Active )
            TryAssignBallOwnershipToHolder( player );

        return true;
    }

    private void RegisterHolderCarrierHeldBall( GameObject player )
    {
        var carrier = player.Components.Get<BallCarrier>() ?? player.Components.GetInChildren<BallCarrier>( true );
        carrier?.NetSetHeldBallFromNetwork( this );
    }

    /// <param name="releaseWorldUpOverride">Si renseigné, remplace <see cref="ThrowReleaseWorldUpOffset"/> pour ce lancer.</param>
    /// <param name="releaseLateralOverride">Si renseigné, remplace <see cref="ThrowReleaseLateralOffset"/> pour ce lancer.</param>
    public void Throw( Vector3 direction, float? customForce = null, float? releaseWorldUpOverride = null, float? releaseLateralOverride = null )
    {
        if ( !IsHeld )
            return;

        if ( Networking.IsActive )
        {
            BroadcastThrow(
                direction,
                customForce ?? 0f,
                customForce.HasValue,
                releaseWorldUpOverride ?? 0f,
                releaseWorldUpOverride.HasValue,
                releaseLateralOverride ?? 0f,
                releaseLateralOverride.HasValue );
            return;
        }

        ApplyThrowState( direction, customForce, releaseWorldUpOverride, releaseLateralOverride );
    }

    [Rpc.Broadcast]
    private void BroadcastThrow(
        Vector3 direction,
        float customForce,
        bool useCustomForce,
        float releaseWorldUpOverride,
        bool useReleaseWorldUpOverride,
        float releaseLateralOverride,
        bool useReleaseLateralOverride )
    {
        float? force = useCustomForce ? customForce : null;
        float? up = useReleaseWorldUpOverride ? releaseWorldUpOverride : null;
        float? lat = useReleaseLateralOverride ? releaseLateralOverride : null;
        ApplyThrowState( direction, force, up, lat );
    }

    private void ApplyThrowState( Vector3 direction, float? customForce, float? releaseWorldUpOverride, float? releaseLateralOverride )
    {
        if ( !IsHeld )
            return;

        var holder = Holder;
        var carrier = holder is not null && holder.IsValid()
            ? holder.Components.Get<BallCarrier>() ?? holder.Components.GetInChildren<BallCarrier>( true )
            : null;
        carrier?.NetClearHeldBallFromNetwork();

        IsHeld = false;
        IsThrown = true;
        _throwBounceCount = 0;
        if ( Networking.IsActive && Networking.IsHost )
            NetThrowSurfaceHits = 0;

        LastThrower = holder;
        Holder = null;
        _holderRenderer = null;
        _timeSinceThrow = 0f;

        var force = customForce ?? ThrowForce;
        var throwDir = direction.Length > 0.001f ? direction.Normal : Vector3.Forward;

        var upExtra = releaseWorldUpOverride ?? ThrowReleaseWorldUpOffset;
        var lateral = releaseLateralOverride ?? ThrowReleaseLateralOffset;
        var releaseRight = HorizontalThrowRight( throwDir );
        var releasePosition = WorldPosition
            + throwDir * ThrowSpawnOffset
            + Vector3.Up * upExtra
            + releaseRight * lateral;
        var releaseRotation = WorldRotation;

        GameObject.Parent = null;
        _holdPoint = null;

        // Même pose sur toutes les machines au moment du RPC (surtout le client qui lance : sinon attente hôte = balle « collée » puis saut).
        WorldPosition = releasePosition;
        WorldRotation = releaseRotation;

        SetCollidersEnabled( true );

        if ( Networking.IsActive && Networking.IsHost && GameObject.Network.Active )
            TryRestoreBallOwnershipToHost();

        if ( _rigidbody is not null )
        {
            if ( !Networking.IsActive || Networking.IsHost )
            {
                _rigidbody.Enabled = true;
                EnsureBallContinuousCollision();
                _rigidbody.Velocity = throwDir * force;
                LastThrowSpeed = _rigidbody.Velocity.Length;
            }
            else
            {
                _rigidbody.Velocity = Vector3.Zero;
                _rigidbody.AngularVelocity = Vector3.Zero;
                _rigidbody.Enabled = false;
                LastThrowSpeed = force;
            }
        }
        else
            LastThrowSpeed = force;

        RefreshBallTint();
    }

    private void TryAssignBallOwnershipToHolder( GameObject player )
    {
        if ( !TryGetNetworkRoot( player, out var root ) || root is null || !root.Network.Active )
            return;

        var conn = root.Network.Owner;
        if ( conn is null )
            return;

        GameObject.Network.AssignOwnership( conn );
    }

    private void TryRestoreBallOwnershipToHost()
    {
        var hostConn = Connection.Host;
        if ( hostConn is null )
            return;

        GameObject.Network.AssignOwnership( hostConn );
    }

    /// <summary> Droite dans le plan horizontal par rapport à la direction de tir (pour offset latéral au lâcher). </summary>
    private static Vector3 HorizontalThrowRight( Vector3 throwDir )
    {
        var f = throwDir.WithZ( 0f );
        if ( f.Length < 0.001f )
            f = throwDir;
        if ( f.Length < 0.001f )
            return Vector3.Right;

        f = f.Normal;
        var r = f.Cross( Vector3.Up );
        return r.Length > 0.001f ? r.Normal : Vector3.Right;
    }

    public void OnCollisionStart( Collision other )
    {
        if ( !IsThrown || IsHeld )
            return;

        var otherObject = other.Other.GameObject;
        if ( otherObject is null )
            return;

        if ( LastThrower is not null && _timeSinceThrow < ThrowerGraceTime )
        {
            if ( IsSameOrChildOf( otherObject, LastThrower ) )
                return;
        }

        var victim = FindPrisonBallPlayer( otherObject );

        if ( victim is null )
        {
            Log.Info( $"[BallPickup] Hit {otherObject.Name} — pas de joueur, rebond." );
            RegisterThrowSurfaceHit();
            return;
        }

        var shouldJail = victim.ShouldJailFromBall( LastThrower );
        if ( !shouldJail )
        {
            Log.Info( $"[BallPickup] Hit {victim.GameObject?.Name ?? "unknown"} ignoré (friendly / prison) — rebond." );
            RegisterThrowSurfaceHit();
            return;
        }

        Vector3? jailKnock = null;
        if ( _rigidbody is not null && _rigidbody.Velocity.Length > 8f )
            jailKnock = _rigidbody.Velocity;
        else
        {
            var push = ( victim.GameObject.WorldPosition - WorldPosition ).WithZ( 0f );
            if ( push.Length > 0.1f )
                jailKnock = push.Normal * 400f;
        }

        victim.Jail( jailKnock );

        // Un prisonnier se libère uniquement en touchant un joueur en jeu avec une balle lancée.
        var thrower = LastThrower is not null ? FindPrisonBallPlayerFromRoot( LastThrower ) : null;
        if ( thrower is not null && thrower != victim && thrower.InPrison )
        {
            thrower.FreeToArena();
        }

        // Comme un rebond : la balle reste létale (même lanceur / teinte) jusqu'à MaxKillBounces contacts cumulés.
        RegisterThrowSurfaceHit();
    }

    /// <summary>
    /// Contact en vol qui consomme un "rebond" : murs, alliés, ou joueur envoyé en prison.
    /// Après <see cref="MaxKillBounces"/> tels contacts cumulés, la balle redevient "clean" (blanche).
    /// </summary>
    private void RegisterThrowSurfaceHit()
    {
        if ( !Networking.IsActive )
        {
            _throwBounceCount++;
            if ( _throwBounceCount >= MaxKillBounces )
                ApplyBallCleanAfterMaxBounces();
            return;
        }

        if ( !Networking.IsHost )
            return;

        NetThrowSurfaceHits++;
        _throwBounceCount = NetThrowSurfaceHits;
        if ( NetThrowSurfaceHits >= MaxKillBounces )
            ApplyBallCleanAfterMaxBounces();
    }

    private void ApplyBallCleanAfterMaxBounces()
    {
        IsThrown = false;
        LastThrower = null;
        RefreshBallTint();

        if ( Networking.IsActive && Networking.IsHost && GameObject.Network.Active )
        {
            TryRestoreBallOwnershipToHost();
            if ( _rigidbody is not null && !IsHeld )
            {
                _rigidbody.Enabled = true;
                EnsureBallContinuousCollision();
            }
        }
    }

    private void SyncThrowSurfaceStateFromHost()
    {
        if ( !Networking.IsActive || Networking.IsHost )
            return;

        _throwBounceCount = NetThrowSurfaceHits;

        if ( MaxKillBounces <= 0 )
            return;

        if ( NetThrowSurfaceHits < MaxKillBounces )
            return;

        if ( !IsThrown && LastThrower is null )
            return;

        ApplyBallCleanAfterMaxBounces();
    }

    private int EffectiveThrowSurfaceHitCount()
    {
        if ( !Networking.IsActive )
            return _throwBounceCount;

        return NetThrowSurfaceHits;
    }

    private void CacheBallRenderers()
    {
        _modelRenderers.Clear();
        _skinnedRenderers.Clear();
        _ballParticleEffects.Clear();
        CollectRenderersRecursive( GameObject );
    }

    private void CollectRenderersRecursive( GameObject go )
    {
        if ( go is null || !go.IsValid() )
            return;

        var mr = go.Components.Get<ModelRenderer>();
        if ( mr is not null && mr.IsValid() )
            _modelRenderers.Add( mr );

        var sk = go.Components.Get<SkinnedModelRenderer>();
        if ( sk is not null && sk.IsValid() )
            _skinnedRenderers.Add( sk );

        var pe = go.Components.Get<ParticleEffect>();
        if ( pe is not null && pe.IsValid() )
            _ballParticleEffects.Add( pe );

        foreach ( var child in go.Children )
            CollectRenderersRecursive( child );
    }

    /// <summary> Évite la traînée en monde : particules déplacées avec la racine de la balle. </summary>
    private void ApplyParticlesFollowBallTransform()
    {
        if ( !ParticlesFollowBallTransform )
            return;

        foreach ( var fx in _ballParticleEffects )
        {
            if ( fx is null || !fx.IsValid() )
                continue;

            fx.Space = ParticleEffect.SimulationSpace.Local;
            fx.ForceSpace = ParticleEffect.SimulationSpace.Local;
            fx.LocalSpace = new ParticleFloat( 1f, 1f );
        }
    }

    private void RefreshBallTint()
    {
        var tint = NeutralTint;
        if ( IsThrown && LastThrower is not null && LastThrower.IsValid() )
        {
            var team = GetTeamOnRoot( LastThrower );
            tint = team == TeamId.Blue ? AttackTintBlue : AttackTintRed;
        }
        else if ( _devAttackFlightPreview )
        {
            if ( _devForcedAttackTintTeam.HasValue )
                tint = _devForcedAttackTintTeam.Value == TeamId.Blue ? AttackTintBlue : AttackTintRed;
            else if ( _devFakeThrowerForTint is not null && _devFakeThrowerForTint.IsValid() )
            {
                var team = GetTeamOnRoot( _devFakeThrowerForTint );
                tint = team == TeamId.Blue ? AttackTintBlue : AttackTintRed;
            }
            else
                tint = AttackTintRed;
        }

        CurrentVisualTint = tint;

        foreach ( var r in _modelRenderers )
        {
            if ( r is not null && r.IsValid() )
                r.Tint = tint;
        }

        foreach ( var r in _skinnedRenderers )
        {
            if ( r is not null && r.IsValid() )
                r.Tint = tint;
        }

        foreach ( var fx in _ballParticleEffects )
        {
            if ( fx is null || !fx.IsValid() )
                continue;

            if ( fx.ApplyColor )
                fx.Tint = tint;
        }
    }

    /// <summary> Même logique que les autres systèmes : <see cref="TeamMember"/> sur l&apos;objet, ses enfants, ou un parent. </summary>
    private static TeamId GetTeamOnRoot( GameObject start )
    {
        if ( start is null || !start.IsValid() )
            return TeamId.Red;

        for ( var go = start; go is not null && go.IsValid(); go = go.Parent )
        {
            var tm = go.Components.Get<TeamMember>() ?? go.Components.GetInChildren<TeamMember>( true );
            if ( tm is not null )
                return tm.Team;
        }

        return TeamId.Red;
    }

    private void SetCollidersEnabled( bool enabled )
    {
        if ( !enabled )
        {
            _disabledColliders.Clear();
            foreach ( var component in Components.GetAll() )
            {
                if ( component is not Collider collider || !collider.Enabled )
                    continue;

                collider.Enabled = false;
                _disabledColliders.Add( collider );
            }

            return;
        }

        foreach ( var collider in _disabledColliders )
        {
            if ( collider is not null )
                collider.Enabled = true;
        }

        _disabledColliders.Clear();
    }

    private bool TryGetRightHandTransform( out Transform tx )
    {
        tx = default;

        if ( _holderRenderer is null || !_holderRenderer.IsValid() )
            return false;

        foreach ( var boneName in RightHandBoneNames )
        {
            if ( _holderRenderer.TryGetBoneTransform( boneName, out tx ) )
                return true;
        }

        return false;
    }

    /// <summary>
    /// Même corps que les anims de charge / port (<see cref="BallCarrier"/>) : évite un skinned « au hasard » (accessoire / LOD) avec des os incorrects.
    /// </summary>
    private static SkinnedModelRenderer ResolveHolderBodySkinnedRenderer( GameObject player )
    {
        if ( player is null || !player.IsValid() )
            return null;

        var pc = player.Components.Get<PlayerController>()
            ?? player.Components.GetInChildren<PlayerController>( true );
        if ( pc is not null && pc.Renderer is SkinnedModelRenderer body && body.IsValid() )
            return body;

        var carrier = player.Components.Get<BallCarrier>()
            ?? player.Components.GetInChildren<BallCarrier>( true );
        if ( carrier is not null && carrier.ChargePoseRenderer.IsValid() )
            return carrier.ChargePoseRenderer;

        return player.Components.GetInChildren<SkinnedModelRenderer>( true );
    }

    private static PrisonBallPlayer FindPrisonBallPlayer( GameObject obj )
    {
        if ( obj is null )
            return null;

        var direct = obj.Components.Get<PrisonBallPlayer>() ?? obj.Components.GetInChildren<PrisonBallPlayer>( true );
        if ( direct is not null )
            return direct;

        var current = obj.Parent;
        while ( current is not null )
        {
            var onParent = current.Components.Get<PrisonBallPlayer>();
            if ( onParent is not null )
                return onParent;

            current = current.Parent;
        }

        current = obj;
        while ( current is not null )
        {
            var ragdollOwner = current.Components.Get<RagdollOwnerLink>();
            if ( ragdollOwner?.OwnerRoot is not null )
            {
                var ownerPlayer = ragdollOwner.OwnerRoot.Components.Get<PrisonBallPlayer>()
                    ?? ragdollOwner.OwnerRoot.Components.GetInChildren<PrisonBallPlayer>( true );
                if ( ownerPlayer is not null )
                    return ownerPlayer;
            }

            current = current.Parent;
        }

        // Fallback robuste: certaines collisions arrivent sur des objets physiques
        // qui ne sont pas dans la hiérarchie directe du joueur.
        var scene = obj.Scene;
        if ( scene is not null )
        {
            foreach ( var candidate in scene.GetAllComponents<PrisonBallPlayer>() )
            {
                if ( candidate is null )
                    continue;

                var root = candidate.GameObject;
                if ( root is null )
                    continue;

                // Le hit object doit appartenir au joueur candidat (et pas l'inverse),
                // sinon un parent global pourrait matcher n'importe quel joueur.
                if ( IsSameOrChildOf( obj, root ) )
                    return candidate;

                if ( TryGetNetworkRoot( obj, out var objNetRoot ) && TryGetNetworkRoot( root, out var candidateNetRoot ) && objNetRoot == candidateNetRoot )
                    return candidate;
            }
        }

        return null;
    }

    private static bool TryGetNetworkRoot( GameObject start, out GameObject root )
    {
        var go = start;
        while ( go is not null )
        {
            if ( go.Network.Active )
            {
                root = go.Network.RootGameObject ?? go;
                return true;
            }

            go = go.Parent;
        }

        root = null;
        return false;
    }

    private static bool IsSameOrChildOf( GameObject candidate, GameObject potentialParent )
    {
        var current = candidate;
        while ( current is not null )
        {
            if ( current == potentialParent )
                return true;

            current = current.Parent;
        }

        return false;
    }

    private static PrisonBallPlayer FindPrisonBallPlayerFromRoot( GameObject root )
    {
        if ( root is null )
            return null;

        var onRoot = root.Components.Get<PrisonBallPlayer>() ?? root.Components.GetInChildren<PrisonBallPlayer>( true );
        if ( onRoot is not null )
            return onRoot;

        for ( var p = root.Parent; p is not null; p = p.Parent )
        {
            var onParent = p.Components.Get<PrisonBallPlayer>();
            if ( onParent is not null )
                return onParent;
        }

        return null;
    }

}
