using Sandbox;

public sealed class DashManager : Component
{
    [Property] public float BaseMoveSpeed { get; set; } = 160.0f;
    [Property] public float DashDistance { get; set; } = 120.0f;
    [Property] public float DashSpeed { get; set; } = 800.0f;
    [Property] public float DashCooldown { get; set; } = 1.0f;
    [Property] public float DoubleJumpTapTime { get; set; } = 0.3f;
    [Property] public float DashInputBufferTime { get; set; } = 0.15f;

    /// <summary> Paramètre booléen du AnimGraph : <c>true</c> pendant tout le dash (tant que la physique dash est active). </summary>
    [Property, Title( "Dash — AnimGraph bool param" )]
    public string DashAnimBoolParameter { get; set; } = "b_dash";

    /// <summary> Renderer du corps (souvent <c>Body</c>). Vide = même logique que <see cref="BallCarrier"/> (PlayerController.Renderer puis premier skinned enfant). </summary>
    [Property, Title( "Dash — Skinned renderer (optional)" )]
    public SkinnedModelRenderer DashPoseRenderer { get; set; }

    /// <summary> Répliqué : dash physique actif (même bool que <c>_dashDistanceRemaining &gt; 0</c> côté owner). </summary>
    [Sync] public bool NetDashPhysicsActive { get; set; }

    private TimeSince _lastJumpTap;
    private TimeSince _lastDash;
    private bool _waitingForSecondJumpTap;

    private float _dashDistanceRemaining;
    private float _dashStartDistance;
    private Vector3 _dashDir;
    private PlayerController _playerController;
    private float _dashRequestRemaining;
    private Vector3 _requestedDashDirection;
    private float _dashActiveTime;
    private float _dashNoMoveTime;

    protected override void OnStart()
    {
        // Permet de dash direct au spawn au lieu d'attendre le cooldown initial.
        _lastDash = DashCooldown;
    }

    protected override void OnUpdate()
    {
        _playerController ??= Components.Get<PlayerController>();
        if ( IsLocallyControlled() )
        {
            DisableRunning();

            if ( _waitingForSecondJumpTap && _lastJumpTap >= DoubleJumpTapTime )
            {
                _waitingForSecondJumpTap = false;
            }

            UpdateDashPhysics();
            UpdateDashRequest();

            if ( Input.Pressed( "Run" ) )
            {
                QueueDashRequest( GetMoveDashDirection() );
            }

            if ( Input.Pressed( "Duck" ) )
            {
                QueueDashRequest( Vector3.Down );
            }

            if ( Input.Pressed( "Jump" ) )
            {
                if ( _waitingForSecondJumpTap )
                {
                    _waitingForSecondJumpTap = false;
                    QueueDashRequest( Vector3.Up );
                }
                else
                {
                    _waitingForSecondJumpTap = true;
                    _lastJumpTap = 0;
                }
            }

            NetDashPhysicsActive = _dashDistanceRemaining > 0f;
        }

        ApplyDashAnimToRenderer();
    }

    /// <summary> Toutes les machines : <see cref="NetDashPhysicsActive"/> + dash surchauffe répliqué sur <see cref="BallCarrier"/>. </summary>
    private void ApplyDashAnimToRenderer()
    {
        if ( string.IsNullOrWhiteSpace( DashAnimBoolParameter ) )
            return;

        SkinnedModelRenderer renderer = default;

        if ( DashPoseRenderer.IsValid() )
            renderer = DashPoseRenderer;
        else
        {
            _playerController ??= Components.Get<PlayerController>();
            if ( _playerController?.Renderer is SkinnedModelRenderer bodySkinned && bodySkinned.IsValid() )
                renderer = bodySkinned;
        }

        if ( !renderer.IsValid() )
            renderer = Components.GetInChildren<SkinnedModelRenderer>( true );

        if ( !renderer.IsValid() )
            return;

        var carrier = Components.Get<BallCarrier>() ?? Components.GetInChildren<BallCarrier>( true );
        var overchargeThrowDash = carrier is not null && carrier.NetAnimOverchargeThrowDash;
        renderer.Set( DashAnimBoolParameter, NetDashPhysicsActive || overchargeThrowDash );
    }

    private void QueueDashRequest( Vector3 direction )
    {
        _requestedDashDirection = direction.Length > 0.001f ? direction.Normal : GetMoveDashDirection();
        _dashRequestRemaining = DashInputBufferTime <= 0f ? 0.01f : DashInputBufferTime;
        TryStartDash( _requestedDashDirection );
    }

    private void UpdateDashRequest()
    {
        if ( _dashRequestRemaining <= 0f )
            return;

        _dashRequestRemaining -= RealTime.Delta;
        if ( _dashRequestRemaining <= 0f )
        {
            _dashRequestRemaining = 0f;
            return;
        }

        TryStartDash( _requestedDashDirection );
    }

    private void TryStartDash( Vector3 direction )
    {
        if ( !CanStartDash() )
        {
            return;
        }

        StartDash( direction );
        _dashRequestRemaining = 0f;
    }

    private bool CanStartDash()
    {
        if ( _dashDistanceRemaining > 0f )
            return false;

        if ( _lastDash < DashCooldown )
            return false;

        if ( DashDistance <= 0.01f || DashSpeed <= 0.01f )
            return false;

        return true;
    }

    private Vector3 GetMoveDashDirection()
    {
        var wishDir = Input.AnalogMove;
        if ( wishDir.Length < 0.1f )
        {
            wishDir = Vector3.Forward;
        }

        var eyeRot = Scene.Camera is not null ? Scene.Camera.WorldRotation : WorldRotation;
        var dashDir = (eyeRot * wishDir).WithZ( 0f );
        if ( dashDir.Length < 0.001f )
        {
            dashDir = WorldRotation.Forward.WithZ( 0f );
        }

        return dashDir.Normal;
    }

    private void StartDash( Vector3 direction )
    {
        _dashDir = direction.Normal;
        _dashStartDistance = DashDistance <= 0f ? 0f : DashDistance;
        _dashDistanceRemaining = _dashStartDistance;
        _dashActiveTime = 0f;
        _dashNoMoveTime = 0f;
        _lastDash = 0f;
    }

    private void UpdateDashPhysics()
    {
        if ( _dashDistanceRemaining <= 0f )
        {
            _dashDistanceRemaining = 0f;
            return;
        }

        _dashActiveTime += RealTime.Delta;
        var speed = GetCurrentDashSpeed();
        var frameDistance = speed * RealTime.Delta;
        if ( frameDistance > _dashDistanceRemaining )
        {
            frameDistance = _dashDistanceRemaining;
        }

        var moveResult = MoveDashWithCollision( _dashDir * frameDistance );
        _dashDistanceRemaining -= moveResult.DistanceMoved;
        _dashDistanceRemaining = _dashDistanceRemaining < 0f ? 0f : _dashDistanceRemaining;

        if ( moveResult.DistanceMoved <= 0.001f )
        {
            _dashNoMoveTime += RealTime.Delta;
        }
        else
        {
            _dashNoMoveTime = 0f;
        }

        var maxDashDuration = DashSpeed <= 0.01f
            ? 0.3f
            : (DashDistance / DashSpeed).Clamp( 0.05f, 1.0f ) + 0.2f;

        // Sécurité anti blocage: si le dash ne bouge plus ou dépasse trop sa durée, on le termine.
        if ( moveResult.Hit || _dashDistanceRemaining <= 0f || _dashNoMoveTime > 0.08f || _dashActiveTime > maxDashDuration )
        {
            _dashDistanceRemaining = 0f;
            _dashActiveTime = 0f;
            _dashNoMoveTime = 0f;
        }
    }

    private float GetCurrentDashSpeed()
    {
        var maxDashSpeed = DashSpeed <= 0f ? 1f : DashSpeed;
        var minSpeed = BaseMoveSpeed <= 0f ? 1f : BaseMoveSpeed;
        var dashDistance = _dashStartDistance <= 0f ? 0.01f : _dashStartDistance;

        if ( maxDashSpeed <= minSpeed )
        {
            return minSpeed;
        }

        // Fenêtre de freinage auto: plus l'écart de vitesse est grand, plus la sortie est longue.
        var speedGapRatio = ((maxDashSpeed - minSpeed) / maxDashSpeed).Clamp( 0f, 1f );
        var autoBrakeDistance = dashDistance * (0.25f + speedGapRatio * 0.45f);

        if ( _dashDistanceRemaining > autoBrakeDistance )
        {
            return maxDashSpeed;
        }

        var t = (_dashDistanceRemaining / autoBrakeDistance).Clamp( 0f, 1f );
        var smoothT = t * t * (3f - 2f * t); // smoothstep
        return minSpeed.LerpTo( maxDashSpeed, smoothT );
    }

    private void DisableRunning()
    {
        _playerController ??= Components.Get<PlayerController>();
        if ( _playerController is null )
        {
            return;
        }

        var speed = BaseMoveSpeed <= 0f ? 1f : BaseMoveSpeed;
        _playerController.WalkSpeed = speed;
        _playerController.RunSpeed = speed;
    }

    /// <summary> Indique si un appui dash serait accepté tout de suite (hors dash en cours). </summary>
    public bool IsDashReady()
    {
        return CanStartDash();
    }

    /// <summary> <c>true</c> pendant que le déplacement dash est appliqué (pour HUD). </summary>
    public bool IsDashActive()
    {
        return _dashDistanceRemaining > 0.001f;
    }

    private (float DistanceMoved, bool Hit) MoveDashWithCollision( Vector3 delta )
    {
        var start = WorldPosition;
        var end = start + delta;

        var tr = Scene.Trace
            .Ray( start, end )
            .IgnoreGameObject( GameObject )
            .Run();

        var finalPosition = tr.EndPosition;
        WorldPosition = finalPosition;

        return (start.Distance( finalPosition ), tr.Hit );
    }

    private bool IsLocallyControlled()
    {
        if ( _playerController is null )
            return false;

        // En solo/local split logique: seul le controller actif doit répondre.
        if ( !_playerController.UseInputControls )
            return false;

        // En réseau: seuls les objets possédés localement doivent consommer l'input.
        if ( !Networking.IsActive )
            return true;

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
}