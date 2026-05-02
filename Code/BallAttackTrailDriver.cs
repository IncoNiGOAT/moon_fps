using System;
using Sandbox;

/// <summary>
/// Sur le GO du trail (ex. <c>Trail_ball</c>) avec un <see cref="TrailRenderer"/>.
/// Largeur : uniquement les courbes « Trail look » × <see cref="WidthBoost"/> (pas liée à la vitesse).
/// Longueur (<see cref="TrailRenderer.LifeTime"/>) et intensité du gradient : selon la vitesse linéaire de la balle
/// (<see cref="Rigidbody.Velocity"/>, ou <see cref="BallPickup.DevSimulatedLinearSpeedForTrail"/> en preview).
/// </summary>
public sealed class BallAttackTrailDriver : Component, Component.ExecuteInEditor
{
    [Property] public BallPickup Ball { get; set; }
    [Property] public TrailRenderer Trail { get; set; }

    [Property, Group( "Trail look" )] public bool PowerShotLook { get; set; } = true;

    [Property, Group( "Trail look" )] public float PointDistance { get; set; } = 2f;
    [Property, Group( "Trail look" )] public float LifeTime { get; set; } = 0.42f;
    [Property, Group( "Trail look" )] public int MaxPoints { get; set; } = 96;

    [Property, Group( "Trail look" )] public float WidthStart { get; set; } = 32f;
    [Property, Group( "Trail look" )] public float WidthMid { get; set; } = 14f;
    [Property, Group( "Trail look" )] public float WidthEnd { get; set; } = 1.5f;

    [Property, Group( "Trail look" )] public bool BillboardCamera { get; set; }

    [Property, Group( "Trail look" )] public float WidthBoost { get; set; } = 1f;

    [Property, Group( "Trail look" )] public float SimpleWidth { get; set; } = 28f;

    [Property, Group( "Trail look" )] public BlendMode TrailBlendMode { get; set; } = BlendMode.Lighten;

    [Property, Group( "Trail look" )] public bool Opaque { get; set; }

    [Property, Group( "Vs vitesse balle" )] public float SpeedForTrailShort { get; set; } = 150f;

    [Property, Group( "Vs vitesse balle" )] public float SpeedForTrailLong { get; set; } = 7000f;

    [Property, Group( "Vs vitesse balle" )] public bool ScaleLifeTimeByBallSpeed { get; set; } = true;

    [Property, Group( "Vs vitesse balle" )] public float LifeTimeMulSlowBall { get; set; } = 0.55f;

    [Property, Group( "Vs vitesse balle" )] public float LifeTimeMulFastBall { get; set; } = 1.25f;

    [Property, Group( "Vs vitesse balle" )] public bool ScaleBrightnessByBallSpeed { get; set; } = true;

    [Property, Group( "Vs vitesse balle" )] public float BrightnessMulSlowBall { get; set; } = 0.65f;

    [Property, Group( "Vs vitesse balle" )] public float BrightnessMulFastBall { get; set; } = 1.35f;

    private GameObject _ballRoot;

    protected override void OnStart()
    {
        ResolveRefs();
    }

    protected override void OnUpdate()
    {
        if ( Trail is null || !Trail.IsValid() )
            return;

        var ball = ResolveBall();
        if ( ball is null || !ball.IsValid() )
        {
            Trail.Enabled = false;
            Trail.Emitting = false;
            return;
        }

        if ( !ball.IsAttackFlightVisual() )
        {
            Trail.Enabled = false;
            Trail.Emitting = false;
            return;
        }

        Trail.Enabled = true;
        Trail.Emitting = true;

        var tint = ball.CurrentVisualTint;
        var speedU = ResolveBallLinearSpeed( ball );
        var speedT = Speed01ForTrail( speedU );

        var lengthMul = ScaleLifeTimeByBallSpeed
            ? LifeTimeMulSlowBall.LerpTo( LifeTimeMulFastBall, speedT )
            : 1f;

        var brightnessMul = ScaleBrightnessByBallSpeed
            ? BrightnessMulSlowBall.LerpTo( BrightnessMulFastBall, speedT )
            : 1f;

        var motionScale = ball.DevPreviewMotionTimeScale;
        var pointDist = PointDistance;
        if ( ball.DevSimulatedLinearSpeedForTrail > 0f && motionScale > 0.001f )
            pointDist *= motionScale;

        Trail.PointDistance = pointDist;
        Trail.LifeTime = LifeTime * lengthMul;
        Trail.MaxPoints = (int)(MaxPoints * lengthMul).Clamp( 16, 512 );
        Trail.Face = BillboardCamera ? SceneLineObject.FaceMode.Camera : SceneLineObject.FaceMode.Normal;
        Trail.BlendMode = TrailBlendMode;
        Trail.Opaque = Opaque;

        var wMul = WidthBoost * BallInverseUniformScale( _ballRoot );

        if ( PowerShotLook )
        {
            var width = new Curve();
            width.AddPoint( 0f, WidthStart * wMul );
            width.AddPoint( 0.35f, WidthMid * wMul );
            width.AddPoint( 1f, WidthEnd * wMul );
            Trail.Width = width;

            var grad = new Gradient();
            grad.AddColor( 0f, ScaleGradientColorIntensity( Color.Lerp( Color.White, tint, 0.2f ), brightnessMul ) );
            grad.AddColor( 0.12f, ScaleGradientColorIntensity( Color.Lerp( Color.White, tint, 0.55f ), brightnessMul ) );
            grad.AddColor( 0.45f, ScaleGradientColorIntensity( tint, brightnessMul ) );
            grad.AddColor( 1f, ScaleGradientColorIntensity( tint.WithAlpha( 0f ), brightnessMul ) );
            Trail.Color = grad;
        }
        else
        {
            var w = SimpleWidth * wMul;
            var widthFlat = new Curve();
            widthFlat.AddPoint( 0f, w );
            widthFlat.AddPoint( 1f, w );
            Trail.Width = widthFlat;

            var head = ScaleGradientColorIntensity( tint, brightnessMul );
            var tail = ScaleGradientColorIntensity( tint.WithAlpha( 0f ), brightnessMul );
            Trail.Color = Gradient.FromColors( new[] { head, tail } );
        }
    }

    private float ResolveBallLinearSpeed( BallPickup ball )
    {
        var rb = _ballRoot?.Components.Get<Rigidbody>();
        var speed = rb is not null && rb.IsValid() ? rb.Velocity.Length : 0f;
        var devLin = ball.DevSimulatedLinearSpeedForTrail;
        if ( devLin > 0f )
            speed = MathF.Max( speed, devLin );
        return speed;
    }

    private float Speed01ForTrail( float speedU )
    {
        var a = SpeedForTrailShort;
        var b = SpeedForTrailLong <= a ? a + 1f : SpeedForTrailLong;
        return InverseLerpClamp( a, b, speedU );
    }

    private static Color ScaleGradientColorIntensity( Color c, float rgbMul )
    {
        rgbMul = rgbMul <= 0f ? 0f : rgbMul;
        var a = c.a * rgbMul > 1f ? 1f : c.a * rgbMul;
        return new Color(
            (c.r * rgbMul).Clamp( 0f, 3f ),
            (c.g * rgbMul).Clamp( 0f, 3f ),
            (c.b * rgbMul).Clamp( 0f, 3f ),
            a.Clamp( 0f, 1f ) );
    }

    private void ResolveRefs()
    {
        if ( Ball is not null && Ball.IsValid() )
            _ballRoot = Ball.GameObject;
        else
        {
            var b = Components.GetInParent<BallPickup>( true );
            if ( b is not null && b.IsValid() )
            {
                Ball = b;
                _ballRoot = b.GameObject;
            }
        }

        if ( Trail is null || !Trail.IsValid() )
            Trail = Components.Get<TrailRenderer>() ?? Components.GetInChildren<TrailRenderer>( true );
    }

    private BallPickup ResolveBall()
    {
        if ( Ball is not null && Ball.IsValid() )
            return Ball;

        Ball = Components.GetInParent<BallPickup>( true );
        if ( Ball is not null && Ball.IsValid() )
            _ballRoot = Ball.GameObject;

        return Ball;
    }

    private static float InverseLerpClamp( float a, float b, float v )
    {
        if ( b <= a )
            return 0f;

        return ((v - a) / (b - a)).Clamp( 0f, 1f );
    }

    private static float BallInverseUniformScale( GameObject ballRoot )
    {
        if ( ballRoot is null || !ballRoot.IsValid() )
            return 1f;

        var s = ballRoot.LocalScale;
        var ax = Math.Abs( s.x );
        var ay = Math.Abs( s.y );
        var az = Math.Abs( s.z );
        var u = Math.Max( ax, Math.Max( ay, az ) );
        return u > 0.0001f ? 1f / u : 1f;
    }
}
