using System.Runtime.InteropServices;
using System.Text.Json;

namespace Editor.CitizenRetarget;

internal sealed class UfbxNative : IDisposable
{
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	private IntPtr _libraryHandle;
	private ScanFbxDelegate _scanFbx;
	private SampleClipDelegate _sampleClip;
	private SampleClipExDelegate _sampleClipEx;
	private InspectSceneDelegate _inspectScene;
	private FreeStringDelegate _freeString;

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	private delegate IntPtr ScanFbxDelegate( [MarshalAs( UnmanagedType.LPUTF8Str )] string sourcePath );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	private delegate IntPtr SampleClipDelegate(
		[MarshalAs( UnmanagedType.LPUTF8Str )] string sourcePath,
		[MarshalAs( UnmanagedType.LPUTF8Str )] string clipName,
		[MarshalAs( UnmanagedType.LPUTF8Str )] string targetPath );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	private delegate IntPtr SampleClipExDelegate(
		[MarshalAs( UnmanagedType.LPUTF8Str )] string sourcePath,
		[MarshalAs( UnmanagedType.LPUTF8Str )] string clipName,
		[MarshalAs( UnmanagedType.LPUTF8Str )] string targetPath,
		[MarshalAs( UnmanagedType.LPUTF8Str )] string sourceProfile,
		[MarshalAs( UnmanagedType.LPUTF8Str )] string targetProfile );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	private delegate IntPtr InspectSceneDelegate(
		[MarshalAs( UnmanagedType.LPUTF8Str )] string scenePath,
		[MarshalAs( UnmanagedType.LPUTF8Str )] string profile );

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	private delegate void FreeStringDelegate( IntPtr pointer );

	public UfbxNative()
	{
		LoadLibrary();
	}

	public NativeScanResult Scan( string sourcePath )
	{
		var json = InvokeUtf8( () => _scanFbx( sourcePath ) );
		var result = JsonSerializer.Deserialize<NativeScanResult>( json, _jsonOptions ) ?? new NativeScanResult();
		if ( !string.IsNullOrWhiteSpace( result.Error ) )
			throw new InvalidOperationException( result.Error );

		return result;
	}

	public NativeSampleResult SampleClip( string sourcePath, string clipName, string targetPath )
	{
		return SampleClip( sourcePath, clipName, targetPath, TargetBindProfile.Current, TargetBindProfile.Current );
	}

	public NativeSampleResult SampleClip(
		string sourcePath,
		string clipName,
		string targetPath,
		TargetBindProfile sourceProfile,
		TargetBindProfile targetProfile )
	{
		var json = InvokeUtf8( () => _sampleClipEx(
			sourcePath,
			clipName,
			targetPath,
			sourceProfile.ToString(),
			targetProfile.ToString() ) );
		var result = JsonSerializer.Deserialize<NativeSampleResult>( json, _jsonOptions ) ?? new NativeSampleResult();
		if ( !string.IsNullOrWhiteSpace( result.Error ) )
			throw new InvalidOperationException( result.Error );

		return result;
	}

	public NativeSceneAuditResult InspectScene( string scenePath, TargetBindProfile profile )
	{
		var json = InvokeUtf8( () => _inspectScene( scenePath, profile.ToString() ) );
		var result = JsonSerializer.Deserialize<NativeSceneAuditResult>( json, _jsonOptions ) ?? new NativeSceneAuditResult();
		if ( !string.IsNullOrWhiteSpace( result.Error ) )
			throw new InvalidOperationException( result.Error );

		return result;
	}

	public void Dispose()
	{
		if ( _libraryHandle == IntPtr.Zero )
			return;

		NativeLibrary.Free( _libraryHandle );
		_libraryHandle = IntPtr.Zero;
		GC.SuppressFinalize( this );
	}

	private void LoadLibrary()
	{
		if ( _libraryHandle != IntPtr.Zero )
			return;

		var dllPath = Path.Combine( CitizenRetargetPaths.NativeRoot, "win-x64", "ual2_ufbx_helper.dll" );
		if ( !File.Exists( dllPath ) )
			throw new FileNotFoundException( $"Missing native helper DLL at '{dllPath}'" );

		_libraryHandle = NativeLibrary.Load( dllPath );
		_scanFbx = LoadFunction<ScanFbxDelegate>( "ual2_scan_fbx_json" );
		_sampleClip = LoadFunction<SampleClipDelegate>( "ual2_sample_clip_json" );
		_sampleClipEx = LoadFunction<SampleClipExDelegate>( "ual2_sample_clip_json_ex" );
		_inspectScene = LoadFunction<InspectSceneDelegate>( "ual2_inspect_scene_json" );
		_freeString = LoadFunction<FreeStringDelegate>( "ual2_free_string" );
	}

	private T LoadFunction<T>( string exportName ) where T : Delegate
	{
		var symbol = NativeLibrary.GetExport( _libraryHandle, exportName );
		return Marshal.GetDelegateForFunctionPointer<T>( symbol );
	}

	private string InvokeUtf8( Func<IntPtr> callback )
	{
		var pointer = callback();
		if ( pointer == IntPtr.Zero )
			throw new InvalidOperationException( "Native helper returned a null pointer" );

		try
		{
			var text = Marshal.PtrToStringUTF8( pointer );
			if ( string.IsNullOrWhiteSpace( text ) )
				throw new InvalidOperationException( "Native helper returned an empty payload" );

			return text;
		}
		finally
		{
			_freeString( pointer );
		}
	}
}
