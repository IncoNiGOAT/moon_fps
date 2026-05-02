using Sandbox;

/// <summary>
/// Volume de spawn (souvent un cube avec <see cref="BoxCollider"/>).
/// En jeu : mesh masqué, collider en trigger pour ne pas gêner. En éditeur : filaire dans la vue scène.
/// <para><b>Setup :</b> GameObject avec <see cref="BoxCollider"/> (taille = volume), optionnel mesh cube pour cadrer ;
/// assigne ce composant sur le même objet (ou le parent du collider).</para>
/// </summary>
[Title( "Ball Spawn Volume" )]
public sealed class BallSpawnVolume : Component
{
    [Property] public Color EditorWireColor { get; set; } = new Color( 0.35f, 1f, 0.45f, 0.9f );
    /// <summary> Désactive les <see cref="ModelRenderer"/> / <see cref="SkinnedModelRenderer"/> sous ce GameObject en jeu. </summary>
    [Property] public bool HideMeshesInPlay { get; set; } = true;
    [Property] public bool ForceTriggerInPlay { get; set; } = true;

    protected override void OnStart()
    {
        if ( HideMeshesInPlay )
            SetRenderersEnabledRecursive( GameObject, false );

        if ( ForceTriggerInPlay )
            SetCollidersTriggerRecursive( GameObject, true );
    }

    /// <summary> Point uniforme à l'intérieur du <see cref="BoxCollider"/> (local), puis monde. </summary>
    public bool TryGetRandomWorldPoint( out Vector3 worldPos, float extraWorldUp = 0f )
    {
        worldPos = default;

        var box = Components.Get<BoxCollider>() ?? Components.GetInChildren<BoxCollider>( true );
        if ( box is null )
        {
            Log.Warning( $"[BallSpawnVolume] {GameObject.Name}: ajoute un BoxCollider pour définir le volume." );
            return false;
        }

        var c = box.Center;
        var ext = box.Scale * 0.5f;
        var local = new Vector3(
            Game.Random.Float( c.x - ext.x, c.x + ext.x ),
            Game.Random.Float( c.y - ext.y, c.y + ext.y ),
            Game.Random.Float( c.z - ext.z, c.z + ext.z ) );

        worldPos = Transform.World.PointToWorld( local ) + Vector3.Up * extraWorldUp;
        return true;
    }

    protected override void DrawGizmos()
    {
        var box = Components.Get<BoxCollider>() ?? Components.GetInChildren<BoxCollider>( true );
        if ( box is null )
            return;

        var c = box.Center;
        var ext = box.Scale * 0.5f;
        var b = new BBox( c - ext, c + ext );

        using ( Gizmo.Scope( $"{GameObject.Name}_BallSpawnVolume", Transform.World ) )
        {
            Gizmo.Draw.Color = EditorWireColor;
            Gizmo.Draw.LineBBox( b );
        }
    }

    private static void SetCollidersTriggerRecursive( GameObject go, bool isTrigger )
    {
        if ( go is null || !go.IsValid() )
            return;

        foreach ( var c in go.Components.GetAll() )
        {
            if ( c is Collider col && col.IsValid() )
                col.IsTrigger = isTrigger;
        }

        foreach ( var child in go.Children )
            SetCollidersTriggerRecursive( child, isTrigger );
    }

    private static void SetRenderersEnabledRecursive( GameObject go, bool enabled )
    {
        if ( go is null || !go.IsValid() )
            return;

        var mr = go.Components.Get<ModelRenderer>();
        if ( mr is not null )
            mr.Enabled = enabled;

        var sk = go.Components.Get<SkinnedModelRenderer>();
        if ( sk is not null )
            sk.Enabled = enabled;

        foreach ( var child in go.Children )
            SetRenderersEnabledRecursive( child, enabled );
    }
}
