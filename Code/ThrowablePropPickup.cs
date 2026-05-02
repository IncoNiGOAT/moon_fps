using System.Collections.Generic;
using Sandbox;

/// <summary>
/// Cube / prop ramassable en prison uniquement (voir <see cref="BallCarrier"/>).
/// Portage devant le perso selon la <b>caméra du joueur</b> (vue TPS), pas la main ; le buste qui tourne suit la vue.
/// </summary>
[Title( "Throwable prop pickup" )]
public sealed class ThrowablePropPickup : Component
{
    [Property] public float ThrowForce { get; set; } = 900f;

    [Property] public float ThrowSpawnOffset { get; set; } = 20f;

    [Property] public float ThrowReleaseWorldUpOffset { get; set; } = 0f;

    [Property] public bool EnhancedCcdWhenThrown { get; set; } = true;

    /// <summary> Distance devant le corps, le long du regard (caméra, projeté sur l&apos;horizontal). </summary>
    [Property] public float BustCarryForward { get; set; } = 48f;

    [Property] public float BustCarryRight { get; set; } = 0f;

    /// <summary> Au-dessus du point corps (souvent buste / torse). </summary>
    [Property] public float BustCarryUp { get; set; } = 40f;

    public bool IsHeld { get; private set; }
    public GameObject Holder { get; private set; }

    private Rigidbody _rigidbody;
    private readonly List<Collider> _disabledColliders = new();

    protected override void OnStart()
    {
        _rigidbody = Components.Get<Rigidbody>();
        ApplyCcdIfNeeded();
    }

    private void ApplyCcdIfNeeded()
    {
        if ( !EnhancedCcdWhenThrown || _rigidbody is null || !_rigidbody.IsValid() )
            return;

        _rigidbody.EnhancedCcd = true;
    }

    protected override void OnUpdate()
    {
        if ( !IsHeld )
            return;

        ApplyBustCarryTransform();
    }

    public bool PickUp( GameObject player )
    {
        if ( IsHeld || player is null || !player.IsValid() )
            return false;

        IsHeld = true;
        Holder = player;

        SetCollidersEnabled( false );

        if ( _rigidbody is not null )
        {
            _rigidbody.Velocity = Vector3.Zero;
            _rigidbody.AngularVelocity = Vector3.Zero;
            _rigidbody.Enabled = false;
        }

        GameObject.Parent = null;
        return true;
    }

    public void Throw( Vector3 direction, float? customForce = null, float? releaseWorldUpOverride = null )
    {
        if ( !IsHeld )
            return;

        IsHeld = false;
        Holder = null;

        var force = customForce ?? ThrowForce;
        var throwDir = direction.Length > 0.001f ? direction.Normal : Vector3.Forward;

        var upExtra = releaseWorldUpOverride ?? ThrowReleaseWorldUpOffset;
        var releasePosition = WorldPosition + throwDir * ThrowSpawnOffset + Vector3.Up * upExtra;
        var releaseRotation = WorldRotation;

        GameObject.Parent = null;
        WorldPosition = releasePosition;
        WorldRotation = releaseRotation;

        SetCollidersEnabled( true );

        if ( _rigidbody is not null )
        {
            _rigidbody.Enabled = true;
            ApplyCcdIfNeeded();
            _rigidbody.Velocity = throwDir * force;
        }
    }

    public void ForceReleaseWithoutThrow()
    {
        if ( !IsHeld )
            return;

        IsHeld = false;
        Holder = null;
        SetCollidersEnabled( true );

        if ( _rigidbody is not null )
        {
            _rigidbody.Enabled = true;
            ApplyCcdIfNeeded();
        }
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
        {
            if ( collider is not null )
                collider.Enabled = true;
        }

        _disabledColliders.Clear();
    }

    private void ApplyBustCarryTransform()
    {
        if ( Holder is null || !Holder.IsValid() )
            return;

        var pc = ResolvePlayerControllerOnRoot( Holder );
        var bodyGo = pc?.Body?.GameObject;
        var origin = bodyGo is not null && bodyGo.IsValid() ? bodyGo.WorldPosition : Holder.WorldPosition;
        var bodyRot = bodyGo is not null && bodyGo.IsValid() ? bodyGo.WorldRotation : Holder.WorldRotation;

        if ( !TryGetCameraRelativeCarryAxes( Holder, pc, Scene, out var forward, out var right ) )
        {
            forward = bodyRot.Forward.WithZ( 0f );
            if ( forward.Length < 0.01f )
                forward = bodyRot.Forward;
            forward = forward.Normal;

            right = forward.Cross( Vector3.Up );
            if ( right.Length < 0.01f )
                right = bodyRot.Right;
            right = right.Normal;
        }

        WorldPosition = origin
            + forward * BustCarryForward
            + right * BustCarryRight
            + Vector3.Up * BustCarryUp;

        WorldRotation = Rotation.LookAt( forward, Vector3.Up );
    }

    /// <summary>
    /// Axe &quot;devant&quot; = forward caméra projeté sur le plan horizontal (même logique qu&apos;un déplacement TPS).
    /// </summary>
    private static bool TryGetCameraRelativeCarryAxes( GameObject holder, PlayerController pc, Scene scene, out Vector3 forward, out Vector3 right )
    {
        forward = default;
        right = default;

        var cam = ResolveHolderCamera( holder, pc, scene );
        if ( cam is null || !cam.IsValid() )
            return false;

        var f = cam.WorldRotation.Forward.WithZ( 0f );
        if ( f.Length < 0.01f )
            f = cam.WorldRotation.Forward;
        if ( f.Length < 0.01f )
            return false;

        forward = f.Normal;
        right = forward.Cross( Vector3.Up );
        if ( right.Length < 0.01f )
        {
            var cr = cam.WorldRotation.Right.WithZ( 0f );
            if ( cr.Length >= 0.01f )
                right = cr.Normal;
        }

        if ( right.Length < 0.01f )
            return false;

        right = right.Normal;
        return true;
    }

    private static CameraComponent ResolveHolderCamera( GameObject holder, PlayerController pc, Scene scene )
    {
        if ( holder is not null && holder.IsValid() )
        {
            var list = new List<CameraComponent>();
            AccumulateCamerasRecursive( holder, list );

            CameraComponent fallback = null;
            foreach ( var cam in list )
            {
                if ( cam is null || !cam.IsValid() || !cam.Enabled )
                    continue;

                if ( cam.IsMainCamera )
                    return cam;

                fallback ??= cam;
            }

            if ( fallback is not null )
                return fallback;
        }

        if ( scene is not null && scene.Camera is not null && scene.Camera.IsValid() && IsHolderLocallyControlled( holder ) )
            return scene.Camera;

        return null;
    }

    private static void AccumulateCamerasRecursive( GameObject go, List<CameraComponent> dst )
    {
        if ( go is null || !go.IsValid() )
            return;

        var cam = go.Components.Get<CameraComponent>();
        if ( cam is not null )
            dst.Add( cam );

        foreach ( var child in go.Children )
            AccumulateCamerasRecursive( child, dst );
    }

    private static bool IsHolderLocallyControlled( GameObject holder )
    {
        if ( holder is null || !holder.IsValid() )
            return false;

        if ( !Networking.IsActive )
            return true;

        return TryGetNetworkRoot( holder, out var root ) && root is not null && root.Network.IsOwner;
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

    private static PlayerController ResolvePlayerControllerOnRoot( GameObject root )
    {
        if ( root is null )
            return null;

        var pc = root.Components.Get<PlayerController>()
            ?? root.Components.GetInChildren<PlayerController>( true );

        var go = root;
        while ( pc is null && go is not null )
        {
            pc = go.Components.Get<PlayerController>();
            go = go.Parent;
        }

        return pc;
    }
}
