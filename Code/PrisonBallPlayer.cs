using System.Collections.Generic;
using System.Linq;
using Sandbox;

/// <summary>
/// À mettre sur chaque joueur (même GameObject que <see cref="PlayerController"/> / <see cref="TeamMember"/>).
/// </summary>
public sealed class PrisonBallPlayer : Component
{
    [Property] public PrisonBallRules Rules { get; set; }
    [Property] public float ConstraintGraceAfterTeleport { get; set; } = 0.4f;
    [Property] public bool FlipLookDirection { get; set; } = true;

    #region Ragdoll avant téléport prison (différent du stunt BallCarrier)

    /// <summary> Préréglage ragdoll (.ragdoll) : à créer dans l&apos;éditeur, distinct de <c>ChargeStuntRagdoll</c>. </summary>
    [Property] public string JailKnockdownRagdollSetup { get; set; } = "PrisonJailRagdoll";

    [Property] public float JailKnockdownDuration { get; set; } = 3f;

    [Property] public float JailKnockdownForwardImpulse { get; set; } = 28f;
    [Property] public float JailKnockdownUpImpulse { get; set; } = 72f;

    /// <summary> Facteur appliqué à la masse de chaque bone (ex. 0.1 = très léger). </summary>
    [Property] public float JailKnockdownMassScale { get; set; } = 0.1f;

    [Property] public float JailKnockdownGravityScale { get; set; } = 0.4f;

    [Property] public float JailKnockdownLinearDamping { get; set; } = 0.35f;
    [Property] public float JailKnockdownAngularDamping { get; set; } = 0.55f;

    [Property] public float JailCameraHeightAboveRagdoll { get; set; } = 38f;
    [Property] public float JailCameraBackDistance { get; set; } = 95f;
    [Property] public float JailCameraUpOffset { get; set; } = 26f;
    [Property] public float JailCameraFollowSpeed { get; set; } = 7f;

    #endregion

    public bool InPrison { get; private set; }
    public bool IgnoreTeamConstraint => _timeSinceLastTeleport < ConstraintGraceAfterTeleport;

    /// <summary> Knockdown ragdoll avant TP prison ; bloque le gameplay (ex. <see cref="BallCarrier"/>). </summary>
    public bool IsInJailKnockdownRagdoll => _jailKnockdownActive;

    private TeamMember _teamMember;
    private PlayerController _playerController;
    private TimeSince _timeSinceLastTeleport;
    private TimeUntil _forceLookUntil;
    private Rotation _forcedLookRotation;
    private bool _restoreLookControls;
    private TimeUntil _forceTeleportUntil;
    private Vector3 _forcedTeleportPosition;

    private bool _jailKnockdownActive;
    private TimeSince _timeSinceJailKnockdown;
    private GameObject _jailKnockdownRagdoll;
    private GameObject _pendingPrisonSpawn;
    private float _savedWalkSpeed;
    private float _savedRunSpeed;
    private float _savedJumpSpeed;
    private Vector3 _jailKnockDirectionWorld;
    private readonly List<(SkinnedModelRenderer Renderer, bool WasEnabled)> _jailKnockdownSkinnedRestore = new();

    /// <summary> Équipe du joueur (lis sur <see cref="TeamMember"/> ). </summary>
    public TeamId Team => _teamMember?.Team ?? TeamId.Red;

    protected override void OnStart()
    {
        _teamMember = ResolveTeamMember();
        _playerController = Components.Get<PlayerController>();

        if ( Rules is null )
            Rules = Scene.GetAllComponents<PrisonBallRules>().FirstOrDefault();

        Rules?.Register( this );
    }

    protected override void OnDestroy()
    {
        Rules?.Unregister( this );
    }

    protected override void OnUpdate()
    {
        if ( _jailKnockdownActive )
        {
            UpdateJailKnockdownRagdoll();
            return;
        }

        if ( _forceTeleportUntil > 0f )
        {
            WorldPosition = _forcedTeleportPosition;
            if ( _playerController?.Body is not null )
            {
                _playerController.Body.WorldPosition = _forcedTeleportPosition;
                _playerController.Body.Velocity = Vector3.Zero;
                _playerController.Body.AngularVelocity = Vector3.Zero;
            }
        }

        if ( _forceLookUntil > 0f )
        {
            ApplyLookRotation( _forcedLookRotation );

            if ( _playerController is not null )
                _playerController.EyeAngles = _forcedLookRotation.Angles();
            return;
        }

        if ( _restoreLookControls && _playerController is not null )
        {
            _playerController.UseLookControls = true;
            _restoreLookControls = false;
        }
    }

    /// <summary> Appelé par la balle : touche valide, pas en prison, pas friendly fire. </summary>
    public bool ShouldJailFromBall( GameObject throwerRoot )
    {
        if ( InPrison || _jailKnockdownActive )
            return false;

        if ( throwerRoot is not null && AreSameTeam( throwerRoot, GameObject ) )
            return false;

        return true;
    }

    /// <param name="knockDirectionWorld">Impulsion ragdoll (ex. vélocité de la balle) ; sinon repère du joueur. </param>
    public void Jail( Vector3? knockDirectionWorld = null )
    {
        if ( InPrison || _jailKnockdownActive )
            return;

        if ( Rules is null )
        {
            Log.Warning( $"[PrisonBallPlayer] Jail aborted: Rules missing on {GameObject?.Name ?? "unknown"} (team={Team})." );
            return;
        }

        if ( !Rules.TryGetPrisonSpawn( Team, out var spawn ) )
        {
            Log.Warning( $"[PrisonBallPlayer] Jail aborted: no prison spawn for team={Team} on {GameObject?.Name ?? "unknown"}." );
            return;
        }

        _pendingPrisonSpawn = spawn;
        _jailKnockDirectionWorld = knockDirectionWorld ?? Vector3.Zero;
        StartJailKnockdownRagdoll();
    }

    /// <summary> Libération (retour en jeu) quand la règle de prison est validée. </summary>
    public void FreeToArena()
    {
        if ( !InPrison )
            return;

        if ( Rules is null )
        {
            Log.Warning( $"[PrisonBallPlayer] Free aborted: Rules missing on {GameObject?.Name ?? "unknown"} (team={Team})." );
            return;
        }

        var slot = _teamMember?.ArenaSlotIndex ?? -1;
        if ( !Rules.TryGetArenaSpawn( Team, slot, out var spawn ) )
        {
            Log.Warning( $"[PrisonBallPlayer] Free aborted: no arena spawn for team={Team}, slot={slot} on {GameObject?.Name ?? "unknown"}." );
            return;
        }

        InPrison = false;
        ApplyTransform( spawn );
        Log.Info( $"[PrisonBallPlayer] Freed {GameObject?.Name ?? "unknown"} -> {spawn?.Name ?? "null"} (team={Team}, slot={slot})." );
    }

    private void StartJailKnockdownRagdoll()
    {
        var carrier = Components.Get<BallCarrier>() ?? Components.GetInChildren<BallCarrier>( true );
        carrier?.AbortStuntRagdollIfAny();

        _playerController = ResolvePlayerController();
        if ( _playerController is null )
        {
            Log.Warning( $"[PrisonBallPlayer] Jail knockdown: pas de PlayerController sur {GameObject?.Name} — TP direct." );
            TeleportToPendingPrisonImmediate();
            return;
        }

        var setup = string.IsNullOrWhiteSpace( JailKnockdownRagdollSetup )
            ? "PrisonJailRagdoll"
            : JailKnockdownRagdollSetup.Trim();

        _savedWalkSpeed = _playerController.WalkSpeed;
        _savedRunSpeed = _playerController.RunSpeed;
        _savedJumpSpeed = _playerController.JumpSpeed;

        _jailKnockdownRagdoll = _playerController.CreateRagdoll( setup );
        if ( _jailKnockdownRagdoll is null )
        {
            Log.Warning( $"[PrisonBallPlayer] CreateRagdoll(\"{setup}\") a échoué — TP direct." );
            TeleportToPendingPrisonImmediate();
            return;
        }

        if ( SoftMidlineBarrier.SceneHasPlayerBlockingSoftWall( Scene ) )
            SoftMidlineBarrier.ConfigureRagdollHierarchy( this, _jailKnockdownRagdoll );

        var ownerLink = _jailKnockdownRagdoll.Components.GetOrCreate<RagdollOwnerLink>();
        ownerLink.OwnerRoot = GameObject;

        var modelPhysics = _jailKnockdownRagdoll.Components.Get<ModelPhysics>();
        if ( modelPhysics is not null )
        {
            ApplyJailKnockdownLightPhysics( modelPhysics );

            var planar = _jailKnockDirectionWorld.WithZ( 0f );
            var dir = planar.Length > 0.15f
                ? planar.Normal
                : WorldRotation.Forward.WithZ( 0f );
            if ( dir.Length < 0.01f )
                dir = WorldRotation.Forward;
            dir = dir.Normal;

            var impulse = dir * JailKnockdownForwardImpulse + Vector3.Up * JailKnockdownUpImpulse;

            foreach ( var body in modelPhysics.Bodies )
            {
                var rb = body.Component;
                if ( rb is null || !rb.Enabled )
                    continue;

                rb.ApplyImpulse( in impulse );
            }
        }

        RagdollSkinnedVisuals.HidePlayerSkinnedForRagdoll( _playerController.GameObject, _jailKnockdownRagdoll, _jailKnockdownSkinnedRestore );

        _playerController.WalkSpeed = 0f;
        _playerController.RunSpeed = 0f;
        _playerController.JumpSpeed = 0f;

        _jailKnockdownActive = true;
        _timeSinceJailKnockdown = 0f;
    }

    private void ApplyJailKnockdownLightPhysics( ModelPhysics modelPhysics )
    {
        var scale = JailKnockdownMassScale.Clamp( 0.02f, 1f );

        foreach ( var body in modelPhysics.Bodies )
        {
            var rb = body.Component;
            if ( rb is null || !rb.Enabled )
                continue;

            var m = rb.Mass;
            if ( m > 0.0001f )
            {
                var target = m * scale;
                if ( target > 0.02f )
                    rb.MassOverride = target;
            }

            rb.GravityScale = JailKnockdownGravityScale;
            rb.LinearDamping = JailKnockdownLinearDamping;
            rb.AngularDamping = JailKnockdownAngularDamping;
        }
    }

    private void UpdateJailKnockdownRagdoll()
    {
        if ( _jailKnockdownRagdoll is null || !_jailKnockdownRagdoll.IsValid() )
        {
            TeleportToPendingPrisonImmediate();
            return;
        }

        UpdateJailKnockdownCamera();

        var dur = JailKnockdownDuration <= 0f ? 0.01f : JailKnockdownDuration;
        if ( _timeSinceJailKnockdown < dur )
            return;

        EndJailKnockdownRagdollAndTeleport();
    }

    private void UpdateJailKnockdownCamera()
    {
        if ( !IsLocalOwnedPlayer() || Scene?.Camera is null || _jailKnockdownRagdoll is null )
            return;

        WorldPosition = _jailKnockdownRagdoll.WorldPosition;
        if ( _playerController?.Body is not null )
        {
            _playerController.Body.Velocity = Vector3.Zero;
            _playerController.Body.AngularVelocity = Vector3.Zero;
        }

        var ragdollPos = _jailKnockdownRagdoll.WorldPosition + Vector3.Up * JailCameraHeightAboveRagdoll;
        var backDir = Scene.Camera.WorldRotation.Forward.WithZ( 0f ).Normal;
        if ( backDir.Length < 0.001f )
            backDir = Vector3.Forward;

        var targetPos = ragdollPos - backDir * JailCameraBackDistance + Vector3.Up * JailCameraUpOffset;
        var spd = JailCameraFollowSpeed <= 0f ? 7f : JailCameraFollowSpeed;
        var followBlend = (RealTime.Delta * spd).Clamp( 0f, 1f );
        Scene.Camera.WorldPosition = Vector3.Lerp( Scene.Camera.WorldPosition, targetPos, followBlend );
    }

    private void EndJailKnockdownRagdollAndTeleport()
    {
        _playerController = ResolvePlayerController();
        if ( _playerController is not null )
        {
            if ( _jailKnockdownRagdoll is not null && _jailKnockdownRagdoll.IsValid() )
            {
                WorldPosition = _jailKnockdownRagdoll.WorldPosition;
                _jailKnockdownRagdoll.Destroy();
            }

            _jailKnockdownRagdoll = null;

            RagdollSkinnedVisuals.RestorePlayerSkinnedAfterRagdoll( _jailKnockdownSkinnedRestore );

            _playerController.WalkSpeed = _savedWalkSpeed;
            _playerController.RunSpeed = _savedRunSpeed;
            _playerController.JumpSpeed = _savedJumpSpeed;
        }
        else
        {
            if ( _jailKnockdownRagdoll is not null && _jailKnockdownRagdoll.IsValid() )
                _jailKnockdownRagdoll.Destroy();
            _jailKnockdownRagdoll = null;

            RagdollSkinnedVisuals.RestorePlayerSkinnedAfterRagdoll( _jailKnockdownSkinnedRestore );
        }

        _jailKnockdownActive = false;

        InPrison = true;
        var spawn = _pendingPrisonSpawn;
        _pendingPrisonSpawn = null;
        if ( spawn is not null && Rules is not null )
        {
            ApplyTransform( spawn );
            Rules.CheckWinCondition();
            Log.Info( $"[PrisonBallPlayer] Jailed {GameObject?.Name ?? "unknown"} -> {spawn.Name} (team={Team})." );
        }
        else
            Log.Warning( "[PrisonBallPlayer] Jail TP impossible : spawn ou Rules manquant." );
    }

    private void TeleportToPendingPrisonImmediate()
    {
        if ( _jailKnockdownRagdoll is not null )
        {
            if ( _jailKnockdownRagdoll.IsValid() )
                _jailKnockdownRagdoll.Destroy();
            _jailKnockdownRagdoll = null;
        }

        _playerController = ResolvePlayerController();
        if ( _playerController is not null )
        {
            RagdollSkinnedVisuals.RestorePlayerSkinnedAfterRagdoll( _jailKnockdownSkinnedRestore );

            _playerController.WalkSpeed = _savedWalkSpeed;
            _playerController.RunSpeed = _savedRunSpeed;
            _playerController.JumpSpeed = _savedJumpSpeed;
        }

        _jailKnockdownActive = false;

        InPrison = true;
        var spawn = _pendingPrisonSpawn;
        _pendingPrisonSpawn = null;
        if ( spawn is not null && Rules is not null )
        {
            ApplyTransform( spawn );
            Rules.CheckWinCondition();
            Log.Info( $"[PrisonBallPlayer] Jailed (sans ragdoll) {GameObject?.Name ?? "unknown"} -> {spawn.Name} (team={Team})." );
        }
    }

    private PlayerController ResolvePlayerController()
    {
        if ( _playerController is not null )
            return _playerController;

        _playerController = Components.Get<PlayerController>()
            ?? Components.GetInChildren<PlayerController>( true );

        var go = GameObject;
        while ( _playerController is null && go is not null )
        {
            _playerController = go.Components.Get<PlayerController>();
            go = go.Parent;
        }

        return _playerController;
    }

    private void ApplyTransform( GameObject spawn )
    {
        if ( spawn is null )
            return;

        WorldPosition = spawn.WorldPosition;
        var targetRotation = spawn.WorldRotation;

        // En prison ET en sortie de prison, regarde le centre de la map.
        if ( Rules is not null && Rules.ArenaSpawns is not null && Rules.ArenaSpawns.TryGetArenaCenter( out var center ) )
        {
            var dir = (center - WorldPosition).WithZ( 0f );
            if ( dir.LengthSquared > 0.0001f )
                targetRotation = Rotation.LookAt( dir.Normal, Vector3.Up );
        }

        ApplyLookRotation( targetRotation );

        // Le controller/camera peut écraser l'angle juste après TP.
        // On réapplique sur quelques frames, en prison ET en sortie de prison.
        if ( InPrison )
        {
            Invoke( 0.02f, () => ApplyLookRotation( targetRotation ) );
            Invoke( 0.08f, () => ApplyLookRotation( targetRotation ) );
            _forcedLookRotation = targetRotation;
            _forceLookUntil = 0.2f;

            if ( _playerController is not null && _playerController.UseLookControls )
            {
                _playerController.UseLookControls = false;
                _restoreLookControls = true;
            }
        }
        else
        {
            Invoke( 0.02f, () => ApplyLookRotation( targetRotation ) );
            Invoke( 0.08f, () => ApplyLookRotation( targetRotation ) );
            _forcedLookRotation = targetRotation;
            _forceLookUntil = 0.2f;

            if ( _playerController is not null && _playerController.UseLookControls )
            {
                _playerController.UseLookControls = false;
                _restoreLookControls = true;
            }
        }

        _timeSinceLastTeleport = 0f;

        if ( _playerController?.Body is not null )
        {
            // Important: le Body peut réécrire la position du root au frame suivant.
            // On force donc aussi le body à la position de téléport.
            _playerController.Body.WorldPosition = WorldPosition;
            _playerController.Body.WorldRotation = WorldRotation;
            _playerController.Body.Velocity = Vector3.Zero;
            _playerController.Body.AngularVelocity = Vector3.Zero;
        }

        // Même idée pour la position: verrouille brièvement pour éviter un snap-back.
        _forcedTeleportPosition = WorldPosition;
        _forceTeleportUntil = 0.2f;
    }

    private void ApplyLookRotation( Rotation rotation )
    {
        if ( FlipLookDirection )
            rotation = rotation * Rotation.FromYaw( 180f );

        WorldRotation = rotation;

        if ( _playerController?.Body is not null )
            _playerController.Body.WorldRotation = rotation;

        if ( Scene?.Camera is not null && IsLocalOwnedPlayer() )
            Scene.Camera.WorldRotation = rotation;
    }

    private bool IsLocalOwnedPlayer()
    {
        if ( !Networking.IsActive )
            return _playerController?.UseCameraControls ?? false;

        return TryGetNetworkRoot( GameObject, out var root ) && root.Network.IsOwner;
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

    private static bool AreSameTeam( GameObject a, GameObject b )
    {
        var ta = FindTeamMember( a );
        var tb = FindTeamMember( b );
        return ta is not null && tb is not null && ta.Team == tb.Team;
    }

    private TeamMember ResolveTeamMember()
    {
        var self = Components.Get<TeamMember>();
        if ( self is not null )
            return self;

        var child = Components.GetInChildren<TeamMember>( true );
        if ( child is not null )
            return child;

        for ( var p = GameObject.Parent; p is not null; p = p.Parent )
        {
            var onParent = p.Components.Get<TeamMember>();
            if ( onParent is not null )
                return onParent;
        }

        return null;
    }

    private static TeamMember FindTeamMember( GameObject obj )
    {
        var current = obj;
        while ( current is not null )
        {
            var team = current.Components.Get<TeamMember>();
            if ( team is not null )
                return team;

            current = current.Parent;
        }

        return null;
    }
}
