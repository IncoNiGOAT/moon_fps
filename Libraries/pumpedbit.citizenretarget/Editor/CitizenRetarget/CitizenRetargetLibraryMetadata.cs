using System.Reflection;

namespace Editor.CitizenRetarget;

/// <summary>
/// s&amp;box currently builds local Library Manager cards from a mock package and does not
/// read README.md or custom .sbproj description fields for local packages. Keep the
/// package metadata hydrated so copy-installed builds do not appear empty.
/// </summary>
internal static class CitizenRetargetLibraryMetadata
{
	const string Ident = "citizenretarget";
	const string TitleFallback = "CARL";
	const string SummaryFallback = "CARL retargets FBX character animations onto Citizen-based s&box models from the editor.";
	const string ThumbFallback = "branding/carl_logo.jpg";
	const string DescriptionFallback =
		"<h2>CARL</h2>" +
		"<p><b>Citizen Animation Retargeting Library</b> imports FBX animation clips, maps source skeletons, runs the Rokoko/Blender retarget backend, and previews generated Citizen target animations inside s&box.</p>" +
		"<p>Open it from <b>CARL > Open Retargeter</b>, then use the Diagnostics tab to verify Blender, the Rokoko addon, bundled backend files, native FBX scanning, and the current target model before running jobs.</p>" +
		"<p>Source, install instructions, setup notes, and support workflow: <a href='https://github.com/pumped-bit/citizen-animation-retargeting-library'>github.com/pumped-bit/citizen-animation-retargeting-library</a>.</p>";

	static bool _applied;

	[EditorEvent.Hotload]
	static void OnHotload()
	{
		_applied = false;
	}

	[EditorEvent.Frame]
	static void ApplyPackageMetadata()
	{
		if ( _applied )
			return;

		_applied = TryApplyPackageMetadata();
	}

	static bool TryApplyPackageMetadata()
	{
		try
		{
			var allField = typeof( Project ).GetField( "All", BindingFlags.NonPublic | BindingFlags.Static );
			if ( allField?.GetValue( null ) is not IEnumerable<Project> projects )
				return false;

			foreach ( var project in projects )
			{
				if ( project?.Config?.Ident != Ident )
					continue;

				var package = project.Package;
				if ( package is null )
					return false;

				TrySetStringMember( package, "Title", string.IsNullOrWhiteSpace( project.Config.Title ) ? TitleFallback : project.Config.Title );
				package.Summary = project.Config.GetMetaOrDefault( "PackageSummary", SummaryFallback );
				package.Description = project.Config.GetMetaOrDefault( "PackageDescription", DescriptionFallback );
				ApplyPackageThumb( package, ResolvePackageThumb( project.Config.GetMetaOrDefault( "PackageThumb", ThumbFallback ) ) );
				return true;
			}
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, "CARL could not hydrate local library metadata." );
			return true;
		}

		return false;
	}

	static string ResolvePackageThumb( string configuredPath )
	{
		if ( string.IsNullOrWhiteSpace( configuredPath ) )
			return string.Empty;

		var path = configuredPath.Trim();
		if ( TryGetLocalFilePath( path, out var localPath ) )
			path = localPath;
		else if ( IsExternalThumbPath( path ) )
			return path;

		var absolutePath = Path.IsPathRooted( path )
			? path
			: CitizenRetargetPaths.GetPluginAbsolutePath( path );

		if ( !File.Exists( absolutePath ) )
			return string.Empty;

		return absolutePath;
	}

	static bool TryGetLocalFilePath( string path, out string localPath )
	{
		localPath = string.Empty;

		if ( !Uri.TryCreate( path, UriKind.Absolute, out var uri ) || !uri.IsFile )
			return false;

		localPath = uri.LocalPath;
		return true;
	}

	static bool IsExternalThumbPath( string path )
	{
		return path.StartsWith( "http://", StringComparison.OrdinalIgnoreCase )
			|| path.StartsWith( "https://", StringComparison.OrdinalIgnoreCase )
			|| path.Contains( ':', StringComparison.Ordinal ) && !Path.IsPathRooted( path );
	}

	static void ApplyPackageThumb( object package, string thumb )
	{
		if ( string.IsNullOrWhiteSpace( thumb ) )
			return;

		TrySetStringMember( package, "Thumb", thumb );
		TrySetStringMember( package, "ThumbWide", thumb );
		TrySetStringMember( package, "ThumbTall", thumb );
	}

	static bool TrySetStringMember( object target, string memberName, string value )
	{
		var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		var property = target.GetType().GetProperty( memberName, flags );
		if ( property?.PropertyType == typeof( string ) && property.SetMethod is not null )
		{
			property.SetValue( target, value );
			return true;
		}

		var field = target.GetType().GetField( $"<{memberName}>k__BackingField", flags )
			?? target.GetType().GetField( memberName, flags );
		if ( field?.FieldType != typeof( string ) || field.IsInitOnly )
			return false;

		field.SetValue( target, value );
		return true;
	}
}
