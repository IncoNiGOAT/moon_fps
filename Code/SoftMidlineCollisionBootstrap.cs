using Sandbox;
using Sandbox.Physics;

/// <summary> Enregistre les paires <see cref="SoftMidlineBarrier"/> (doublon de <c>Collision.config</c> au runtime). </summary>
[Title( "Soft Midline Collision Bootstrap" )]
public sealed class SoftMidlineCollisionBootstrap : Component
{
    protected override void OnAwake()
    {
        var rules = ProjectSettings.Collision;
        if ( rules is null )
            return;

        SoftMidlineBarrier.RegisterCollisionPairs( rules );
    }
}
