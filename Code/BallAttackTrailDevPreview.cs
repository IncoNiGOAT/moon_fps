using System;
using Sandbox;

/// <summary>
/// Prévisualise trail + teinte « vol attaque » sans arène. Anime uniquement le GO de ce composant
/// (ex. <c>BallVisual_Ref</c>) : oscillation « lancer » sur place le long de <b>+X monde</b>, vitesse de crête = force simulée arène.
/// Ne modifie pas <c>Trail_ball</c> : son offset local sous la balle reste celui que tu as défini dans l’éditeur.
/// <see cref="PreviewMotionTimeScale"/> ralentit seulement l’oscillation ; les vitesses passées au trail restent nominales.
/// </summary>
public sealed class BallAttackTrailDevPreview : Component, Component.ExecuteInEditor
{
    private static readonly Vector3 SimulatedThrowDirectionWorld = new Vector3( 1f, 0f, 0f );

    /// <summary> Demi-course (u) de l’oscillation ; ω = vitesse / A pour que la vitesse max ≈ force simulée. </summary>
    [Property, Group( "Mouvement sur place (dev)" )] public float ThrowSimulationSweepHalf { get; set; } = 24f;

    /// <summary>
    /// Ralenti sur le <b>mouvement</b> de la balle uniquement (multiplie <see cref="Time.Delta"/> pour la phase).
    /// 1 = vitesse réelle ; 0,25 = quatre fois plus lent. Transmis au driver pour compenser <see cref="TrailRenderer.PointDistance"/> (longueur du ruban stable).
    /// </summary>
    [Property, Group( "Mouvement sur place (dev)" )] public float PreviewMotionTimeScale { get; set; } = 1f;

    [Property] public BallPickup Ball { get; set; }

    [Property] public bool Preview { get; set; } = true;

    [Property] public bool RunWithoutPlayInEditor { get; set; } = true;

    [Property] public bool RunDuringPlay { get; set; }

    [Property] public bool AllowWhenNotEditor { get; set; }

    [Property] public TeamId PreviewTeamTint { get; set; } = TeamId.Red;

    [Property, Group( "Lancer simulé (arène)" )] public float RefNormalThrowForceMin { get; set; } = 700f;

    [Property, Group( "Lancer simulé (arène)" )] public float RefNormalThrowForceMax { get; set; } = 2500f;

    [Property, Group( "Lancer simulé (arène)" )] public float RefOverchargeThrowForce { get; set; } = 6400f;

    [Property, Group( "Lancer simulé (arène)" )] public bool SimCharge25Percent { get; set; }

    [Property, Group( "Lancer simulé (arène)" )] public bool SimCharge50Percent { get; set; }

    [Property, Group( "Lancer simulé (arène)" )] public bool SimCharge75Percent { get; set; }

    [Property, Group( "Lancer simulé (arène)" )] public bool SimOvercharge { get; set; }

    private float _oscillationPhase;
    private Vector3 _restWorldPosition;
    private Rotation _restWorldRotation;
    private bool _restTransformCaptured;
    private bool _hadPreviewActive;

    protected override void OnDisabled()
    {
        StopPreviewIfNeeded();
    }

    protected override void OnDestroy()
    {
        StopPreviewIfNeeded();
    }

    protected override void OnUpdate()
    {
        if ( !ShouldRunEnvironment() )
        {
            StopPreviewIfNeeded();
            return;
        }

        if ( !Preview )
        {
            StopPreviewIfNeeded();
            return;
        }

        var ball = ResolveBall();
        if ( ball is null || !ball.IsValid() )
            return;

        var throwSpeed = ResolveSimulatedThrowSpeed();
        var linearForTrail = throwSpeed;
        var dt = Time.Delta;

        var motionScale = PreviewMotionTimeScale.Clamp( 0.05f, 2f );
        ball.SetDevAttackFlightPreview( true, null, PreviewTeamTint, throwSpeed, linearForTrail, motionScale );
        _hadPreviewActive = true;

        EnsureRestTransformCaptured();
        StepThrowOnPlaceOscillation( throwSpeed, dt );
    }

    private void EnsureRestTransformCaptured()
    {
        if ( _restTransformCaptured )
            return;

        _restWorldPosition = WorldPosition;
        _restWorldRotation = WorldRotation;
        _oscillationPhase = 0f;
        _restTransformCaptured = true;
    }

    /// <summary> x(t)=A·sin(ωt), v_max=A·ω = throwSpeed (à t réel ; le ralenti ne fait qu’étirer le temps de la phase). </summary>
    private void StepThrowOnPlaceOscillation( float throwSpeedU, float dt )
    {
        var a = ThrowSimulationSweepHalf > 0.01f ? ThrowSimulationSweepHalf : 0.01f;
        var omega = throwSpeedU / a;
        var motionScale = PreviewMotionTimeScale.Clamp( 0.05f, 2f );
        _oscillationPhase += omega * dt * motionScale;
        var axis = SimulatedThrowDirectionWorld.Normal;
        WorldPosition = _restWorldPosition + axis * (a * MathF.Sin( _oscillationPhase ));
        WorldRotation = _restWorldRotation;
    }

    private float ResolveSimulatedThrowSpeed()
    {
        if ( SimOvercharge )
            return RefOverchargeThrowForce > 0f ? RefOverchargeThrowForce : RefNormalThrowForceMax;

        var minF = RefNormalThrowForceMin <= 0f ? 1f : RefNormalThrowForceMin;
        var maxF = RefNormalThrowForceMax < minF ? minF : RefNormalThrowForceMax;

        if ( SimCharge75Percent )
            return minF.LerpTo( maxF, 0.75f );
        if ( SimCharge50Percent )
            return minF.LerpTo( maxF, 0.5f );
        if ( SimCharge25Percent )
            return minF.LerpTo( maxF, 0.25f );

        return maxF;
    }

    private BallPickup ResolveBall()
    {
        if ( Ball is not null && Ball.IsValid() )
            return Ball;

        return Components.Get<BallPickup>()
            ?? Components.GetInParent<BallPickup>( true )
            ?? Components.GetInChildren<BallPickup>( true );
    }

    private bool ShouldRunEnvironment()
    {
        if ( Game.IsEditor && !Game.IsPlaying && RunWithoutPlayInEditor )
            return true;
        if ( Game.IsPlaying && RunDuringPlay )
            return true;
        if ( AllowWhenNotEditor && !Game.IsEditor )
            return true;
        return false;
    }

    private void StopPreviewIfNeeded()
    {
        if ( !_hadPreviewActive && !_restTransformCaptured )
            return;

        var ball = ResolveBall();
        if ( ball is not null && ball.IsValid() )
            ball.SetDevAttackFlightPreview( false );

        if ( _restTransformCaptured )
        {
            WorldPosition = _restWorldPosition;
            WorldRotation = _restWorldRotation;
        }

        _hadPreviewActive = false;
        _restTransformCaptured = false;
        _oscillationPhase = 0f;
    }
}
