using Sandbox;

public sealed partial class BallCarrier
{
    private void HideSkinnedRenderersForStuntRagdoll()
    {
        if ( _playerController is null )
            return;

        RagdollSkinnedVisuals.HidePlayerSkinnedForRagdoll( _playerController.GameObject, _activeRagdoll, _ragdollSkinnedRestore );
    }

    private void RestoreSkinnedRenderersAfterStuntRagdoll()
    {
        RagdollSkinnedVisuals.RestorePlayerSkinnedAfterRagdoll( _ragdollSkinnedRestore );
    }

    /// <summary>
    /// Proxies : début / fin du stunt et pose du ragdoll suivent uniquement le propriétaire (serveur relaie les [Sync]).
    /// </summary>
    private void SyncChargeStuntRagdollFromOwnerAuthority()
    {
        if ( !Networking.IsActive || !HasActiveNetworkRoot() )
            return;

        if ( IsLocallyControlled() )
            return;

        if ( NetChargeStuntRagdollActive )
        {
            if ( !_isRagdolled && !IsCarrierInPrison() )
                BeginChargeStuntRagdollShared( default, lockMovementForOwner: false, networkProxyStuntVisual: true );
        }
        else if ( _isRagdolled && _stuntRagdollNetworkProxyVisual )
        {
            EndRagdoll( snapToRagdollPosition: true );
        }
    }

    private bool HasActiveNetworkRoot()
    {
        for ( var go = GameObject; go is not null; go = go.Parent )
        {
            if ( go.Network.Active )
                return true;
        }

        return false;
    }

    /// <summary> Libère le ragdoll stunt sans snap (ex. knockdown prison qui remplace le ragdoll). </summary>
    public void AbortStuntRagdollIfAny()
    {
        if ( !_isRagdolled )
            return;

        EndRagdoll( snapToRagdollPosition: false );
    }

    private void TriggerChargeStuntRagdoll()
    {
        if ( _isRagdolled )
            return;

        if ( IsCarrierInPrison() )
            return;

        _isChargingThrow = false;
        _isThrowing = false;
        _currentCharge01 = 0f;
        _queuedThrowForce = 0f;
        _freeOverchargeFromEnemyCatch = false;
        _enemyCatchBonusFromEnemy = false;
        _pinChargeAtOverchargePeakUntilRelease = false;
        _overchargeDashAnimHoldActive = false;

        if ( _heldBall is not null )
        {
            var dropDirection = GetCameraThrowDirection();
            var dropF = NormalThrowForceMin > 0f ? NormalThrowForceMin * 0.6f : ThrowForce * 0.5f;
            if ( _heldBall.Throw( dropDirection, dropF, GetThrowReleaseWorldUpOverrideOrNull(), GetThrowReleaseLateralOverrideOrNull() ) )
                _heldBall = null;
        }

        if ( _heldProp is not null )
        {
            var dropDirection = GetCameraThrowDirection();
            var dropF = _heldProp.ThrowForce > 0f ? _heldProp.ThrowForce * 0.55f : 400f;
            if ( _heldProp.Throw( dropDirection, dropF, GetThrowReleaseWorldUpOverrideOrNull() ) )
                _heldProp = null;
        }

        _playerController = ResolvePlayerController();
        if ( _playerController is null )
            return;

        var hasMovementInput = Input.AnalogMove.Length > 0.1f;
        var movingFast = _playerController.Body is not null && _playerController.Body.Velocity.Length > 30f;
        var isMoving = hasMovementInput || movingFast;

        var punchDirection = Scene.Camera is not null ? Scene.Camera.WorldRotation.Forward : WorldRotation.Forward;
        var forwardImpulse = isMoving ? 80f : 18f;
        var upImpulse = isMoving ? 35f : 8f;
        var impulse = punchDirection * forwardImpulse + Vector3.Up * upImpulse;

        BeginChargeStuntRagdollShared( impulse, lockMovementForOwner: true, networkProxyStuntVisual: false );

        if ( Networking.IsActive && HasActiveNetworkRoot() && _activeRagdoll is not null )
        {
            NetChargeStuntRagdollImpulse = impulse;
            NetChargeStuntRagdollVersion++;
            NetChargeStuntRagdollAnchorWorld = _activeRagdoll.WorldPosition;
            NetChargeStuntRagdollActive = true;
        }
    }

    /// <param name="lockMovementForOwner">Faux sur les proxies : pas de blocage marche (réplication). </param>
    /// <param name="networkProxyStuntVisual">
    /// Observateurs : pas de physique locale ; pose depuis <see cref="NetChargeStuntRagdollAnchorWorld"/> (propriétaire).
    /// </param>
    private void BeginChargeStuntRagdollShared(
        Vector3 ragdollImpulse,
        bool lockMovementForOwner,
        bool networkProxyStuntVisual )
    {
        if ( _isRagdolled )
            return;

        if ( IsCarrierInPrison() )
            return;

        _playerController = ResolvePlayerController();
        if ( _playerController is null )
            return;

        _stuntRagdollNetworkProxyVisual = networkProxyStuntVisual;

        if ( lockMovementForOwner )
        {
            _savedWalkSpeed = _playerController.WalkSpeed;
            _savedRunSpeed = _playerController.RunSpeed;
            _savedJumpSpeed = _playerController.JumpSpeed;
        }

        _activeRagdoll = _playerController.CreateRagdoll( "ChargeStuntRagdoll" );
        if ( _activeRagdoll is not null )
        {
            if ( PlayerOnlyWall.SceneHasPlayerBlockingSoftWall( Scene ) )
                PlayerOnlyWall.ConfigureRagdollHierarchy( this, _activeRagdoll );

            var ownerLink = _activeRagdoll.Components.GetOrCreate<RagdollOwnerLink>();
            ownerLink.OwnerRoot = GameObject;

            var modelPhysics = _activeRagdoll.Components.Get<ModelPhysics>();
            if ( modelPhysics is not null )
            {
                if ( networkProxyStuntVisual )
                {
                    foreach ( var body in modelPhysics.Bodies )
                    {
                        var rb = body.Component;
                        if ( rb is null || !rb.Enabled )
                            continue;

                        rb.MotionEnabled = false;
                    }
                }
                else
                {
                    foreach ( var body in modelPhysics.Bodies )
                    {
                        var rb = body.Component;
                        if ( rb is null || !rb.Enabled )
                            continue;

                        rb.ApplyImpulse( in ragdollImpulse );
                    }
                }
            }
        }

        HideSkinnedRenderersForStuntRagdoll();

        if ( lockMovementForOwner )
        {
            _playerController.WalkSpeed = 0f;
            _playerController.RunSpeed = 0f;
            _playerController.JumpSpeed = 0f;
            _stuntRagdollWalkLocked = true;
        }

        _isRagdolled = true;
        _timeSinceRagdoll = 0f;
        _currentRagdollDuration = RagdollDuration;
    }

    private void UpdateRagdollRecovery()
    {
        var prison = Components.Get<PrisonBallPlayer>() ?? Components.GetInChildren<PrisonBallPlayer>( true );
        if ( prison is not null && prison.InPrison )
        {
            EndRagdoll( snapToRagdollPosition: false );
            return;
        }

        UpdateStuntRagdollRootFollow();
        UpdateStuntRagdollLocalCamera();

        if ( _stuntRagdollNetworkProxyVisual )
            return;

        var ragdollTime = _currentRagdollDuration <= 0f ? 0.01f : _currentRagdollDuration;
        if ( _timeSinceRagdoll < ragdollTime )
            return;

        EndRagdoll( snapToRagdollPosition: true );
    }

    private void EndRagdoll( bool snapToRagdollPosition )
    {
        if ( IsLocallyControlled() && Networking.IsActive && HasActiveNetworkRoot() )
            NetChargeStuntRagdollActive = false;

        if ( _playerController is not null )
        {
            if ( _activeRagdoll is not null )
            {
                if ( snapToRagdollPosition )
                    WorldPosition = _activeRagdoll.WorldPosition;

                _activeRagdoll.Destroy();
                _activeRagdoll = null;
            }

            if ( IsLocallyControlled() && Scene.Camera is not null )
            {
                var planarForward = Scene.Camera.WorldRotation.Forward.WithZ( 0f );
                if ( planarForward.Length > 0.001f )
                    WorldRotation = Rotation.LookAt( planarForward.Normal, Vector3.Up );
            }

            RestoreSkinnedRenderersAfterStuntRagdoll();

            if ( _stuntRagdollWalkLocked )
            {
                _playerController.WalkSpeed = _savedWalkSpeed;
                _playerController.RunSpeed = _savedRunSpeed;
                _playerController.JumpSpeed = _savedJumpSpeed;
                _stuntRagdollWalkLocked = false;
            }
        }

        _stuntRagdollNetworkProxyVisual = false;
        _isRagdolled = false;
    }

    /// <summary> Propriétaire : root + ancre réseau. Proxy : ragdoll visuel à l&apos;ancre du propriétaire. </summary>
    private void UpdateStuntRagdollRootFollow()
    {
        if ( _activeRagdoll is null )
            return;

        if ( _stuntRagdollNetworkProxyVisual )
        {
            PinStuntRagdollVisualFromOwnerAnchor();
            return;
        }

        WorldPosition = _activeRagdoll.WorldPosition;
        if ( _playerController?.Body is not null )
        {
            _playerController.Body.Velocity = Vector3.Zero;
            _playerController.Body.AngularVelocity = Vector3.Zero;
        }

        if ( Networking.IsActive && HasActiveNetworkRoot() && IsLocallyControlled() )
            NetChargeStuntRagdollAnchorWorld = _activeRagdoll.WorldPosition;
    }

    private void PinStuntRagdollVisualFromOwnerAnchor()
    {
        _activeRagdoll.WorldPosition = NetChargeStuntRagdollAnchorWorld;
        _activeRagdoll.WorldRotation = WorldRotation;

        if ( _playerController?.Body is not null )
        {
            _playerController.Body.Velocity = Vector3.Zero;
            _playerController.Body.AngularVelocity = Vector3.Zero;
        }
    }

    /// <summary> Uniquement le joueur local : évite de voler la caméra des autres quand un proxy fait un stunt. </summary>
    private void UpdateStuntRagdollLocalCamera()
    {
        if ( !IsLocallyControlled() || Scene.Camera is null || _activeRagdoll is null )
            return;

        var ragdollPos = _activeRagdoll.WorldPosition + Vector3.Up * 44f;
        var backDir = Scene.Camera.WorldRotation.Forward.WithZ( 0f ).Normal;
        if ( backDir.Length < 0.001f )
            backDir = Vector3.Forward;

        var targetPos = ragdollPos - backDir * 120f + Vector3.Up * 22f;
        var followBlend = (RealTime.Delta * 10f).Clamp( 0f, 1f );
        Scene.Camera.WorldPosition = Vector3.Lerp( Scene.Camera.WorldPosition, targetPos, followBlend );
    }
}
