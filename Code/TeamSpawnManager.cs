using Sandbox;
using System.Collections.Generic;

public sealed class TeamSpawnManager : Component
{
    /// <summary>
    /// Si activé, l’index dans <see cref="RedSpawnPoints"/> / <see cref="BlueSpawnPoints"/> = slot joueur (5v5 : 5 entrées par équipe).
    /// Sinon, comportement aléatoire avec sac comme avant.
    /// </summary>
    [Property] public bool UseSlotBasedArenaSpawns { get; set; } = true;
    [Property] public GameObject ArenaCenterPoint { get; set; }

    [Property] public List<GameObject> RedSpawnPoints { get; set; } = new();
    [Property] public List<GameObject> BlueSpawnPoints { get; set; } = new();

    private readonly List<GameObject> _redBag = new();
    private readonly List<GameObject> _blueBag = new();

    /// <summary> Spawn d’arène pour le slot donné (même logique que spawn initial / sortie prison). </summary>
    public GameObject GetArenaSpawnForSlot( TeamId team, int slotIndex )
    {
        if ( slotIndex < 0 )
            return null;

        var source = team == TeamId.Red ? RedSpawnPoints : BlueSpawnPoints;
        if ( slotIndex >= source.Count )
            return null;

        return source[slotIndex];
    }

    public GameObject GetSpawnPoint( TeamId team )
    {
        var source = team == TeamId.Red ? RedSpawnPoints : BlueSpawnPoints;
        var bag = team == TeamId.Red ? _redBag : _blueBag;

        bag.RemoveAll( x => x is null || !source.Contains( x ) );

        if ( bag.Count == 0 )
        {
            foreach ( var point in source )
            {
                if ( point is not null )
                    bag.Add( point );
            }

            Shuffle( bag );
        }

        if ( bag.Count == 0 )
            return null;

        var chosen = bag[0];
        bag.RemoveAt( 0 );
        return chosen;
    }

    public bool TryGetArenaCenter( out Vector3 center )
    {
        if ( ArenaCenterPoint is not null && ArenaCenterPoint.IsValid() )
        {
            center = ArenaCenterPoint.WorldPosition;
            return true;
        }

        var all = new List<GameObject>();
        all.AddRange( RedSpawnPoints );
        all.AddRange( BlueSpawnPoints );
        all.RemoveAll( x => x is null || !x.IsValid() );

        if ( all.Count == 0 )
        {
            center = default;
            return false;
        }

        var sum = Vector3.Zero;
        foreach ( var p in all )
            sum += p.WorldPosition;

        center = sum / all.Count;
        return true;
    }

    private static void Shuffle( List<GameObject> list )
    {
        for ( var i = list.Count - 1; i > 0; i-- )
        {
            var j = Game.Random.Int( 0, i );
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
