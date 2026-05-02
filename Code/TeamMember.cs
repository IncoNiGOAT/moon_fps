using Sandbox;

public enum TeamId
{
    Red,
    Blue
}

public sealed class TeamMember : Component
{
    [Property] public TeamId Team { get; set; } = TeamId.Red;
    /// <summary>
    /// Index fixe sur le terrain (0 = premier spawn rouge/bleu, etc.). Utilisé avec
    /// <see cref="TeamSpawnManager.UseSlotBasedArenaSpawns"/> pour le même point au spawn et à la sortie de prison.
    /// </summary>
    [Property] public int ArenaSlotIndex { get; set; } = -1;
    [Property] public bool TintBodyByTeam { get; set; } = true;
    [Property] public Color RedColor { get; set; } = new Color( 1f, 0.25f, 0.25f );
    [Property] public Color BlueColor { get; set; } = new Color( 0.25f, 0.45f, 1f );

    protected override void OnStart()
    {
        ApplyTeamVisual();
    }

    public void ApplyTeamVisual()
    {
        if ( !TintBodyByTeam )
            return;

        var renderer = Components.GetInChildren<SkinnedModelRenderer>( true );
        if ( renderer is null )
            return;

        renderer.Tint = Team == TeamId.Red ? RedColor : BlueColor;
    }
}
