using Sandbox;

public sealed partial class BallCarrier
{
    private void StartThrowCharge()
    {
        if ( _heldBall is null )
            return;

        _isChargingThrow = true;
        var maxCharge = MaxChargeTime <= 0f ? 0.01f : MaxChargeTime;
        _timeSinceChargeStarted = 0f;
        _currentCharge01 = 0f;

        // Ne pas effacer _freeOverchargeFromEnemyCatch ici : sinon <see cref="UpdateEnemyCatchBonusExpiry"/>
        // ne tourne plus et le bonus « surchauffe gratuite » reste épinglé tant que le clic est maintenu.
        if ( _freeOverchargeFromEnemyCatch )
        {
            _pinChargeAtOverchargePeakUntilRelease = true;
            var peakOvercharge01 = GetOverchargeZoneEnd01();
            _timeSinceChargeStarted = peakOvercharge01 * maxCharge;
            _currentCharge01 = peakOvercharge01;
        }
    }

    private void UpdateThrowCharge()
    {
        if ( _heldBall is null )
        {
            CancelThrowCharge();
            return;
        }

        if ( !string.IsNullOrWhiteSpace( CancelChargeAction ) && Input.Pressed( CancelChargeAction ) )
        {
            CancelThrowCharge();
            return;
        }

        var maxCharge = MaxChargeTime <= 0f ? 0.01f : MaxChargeTime;
        var overchargeEnd = GetOverchargeZoneEnd01();
        float charge01;
        if ( _pinChargeAtOverchargePeakUntilRelease )
            charge01 = GetOverchargeZoneEnd01();
        else
            charge01 = (_timeSinceChargeStarted / maxCharge).Clamp( 0f, 1f );

        _currentCharge01 = charge01;

        if ( _currentCharge01 > overchargeEnd )
        {
            if ( IsCarrierInPrison() )
            {
                StartPrisonWeakThrowFromOverhold();
                return;
            }

            TriggerChargeStuntRagdoll();
            return;
        }

        if ( !Input.Down( "Attack1" ) )
        {
            var c = _currentCharge01;
            var normalEnd = GetNormalZoneEnd01();

            if ( c > overchargeEnd )
            {
                if ( IsCarrierInPrison() )
                {
                    StartPrisonWeakThrowFromOverhold();
                    return;
                }

                TriggerChargeStuntRagdoll();
                return;
            }

            var minF = NormalThrowForceMin <= 0f ? 1f : NormalThrowForceMin;
            var maxF = NormalThrowForceMax < minF ? minF : NormalThrowForceMax;

            if ( c <= normalEnd )
            {
                var t = normalEnd > 1e-5f ? (c / normalEnd).Clamp( 0f, 1f ) : 0f;
                _queuedThrowForce = minF.LerpTo( maxF, t );
                StartThrowSequence( overchargeThrow: false );
            }
            else
            {
                _queuedThrowForce = OverchargeThrowForce <= 0f ? maxF : OverchargeThrowForce;
                PlayOverchargeThrowSound();
                _pendingOverchargeThrowFx = true;
                StartThrowSequence( overchargeThrow: true );
            }
        }
    }

    private void StartPrisonWeakThrowFromOverhold()
    {
        var minF = NormalThrowForceMin <= 0f ? 1f : NormalThrowForceMin;
        _queuedThrowForce = minF;
        _freeOverchargeFromEnemyCatch = false;
        _enemyCatchBonusFromEnemy = false;
        _pinChargeAtOverchargePeakUntilRelease = false;
        StartThrowSequence( overchargeThrow: false );
    }

    private void PlayOverchargeThrowSound()
    {
        if ( OverchargeThrowSound is null || !OverchargeThrowSound.IsValid )
            return;

        var pos = WorldPosition + Vector3.Up * 40f;
        var handle = Sound.Play( OverchargeThrowSound, pos, 0f );
        if ( handle is not null && OverchargeThrowSoundVolume >= 0f )
            handle.Volume = OverchargeThrowSoundVolume;
    }

    private void StartThrowSequence( bool overchargeThrow )
    {
        _pinChargeAtOverchargePeakUntilRelease = false;
        _isChargingThrow = false;
        _isThrowing = true;
        _freeOverchargeFromEnemyCatch = false;
        _enemyCatchBonusFromEnemy = false;
        if ( overchargeThrow )
        {
            _overchargeDashAnimHoldActive = true;
            _timeSinceOverchargeDashAnimStarted = 0f;
        }
        else
            _overchargeDashAnimHoldActive = false;

        _timeSinceThrowStarted = 0f;
    }

    private void UpdateThrowWindup()
    {
        if ( _heldBall is null )
        {
            StopThrowAnimation();
            return;
        }

        var windupDuration = ThrowWindupTime <= 0f ? 0.01f : ThrowWindupTime;
        var t = (_timeSinceThrowStarted / windupDuration).Clamp( 0f, 1f );

        if ( t >= 1f )
            ThrowHeldBallNow();
    }

    private void ThrowHeldBallNow()
    {
        if ( _heldBall is null )
        {
            StopThrowAnimation();
            return;
        }

        if ( _pendingOverchargeThrowFx )
        {
            _pendingOverchargeThrowFx = false;
            Components.Get<BallCarrierAimZoom>()?.TriggerOverchargeThrowEffects();
        }

        var throwDirection = GetCameraThrowDirection();
        var forceToUse = _queuedThrowForce > 0f ? _queuedThrowForce : ThrowForce;
        if ( !_heldBall.Throw( throwDirection, forceToUse, GetThrowReleaseWorldUpOverrideOrNull(), GetThrowReleaseLateralOverrideOrNull() ) )
        {
            StopThrowAnimation();
            return;
        }

        _heldBall = null;
        _queuedThrowForce = 0f;
        StopThrowAnimation();
    }

    private void StopThrowAnimation()
    {
        _isChargingThrow = false;
        _isThrowing = false;
        _currentCharge01 = 0f;
    }

    private void CancelThrowCharge()
    {
        _isChargingThrow = false;
        _currentCharge01 = 0f;
        _queuedThrowForce = 0f;
        _pendingOverchargeThrowFx = false;
        _freeOverchargeFromEnemyCatch = false;
        _enemyCatchBonusFromEnemy = false;
        _pinChargeAtOverchargePeakUntilRelease = false;
        _overchargeDashAnimHoldActive = false;
        Components.Get<BallCarrierAimZoom>()?.SnapZoomOut();
    }

    private float GetNormalZoneEnd01()
    {
        return ChargeNormalZoneEnd01.Clamp( 0.05f, 0.95f );
    }

    private float GetOverchargeZoneEnd01()
    {
        var n = GetNormalZoneEnd01();
        var maxWidth = (1f - n - 0.01f).Clamp( 0.001f, 1f );
        var w = ChargeOverchargeZoneWidth01.Clamp( 0.001f, maxWidth );
        return (n + w).Clamp( n + 0.001f, 0.999f );
    }

    public float GetHudNormalZoneEnd01() => GetNormalZoneEnd01();

    public float GetHudOverchargeZoneEnd01() => GetOverchargeZoneEnd01();

    private Vector3 GetCameraThrowDirection()
    {
        var dir = Scene.Camera is not null ? Scene.Camera.WorldRotation.Forward : WorldRotation.Forward;
        if ( ThrowAimUpNudge > 0f )
            dir = (dir + Vector3.Up * ThrowAimUpNudge).Normal;
        return dir;
    }

    /// <summary> Null = la balle utilise sa propre propriété <see cref="BallPickup.ThrowReleaseWorldUpOffset"/>. </summary>
    private float? GetThrowReleaseWorldUpOverrideOrNull()
    {
        if ( System.Math.Abs( ThrowReleaseWorldUpOffset ) < 0.0001f )
            return null;
        return ThrowReleaseWorldUpOffset;
    }

    /// <summary> Null = la balle utilise <see cref="BallPickup.ThrowReleaseLateralOffset"/>. </summary>
    private float? GetThrowReleaseLateralOverrideOrNull()
    {
        if ( System.Math.Abs( ThrowReleaseLateralOffset ) < 0.0001f )
            return null;
        return ThrowReleaseLateralOffset;
    }
}
