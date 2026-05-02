using Sandbox;
using System.Collections.Generic;

/// <summary>
/// Balle 100% locale (gameplay solo). Aucun code reseau ici.
///
/// Si un <see cref="BallNetworkSync"/> est present sur le meme GameObject (multijoueur),
/// les API publiques <see cref="PickUp"/> et <see cref="Throw"/> lui sont deleguees pour
/// que l'hote reste autoritaire. Sinon (offline/editeur), tout s'applique directement
/// via les methodes <c>ApplyXxxLocal</c>.
/// </summary>
public sealed class BallPickup : Component, Component.ICollisionListener
{
    #region Inspector
    [Property] public float ThrowForce { get; set; } = 1200f;
    [Property] public float ThrowSpawnOffset { get; set; } = 26f;
    [Property] public float ThrowReleaseWorldUpOffset { get; set; } = 0f;
    [Property] public float ThrowReleaseLateralOffset { get; set; } = 0f;

    [Property] public float ThrowerGraceTime { get; set; } = 0.15f;
    [Property] public int MaxKillBounces { get; set; } = 1;

    [Property] public Color NeutralTint { get; set; } = Color.White;
    [Property] public Color AttackTintRed { get; set; } = new Color( 1f, 0.25f, 0.25f );
    [Property] public Color AttackTintBlue { get; set; } = new Color( 0.25f, 0.45f, 1f );

    [Property] public bool ParticlesFollowBallTransform { get; set; } = true;

    [Property] public float HandForwardOffset { get; set; } = 6f;
    [Property] public float HandRightOffset { get; set; } = 2f;
    [Property] public float HandUpOffset { get; set; } = -2f;
    #endregion

    #region Etat public (lecture seule)
    public bool IsHeld { get; private set; }
    public bool IsThrown { get; private set; }
    public GameObject Holder { get; private set; }
    public GameObject LastThrower { get; private set; }
    public float LastThrowSpeed { get; private set; }
    public Color CurrentVisualTint { get; private set; } = Color.White;
    #endregion

    #region Internes
    private Rigidbody _rigidbody;
    private GameObject _holdPoint;
    private SkinnedModelRenderer _holderRenderer;
    private readonly List<Collider> _disabledColliders = new();
    private readonly List<ModelRenderer> _modelRenderers = new();
    private readonly List<SkinnedModelRenderer> _skinnedRenderers = new();
    private readonly List<ParticleEffect> _ballParticleEffects = new();

    private TimeSince _timeSinceThrow;
    private int _throwBounceCount;

    /// <summary> Permet a <see cref="BallNetworkSync"/> d'inhiber la simulation locale du rigidbody. </summary>
    internal bool LocalPhysicsInhibited { get; set; }

    private bool _devAttackFlightPreview;
    private GameObject _devFakeThrowerForTint;
    private TeamId? _devForcedAttackTintTeam;
    private float _devSimulatedLinearSpeedForTrail;
    private float _devPreviewMotionTimeScale = 1f;

    public float DevSimulatedLinearSpeedForTrail => _devSimulatedLinearSpeedForTrail;
    public float DevPreviewMotionTimeScale => _devPreviewMotionTimeScale;

    private static readonly string[] RightHandBoneNames =
    {
        "hand_R", "hand_r", "ValveBiped.Bip01_R_Hand", "right_hand", "r_hand",
        "RightHand", "mixamorig:RightHand", "R_Hand", "Hand_R", "Bip01 R Hand",
    };
    #endregion

    protected override void OnStart()
    {
        MoonFpsNetworkSanitizer.DisableTemplateGameManagers( Scene );

        _rigidbody = Components.Get<Rigidbody>();
        EnsureBallContinuousCollision();
        CacheBallRenderers();
        ApplyParticlesFollowBallTransform();
        RefreshBallTint();
    }

    protected override void OnUpdate()
    {
        // Proxy reseau : le sync moteur drive le transform (NetworkMode=Object).
        // On ne doit PAS ecrire la position, sinon conflit avec le sync.
        if ( LocalPhysicsInhibited )
            return;

        if ( !IsHeld )
            return;

        if ( Holder is null || !Holder.IsValid() )
            return;

        // Tenue : on neutralise la velocite chaque frame pour empecher la gravite d'accumuler
        // une vitesse sur le rigidbody (qui reste enabled pour eviter les bugs de toggle s&box).
        if ( _rigidbody is not null && _rigidbody.Enabled )
        {
            _rigidbody.Velocity = Vector3.Zero;
            _rigidbody.AngularVelocity = Vector3.Zero;
        }

        if ( _holderRenderer is null || !_holderRenderer.IsValid() )
            _holderRenderer = ResolveHolderBodySkinnedRenderer( Holder );

        if ( TryGetRightHandTransform( out var handTx ) )
        {
            WorldPosition = handTx.Position
                + handTx.Rotation.Forward * HandForwardOffset
                + handTx.Rotation.Right * HandRightOffset
                + handTx.Rotation.Up * HandUpOffset;
            WorldRotation = handTx.Rotation;
            return;
        }

        if ( _holdPoint is not null && _holdPoint.IsValid() )
        {
            WorldPosition = _holdPoint.WorldPosition;
            WorldRotation = _holdPoint.WorldRotation;
            return;
        }

        WorldPosition = Holder.WorldPosition + Vector3.Up * 48f;
        WorldRotation = Holder.WorldRotation;
    }

    #region API publique (utilisee par BallCarrier) — delegue au reseau si dispo
    public bool PickUp( GameObject player, GameObject holdPoint )
    {
        var sync = Components.Get<BallNetworkSync>();
        if ( sync is not null )
            return sync.RequestPickup( player, holdPoint );

        return ApplyPickupLocal( player, holdPoint );
    }

    public void Throw( Vector3 direction, float? customForce = null, float? releaseWorldUpOverride = null, float? releaseLateralOverride = null )
    {
        var sync = Components.Get<BallNetworkSync>();
        if ( sync is not null )
        {
            sync.RequestThrow( direction, customForce, releaseWorldUpOverride, releaseLateralOverride );
            return;
        }

        ApplyThrowLocal( direction, customForce, releaseWorldUpOverride, releaseLateralOverride );
    }
    #endregion

    #region Application locale (gameplay solo / appele par BallNetworkSync sur l'autoritaire)
    /// <summary> Applique le ramassage purement localement (sans reseau). </summary>
    internal bool ApplyPickupLocal( GameObject player, GameObject holdPoint )
    {
        if ( IsHeld || player is null || !player.IsValid() || holdPoint is null || !holdPoint.IsValid() )
            return false;

        IsHeld = true;
        IsThrown = false;
        LastThrowSpeed = 0f;
        _throwBounceCount = 0;

        Holder = player;
        LastThrower = null;
        _holdPoint = holdPoint;
        _holderRenderer = ResolveHolderBodySkinnedRenderer( player );

        SetCollidersEnabled( false );

        // Stoppe la balle mais NE TOGGLE PAS Rigidbody.Enabled : dans s&box, desactiver/reactiver
        // un Rigidbody peut casser le setter Velocity au prochain throw. On garde le rigidbody
        // toujours actif cote owner ; OnUpdate neutralise la velocite chaque frame pendant la tenue.
        if ( _rigidbody is not null )
        {
            _rigidbody.Velocity = Vector3.Zero;
            _rigidbody.AngularVelocity = Vector3.Zero;
        }

        var carrier = player.Components.Get<BallCarrier>() ?? player.Components.GetInChildren<BallCarrier>( true );
        carrier?.NetSetHeldBallFromNetwork( this );

        RefreshBallTint();
        return true;
    }

    /// <summary> Applique le lancer purement localement. Active la physique locale sauf si <see cref="LocalPhysicsInhibited"/>. </summary>
    internal void ApplyThrowLocal( Vector3 direction, float? customForce, float? releaseWorldUpOverride, float? releaseLateralOverride )
    {
        if ( !IsHeld )
            return;

        var holder = Holder;
        var carrier = holder is not null && holder.IsValid()
            ? holder.Components.Get<BallCarrier>() ?? holder.Components.GetInChildren<BallCarrier>( true )
            : null;
        carrier?.NetClearHeldBallFromNetwork();

        var force = customForce ?? ThrowForce;
        var throwDir = direction.Length > 0.001f ? direction.Normal : Vector3.Forward;
        var upExtra = releaseWorldUpOverride ?? ThrowReleaseWorldUpOffset;
        var lateral = releaseLateralOverride ?? ThrowReleaseLateralOffset;

        ComputeThrowReleasePose( holder, throwDir, upExtra, lateral, out var releasePos, out var releaseRot );

        IsHeld = false;
        IsThrown = true;
        _throwBounceCount = 0;
        _timeSinceThrow = 0f;

        LastThrower = holder;
        Holder = null;
        _holdPoint = null;
        _holderRenderer = null;

        WorldPosition = releasePos;
        WorldRotation = releaseRot;

        SetCollidersEnabled( true );

        if ( !LocalPhysicsInhibited && _rigidbody is not null )
        {
            // Ensure enabled (defensive) - mais on doit JAMAIS avoir besoin de toggler ici
            // si BallNetworkSync.UpdateLocalPhysicsBasedOnOwnership a fait son boulot.
            if ( !_rigidbody.Enabled )
                _rigidbody.Enabled = true;

            EnsureBallContinuousCollision();

            var releaseVelocity = throwDir * force;
            _rigidbody.Velocity = releaseVelocity;
            _rigidbody.AngularVelocity = Vector3.Zero;

            LastThrowSpeed = releaseVelocity.Length;
        }
        else
        {
            LastThrowSpeed = force;
        }

        RefreshBallTint();
    }

    /// <summary>
    /// Variante "ack reseau" : applique l'etat lance sans toucher au rigidbody local
    /// (le sync moteur de transform/velocite va piloter la balle).
    /// </summary>
    internal void ApplyThrowAckFromNetwork( GameObject thrower )
    {
        if ( IsHeld )
        {
            var holder = Holder;
            var carrier = holder is not null && holder.IsValid()
                ? holder.Components.Get<BallCarrier>() ?? holder.Components.GetInChildren<BallCarrier>( true )
                : null;
            carrier?.NetClearHeldBallFromNetwork();
        }

        IsHeld = false;
        IsThrown = true;
        _throwBounceCount = 0;
        _timeSinceThrow = 0f;
        LastThrower = thrower;
        Holder = null;
        _holdPoint = null;
        _holderRenderer = null;

        SetCollidersEnabled( true );
        RefreshBallTint();
    }

    /// <summary> Reset complet (etat neutre — apres rebonds max ou apres respawn). </summary>
    internal void ApplyCleanLocal()
    {
        IsThrown = false;
        LastThrower = null;
        RefreshBallTint();

        if ( !LocalPhysicsInhibited && _rigidbody is not null && !IsHeld )
        {
            _rigidbody.Enabled = true;
            EnsureBallContinuousCollision();
        }
    }

    /// <summary> Reset visuel cote spectateur quand on n'a pas la simulation locale. </summary>
    internal void ApplyForceReleaseFromNetwork()
    {
        if ( IsHeld )
        {
            var holder = Holder;
            var carrier = holder is not null && holder.IsValid()
                ? holder.Components.Get<BallCarrier>() ?? holder.Components.GetInChildren<BallCarrier>( true )
                : null;
            carrier?.NetClearHeldBallFromNetwork();
        }

        IsHeld = false;
        Holder = null;
        _holdPoint = null;
        _holderRenderer = null;

        SetCollidersEnabled( true );
        RefreshBallTint();
    }
    #endregion

    private void ComputeThrowReleasePose( GameObject holder, Vector3 throwDir, float upExtra, float lateral, out Vector3 releasePosition, out Rotation releaseRotation )
    {
        var basePos = WorldPosition;
        var baseRot = WorldRotation;

        if ( holder is not null && holder.IsValid() )
        {
            var resolved = ResolveHolderBodySkinnedRenderer( holder );
            var savedRenderer = _holderRenderer;
            if ( resolved is not null && resolved.IsValid() )
                _holderRenderer = resolved;

            if ( TryGetRightHandTransform( out var handTx ) )
            {
                basePos = handTx.Position
                    + handTx.Rotation.Forward * HandForwardOffset
                    + handTx.Rotation.Right * HandRightOffset
                    + handTx.Rotation.Up * HandUpOffset;
                baseRot = handTx.Rotation;
            }
            else if ( _holdPoint is not null && _holdPoint.IsValid() )
            {
                basePos = _holdPoint.WorldPosition;
                baseRot = _holdPoint.WorldRotation;
            }

            _holderRenderer = savedRenderer;
        }
        else if ( _holdPoint is not null && _holdPoint.IsValid() )
        {
            basePos = _holdPoint.WorldPosition;
            baseRot = _holdPoint.WorldRotation;
        }

        var releaseRight = HorizontalThrowRight( throwDir );
        releasePosition = basePos
            + throwDir * ThrowSpawnOffset
            + Vector3.Up * upExtra
            + releaseRight * lateral;
        releaseRotation = baseRot;
    }

    public bool IsLiveEnemyBallFor( GameObject pickerRoot )
    {
        if ( !IsThrown || LastThrower is null || !LastThrower.IsValid() || pickerRoot is null || !pickerRoot.IsValid() )
            return false;

        return GetTeamOnRoot( LastThrower ) != GetTeamOnRoot( pickerRoot );
    }

    public bool IsCleanAllyPassFor( GameObject pickerRoot, int maxBouncesSinceThrow = 0 )
    {
        if ( !IsThrown || LastThrower is null || !LastThrower.IsValid() || pickerRoot is null || !pickerRoot.IsValid() )
            return false;

        var maxB = maxBouncesSinceThrow < 0 ? 0 : maxBouncesSinceThrow;
        if ( _throwBounceCount > maxB )
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

        return IsSameOrChildOf( a, b ) || IsSameOrChildOf( b, a );
    }

    public void OnCollisionStart( Collision other )
    {
        if ( !IsThrown || IsHeld )
            return;

        // Seul le owner reseau (qui simule physiquement) detecte les collisions et decide.
        // Sur les proxies, LocalPhysicsInhibited = true → on ignore (le sync recevra le resultat).
        if ( LocalPhysicsInhibited )
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
            RegisterThrowSurfaceHit();
            return;
        }

        if ( !victim.ShouldJailFromBall( LastThrower ) )
        {
            RegisterThrowSurfaceHit();
            return;
        }

        // Broadcast du jail aux autres clients via le sync reseau (chacun appliquera le ragdoll
        // sur sa copie locale du joueur cible). En offline, applique direct.
        var ballVel = _rigidbody is not null ? _rigidbody.Velocity : Vector3.Zero;
        var sync = Components.Get<BallNetworkSync>();
        if ( sync is not null )
        {
            sync.BroadcastJailEvent( victim.GameObject, LastThrower, ballVel );
        }
        else
        {
            // Solo / pas de sync : applique directement
            Vector3? jailKnock = null;
            if ( ballVel.Length > 8f )
                jailKnock = ballVel;
            else
            {
                var push = ( victim.GameObject.WorldPosition - WorldPosition ).WithZ( 0f );
                if ( push.Length > 0.1f )
                    jailKnock = push.Normal * 400f;
            }

            victim.Jail( jailKnock );

            var thrower = LastThrower is not null ? FindPrisonBallPlayerFromRoot( LastThrower ) : null;
            if ( thrower is not null && thrower != victim && thrower.InPrison )
                thrower.FreeToArena();
        }

        RegisterThrowSurfaceHit();
    }

    private void RegisterThrowSurfaceHit()
    {
        _throwBounceCount++;
        if ( _throwBounceCount >= MaxKillBounces )
        {
            ApplyCleanLocal();

            // Notifie le sync reseau (s'il existe) pour repliquer le clean aux autres machines.
            var sync = Components.Get<BallNetworkSync>();
            sync?.OnHostBallCleaned();
        }
    }

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

    public bool IsAttackFlightVisual()
    {
        return ( IsThrown && LastThrower is not null && LastThrower.IsValid() ) || _devAttackFlightPreview;
    }

    private void EnsureBallContinuousCollision()
    {
        if ( _rigidbody is null || !_rigidbody.IsValid() )
            return;

        _rigidbody.EnhancedCcd = true;
    }

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

    private void ApplyParticlesFollowBallTransform()
    {
        if ( !ParticlesFollowBallTransform )
            return;

        foreach ( var fx in _ballParticleEffects )
        {
            if ( fx is null || !fx.IsValid() )
                continue;

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
            if ( r is not null && r.IsValid() )
                r.Tint = tint;

        foreach ( var r in _skinnedRenderers )
            if ( r is not null && r.IsValid() )
                r.Tint = tint;

        foreach ( var fx in _ballParticleEffects )
            if ( fx is not null && fx.IsValid() && fx.ApplyColor )
                fx.Tint = tint;
    }

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
            if ( collider is not null )
                collider.Enabled = true;

        _disabledColliders.Clear();
    }

    private bool TryGetRightHandTransform( out Transform tx )
    {
        tx = default;

        if ( _holderRenderer is null || !_holderRenderer.IsValid() )
            return false;

        foreach ( var boneName in RightHandBoneNames )
            if ( _holderRenderer.TryGetBoneTransform( boneName, out tx ) )
                return true;

        return false;
    }

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

                if ( IsSameOrChildOf( obj, root ) )
                    return candidate;
            }
        }

        return null;
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
}
