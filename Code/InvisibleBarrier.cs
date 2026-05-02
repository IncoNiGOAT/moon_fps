using Sandbox;

/// <summary>
/// Cube / mur invisible. <see cref="PassThroughEverything"/> : props & balle traversent.
/// Avec <see cref="BlockPlayer"/>, le joueur reste bloqué (mur <see cref="SoftMidlineBarrier.WallTag"/> + corps <see cref="SoftMidlineBarrier.PlayerTag"/>).
/// </summary>
[Title( "Invisible Barrier" )]
public sealed class InvisibleBarrier : Component
{
    /// <summary>
    /// <b>On</b> : balle &amp; props ne sont pas bloqués par ce volume (trigger, ou mur <c>soft_midline</c> si <see cref="BlockPlayer"/>).
    /// <b>Off</b> : mur solide classique pour tout le monde.
    /// </summary>
    [Property] public bool PassThroughEverything { get; set; }

    /// <summary> Uniquement si <see cref="PassThroughEverything"/> : le joueur ne peut pas traverser (le reste oui). </summary>
    [Property, Title( "Player ne passe pas" )]
    public bool BlockPlayer { get; set; }

    [Property] public Color EditorGizmoColor { get; set; } = new Color( 0.4f, 0.75f, 1f, 0.85f );
    [Property] public bool HideMeshesInPlay { get; set; } = true;
    [Property] public bool ShowTranslucentVisualInPlay { get; set; } = false;
    [Property] public Color TranslucentTint { get; set; } = new Color( 1f, 1f, 1f, 0.12f );

    /// <summary> Ignoré en mode tout-passage (trigger ou soft midline). </summary>
    [Property] public bool ForceSolidCollidersInPlay { get; set; } = true;

    /// <summary> Mur solide ou soft midline : colliders statiques. Ignoré en mode trigger pur. </summary>
    [Property] public bool UseStaticColliders { get; set; } = true;

    protected override void OnStart()
    {
        if ( BlockPlayer && !PassThroughEverything )
            Log.Warning( $"[InvisibleBarrier] {GameObject.Name}: « Player ne passe pas » sans « Tout traverse » est ignoré. Coche PassThroughEverything." );

        if ( PassThroughEverything && BlockPlayer )
            SetupSoftMidlinePlayerWall();
        else if ( PassThroughEverything )
            SetCollidersPassThroughRecursive( GameObject );
        else if ( ForceSolidCollidersInPlay )
            SetCollidersSolidRecursive( GameObject, false );

        if ( ShowTranslucentVisualInPlay )
        {
            ApplyTintRecursive( GameObject, TranslucentTint );
            return;
        }

        if ( HideMeshesInPlay )
            SetRenderersEnabledRecursive( GameObject, false );
    }

    /// <summary> Colliders solides, tag mur, sans <c>solid</c> sur le volume (props <c>solid</c> traversent via la matrice). </summary>
    private void SetupSoftMidlinePlayerWall()
    {
        if ( !GameObject.Tags.Has( SoftMidlineBarrier.WallTag ) )
            GameObject.Tags.Add( SoftMidlineBarrier.WallTag );

        SetCollidersSolidRecursive( GameObject, false );
        ScheduleStripSolidOnColliderObjects( GameObject );
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

    private void SetCollidersPassThroughRecursive( GameObject go )
    {
        if ( go is null || !go.IsValid() )
            return;

        foreach ( var c in go.Components.GetAll() )
        {
            if ( c is not Collider col || !col.IsValid() )
                continue;

            col.IsTrigger = true;
            col.Static = false;
        }

        foreach ( var child in go.Children )
            SetCollidersPassThroughRecursive( child );
    }

    protected override void DrawGizmos()
    {
        var box = Components.Get<BoxCollider>() ?? Components.GetInChildren<BoxCollider>( true );
        if ( box is null )
            return;

        var c = box.Center;
        var ext = box.Scale * 0.5f;
        var b = new BBox( c - ext, c + ext );

        using ( Gizmo.Scope( $"{GameObject.Name}_InvisibleBarrier", Transform.World ) )
        {
            Color color;
            if ( PassThroughEverything && BlockPlayer )
                color = new Color( 1f, 0.85f, 0.25f, 0.9f );
            else if ( PassThroughEverything )
                color = new Color( 0.35f, 1f, 0.5f, 0.85f );
            else
                color = EditorGizmoColor;

            Gizmo.Draw.Color = color;
            Gizmo.Draw.LineBBox( b );
        }
    }

    private void SetCollidersSolidRecursive( GameObject go, bool isTrigger )
    {
        if ( go is null || !go.IsValid() )
            return;

        foreach ( var c in go.Components.GetAll() )
        {
            if ( c is not Collider col || !col.IsValid() )
                continue;

            col.IsTrigger = isTrigger;
            if ( UseStaticColliders && !isTrigger )
                col.Static = true;
        }

        foreach ( var child in go.Children )
            SetCollidersSolidRecursive( child, isTrigger );
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

    private static void ApplyTintRecursive( GameObject go, Color tint )
    {
        if ( go is null || !go.IsValid() )
            return;

        var mr = go.Components.Get<ModelRenderer>();
        if ( mr is not null )
        {
            mr.Enabled = true;
            mr.Tint = tint;
        }

        var sk = go.Components.Get<SkinnedModelRenderer>();
        if ( sk is not null )
        {
            sk.Enabled = true;
            sk.Tint = tint;
        }

        foreach ( var child in go.Children )
            ApplyTintRecursive( child, tint );
    }
}
