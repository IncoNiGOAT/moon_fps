using Sandbox;
using Sandbox.Physics;

/// <summary>
/// Murs <see cref="InvisibleBarrier"/> avec « tout passe » + « joueur ne passe pas » : tag <see cref="WallTag"/> sur le mur (sans <c>solid</c>),
/// tag <see cref="PlayerTag"/> sur le corps du joueur (sans <c>solid</c> sur la capsule) — voir <c>ProjectSettings/Collision.config</c>.
/// </summary>
public static class SoftMidlineBarrier
{
    public const string WallTag = "soft_midline";
    public const string PlayerTag = "barrier_player";

    public static bool SceneHasPlayerBlockingSoftWall( Scene scene )
    {
        if ( scene is null )
            return false;

        foreach ( var barrier in scene.GetAllComponents<InvisibleBarrier>() )
        {
            if ( barrier is null || !barrier.Enabled )
                continue;

            if ( barrier.PassThroughEverything && barrier.BlockPlayer )
                return true;
        }

        return false;
    }

    public static void RegisterCollisionPairs( CollisionRules rules )
    {
        if ( rules is null )
            return;

        var w = WallTag;
        var p = PlayerTag;

        rules.Pairs[new CollisionRules.Pair( w, "solid" )] = CollisionRules.Result.Ignore;
        rules.Pairs[new CollisionRules.Pair( w, p )] = CollisionRules.Result.Collide;
        rules.Pairs[new CollisionRules.Pair( w, "world" )] = CollisionRules.Result.Collide;

        rules.Pairs[new CollisionRules.Pair( p, "world" )] = CollisionRules.Result.Collide;
        rules.Pairs[new CollisionRules.Pair( p, "solid" )] = CollisionRules.Result.Collide;
        rules.Pairs[new CollisionRules.Pair( p, "trigger" )] = CollisionRules.Result.Trigger;
        rules.Pairs[new CollisionRules.Pair( p, "playerclip" )] = CollisionRules.Result.Collide;

#pragma warning disable CS0612 // CollisionRules.Clean : pas d’API de remplacement documentée (normalise les paires).
        rules.Clean();
#pragma warning restore CS0612
    }

    /// <summary> Capsule : <see cref="PlayerTag"/> et retrait <c>solid</c> pour que <c>soft_midline</c>×<c>solid</c> Ignore ne s’applique pas au joueur. </summary>
    public static void ConfigurePlayerBody( Component invokeComponent, Rigidbody body )
    {
        if ( invokeComponent is null || body is null || body.GameObject is null || !body.GameObject.IsValid() )
            return;

        var go = body.GameObject;
        if ( !go.Tags.Has( PlayerTag ) )
            go.Tags.Add( PlayerTag );

        ScheduleStripSolid( invokeComponent, go );
    }

    public static void ConfigureRagdollHierarchy( Component invokeComponent, GameObject root )
    {
        if ( invokeComponent is null || root is null || !root.IsValid() )
            return;

        TagPlayerRecursive( root );
        StripSolidPhysicsRecursive( invokeComponent, root );
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
            ScheduleStripSolid( invokeComponent, go );

        foreach ( var child in go.Children )
            StripSolidPhysicsRecursive( invokeComponent, child );
    }

    private static void ScheduleStripSolid( Component invokeComponent, GameObject go )
    {
        go.Tags.Add( "solid" );
        var capture = go;
        invokeComponent.Invoke( 0.05f, () =>
        {
            if ( capture is null || !capture.IsValid() )
                return;

            if ( capture.Tags.Has( "solid" ) )
                capture.Tags.Remove( "solid" );
        } );
    }
}

/// <summary> Optionnel : applique <see cref="SoftMidlineBarrier.ConfigurePlayerBody"/> si la scène a un mur « joueur bloqué ». </summary>
[Title( "Soft Midline Player Body" )]
public sealed class SoftMidlinePlayerBody : Component
{
    protected override void OnStart()
    {
        if ( !SoftMidlineBarrier.SceneHasPlayerBlockingSoftWall( Scene ) )
            return;

        var pc = Components.Get<PlayerController>() ?? Components.GetInChildren<PlayerController>( true );
        if ( pc?.Body is not null )
            SoftMidlineBarrier.ConfigurePlayerBody( this, pc.Body );
    }
}
