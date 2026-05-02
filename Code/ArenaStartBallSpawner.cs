using Sandbox;

/// <summary>
/// Au démarrage de l'arène : instancie plusieurs balles au centre (ou sur <see cref="CenterPoint"/>).
/// Alternative : dupliquer manuellement la balle dans la scène en décalant légèrement X/Y pour éviter les overlaps physiques.
/// </summary>
[Title( "Arena Start Ball Spawner" )]
public sealed class ArenaStartBallSpawner : Component
{
    [Property] public GameObject BallPrefab { get; set; }
    [Property] public int BallCount { get; set; } = 3;
    /// <summary> Si vide, utilise la position de l'objet qui porte ce composant. </summary>
    [Property] public GameObject CenterPoint { get; set; }
    /// <summary> Distance entre chaque balle sur le sol. </summary>
    [Property] public float HorizontalSpacing { get; set; } = 48f;
    /// <summary>
    /// Direction voulue pour la rangée. Elle est projetée sur le plan horizontal (perpendiculaire à <see cref="Vector3.Up"/>)
    /// pour éviter d’espacer les balles en hauteur (pile comme sur ta capture).
    /// </summary>
    [Property] public Vector3 SpreadDirection { get; set; } = new Vector3( 1f, 0f, 0f );
    /// <summary> Petit décalage le long du haut du monde au spawn pour limiter les overlaps avec le sol / la physique. </summary>
    [Property] public float SpawnHeightLift { get; set; } = 6f;
    [Property] public bool SpawnOnStart { get; set; } = true;
    [Property] public float SpawnDelaySeconds { get; set; } = 0.05f;

    protected override void OnStart()
    {
        if ( !SpawnOnStart || BallCount <= 0 || !BallPrefab.IsValid() )
            return;

        if ( Networking.IsActive && !Networking.IsHost )
            return;

        if ( SpawnDelaySeconds <= 0f )
            SpawnBalls();
        else
            Invoke( SpawnDelaySeconds, SpawnBalls );
    }

    [Button]
    public void SpawnBalls()
    {
        if ( !BallPrefab.IsValid() || BallCount <= 0 )
            return;

        // Une seule machine doit instancier les Network Objects ; sinon chaque client a ses propres balles (physique désynchronisée).
        if ( Networking.IsActive && !Networking.IsHost )
            return;

        var centerGo = CenterPoint is not null && CenterPoint.IsValid() ? CenterPoint : GameObject;
        var center = centerGo.WorldPosition;
        var mid = (BallCount - 1) * 0.5f;
        var up = Vector3.Up;
        var dir = PlanarSpreadDirection( SpreadDirection, up );

        for ( var i = 0; i < BallCount; i++ )
        {
            var pos = center + up * SpawnHeightLift + dir * (i - mid) * HorizontalSpacing;
            var clone = BallPrefab.Clone( pos, Rotation.Identity );
            if ( clone is null || !clone.IsValid() )
                continue;

            clone.Enabled = true;
            if ( Networking.IsActive )
                clone.NetworkSpawn();
        }
    }

    /// <summary> Direction unitaire dans le plan horizontal (aucune composante “debout” sur Vector3.Up). </summary>
    private static Vector3 PlanarSpreadDirection( Vector3 spreadHint, Vector3 worldUp )
    {
        var u = worldUp.Normal;
        var v = spreadHint;
        if ( v.LengthSquared < 0.0001f )
            v = Vector3.Right;

        var horizontal = v - u * Vector3.Dot( v, u );
        if ( horizontal.LengthSquared < 0.0001f )
        {
            var cross = Vector3.Cross( u, Vector3.Right );
            if ( cross.LengthSquared < 0.0001f )
                cross = Vector3.Cross( u, Vector3.Forward );

            horizontal = cross;
        }

        return horizontal.Normal;
    }
}
