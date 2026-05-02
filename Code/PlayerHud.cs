using System;
using Sandbox;

/// <summary>
/// HUD joueur local : dash, barre de charge, panneau bonus surchauffe, prompt catch ennemi, réticule. Un seul composant sur le prefab.
/// </summary>
[Title( "Player HUD" )]
public sealed class PlayerHud : Component
{
    [Property] public BallCarrier Carrier { get; set; }

    [Property] public DashManager Dash { get; set; }

    [Property, Group( "HUD — Dash" )] public bool ShowDashHud { get; set; } = true;

    [Property, Group( "HUD — Dash" )] public float DashHudMarginRight { get; set; } = 36f;

    [Property, Group( "HUD — Dash" )] public float DashHudMarginBottom { get; set; } = 36f;

    [Property, Group( "HUD — Dash" )] public float DashHudIconSize { get; set; } = 56f;

    [Property, Group( "HUD — Dash" )] public Texture DashHudIconTexture { get; set; }

    [Property, Group( "HUD — Dash" )] public Color DashHudIconColorReady { get; set; } = new Color( 0.45f, 0.95f, 1f, 1f );

    [Property, Group( "HUD — Dash" )] public Color DashHudIconColorDim { get; set; } = new Color( 0.42f, 0.44f, 0.48f, 0.42f );

    [Property, Group( "HUD — Dash" )] public Color DashHudPlateReady { get; set; } = new Color( 0.06f, 0.08f, 0.1f, 0.88f );

    [Property, Group( "HUD — Dash" )] public Color DashHudPlateDim { get; set; } = new Color( 0.05f, 0.06f, 0.07f, 0.5f );

    [Property, Group( "HUD — Charge" )] public bool ShowChargeHud { get; set; } = true;

    /// <summary> Décalage depuis le milieu vertical de l’écran (positif = plus bas). </summary>
    [Property, Group( "HUD — Charge" )] public float ChargeHudBarOffsetBelowScreenCenter { get; set; } = 200f;

    [Property, Group( "HUD — Charge" )] public Color ChargeHudNormalZoneColor { get; set; } = new Color( 0.95f, 0.82f, 0.15f, 0.4f );

    [Property, Group( "HUD — Charge" )] public Color ChargeHudOverchargeZoneColor { get; set; } = new Color( 1f, 0.65f, 0.1f, 0.45f );

    [Property, Group( "HUD — Charge" )] public Color ChargeHudStuntZoneColor { get; set; } = new Color( 0.95f, 0.2f, 0.15f, 0.5f );

    [Property, Group( "HUD — Charge" )] public float ChargeHudBarWidth { get; set; } = 448f;

    [Property, Group( "HUD — Charge" )] public float ChargeHudBarHeight { get; set; } = 28f;

    [Property, Group( "HUD — Charge" )] public int ChargeHudZoneLabelSize { get; set; } = 12;

    [Property, Group( "HUD — Bonus surchauffe" )] public bool ShowOverchargeBonusHud { get; set; } = true;

    [Property, Group( "HUD — Bonus surchauffe" )]
    public float OverchargeBonusHudBarOffsetBelowScreenCenter { get; set; } = 200f;

    [Property, Group( "HUD — Bonus surchauffe" )] public float OverchargeBonusTitleSize { get; set; } = 38f;

    [Property, Group( "HUD — Bonus surchauffe" )] public bool ShowOverchargeBonusTimer { get; set; } = true;

    [Property, Group( "HUD — Bonus surchauffe" )] public float OverchargeBonusTimerBarHeight { get; set; } = 16f;

    [Property, Group( "HUD — Bonus surchauffe" )] public float OverchargeBonusTimerOutline { get; set; } = 3.5f;

    [Property, Group( "HUD — Bonus surchauffe" )]
    public Color OverchargeBonusTimerTrackColor { get; set; } = Color.White;

    [Property, Group( "HUD — Bonus surchauffe" )]
    public Color OverchargeBonusTimerBarFill { get; set; } = new Color( 0.12f, 0.98f, 0.38f, 1f );

    [Property, Group( "HUD — Bonus surchauffe" )]
    public Color OverchargeBonusTimerOutlineColor { get; set; } = Color.Black;

    /// <summary> DEV : tant que coché, le porteur local a en permanence la fenêtre bonus surchauffe (balle en main, hors charge / windup). </summary>
    [Property, Group( "HUD — Bonus surchauffe" ), Title( "DEV — Toujours bonus surchauffe" )]
    public bool DevAlwaysOverchargeBonus { get; set; }

    [Property, Group( "HUD — Crosshair" )] public bool ShowCrosshairHud { get; set; } = true;

    [Property, Group( "HUD — Crosshair" )] public bool AlwaysShowCrosshair { get; set; } = true;

    [Property, Group( "HUD — Crosshair" )] public Color CrosshairColor { get; set; } = new Color( 1f, 1f, 1f, 0.92f );

    [Property, Group( "HUD — Crosshair" )] public float CrosshairArmLength { get; set; } = 10f;

    [Property, Group( "HUD — Crosshair" )] public float CrosshairThickness { get; set; } = 2f;

    [Property, Group( "HUD — Prompt catch ennemi" )]
    public bool ShowEnemyCatchBonusPromptHud { get; set; } = true;

    [Property, Group( "HUD — Prompt catch ennemi" )] public float EnemyCatchPromptCircleDiameter { get; set; } = 104f;

    [Property, Group( "HUD — Prompt catch ennemi" )] public float EnemyCatchPromptRingThickness { get; set; } = 5f;

    [Property, Group( "HUD — Prompt catch ennemi" )] public float EnemyCatchPromptMarginRight { get; set; } = 48f;

    /// <summary> Décalage vertical depuis le milieu de l’écran (positif = plus bas). </summary>
    [Property, Group( "HUD — Prompt catch ennemi" )]
    public float EnemyCatchPromptOffsetFromScreenCenterY { get; set; }

    [Property, Group( "HUD — Prompt catch ennemi" )]
    public Color EnemyCatchPromptRingColor { get; set; } = new Color( 0.06f, 0.08f, 0.12f, 0.94f );

    [Property, Group( "HUD — Prompt catch ennemi" )]
    public Color EnemyCatchPromptFillColor { get; set; } = new Color( 0.12f, 0.14f, 0.2f, 0.92f );

    [Property, Group( "HUD — Prompt catch ennemi" )] public string EnemyCatchPromptText { get; set; } = "Press E";

    [Property, Group( "HUD — Prompt catch ennemi" )] public float EnemyCatchPromptTextSize { get; set; } = 17f;

    /// <summary> DEV : affiche toujours le rond « Press E » (local) pour le positionner sans balle ennemie à portée. </summary>
    [Property, Group( "HUD — Prompt catch ennemi" ), Title( "DEV — Toujours prompt catch" )]
    public bool DevAlwaysShowEnemyCatchPrompt { get; set; }

    protected override void OnUpdate()
    {
        var carrier = ResolveCarrier();
        if ( carrier is null || !carrier.ShouldShowLocalHud() || Scene.Camera is null )
            return;

        var hud = Scene.Camera.Hud;

        if ( ShowDashHud )
            DrawDashHud( hud );

        if ( ShowChargeHud && carrier.HasHeldBall )
            DrawChargeHudBar( carrier, hud );

        if ( ShowEnemyCatchBonusPromptHud && (DevAlwaysShowEnemyCatchPrompt || carrier.IsInRangeForLiveEnemyBallBonusPickup()) )
            DrawEnemyCatchBonusPromptHud( hud );

        if ( ShowCrosshairHud && (AlwaysShowCrosshair || carrier.HasHeldBall || carrier.HasHeldProp) )
            DrawCrosshair( hud );
    }

    private BallCarrier ResolveCarrier()
    {
        if ( Carrier is not null && Carrier.IsValid() )
            return Carrier;

        return Components.Get<BallCarrier>()
            ?? Components.GetInChildren<BallCarrier>( true )
            ?? Components.GetInParent<BallCarrier>( true );
    }

    private DashManager ResolveDash()
    {
        if ( Dash is not null && Dash.IsValid() )
            return Dash;

        return Components.Get<DashManager>()
            ?? Components.GetInChildren<DashManager>( true )
            ?? Components.GetInParent<DashManager>( true );
    }

    private void DrawEnemyCatchBonusPromptHud( Sandbox.Rendering.HudPainter hud )
    {
        var d = EnemyCatchPromptCircleDiameter < 40f ? 104f : EnemyCatchPromptCircleDiameter;
        var ring = EnemyCatchPromptRingThickness.Clamp( 1f, 16f );
        var mr = EnemyCatchPromptMarginRight < 4f ? 48f : EnemyCatchPromptMarginRight;

        var cx = Screen.Width - mr - d * 0.5f;
        var cy = Screen.Height * 0.5f + EnemyCatchPromptOffsetFromScreenCenterY;

        var outer = d + ring * 2f;
        hud.DrawCircle( new Vector2( cx, cy ), new Vector2( outer, outer ), EnemyCatchPromptRingColor );
        hud.DrawCircle( new Vector2( cx, cy ), new Vector2( d, d ), EnemyCatchPromptFillColor );

        var text = string.IsNullOrWhiteSpace( EnemyCatchPromptText ) ? "Press E" : EnemyCatchPromptText;
        var textSize = EnemyCatchPromptTextSize < 8f ? 17f : EnemyCatchPromptTextSize;
        var textW = text.Length * textSize * 0.48f;
        var textPos = new Vector2( cx - textW * 0.5f, cy - textSize * 0.45f );
        DrawPromptOutlinedText( hud, text, textSize, textPos, new Color( 0.98f, 0.98f, 1f, 1f ) );
    }

    private static void DrawPromptOutlinedText( Sandbox.Rendering.HudPainter hud, string text, float size, Vector2 pos, Color fill )
    {
        var outline = new Color( 0f, 0f, 0f, 0.88f );
        for ( var a = 0; a < 8; a++ )
        {
            var rad = a * (Math.PI / 4.0);
            var ox = (float)(Math.Cos( rad ) * 1.6);
            var oy = (float)(Math.Sin( rad ) * 1.6);
            hud.DrawText( new TextRendering.Scope( text, outline, size ), pos + new Vector2( ox, oy ) );
        }

        hud.DrawText( new TextRendering.Scope( text, fill, size ), pos );
    }

    private void DrawDashHud( Sandbox.Rendering.HudPainter hud )
    {
        var dash = ResolveDash();
        if ( dash is null || !dash.IsValid() )
            return;

        var size = DashHudIconSize < 24f ? 56f : DashHudIconSize;
        var mr = DashHudMarginRight;
        var mb = DashHudMarginBottom;
        var x = Screen.Width - mr - size;
        var y = Screen.Height - mb - size;
        var rect = new Rect( x, y, size, size );

        var dashing = dash.IsDashActive();
        var ready = dash.IsDashReady();
        var lit = ready || dashing;

        var plate = lit ? DashHudPlateReady : DashHudPlateDim;
        var glyph = lit ? DashHudIconColorReady : DashHudIconColorDim;
        var borderA = lit ? 0.55f : 0.18f;
        var borderCol = new Color( glyph.r, glyph.g, glyph.b, borderA );

        hud.DrawRect( new Rect( rect.Left - 3f, rect.Top - 3f, rect.Width + 6f, rect.Height + 6f ),
            new Color( 0f, 0f, 0f, lit ? 0.45f : 0.28f ) );
        hud.DrawRect( rect, plate );
        hud.DrawRect( new Rect( rect.Left, rect.Top, rect.Width, 2f ), borderCol );
        hud.DrawRect( new Rect( rect.Left, rect.Bottom - 2f, rect.Width, 2f ), borderCol );
        hud.DrawRect( new Rect( rect.Left, rect.Top, 2f, rect.Height ), borderCol );
        hud.DrawRect( new Rect( rect.Right - 2f, rect.Top, 2f, rect.Height ), borderCol );

        if ( DashHudIconTexture is not null )
            hud.DrawTexture( DashHudIconTexture, rect, glyph );
        else
            DrawDashPlaceholderGlyph( hud, rect, glyph, dashing );
    }

    private void DrawChargeHudBar( BallCarrier c, Sandbox.Rendering.HudPainter hud )
    {
        var barWidth = ChargeHudBarWidth <= 8f ? 448f : ChargeHudBarWidth;
        var barHeight = ChargeHudBarHeight <= 4f ? 28f : ChargeHudBarHeight;
        var x = (Screen.Width - barWidth) * 0.5f;

        if ( ShowOverchargeBonusHud && (c.HasEnemyCatchBonus || c.IsOverchargePeakPinned) )
        {
            var yb = Screen.Height * 0.5f + OverchargeBonusHudBarOffsetBelowScreenCenter;
            DrawOverchargeBonusHud( c, hud, x, yb, barWidth, barHeight );
            return;
        }

        var y = Screen.Height * 0.5f + ChargeHudBarOffsetBelowScreenCenter;
        DrawNormalChargeHudBar( c, hud, x, y, barWidth, barHeight );
    }

    private void DrawNormalChargeHudBar( BallCarrier c, Sandbox.Rendering.HudPainter hud, float x, float y, float barWidth, float barHeight )
    {
        var nEnd = c.GetHudNormalZoneEnd01();
        var ocEnd = c.GetHudOverchargeZoneEnd01();
        var wN = barWidth * nEnd;
        var wO = barWidth * (ocEnd - nEnd);
        var wS = barWidth * (1f - ocEnd);

        static Color ZoneTrack( Color zoneTint )
        {
            return new Color( zoneTint.r * 0.38f, zoneTint.g * 0.38f, zoneTint.b * 0.38f, 0.88f );
        }

        var bN = new Color( 1f, 0.92f, 0.22f, 0.94f );
        var bO = new Color( 1f, 0.52f, 0.06f, 0.94f );
        var bS = new Color( 1f, 0.16f, 0.12f, 0.96f );

        var frame = new Color( 0.04f, 0.04f, 0.06f, 0.94f );
        var frameHighlight = new Color( 1f, 1f, 1f, 0.22f );
        var divider = new Color( 0f, 0f, 0f, 0.92f );
        var pad = 4f;

        hud.DrawRect( new Rect( x - pad, y - pad, barWidth + pad * 2f, barHeight + pad * 2f ), frame );
        hud.DrawRect( new Rect( x - pad, y - pad, barWidth + pad * 2f, 2f ), frameHighlight );

        hud.DrawRect( new Rect( x, y, wN, barHeight ), ZoneTrack( ChargeHudNormalZoneColor ) );
        hud.DrawRect( new Rect( x + wN, y, wO, barHeight ), ZoneTrack( ChargeHudOverchargeZoneColor ) );
        hud.DrawRect( new Rect( x + wN + wO, y, wS, barHeight ), ZoneTrack( ChargeHudStuntZoneColor ) );

        var divW = 3f;
        hud.DrawRect( new Rect( x + wN - divW * 0.5f, y - 1f, divW, barHeight + 2f ), divider );
        hud.DrawRect( new Rect( x + wN + wO - divW * 0.5f, y - 1f, divW, barHeight + 2f ), divider );

        var chargeDisplay = GetChargeHudDisplay01( c ).Clamp( 0f, 1f );
        var fillW = barWidth * chargeDisplay;

        var wFillN = fillW < wN ? fillW : wN;
        if ( wFillN > 0.5f )
            hud.DrawRect( new Rect( x, y, wFillN, barHeight ), bN );

        if ( fillW > wN )
        {
            var rem = fillW - wN;
            var wFillO = rem < wO ? rem : wO;
            if ( wFillO > 0.5f )
                hud.DrawRect( new Rect( x + wN, y, wFillO, barHeight ), bO );
        }

        if ( fillW > wN + wO )
        {
            var rem = fillW - wN - wO;
            var wFillS = rem < wS ? rem : wS;
            if ( wFillS > 0.5f )
                hud.DrawRect( new Rect( x + wN + wO, y, wFillS, barHeight ), bS );
        }

        if ( fillW > 2f && chargeDisplay < 0.998f )
        {
            var tipX = x + fillW;
            hud.DrawRect( new Rect( tipX - 1.5f, y - 2f, 3f, barHeight + 4f ), new Color( 1f, 1f, 1f, 0.92f ) );
        }

        var labelSize = ChargeHudZoneLabelSize < 8 ? 12 : ChargeHudZoneLabelSize;
        var labelY = y - 20f;
        var lblN = new Color( 0.98f, 0.9f, 0.35f, 0.95f );
        var lblO = new Color( 1f, 0.72f, 0.35f, 0.95f );
        var lblS = new Color( 1f, 0.45f, 0.4f, 0.95f );

        hud.DrawText( new TextRendering.Scope( "NORMAL", lblN, labelSize ), new Vector2( x + 8f, labelY ) );

        var midLabel = wO >= 44f ? "SURCHARGE" : "OC";
        var midTx = x + wN + (wO - midLabel.Length * (labelSize * 0.52f)) * 0.5f;
        midTx = midTx < x + wN + 2f ? x + wN + 2f : midTx;
        hud.DrawText( new TextRendering.Scope( midLabel, lblO, labelSize ), new Vector2( midTx, labelY ) );

        hud.DrawText( new TextRendering.Scope( "STUNT", lblS, labelSize ), new Vector2( x + wN + wO + 8f, labelY ) );
    }

    private void DrawOverchargeBonusHud( BallCarrier c, Sandbox.Rendering.HudPainter hud, float x, float pillTop, float barWidth, float barHeight )
    {
        _ = barHeight;

        var titleSize = OverchargeBonusTitleSize < 10f ? 38f : OverchargeBonusTitleSize;
        var pillH = OverchargeBonusTimerBarHeight < 6f ? 16f : OverchargeBonusTimerBarHeight;
        var outline = OverchargeBonusTimerOutline.Clamp( 1f, 10f );
        var centerX = x + barWidth * 0.5f;

        var titleY = pillTop - titleSize - 14f;
        DrawOverchargeStrikersTitle( hud, "OVERCHARGE", centerX, titleY, titleSize );

        if ( !ShowOverchargeBonusTimer || !c.HasEnemyCatchBonus )
            return;

        var rem = c.GetEnemyCatchBonusSecondsRemaining();
        if ( rem <= 0.02f )
            return;

        var t01 = c.GetEnemyCatchBonusTimeLeft01().Clamp( 0f, 1f );
        DrawOverchargePillTimerBar( hud, x, pillTop, barWidth, pillH, outline, t01 );
    }

    /// <summary> Titre façon « Strikers » : ombre, halo bleu, contour marine, face claire. </summary>
    private static void DrawOverchargeStrikersTitle( Sandbox.Rendering.HudPainter hud, string text, float centerX, float topY, float size )
    {
        var textW = text.Length * size * 0.56f;
        var left = centerX - textW * 0.5f;
        var pos = new Vector2( left, topY );

        hud.DrawText( new TextRendering.Scope( text, new Color( 0f, 0f, 0f, 0.72f ), size ), pos + new Vector2( 3.5f, 4.5f ) );

        var skyGlow = new Color( 0.38f, 0.72f, 1f, 0.48f );
        for ( var a = 0; a < 12; a++ )
        {
            var rad = a * (Math.PI / 6.0);
            var ox = (float)(Math.Cos( rad ) * 5.2);
            var oy = (float)(Math.Sin( rad ) * 5.2);
            hud.DrawText( new TextRendering.Scope( text, skyGlow, size ), pos + new Vector2( ox, oy ) );
        }

        var navy = new Color( 0.02f, 0.07f, 0.22f, 1f );
        for ( var ring = 4; ring >= 1; ring-- )
        {
            for ( var a = 0; a < 8; a++ )
            {
                var rad = a * (Math.PI / 4.0);
                var ox = (float)(Math.Cos( rad ) * ring * 1.18f);
                var oy = (float)(Math.Sin( rad ) * ring * 1.18f);
                hud.DrawText( new TextRendering.Scope( text, navy, size ), pos + new Vector2( ox, oy ) );
            }
        }

        var face = new Color( 0.93f, 0.98f, 1f, 1f );
        hud.DrawText( new TextRendering.Scope( text, new Color( 1f, 1f, 1f, 0.42f ), size ), pos + new Vector2( -1f, -1.4f ) );
        hud.DrawText( new TextRendering.Scope( text, face, size ), pos );
    }

    private void DrawOverchargePillTimerBar( Sandbox.Rendering.HudPainter hud, float x, float y, float w, float h, float outline, float fill01 )
    {
        var stroke = OverchargeBonusTimerOutlineColor;
        var track = OverchargeBonusTimerTrackColor;
        var fillCol = OverchargeBonusTimerBarFill;

        DrawHudCapsule( hud, new Rect( x - outline, y - outline, w + outline * 2f, h + outline * 2f ), stroke );
        DrawHudCapsule( hud, new Rect( x, y, w, h ), track );

        var fw = (w * fill01).Clamp( 0f, w );
        if ( fw > 0.35f )
            DrawHudCapsule( hud, new Rect( x, y, fw, h ), fillCol );
    }

    /// <summary> Barre « pilule » (extrémités arrondies). </summary>
    private static void DrawHudCapsule( Sandbox.Rendering.HudPainter hud, Rect b, Color color )
    {
        var h = b.Height;
        var w = b.Width;
        if ( h <= 0.5f || w <= 0.5f )
            return;

        var r = h * 0.5f;
        if ( w <= h + 0.01f )
        {
            hud.DrawCircle( new Vector2( b.Left + w * 0.5f, b.Top + r ), new Vector2( w, h ), color );
            return;
        }

        hud.DrawCircle( new Vector2( b.Left + r, b.Top + r ), new Vector2( h, h ), color );
        hud.DrawCircle( new Vector2( b.Left + w - r, b.Top + r ), new Vector2( h, h ), color );
        hud.DrawRect( new Rect( b.Left + r, b.Top, w - h, h ), color );
    }

    private static float GetChargeHudDisplay01( BallCarrier c )
    {
        if ( c.HasEnemyCatchBonus || c.IsOverchargePeakPinned )
            return c.GetHudOverchargeZoneEnd01();

        if ( c.IsChargingThrowActive || c.IsThrowWindupActive )
            return c.Charge01.Clamp( 0f, 1f );

        return 0f;
    }

    private static void DrawDashPlaceholderGlyph( Sandbox.Rendering.HudPainter hud, Rect rect, Color color, bool dashing )
    {
        var c = dashing
            ? new Color( 1f, 0.82f, 0.25f, color.a )
            : color;

        var thick = (rect.Width * 0.1f).Clamp( 3f, 8f );
        var hBand = rect.Height * 0.52f;
        var left = rect.Left + rect.Width * 0.18f;
        var top = rect.Top + (rect.Height - hBand) * 0.5f;

        hud.DrawRect( new Rect( left, top, thick, hBand ), c );

        var x0 = left + thick + rect.Width * 0.12f;
        var wFull = rect.Width * 0.42f;
        hud.DrawRect( new Rect( x0, top + hBand * 0.06f, wFull, thick ), c );
        hud.DrawRect( new Rect( x0 + rect.Width * 0.04f, top + hBand * 0.42f, wFull * 0.82f, thick ), c );
        hud.DrawRect( new Rect( x0 + rect.Width * 0.08f, top + hBand * 0.78f, wFull * 0.64f, thick ), c );
    }

    private void DrawCrosshair( Sandbox.Rendering.HudPainter hud )
    {
        var cx = Screen.Width * 0.5f;
        var cy = Screen.Height * 0.5f;
        var halfW = CrosshairArmLength;
        var t = CrosshairThickness;

        hud.DrawRect( new Rect( cx - halfW, cy - t * 0.5f, halfW * 2f, t ), CrosshairColor );
        hud.DrawRect( new Rect( cx - t * 0.5f, cy - halfW, t, halfW * 2f ), CrosshairColor );
    }
}
