using System;
using Sandbox;

/// <summary>
/// Zoom FOV pendant charge / windup ; à placer sur le même objet que <see cref="BallCarrier"/>.
/// Dézoom + léger tremblement au tir en zone surchauffe.
/// </summary>
[Title( "Ball Carrier — Aim zoom" )]
public sealed class BallCarrierAimZoom : Component, PlayerController.IEvents
{
    [Property] public float ChargeAimFovReduction { get; set; } = 10f;
    [Property] public float ChargeAimZoomInSpeed { get; set; } = 16f;
    [Property] public float ChargeAimZoomOutSpeed { get; set; } = 14f;

    #region Tir surchauffe — feedback caméra

    /// <summary> +FOV au pic (dézoom, sensation de puissance). </summary>
    [Property] public float OverchargeThrowFovBump { get; set; } = 8f;

    /// <summary>
    /// Vitesse (0→1 / s) du bump FOV surchauffe. Ne pilote pas le shake.
    /// </summary>
    [Property, Title( "Overcharge shot — recul FOV (vitesse)" )]
    public float OverchargeThrowFovRecoilSpeed { get; set; } = 13.33f;

    /// <summary>
    /// Vitesse (1→0 / s) retour FOV de base. Ne pilote pas le shake.
    /// </summary>
    [Property, Title( "Overcharge shot — retour FOV base (vitesse)" )]
    public float OverchargeThrowFovReturnSpeed { get; set; } = 3.125f;

    /// <summary>
    /// Vitesse (0→1 / s) montée du shake (indépendante du FOV).
    /// </summary>
    [Property, Title( "Overcharge shake — montée (vitesse)" )]
    public float OverchargeThrowShakeRecoilSpeed { get; set; } = 13.33f;

    /// <summary>
    /// Vitesse (1→0 / s) extinction du shake (indépendante du FOV).
    /// </summary>
    [Property, Title( "Overcharge shake — extinction (vitesse)" )]
    public float OverchargeThrowShakeReturnSpeed { get; set; } = 3.125f;

    /// <summary> Amplitude max (degrés) pitch / yaw du tremblement. </summary>
    [Property] public float OverchargeThrowShakeDegrees { get; set; } = 0.42f;

    #endregion

    private float _zoom01;

    private float _fovBumpEnvelope;
    private bool _fovBumpRising;
    private bool _fovBumpActive;

    private float _shakeEnvelope;
    private bool _shakeRising;
    private bool _shakeActive;

    public void SnapZoomOut()
    {
        _zoom01 = 0f;
        _fovBumpEnvelope = 0f;
        _fovBumpRising = false;
        _fovBumpActive = false;
        _shakeEnvelope = 0f;
        _shakeRising = false;
        _shakeActive = false;
    }

    /// <summary> Appelé au lâcher réel de la balle en surchauffe. </summary>
    public void TriggerOverchargeThrowEffects()
    {
        _fovBumpActive = OverchargeThrowFovBump > 0f;
        _fovBumpRising = true;
        _fovBumpEnvelope = 0f;

        _shakeActive = OverchargeThrowShakeDegrees > 0f;
        _shakeRising = true;
        _shakeEnvelope = 0f;
    }

    void PlayerController.IEvents.PostCameraSetup( CameraComponent cam )
    {
        var carrier = ResolveCarrier();
        if ( carrier is null || cam is null || !cam.IsValid() || carrier.IsStunnedRagdoll || carrier.IsJailKnockdownRagdollActive )
            return;

        if ( !carrier.ShouldShowLocalHud() )
            return;

        var wantAimZoom = carrier.WantsChargeAimZoom;
        var dt = RealTime.Delta;

        if ( wantAimZoom && ChargeAimFovReduction > 0f )
            _zoom01 = (_zoom01 + dt * ChargeAimZoomInSpeed).Clamp( 0f, 1f );
        else
            _zoom01 = (_zoom01 - dt * ChargeAimZoomOutSpeed).Clamp( 0f, 1f );

        var baseFov = cam.FieldOfView;
        var zoomedFov = (baseFov - ChargeAimFovReduction * _zoom01).Clamp( 25f, 120f );
        cam.FieldOfView = zoomedFov;

        if ( _fovBumpActive && OverchargeThrowFovBump > 0f )
        {
            var fovMul = FovBumpMultiplier();
            cam.FieldOfView = (cam.FieldOfView + OverchargeThrowFovBump * fovMul).Clamp( 25f, 130f );
        }

        if ( _shakeActive && OverchargeThrowShakeDegrees > 0f )
            ApplyOverchargeCameraShake( cam );

        StepFovBump( dt );
        StepShake( dt );
    }

    /// <summary> Montée linéaire ; descente au carré, vitesses FOV. </summary>
    private float FovBumpMultiplier()
    {
        if ( !_fovBumpActive )
            return 0f;

        if ( _fovBumpRising )
            return _fovBumpEnvelope;

        return _fovBumpEnvelope * _fovBumpEnvelope;
    }

    private float ShakeMultiplier()
    {
        if ( !_shakeActive )
            return 0f;

        if ( _shakeRising )
            return _shakeEnvelope;

        return _shakeEnvelope * _shakeEnvelope;
    }

    private void StepFovBump( float dt )
    {
        if ( !_fovBumpActive )
            return;

        var recoil = OverchargeThrowFovRecoilSpeed <= 0f ? 0.001f : OverchargeThrowFovRecoilSpeed;
        var ret = OverchargeThrowFovReturnSpeed <= 0f ? 0.001f : OverchargeThrowFovReturnSpeed;

        if ( _fovBumpRising )
        {
            _fovBumpEnvelope = MathF.Min( 1f, _fovBumpEnvelope + dt * recoil );
            if ( _fovBumpEnvelope >= 1f )
            {
                _fovBumpEnvelope = 1f;
                _fovBumpRising = false;
            }

            return;
        }

        _fovBumpEnvelope = MathF.Max( 0f, _fovBumpEnvelope - dt * ret );
        if ( _fovBumpEnvelope <= 0f )
        {
            _fovBumpEnvelope = 0f;
            _fovBumpActive = false;
        }
    }

    private void StepShake( float dt )
    {
        if ( !_shakeActive )
            return;

        var recoil = OverchargeThrowShakeRecoilSpeed <= 0f ? 0.001f : OverchargeThrowShakeRecoilSpeed;
        var ret = OverchargeThrowShakeReturnSpeed <= 0f ? 0.001f : OverchargeThrowShakeReturnSpeed;

        if ( _shakeRising )
        {
            _shakeEnvelope = MathF.Min( 1f, _shakeEnvelope + dt * recoil );
            if ( _shakeEnvelope >= 1f )
            {
                _shakeEnvelope = 1f;
                _shakeRising = false;
            }

            return;
        }

        _shakeEnvelope = MathF.Max( 0f, _shakeEnvelope - dt * ret );
        if ( _shakeEnvelope <= 0f )
        {
            _shakeEnvelope = 0f;
            _shakeActive = false;
        }
    }

    private void ApplyOverchargeCameraShake( CameraComponent cam )
    {
        var go = cam.GameObject;
        if ( go is null || !go.IsValid() )
            return;

        var amp = OverchargeThrowShakeDegrees * ShakeMultiplier();
        var t = (float)RealTime.Now;
        var pitch = MathF.Sin( t * 56f ) * amp;
        var yaw = MathF.Sin( t * 43f ) * amp * 0.88f;
        go.WorldRotation *= Rotation.FromYaw( yaw ) * Rotation.FromPitch( pitch );
    }

    private BallCarrier ResolveCarrier()
    {
        return Components.Get<BallCarrier>()
            ?? Components.GetInChildren<BallCarrier>( true );
    }
}
