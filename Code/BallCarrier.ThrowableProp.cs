using Sandbox;

public sealed partial class BallCarrier
{
    private void ThrowHeldPropNow()
    {
        if ( _heldProp is null )
            return;

        var dir = GetCameraThrowDirection();
        _heldProp.Throw( dir, null, GetThrowReleaseWorldUpOverrideOrNull() );
        _heldProp = null;
    }

    /// <summary> Knockdown prison : le prop ne doit pas rester attaché au joueur figé. </summary>
    private void ReleaseHeldPropForJailOrInterrupt()
    {
        if ( _heldProp is null )
            return;

        _heldProp.ForceReleaseWithoutThrow();
        _heldProp = null;
    }
}
