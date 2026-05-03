using Sandbox;
using Sandbox.Physics;

/// <summary>
/// <b>Un seul composant</b> sur le cube (ModelRenderer + BoxCollider) : <b>tout traverse sauf les joueurs</b>.
/// Enregistre les paires collision (doublon <c>Collision.config</c>), tague le mur (<see cref="WallTag"/> / retrait <c>solid</c>),
/// et maintient les joueurs locaux tagués <see cref="PlayerTag"/> (plus besoin d’un composant sur le prefab joueur).
/// </summary>
[Title( "Player Only Wall" )]
public sealed class PlayerOnlyWall : Component
{
    public const string WallTag = "soft_midline";
    public const string PlayerTag = "barrier_player";

    [Property] public bool UseStaticColliders { get; set; } = true;

    /// <summary> Désactive les <see cref="ModelRenderer"/> sous ce volume. Laisse faux pour texture / transparence sur le cube. </summary>
    [Property] public bool HideModelRendererInPlay { get; set; }

    private static bool _collisionPairsRegistered;
    private static TimeSince _sincePlayerRefresh;

    private bool _stripSolidScheduled;
    private bool _wallFullyApplied;

    protected override void OnAwake()
    {
        EnsureCollisionPairsRegistered();
    }

    protected override void OnStart()
    {
        TryApplyWall();
        Invoke( 0f, TryApplyWall );
        Invoke( 0.05f, TryApplyWall );
        Invoke( 0.15f, TryApplyWall );
    }

    protected override void OnUpdate()
    {
        if ( !_wallFullyApplied )
        {
            TryApplyWall();
            return;
        }

        TryRefreshLocalPlayers();
    }

    public static bool SceneHasWall( Scene scene )
    {
        if ( scene is null )
            return false;

        foreach ( var w in scene.GetAllComponents<PlayerOnlyWall>() )
        {
            if ( w is not null && w.Enabled )
                return true;
        }

        return false;
    }

    /// <summary> Compat : nom utilisé par le ragdoll / spawner. </summary>
    public static bool SceneHasPlayerBlockingSoftWall( Scene scene ) => SceneHasWall( scene );

    public static void ConfigurePlayerBody( Component invokeComponent, Rigidbody body )
    {
        if ( invokeComponent is null || body is null || body.GameObject is null || !body.GameObject.IsValid() )
            return;

        var root = body.GameObject;
        if ( !root.Tags.Has( PlayerTag ) )
            root.Tags.Add( PlayerTag );

        TagBarrierPlayerOnColliderHierarchy( invokeComponent, root );
    }

    public static void ConfigureRagdollHierarchy( Component invokeComponent, GameObject root )
    {
        if ( invokeComponent is null || root is null || !root.IsValid() )
            return;

        TagPlayerRecursive( root );
        StripSolidPhysicsRecursive( invokeComponent, root );
    }

    private static void EnsureCollisionPairsRegistered()
    {
        if ( _collisionPairsRegistered )
            return;

        var rules = ProjectSettings.Collision;
        if ( rules is null )
            return;

        var w = WallTag;
        var p = PlayerTag;

        rules.Pairs[new CollisionRules.Pair( w, "solid" )] = CollisionRules.Result.Ignore;
        rules.Pairs[new CollisionRules.Pair( w, p )] = CollisionRules.Result.Collide;
        rules.Pairs[new CollisionRules.Pair( w, "world" )] = CollisionRules.Result.Collide;

        rules.Pairs[new CollisionRules.Pair( p, "world" )] = CollisionRules.Result.Collide;
        rules.Pairs[new CollisionRules.Pair( p, "solid" )] = CollisionRules.Result.Collide;
        rules.Pairs[new CollisionRules.Pair( p, p )] = CollisionRules.Result.Collide;
        rules.Pairs[new CollisionRules.Pair( p, "trigger" )] = CollisionRules.Result.Trigger;
        rules.Pairs[new CollisionRules.Pair( p, "playerclip" )] = CollisionRules.Result.Collide;

#pragma warning disable CS0612 // CollisionRules.Clean : pas d'API de remplacement documentée.
        rules.Clean();
#pragma warning restore CS0612

        _collisionPairsRegistered = true;
    }

    private void TryApplyWall()
    {
        if ( _wallFullyApplied )
            return;

        // Ne pas attendre Network.Active : murs de scène (Snapshot / etc.) — chaque machine doit
        // tagger tout de suite pour que la physique locale (client + host) voie soft_midline + colliders.

        TagWallOnColliderHierarchy( GameObject );
        SetCollidersSolidNonTrigger( GameObject );

        if ( !_stripSolidScheduled )
        {
            _stripSolidScheduled = true;
            ScheduleStripSolidOnColliderObjects( GameObject );
        }

        if ( HideModelRendererInPlay )
            SetModelRenderersEnabledRecursive( GameObject, false );

        _wallFullyApplied = true;
    }

    private void TryRefreshLocalPlayers()
    {
        if ( !SceneHasWall( Scene ) )
            return;

        if ( _sincePlayerRefresh < 0.05f )
            return;

        _sincePlayerRefresh = 0f;

        foreach ( var pc in Scene.GetAllComponents<PlayerController>() )
        {
            if ( pc is null || pc.Body is null )
                continue;

            if ( !ShouldTagThisPlayer( pc ) )
                continue;

            ConfigurePlayerBody( pc, pc.Body );
        }
    }

    /// <summary>
    /// Ne pas taguer les copies d&apos;autres joueurs : uniquement si <see cref="GameObject.NetworkAccessor.IsProxy"/>
    /// et <see cref="GameObject.NetworkAccessor.IsOwner"/> faux sont confirmés sur le root (évite de bloquer le tagging
    /// tant que l&apos;ownership n&apos;est pas résolu).
    /// </summary>
    private static bool ShouldTagThisPlayer( PlayerController pc )
    {
        if ( pc?.GameObject is null )
            return false;

        if ( !Networking.IsActive )
            return true;

        var go = pc.GameObject;

        if ( !go.Network.Active )
            return pc.UseInputControls;

        if ( go.Network.IsProxy && !go.Network.IsOwner )
            return false;

        if ( IsLocallyOwnedHierarchy( go ) )
            return true;

        return pc.UseInputControls;
    }

    private static bool IsLocallyOwnedHierarchy( GameObject start )
    {
        if ( start is null || !start.IsValid() )
            return false;

        for ( var go = start; go is not null; go = go.Parent )
        {
            if ( go.Network.Active )
                return go.Network.IsOwner;
        }

        return false;
    }

    private static void TagWallOnColliderHierarchy( GameObject go )
    {
        if ( go is null || !go.IsValid() )
            return;

        if ( go.Components.Get<Collider>() is not null && !go.Tags.Has( WallTag ) )
            go.Tags.Add( WallTag );

        foreach ( var child in go.Children )
            TagWallOnColliderHierarchy( child );
    }

    private void SetCollidersSolidNonTrigger( GameObject go )
    {
        if ( go is null || !go.IsValid() )
            return;

        foreach ( var c in go.Components.GetAll() )
        {
            if ( c is not Collider col || !col.IsValid() )
                continue;

            col.IsTrigger = false;
            if ( UseStaticColliders )
                col.Static = true;
        }

        foreach ( var child in go.Children )
            SetCollidersSolidNonTrigger( child );
    }

    private void ScheduleStripSolidOnColliderObjects( GameObject go )
    {
        if ( go is null || !go.IsValid() )
            return;

        if ( go.Components.Get<Collider>() is not null )
        {
            go.Tags.Add( "solid" );
            var capture = go;
            Invoke( 0.05f, () =>
            {
                if ( capture is null || !capture.IsValid() )
                    return;

                if ( capture.Tags.Has( "solid" ) )
                    capture.Tags.Remove( "solid" );
            } );
        }

        foreach ( var child in go.Children )
            ScheduleStripSolidOnColliderObjects( child );
    }

    private static void SetModelRenderersEnabledRecursive( GameObject go, bool enabled )
    {
        if ( go is null || !go.IsValid() )
            return;

        var mr = go.Components.Get<ModelRenderer>();
        if ( mr is not null )
            mr.Enabled = enabled;

        foreach ( var child in go.Children )
            SetModelRenderersEnabledRecursive( child, enabled );
    }

    private static void TagBarrierPlayerOnColliderHierarchy( Component invokeComponent, GameObject go )
    {
        if ( go is null || !go.IsValid() )
            return;

        if ( go.Components.Get<Collider>() is not null )
        {
            if ( !go.Tags.Has( PlayerTag ) )
                go.Tags.Add( PlayerTag );

            ScheduleStripSolidOnPlayerCollider( invokeComponent, go );
        }

        foreach ( var child in go.Children )
            TagBarrierPlayerOnColliderHierarchy( invokeComponent, child );
    }

    private static void ScheduleStripSolidOnPlayerCollider( Component invokeComponent, GameObject go )
    {
        if ( !go.Tags.Has( PlayerTag ) )
            go.Tags.Add( PlayerTag );

        go.Tags.Add( "solid" );
        var capture = go;
        invokeComponent.Invoke( 0.05f, () =>
        {
            if ( capture is null || !capture.IsValid() )
                return;

            if ( capture.Tags.Has( "solid" ) )
                capture.Tags.Remove( "solid" );

            if ( !capture.Tags.Has( PlayerTag ) )
                capture.Tags.Add( PlayerTag );
        } );
    }

    private static void TagPlayerRecursive( GameObject go )
    {
        if ( go is null || !go.IsValid() )
            return;

        if ( !go.Tags.Has( PlayerTag ) )
            go.Tags.Add( PlayerTag );

        foreach ( var child in go.Children )
            TagPlayerRecursive( child );
    }

    private static void StripSolidPhysicsRecursive( Component invokeComponent, GameObject go )
    {
        if ( go is null || !go.IsValid() )
            return;

        var hasPhys = go.Components.Get<Collider>() is not null || go.Components.Get<Rigidbody>() is not null;
        if ( hasPhys )
            ScheduleStripSolidOnPlayerCollider( invokeComponent, go );

        foreach ( var child in go.Children )
            StripSolidPhysicsRecursive( invokeComponent, child );
    }

    protected override void DrawGizmos()
    {
        var box = Components.Get<BoxCollider>() ?? Components.GetInChildren<BoxCollider>( true );
        if ( box is null )
            return;

        var c = box.Center;
        var ext = box.Scale * 0.5f;
        var b = new BBox( c - ext, c + ext );

        using ( Gizmo.Scope( $"{GameObject.Name}_PlayerOnlyWall", Transform.World ) )
        {
            Gizmo.Draw.Color = new Color( 0.35f, 0.85f, 1f, 0.55f );
            Gizmo.Draw.LineBBox( b );
        }
    }
}
