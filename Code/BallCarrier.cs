using System;
using System.Collections.Generic;
using Sandbox;

/// <summary>
/// Porteur de balle : ramassage, charge à 3 zones, stunt ragdoll, bonus catch ennemi / passe allié (0 rebond).
/// UI : <see cref="PlayerHud"/> (dash, charge, réticule) ; <see cref="BallCarrierAimZoom"/> pour le zoom FOV.
/// </summary>
public sealed partial class BallCarrier : Component
{
    #region Inspector — Ramassage & lancer

    [Property] public GameObject HoldPoint { get; set; }

    /// <summary> Portée ramassage / détection balle (prioritaire sur E). </summary>
    [Property, Title( "Ball pickup range" )]
    public float PickupRange { get; set; } = 120f;

    /// <summary> Portée ramassage props (<see cref="ThrowablePropPickup"/>), uniquement en prison, après la balle. </summary>
    [Property, Title( "Prop pickup range (prison)" )]
    public float PropPickupRange { get; set; } = 100f;
    [Property] public float ThrowForce { get; set; } = 1200f;

    /// <summary>
    /// Pousse la direction de lancer vers le haut (0 = direction caméra pure).
    /// Augmente si la trajectoire part trop bas par rapport au réticule (ex. 0.04–0.12).
    /// </summary>
    [Property] public float ThrowAimUpNudge { get; set; } = 0f;

    /// <summary>
    /// +Z monde au lâcher uniquement. Laisse 0 pour utiliser <see cref="BallPickup.ThrowReleaseWorldUpOffset"/> sur la balle.
    /// </summary>
    [Property] public float ThrowReleaseWorldUpOffset { get; set; } = 0f;

    /// <summary>
    /// Gauche / droite au lâcher (remplace la valeur sur la balle si non 0). Positif = droite, plan de visée horizontal.
    /// </summary>
    [Property] public float ThrowReleaseLateralOffset { get; set; } = 0f;

    [Property] public float MaxChargeTime { get; set; } = 1.0f;
    [Property] public float ThrowWindupTime { get; set; } = 0.12f;
    [Property] public string CancelChargeAction { get; set; } = "Attack2";

    #endregion

    #region Inspector — Zones de charge & forces

    /// <summary> Fin de la zone « charge normale » sur la barre 0–1 (ex. 0.8 = 80 %). </summary>
    [Property] public float ChargeNormalZoneEnd01 { get; set; } = 0.80f;

    /// <summary> Largeur de la zone overcharge après la normale (ex. 0.05 = 5 % de la barre totale). </summary>
    [Property] public float ChargeOverchargeZoneWidth01 { get; set; } = 0.05f;

    [Property] public float NormalThrowForceMin { get; set; } = 700f;
    [Property] public float NormalThrowForceMax { get; set; } = 2500f;
    [Property] public float OverchargeThrowForce { get; set; } = 6400f;

    #endregion

    #region Inspector — Son & bonus catch

    /// <summary> Son au relâchement en zone surchauffe (Sound Event .sound). </summary>
    [Property] public SoundEvent OverchargeThrowSound { get; set; }

    [Property] public float OverchargeThrowSoundVolume { get; set; } = 1f;

    /// <summary> Temps pour utiliser la surchauffe gratuite après un catch ennemi ou une passe allié (voir <see cref="AllyPassOverchargeMaxBounces"/>). </summary>
    [Property] public float EnemyCatchOverchargeBonusSeconds { get; set; } = 5f;

    /// <summary> Bonus surchauffe au ramassage d&apos;une passe allié (pas soi-même). </summary>
    [Property] public bool GrantOverchargeOnAllyPassCatch { get; set; } = true;

    /// <summary>
    /// Rebonds max cumulés depuis le lancer pour garder le bonus passe allié (0 = aucun rebond ; 1 = un mur ou contact &quot;rebond&quot;, etc.).
    /// </summary>
    [Property] public int AllyPassOverchargeMaxBounces { get; set; } = 0;

    #endregion

    #region Network — Anim répliquée (proxies / host voient le même graphe)

    /// <summary> Port d’arme / buste « balle en main » (<see cref="BallCarryUpperBodyAnimParameter"/>). </summary>
    [Sync] public bool NetAnimBallCarryUpperBody { get; set; }

    /// <summary> Maintien charge avant lancer (<see cref="ChargeHoldAnimParameter"/>). </summary>
    [Sync] public bool NetAnimBallChargeHold { get; set; }

    /// <summary> Extension dash après lancer surchauffe (<see cref="DashManager"/> lit cette valeur). </summary>
    [Sync] public bool NetAnimOverchargeThrowDash { get; set; }

    #endregion

    #region Inspector — Stunt (ragdoll)

    [Property] public float RagdollDuration { get; set; } = 1.0f;

    #endregion

    #region Inspector — Animation (charge)

    /// <summary>
    /// Renderer du corps (souvent l&apos;enfant <c>Body</c>). Si null, le premier <see cref="SkinnedModelRenderer"/> enfant est utilisé.
    /// </summary>
    [Property, Title( "Charge pose — Skinned renderer" )]
    public SkinnedModelRenderer ChargePoseRenderer { get; set; }

    /// <summary>
    /// Paramètre booléen du Animgraph : à mettre à <c>true</c> pendant le maintien du clic de charge (<c>Attack1</c>).
    /// Tu dois créer ce paramètre dans ton <c>.vanmgrph</c> et y brancher la séquence (ex. <c>citizen_retarget_mixamo_com</c>) pendant le maintien de <c>Attack1</c> en charge.
    /// </summary>
    [Property] public string ChargeHoldAnimParameter { get; set; } = "b_charge_hold";

    /// <summary>
    /// Bool AnimGraph : torse / bras en pose « porte la balle » tant que la balle est en main (hors charge / lancer).
    /// À brancher sur une couche masquée (jambe poids 0) — voir doc animation s&amp;box (bone mask / weight list).
    /// </summary>
    [Property] public string BallCarryUpperBodyAnimParameter { get; set; } = "b_carry_ball";

    /// <summary>
    /// Durée pendant laquelle <see cref="DashManager"/> garde <c>b_dash</c> actif après un lancer en surchauffe (windup + après le relâchement).
    /// Règle à peu près la durée de ton clip dash en secondes (ex. 0,8–1,2). Le windup <see cref="ThrowWindupTime"/> reste court pour la physique ; seul l&apos;anim est étirée.
    /// </summary>
    [Property, Title( "Overcharge throw — dash anim hold (s)" )]
    public float OverchargeThrowDashAnimHoldSeconds { get; set; } = 0.9f;

    #endregion

    private BallPickup _heldBall;
    private ThrowablePropPickup _heldProp;
    private PlayerController _playerController;
    private bool _isChargingThrow;
    private bool _isThrowing;
    private TimeSince _timeSinceThrowStarted;
    private TimeSince _timeSinceChargeStarted;
    private float _queuedThrowForce;
    private bool _pendingOverchargeThrowFx;
    private float _currentCharge01;
    private bool _freeOverchargeFromEnemyCatch;
    private bool _enemyCatchBonusFromEnemy;
    private TimeSince _timeSinceEnemyCatchBonusGranted;
    private bool _devAlwaysOverchargeBonus;
    private bool _pinChargeAtOverchargePeakUntilRelease;
    private bool _overchargeDashAnimHoldActive;
    private TimeSince _timeSinceOverchargeDashAnimStarted;
    private bool _isRagdolled;
    private TimeSince _timeSinceRagdoll;
    private GameObject _activeRagdoll;
    private float _savedWalkSpeed;
    private float _savedRunSpeed;
    private float _savedJumpSpeed;
    private float _currentRagdollDuration;
    private readonly List<(SkinnedModelRenderer Renderer, bool WasEnabled)> _ragdollSkinnedRestore = new();

    /// <summary> État exposé à <see cref="PlayerHud"/> / <see cref="BallCarrierAimZoom"/>. </summary>
    public bool HasHeldBall => _heldBall is not null;

    public bool HasHeldProp => _heldProp is not null;

    public bool IsChargingThrowActive => _isChargingThrow;
    public bool IsThrowWindupActive => _isThrowing;
    public float Charge01 => _currentCharge01;
    /// <summary> Fenêtre surchauffe gratuite : catch balle ennemie vivante ou passe allié sans rebond. </summary>
    public bool HasEnemyCatchBonus => _freeOverchargeFromEnemyCatch;

    /// <summary> Si <see cref="HasEnemyCatchBonus"/>, indique un catch sur balle ennemie (sinon bonus passe alliée). </summary>
    public bool IsEnemyCatchBonusFromEnemyCatch => _freeOverchargeFromEnemyCatch && _enemyCatchBonusFromEnemy;

    public bool IsOverchargePeakPinned => _pinChargeAtOverchargePeakUntilRelease;

    /// <summary> Secondes restantes avant expiration du bonus surchauffe, ou 0 si inactif. </summary>
    public float GetEnemyCatchBonusSecondsRemaining()
    {
        if ( !_freeOverchargeFromEnemyCatch || _heldBall is null )
            return 0f;

        var limit = EnemyCatchOverchargeBonusSeconds <= 0f ? 5f : EnemyCatchOverchargeBonusSeconds;
        return (limit - (float)_timeSinceEnemyCatchBonusGranted).Clamp( 0f, limit );
    }

    /// <summary> 1 = bonus vient d&apos;expirer, 0 = plus de fenêtre ; uniquement si <see cref="HasEnemyCatchBonus"/>. </summary>
    public float GetEnemyCatchBonusTimeLeft01()
    {
        if ( !_freeOverchargeFromEnemyCatch )
            return 0f;

        var dur = EnemyCatchOverchargeBonusSeconds <= 0f ? 5f : EnemyCatchOverchargeBonusSeconds;
        return dur > 1e-5f ? (GetEnemyCatchBonusSecondsRemaining() / dur).Clamp( 0f, 1f ) : 0f;
    }
    public bool IsStunnedRagdoll => _isRagdolled;

    /// <summary> Lancer surchauffe : <see cref="DashManager"/> garde <c>b_dash</c> pendant <see cref="OverchargeThrowDashAnimHoldSeconds"/>. </summary>
    public bool WantsOverchargeThrowDashAnim() => _overchargeDashAnimHoldActive;

    /// <summary> Ragdoll prison (pas le stunt) — désactive charge / ramassage. </summary>
    public bool IsJailKnockdownRagdollActive =>
        ( Components.Get<PrisonBallPlayer>() ?? Components.GetInChildren<PrisonBallPlayer>( true ) )
        ?.IsInJailKnockdownRagdoll ?? false;

    public bool WantsChargeAimZoom => (_isChargingThrow || _isThrowing) && _heldBall is not null;

    /// <summary>
    /// La balle prioritaire à <see cref="PickupRange"/> est une balle ennemie « vivante » : un catch donnerait le bonus surchauffe (<see cref="BallPickup.IsLiveEnemyBallFor"/>).
    /// Contrôle local uniquement ; faux si ragdoll stunt, prison knockdown, ou mains déjà occupées.
    /// </summary>
    public bool IsInRangeForLiveEnemyBallBonusPickup()
    {
        if ( !IsLocallyControlled() || IsJailKnockdownRagdollActive || _isRagdolled )
            return false;
        if ( _heldBall is not null || _heldProp is not null )
            return false;

        var best = FindNearestUnheldBallInPickupRange();
        return best is not null && best.IsLiveEnemyBallFor( GameObject );
    }

    protected override void OnStart()
    {
        if ( HoldPoint is null )
            HoldPoint = Scene.Camera is not null ? Scene.Camera.GameObject : GameObject;

        _playerController = ResolvePlayerController();
        _currentRagdollDuration = RagdollDuration;
    }

    protected override void OnUpdate()
    {
        try
        {
            OnUpdateCore();
        }
        finally
        {
            UpdateBallThrowAnimParameters();
        }
    }

    private void OnUpdateCore()
    {
        RefreshDevAlwaysOverchargeBonusFromPlayerHud();

        if ( IsJailKnockdownRagdollActive )
        {
            ReleaseHeldPropForJailOrInterrupt();
            return;
        }

        if ( _isRagdolled )
        {
            UpdateRagdollRecovery();
            return;
        }

        UpdateEnemyCatchBonusExpiry();

        if ( !IsLocallyControlled() )
            return;

        if ( _isChargingThrow )
        {
            UpdateThrowCharge();
            return;
        }

        if ( _isThrowing )
        {
            UpdateThrowWindup();
            return;
        }

        if ( Input.Pressed( "Use" ) && !_isThrowing )
            TryPickupsOnUse();

        if ( _heldProp is not null && !_isThrowing && Input.Pressed( "Attack1" ) )
            ThrowHeldPropNow();

        if ( _heldBall is not null && !_isThrowing && Input.Pressed( "Attack1" ) )
            StartThrowCharge();
    }

    /// <summary>
    /// Synchronise charge / port de balle sur l’AnimGraph. Les bools <see cref="NetAnimBallCarryUpperBody"/> /
    /// <see cref="NetAnimBallChargeHold"/> sont poussés par le <b>propriétaire</b> du pawn ; toutes les instances
    /// (host, autres clients) appliquent les mêmes paramètres au renderer.
    /// </summary>
    private void UpdateBallThrowAnimParameters()
    {
        UpdateOverchargeDashAnimHoldExpiry();

        if ( IsLocallyControlled() )
        {
            NetAnimBallCarryUpperBody = _heldBall is not null && !_isChargingThrow && !_isThrowing;
            NetAnimBallChargeHold = _isChargingThrow && !_isThrowing;
            NetAnimOverchargeThrowDash = _overchargeDashAnimHoldActive;
        }

        SkinnedModelRenderer renderer = default;

        if ( ChargePoseRenderer.IsValid() )
            renderer = ChargePoseRenderer;
        else
        {
            _playerController ??= ResolvePlayerController();
            if ( _playerController?.Renderer is SkinnedModelRenderer bodySkinned && bodySkinned.IsValid() )
                renderer = bodySkinned;
        }

        if ( !renderer.IsValid() )
            renderer = Components.GetInChildren<SkinnedModelRenderer>( true );

        if ( !renderer.IsValid() )
            return;

        if ( !string.IsNullOrWhiteSpace( ChargeHoldAnimParameter ) )
            renderer.Set( ChargeHoldAnimParameter, NetAnimBallChargeHold );

        if ( !string.IsNullOrWhiteSpace( BallCarryUpperBodyAnimParameter ) )
            renderer.Set( BallCarryUpperBodyAnimParameter, NetAnimBallCarryUpperBody );
    }

    private void UpdateOverchargeDashAnimHoldExpiry()
    {
        if ( !IsLocallyControlled() )
            return;

        if ( !_overchargeDashAnimHoldActive )
            return;
        var hold = OverchargeThrowDashAnimHoldSeconds <= 0f ? 0.01f : OverchargeThrowDashAnimHoldSeconds;
        if ( _timeSinceOverchargeDashAnimStarted >= hold )
            _overchargeDashAnimHoldActive = false;
    }

    /// <summary> E : balle si à portée ; sinon prop seulement en prison. Contrôle local uniquement. </summary>
    private void TryPickupsOnUse()
    {
        if ( HoldPoint is null )
            return;

        if ( _heldBall is not null || _heldProp is not null )
            return;

        if ( TryPickupNearestBall() )
            return;

        if ( IsCarrierInPrison() )
            TryPickupNearestProp();
    }

    private BallPickup FindNearestUnheldBallInPickupRange()
    {
        if ( HoldPoint is null )
            return null;

        var range = PickupRange <= 0f ? 0.001f : PickupRange;
        BallPickup bestBall = null;
        var bestDistance = float.MaxValue;

        foreach ( var ball in Scene.GetAllComponents<BallPickup>() )
        {
            if ( ball is null || ball.IsHeld )
                continue;

            var distance = WorldPosition.Distance( ball.WorldPosition );
            if ( distance > range || distance >= bestDistance )
                continue;

            bestDistance = distance;
            bestBall = ball;
        }

        return bestBall;
    }

    private bool TryPickupNearestBall()
    {
        if ( _heldBall is not null || HoldPoint is null )
            return false;

        var bestBall = FindNearestUnheldBallInPickupRange();
        if ( bestBall is null )
            return false;

        var enemyCatchOvercharge = bestBall.IsLiveEnemyBallFor( GameObject );
        var allyPassOvercharge = GrantOverchargeOnAllyPassCatch && bestBall.IsCleanAllyPassFor( GameObject, AllyPassOverchargeMaxBounces );

        // Repli suivi main : éviter HoldPoint=caméra (bouge trop / « flotte ») ; préférer le renderer corps.
        var holdForBall = HoldPoint;
        _playerController ??= ResolvePlayerController();
        if ( _playerController?.Renderer is SkinnedModelRenderer bodyR && bodyR.GameObject.IsValid() )
            holdForBall = bodyR.GameObject;
        else if ( ChargePoseRenderer.IsValid() )
            holdForBall = ChargePoseRenderer.GameObject;

        if ( !bestBall.PickUp( GameObject, holdForBall ) )
            return false;

        _heldBall = bestBall;
        if ( enemyCatchOvercharge || allyPassOvercharge )
        {
            _freeOverchargeFromEnemyCatch = true;
            _enemyCatchBonusFromEnemy = enemyCatchOvercharge;
            _timeSinceEnemyCatchBonusGranted = 0f;
        }

        return true;
    }

    private void TryPickupNearestProp()
    {
        if ( !IsCarrierInPrison() )
            return;

        if ( _heldProp is not null || _heldBall is not null || HoldPoint is null )
            return;

        var maxDist = PropPickupRange <= 0f ? 0.001f : PropPickupRange;
        ThrowablePropPickup best = null;
        var bestDistance = float.MaxValue;

        foreach ( var prop in Scene.GetAllComponents<ThrowablePropPickup>() )
        {
            if ( prop is null || prop.IsHeld )
                continue;

            var distance = WorldPosition.Distance( prop.WorldPosition );
            if ( distance > maxDist || distance >= bestDistance )
                continue;

            bestDistance = distance;
            best = prop;
        }

        if ( best is null )
            return;

        if ( best.PickUp( GameObject ) )
            _heldProp = best;
    }

    private void UpdateEnemyCatchBonusExpiry()
    {
        if ( _heldBall is null )
        {
            _freeOverchargeFromEnemyCatch = false;
            _enemyCatchBonusFromEnemy = false;
            return;
        }

        if ( _devAlwaysOverchargeBonus )
        {
            if ( !_isChargingThrow && !_isThrowing )
            {
                _freeOverchargeFromEnemyCatch = true;
                _enemyCatchBonusFromEnemy = true;
                _timeSinceEnemyCatchBonusGranted = 0f;
            }

            return;
        }

        if ( !_freeOverchargeFromEnemyCatch )
            return;

        var limit = EnemyCatchOverchargeBonusSeconds <= 0f ? 5f : EnemyCatchOverchargeBonusSeconds;
        if ( _timeSinceEnemyCatchBonusGranted >= limit )
        {
            _freeOverchargeFromEnemyCatch = false;
            _enemyCatchBonusFromEnemy = false;
        }
    }

    private void RefreshDevAlwaysOverchargeBonusFromPlayerHud()
    {
        if ( !IsLocallyControlled() )
        {
            _devAlwaysOverchargeBonus = false;
            return;
        }

        var hud = Components.Get<PlayerHud>()
            ?? Components.GetInChildren<PlayerHud>( true )
            ?? Components.GetInParent<PlayerHud>( true );

        _devAlwaysOverchargeBonus = hud is not null && hud.DevAlwaysOverchargeBonus;
    }

    private bool IsCarrierInPrison()
    {
        for ( var go = GameObject; go is not null && go.IsValid(); go = go.Parent )
        {
            var prison = go.Components.Get<PrisonBallPlayer>() ?? go.Components.GetInChildren<PrisonBallPlayer>( true );
            if ( prison is not null && prison.InPrison )
                return true;
        }

        return false;
    }

    private PlayerController ResolvePlayerController()
    {
        var pc = Components.Get<PlayerController>()
            ?? Components.GetInChildren<PlayerController>( true );

        var go = GameObject;
        while ( pc is null && go is not null )
        {
            pc = go.Components.Get<PlayerController>();
            go = go.Parent;
        }

        return pc;
    }

    public bool ShouldShowLocalHud()
    {
        _playerController = ResolvePlayerController();
        if ( _playerController is null )
            return false;

        if ( Networking.IsActive )
            return TryGetNetworkRoot( GameObject, out var root ) && root.Network.IsOwner;

        if ( _playerController.UseCameraControls )
            return true;

        var n = 0;
        foreach ( var p in Scene.GetAllComponents<PlayerController>() )
        {
            if ( p is not null )
                n++;
        }

        return n == 1;
    }

    /// <summary> Même règle que <see cref="DashManager"/> : entrées clavier / souris uniquement pour ce joueur. </summary>
    private bool IsLocallyControlled()
    {
        _playerController = ResolvePlayerController();
        if ( _playerController is null )
            return false;

        if ( !_playerController.UseInputControls )
            return false;

        if ( !Networking.IsActive )
            return true;

        return TryGetNetworkRoot( GameObject, out var root ) && root.Network.IsOwner;
    }

    /// <summary> <see cref="BallPickup"/> réplique le ramassage (RPC) : chaque instance du porteur met à jour sa référence balle. </summary>
    internal void NetSetHeldBallFromNetwork( BallPickup ball )
    {
        _heldBall = ball;
    }

    /// <summary> Aligné sur le lancer répliqué : libère la référence balle sur toutes les machines. </summary>
    internal void NetClearHeldBallFromNetwork()
    {
        _heldBall = null;
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
