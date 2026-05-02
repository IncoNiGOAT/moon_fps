namespace Editor.CitizenRetarget;

internal static class RetargetFileDialogs
{
	public static string PickExistingFile( string title, string extension, string currentPath, string fallbackDirectory )
	{
		extension = extension.Trim().TrimStart( '.' );
		var defaultPath = BuildDefaultFilePath( currentPath, fallbackDirectory, extension );
		var dialog = new FileDialog( null )
		{
			Title = title,
			Directory = ResolveInitialDirectory( currentPath, fallbackDirectory ),
			DefaultSuffix = $".{extension}"
		};
		dialog.SelectFile( Path.GetFileName( defaultPath ) );
		dialog.SetFindExistingFile();
		dialog.SetModeOpen();
		dialog.SetNameFilter( $"{extension.ToUpperInvariant()} (*.{extension})" );

		return dialog.Execute() ? dialog.SelectedFile : null;
	}

	public static string PickDirectory( string title, string currentPath, string fallbackDirectory )
	{
		var dialog = new FileDialog( null )
		{
			Title = title,
			Directory = ResolveInitialDirectory( currentPath, fallbackDirectory )
		};
		dialog.SetFindDirectory();
		dialog.SetModeOpen();

		return dialog.Execute() ? dialog.SelectedFile : null;
	}

	public static string SaveZipFile( string title, string defaultPath )
	{
		var selected = EditorUtility.SaveFileDialog( title, "zip", EnsureExtension( defaultPath, ".zip" ) );
		return string.IsNullOrWhiteSpace( selected ) ? null : EnsureExtension( selected, ".zip" );
	}

	private static string BuildDefaultFilePath( string currentPath, string fallbackDirectory, string extension )
	{
		var normalizedCurrent = NormalizePath( currentPath );
		if ( !string.IsNullOrWhiteSpace( normalizedCurrent ) && File.Exists( normalizedCurrent ) )
			return normalizedCurrent;

		var directory = ResolveInitialDirectory( currentPath, fallbackDirectory );
		return Path.Combine( directory, $"untitled.{extension}" );
	}

	private static string ResolveInitialDirectory( string currentPath, string fallbackDirectory )
	{
		var normalizedCurrent = NormalizePath( currentPath );
		if ( !string.IsNullOrWhiteSpace( normalizedCurrent ) )
		{
			if ( Directory.Exists( normalizedCurrent ) )
				return normalizedCurrent;

			var parent = Path.GetDirectoryName( normalizedCurrent );
			if ( !string.IsNullOrWhiteSpace( parent ) && Directory.Exists( parent ) )
				return parent;
		}

		var normalizedFallback = NormalizePath( fallbackDirectory );
		if ( !string.IsNullOrWhiteSpace( normalizedFallback ) && Directory.Exists( normalizedFallback ) )
			return normalizedFallback;

		return Environment.GetFolderPath( Environment.SpecialFolder.MyDocuments );
	}

	private static string EnsureExtension( string path, string extension )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return path;

		return Path.GetExtension( path ).Equals( extension, StringComparison.OrdinalIgnoreCase )
			? path
			: path + extension;
	}

	private static string NormalizePath( string path )
	{
		return CitizenRetargetPaths.DecodeExternalPath( path ?? string.Empty ).Trim().Trim( '"' );
	}
}
