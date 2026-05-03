using Sandbox;

public enum TeamConstraintAxis
{
    X,
    Y
}

/// <summary>
/// <b>Recommandé :</b> ne plus utiliser le clamp ici ; murs médians avec <see cref="PlayerOnlyWall"/> (tout traverse sauf joueurs).
/// <para>Ce composant reste en secours si <see cref="UseCodeClamp"/> est coché (ancien comportement).</para>
/// </summary>
public sealed class TeamSideConstraint : Component
{
    [Property] public bool UseCodeClamp { get; set; } = false;
    [Property] public TeamConstraintAxis Axis { get; set; } = TeamConstraintAxis.X;
    [Property] public float MidlineX { get; set; } = 0f;
    [Property] public float Margin { get; set; } = 4f;

    private TeamMember _teamMember;
    private PlayerController _playerController;
    private PrisonBallPlayer _prisonPlayer;

    protected override void OnStart()
    {
        _teamMember = ResolveComponent<TeamMember>();
        _playerController = ResolveComponent<PlayerController>();
        _prisonPlayer = ResolveComponent<PrisonBallPlayer>();
    }

    protected override void OnUpdate()
    {
        if ( !UseCodeClamp )
            return;

        _teamMember ??= ResolveComponent<TeamMember>();
        _playerController ??= ResolveComponent<PlayerController>();
        _prisonPlayer ??= ResolveComponent<PrisonBallPlayer>();

        if ( _teamMember is null )
            return;

        if ( _prisonPlayer is not null && _prisonPlayer.InPrison )
            return;
        if ( _prisonPlayer is not null && _prisonPlayer.IgnoreTeamConstraint )
            return;

        var pos = WorldPosition;
        var clamped = false;
        var value = Axis == TeamConstraintAxis.X ? pos.x : pos.y;

        // Red stays on <= Midline, Blue stays on >= Midline for selected axis.
        if ( _teamMember.Team == TeamId.Red && value > MidlineX - Margin )
        {
            SetAxisValue( ref pos, MidlineX - Margin );
            clamped = true;
        }
        else if ( _teamMember.Team == TeamId.Blue && value < MidlineX + Margin )
        {
            SetAxisValue( ref pos, MidlineX + Margin );
            clamped = true;
        }

        if ( !clamped )
            return;

        WorldPosition = pos;

        // Cancel movement toward enemy side to avoid jitter at the boundary.
        if ( _playerController?.Body is not null )
        {
            var vel = _playerController.Body.Velocity;
            var axisVelocity = Axis == TeamConstraintAxis.X ? vel.x : vel.y;

            if ( _teamMember.Team == TeamId.Red && axisVelocity > 0f )
                SetAxisVelocity( ref vel, 0f );
            else if ( _teamMember.Team == TeamId.Blue && axisVelocity < 0f )
                SetAxisVelocity( ref vel, 0f );

            _playerController.Body.Velocity = vel;
        }
    }

    private T ResolveComponent<T>() where T : Component
    {
        var self = Components.Get<T>();
        if ( self is not null )
            return self;

        var child = Components.GetInChildren<T>( true );
        if ( child is not null )
            return child;

        for ( var p = GameObject.Parent; p is not null; p = p.Parent )
        {
            var onParent = p.Components.Get<T>();
            if ( onParent is not null )
                return onParent;
        }

        return null;
    }

    private void SetAxisValue( ref Vector3 pos, float value )
    {
        if ( Axis == TeamConstraintAxis.X )
            pos.x = value;
        else
            pos.y = value;
    }

    private void SetAxisVelocity( ref Vector3 vel, float value )
    {
        if ( Axis == TeamConstraintAxis.X )
            vel.x = value;
        else
            vel.y = value;
    }
}
