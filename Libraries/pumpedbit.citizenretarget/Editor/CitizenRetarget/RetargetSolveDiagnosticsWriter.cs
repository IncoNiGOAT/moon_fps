using System.Text;
using System.Text.Json;

namespace Editor.CitizenRetarget;

[Obsolete( "Deprecated solve-summary writer retained for historical troubleshooting only.", false )]
internal sealed class RetargetSolveDiagnosticsWriter
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	public RetargetSolveArtifactResult Write( RetargetSolveDiagnostics diagnostics )
	{
		ArgumentNullException.ThrowIfNull( diagnostics );

		var diagnosticsFolder = CitizenRetargetPaths.GetProjectAbsolutePath( CitizenTargetProfile.SolveDiagnosticsRelativeFolder );
		var markdownPath = CitizenRetargetPaths.GetProjectAbsolutePath( CitizenTargetProfile.SolveSummaryMarkdownRelativePath );
		Directory.CreateDirectory( diagnosticsFolder );
		Directory.CreateDirectory( Path.GetDirectoryName( markdownPath )! );

		var jsonPath = Path.Combine( diagnosticsFolder, $"{diagnostics.SequenceName}.json" );
		File.WriteAllText( jsonPath, JsonSerializer.Serialize( diagnostics, JsonOptions ) );
		WriteMarkdownSummary( diagnosticsFolder, markdownPath );

		return new RetargetSolveArtifactResult
		{
			JsonAbsolutePath = jsonPath,
			MarkdownAbsolutePath = markdownPath
		};
	}

	private static void WriteMarkdownSummary( string diagnosticsFolder, string markdownPath )
	{
		var reports = Directory.GetFiles( diagnosticsFolder, "*.json", SearchOption.TopDirectoryOnly )
			.Select( path => JsonSerializer.Deserialize<RetargetSolveDiagnostics>( File.ReadAllText( path ) ) )
			.Where( report => report is not null )
			.Select( report => report! )
			.OrderBy( report => GetSortKey( report.SequenceName ) )
			.ThenBy( report => report.SequenceName, StringComparer.OrdinalIgnoreCase )
			.ToList();

		var builder = new StringBuilder();
		builder.AppendLine( "# CARL Solve Summary" );
		builder.AppendLine();
		builder.AppendLine( "- `DMX` is the active output path." );
		builder.AppendLine( "- `SMD` remains diagnostics-only." );
		builder.AppendLine();

		foreach ( var report in reports )
		{
			builder.AppendLine( $"## {report.SequenceName}" );
			builder.AppendLine();
			builder.AppendLine( $"- Source clip: `{report.SourceClipName}`" );
			builder.AppendLine( $"- Solve stage: `{report.Stage}`" );
			builder.AppendLine( $"- Source profile: `{report.SourceProfile}`" );
			builder.AppendLine( $"- Target bind asset: `{report.TargetBindAssetPath}`" );
			builder.AppendLine( $"- Source scale ratio: `{report.SourceScaleRatio:0.###}`" );
			builder.AppendLine( $"- Clip alignment translation: `{FormatArray( report.ClipAlignmentTranslation )}`" );
			builder.AppendLine( $"- Clip alignment rotation: `{FormatArray( report.ClipAlignmentRotation )}`" );
			builder.AppendLine( $"- Pelvis delta min/max: `{FormatArray( report.PelvisTranslationDeltaMin )}` / `{FormatArray( report.PelvisTranslationDeltaMax )}`" );
			builder.AppendLine( $"- Full target skeleton written every frame: `{YesNo( report.FullTargetSkeletonWrittenEveryFrame )}`" );
			builder.AppendLine( $"- Only pelvis local translation animated: `{YesNo( report.OnlyPelvisLocalTranslationAnimated )}`" );
			builder.AppendLine( $"- Unmapped bones changed from rest: `{YesNo( report.AnyUnmappedBoneReceivedNonRestLocal )}`" );
			builder.AppendLine( $"- Non-finite animated quaternions: `{YesNo( report.AnyAnimatedBoneReceivedNonFiniteQuaternion )}`" );
			builder.AppendLine( $"- Non-unit animated quaternions: `{YesNo( report.AnyAnimatedBoneReceivedNonUnitQuaternion )}`" );
			builder.AppendLine();
			builder.AppendLine( "Top angular deltas:" );
			foreach ( var bone in report.MappedBones
				.OrderByDescending( item => item.MaxAngularDeltaDegrees )
				.Take( 8 ) )
			{
				builder.AppendLine( $"- `{bone.BoneName}`: `{bone.MaxAngularDeltaDegrees:0.##} deg`" );
			}

			builder.AppendLine();
		}

		File.WriteAllText( markdownPath, builder.ToString() );
	}

	private static int GetSortKey( string sequenceName )
	{
		if ( sequenceName.Contains( "a_tpose", StringComparison.OrdinalIgnoreCase ) )
			return 0;
		if ( sequenceName.Contains( "walk_fwd_loop", StringComparison.OrdinalIgnoreCase ) )
			return 1;
		if ( sequenceName.Contains( "wallrun_l_loop", StringComparison.OrdinalIgnoreCase ) )
			return 2;

		return 3;
	}

	private static string YesNo( bool value ) => value ? "yes" : "no";

	private static string FormatArray( IReadOnlyList<float> values )
	{
		return string.Join( ", ", values.Select( value => value.ToString( "0.###" ) ) );
	}
}
