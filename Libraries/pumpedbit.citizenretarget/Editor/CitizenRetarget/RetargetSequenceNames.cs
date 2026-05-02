namespace Editor.CitizenRetarget;

internal static class RetargetSequenceNames
{
	public static string Build( string sequencePrefix, string clipDisplayName )
	{
		return (sequencePrefix ?? string.Empty) + Slugify( clipDisplayName ?? string.Empty );
	}

	private static string Slugify( string value )
	{
		var builder = new StringBuilder( value.Length );
		foreach ( var character in value )
		{
			if ( char.IsLetterOrDigit( character ) )
			{
				builder.Append( char.ToLowerInvariant( character ) );
			}
			else if ( builder.Length > 0 && builder[^1] != '_' )
			{
				builder.Append( '_' );
			}
		}

		return builder.ToString().Trim( '_' );
	}
}
