using Sandbox;

/// <summary>
/// <see cref="Component.INetworkListener.OnActive"/> sur un petit composant → délègue au <see cref="MoonPlayerSpawner"/>.
/// </summary>
[Title( "Moon Player Spawner — Network Relay" )]
public sealed class MoonPlayerSpawnerNetworkRelay : Component, Component.INetworkListener
{
    [Property] public MoonPlayerSpawner Spawner { get; set; }

    protected override void OnStart() => ResolveSpawnerIfNeeded();

    public void OnActive( Connection channel )
    {
        try
        {
            ResolveSpawnerIfNeeded();
            if ( Spawner is null )
            {
                Log.Error( "[MoonPlayerSpawnerNetworkRelay] MoonPlayerSpawner introuvable." );
                return;
            }

            Spawner.ReceiveConnectionActive( channel );
        }
        catch ( System.Exception e )
        {
            Log.Error( $"[MoonPlayerSpawnerNetworkRelay] OnActive: {e.Message}\n{e.StackTrace}" );
        }
    }

    private void ResolveSpawnerIfNeeded()
    {
        if ( Spawner is not null && Spawner.GameObject is not null && Spawner.GameObject.IsValid() )
            return;

        Spawner = Components.Get<MoonPlayerSpawner>();
    }
}
