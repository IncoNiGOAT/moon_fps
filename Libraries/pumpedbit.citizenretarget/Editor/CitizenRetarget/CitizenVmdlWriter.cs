#nullable enable

namespace Editor.CitizenRetarget;

internal sealed class CitizenVmdlWriter
{
	private const string CitizenAnimGraphPath = CitizenTargetProfile.DefaultCitizenAnimGraphPath;
	private const string ScaleAndMirrorNote = "We\\'re working in centimeters at the source (which makes more sense for us), and then letting the engine take care of the conversion to inches at this step. So if you want to create something for the Citizen (like clothing), you should also model it in centimeters (matching the provided source files), and use a ScaleAndMirror modifier at 0.3937.";
	private const string MaterialGroupNote = "These skins are now here only for preview purposes.\\n";

	private static readonly string[] AnimationPrefabs =
	[
		"models/citizen/prefabs/citizen_animationlist.vmdl_prefab",
		"models/citizen/prefabs/citizen_animationlist_unicycle.vmdl_prefab",
		"models/citizen/prefabs/citizen_animationlist_debug.vmdl_prefab",
		"models/citizen/prefabs/citizen_animationlist_visemes.vmdl_prefab",
		"models/citizen/prefabs/citizen_animationlist_menu.vmdl_prefab"
	];

	public string WriteSharedVmdl( string targetVmdlPath, string outputAnimationFolder, params string[] extensions )
	{
		return WriteSharedVmdl( targetVmdlPath, outputAnimationFolder, CitizenTargetProfile.DefaultSequencePrefix, extensions );
	}

	public string WriteSharedVmdl( string targetVmdlPath, string outputAnimationFolder, string sequencePrefix, params string[] extensions )
	{
		return WriteSharedVmdl( targetVmdlPath, outputAnimationFolder, sequencePrefix, null, extensions );
	}

	public string WriteSharedVmdl( string targetVmdlPath, string outputAnimationFolder, string sequencePrefix, string? animGraphResourcePath, params string[] extensions )
	{
		var clips = EnumerateSourcesFromFolder( outputAnimationFolder, sequencePrefix, extensions );
		return WriteSharedVmdl( targetVmdlPath, clips, animGraphResourcePath );
	}

	public string WriteSharedVmdl( string targetVmdlPath, IReadOnlyList<GeneratedAnimationSource> clips, string? animGraphResourcePath = null )
	{
		var absoluteVmdlPath = CitizenRetargetPaths.GetAssetAbsolutePath( targetVmdlPath );
		Directory.CreateDirectory( Path.GetDirectoryName( absoluteVmdlPath )! );

		var builder = new System.Text.StringBuilder();
		builder.AppendLine( "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->" );
		builder.AppendLine( "{" );
		builder.AppendLine( "\trootNode = " );
		builder.AppendLine( "\t{" );
		builder.AppendLine( "\t\t_class = \"RootNode\"" );
		builder.AppendLine( "\t\tchildren = " );
		builder.AppendLine( "\t\t[" );

		AppendAnimConstraintList( builder );
		AppendModelModifierList( builder );
		AppendMaterialGroupList( builder );
		AppendPrefabList( builder, "RenderMeshList", "models/citizen/prefabs/citizen_rendermeshlist.vmdl_prefab" );
		AppendAnimationList( builder, clips );
		AppendPrefabList( builder, "BoneMarkupList", "models/citizen/prefabs/citizen_bonemarkuplist.vmdl_prefab", "\t\t\t\tbone_cull_type = \"None\"" );
		AppendPrefabList( builder, "AttachmentList", "models/citizen/prefabs/citizen_attachmentlist.vmdl_prefab" );
		AppendPrefabList( builder, "PhysicsJointList", "models/citizen/prefabs/citizen_physicsjointlist.vmdl_prefab" );
		AppendPrefabList( builder, "PhysicsShapeList", "models/citizen/prefabs/citizen_physicsshapelist.vmdl_prefab" );
		AppendPrefabList( builder, "HitboxSetList", "models/citizen/prefabs/citizen_hitboxsetlist.vmdl_prefab" );
		AppendPrefabList( builder, "IKData", "models/citizen/prefabs/citizen_ikdata.vmdl_prefab" );
		AppendPrefabList( builder, "PoseParamList", "models/citizen/prefabs/citizen_poseparamlist.vmdl_prefab" );
		AppendPrefabList( builder, "WeightListList", "models/citizen/prefabs/citizen_weightlistlist.vmdl_prefab" );
		AppendPrefabList( builder, "GameDataList", "models/citizen/prefabs/citizen_gamedatalist.vmdl_prefab" );
		AppendPrefabList( builder, "BodyGroupList", "models/citizen/prefabs/citizen_bodygrouplist.vmdl_prefab" );
		AppendPrefabList( builder, "LODGroupList", "models/citizen/prefabs/citizen_lodgrouplist.vmdl_prefab" );

		var animGraphPath = string.IsNullOrWhiteSpace( animGraphResourcePath )
			? CitizenAnimGraphPath
			: animGraphResourcePath.Trim().Replace( '\\', '/' );

		builder.AppendLine( "\t\t]" );
		builder.AppendLine( "\t\tmodel_archetype = \"\"" );
		builder.AppendLine( "\t\tprimary_associated_entity = \"\"" );
		builder.AppendLine( $"\t\tanim_graph_name = \"{animGraphPath}\"" );
		builder.AppendLine( "\t\tbase_model_name = \"\"" );
		builder.AppendLine( "\t}" );
		builder.AppendLine( "}" );

		File.WriteAllText( absoluteVmdlPath, builder.ToString() );
		return absoluteVmdlPath;
	}

	public string WriteSourcePreviewVmdl( string targetVmdlPath, GeneratedAnimationSource clip )
	{
		var absoluteVmdlPath = CitizenRetargetPaths.GetAssetAbsolutePath( targetVmdlPath );
		Directory.CreateDirectory( Path.GetDirectoryName( absoluteVmdlPath )! );

		var builder = new System.Text.StringBuilder();
		builder.AppendLine( "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->" );
		builder.AppendLine( "{" );
		builder.AppendLine( "\trootNode = " );
		builder.AppendLine( "\t{" );
		builder.AppendLine( "\t\t_class = \"RootNode\"" );
		builder.AppendLine( "\t\tchildren = " );
		builder.AppendLine( "\t\t[" );
		AppendSourcePreviewRenderMeshList( builder, clip );
		AppendSourcePreviewAnimationList( builder, clip );
		builder.AppendLine( "\t\t]" );
		builder.AppendLine( "\t\tmodel_archetype = \"\"" );
		builder.AppendLine( "\t\tprimary_associated_entity = \"\"" );
		builder.AppendLine( "\t\tanim_graph_name = \"\"" );
		builder.AppendLine( "\t\tbase_model_name = \"\"" );
		builder.AppendLine( "\t}" );
		builder.AppendLine( "}" );

		File.WriteAllText( absoluteVmdlPath, builder.ToString() );
		return absoluteVmdlPath;
	}

	public List<GeneratedAnimationSource> EnumerateSourcesFromFolder( string outputAnimationFolder, string sequencePrefix, params string[] extensions )
	{
		return EnumerateSourcesFromFolderInternal( outputAnimationFolder, sequencePrefix, extensions );
	}

	private static void AppendAnimConstraintList( System.Text.StringBuilder builder )
	{
		AppendPrefabList( builder, "AnimConstraintList", "models/citizen/prefabs/citizen_animconstraintlist.vmdl_prefab" );
	}

	private static void AppendModelModifierList( System.Text.StringBuilder builder )
	{
		builder.AppendLine( "\t\t\t{" );
		builder.AppendLine( "\t\t\t\t_class = \"ModelModifierList\"" );
		builder.AppendLine( "\t\t\t\tchildren = " );
		builder.AppendLine( "\t\t\t\t[" );
		builder.AppendLine( "\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t_class = \"ModelModifier_ScaleAndMirror\"" );
		builder.AppendLine( $"\t\t\t\t\t\tnote = \"{ScaleAndMirrorNote}\"" );
		builder.AppendLine( "\t\t\t\t\t\tscale = 0.3937" );
		builder.AppendLine( "\t\t\t\t\t\tmirror_x = false" );
		builder.AppendLine( "\t\t\t\t\t\tmirror_y = false" );
		builder.AppendLine( "\t\t\t\t\t\tmirror_z = false" );
		builder.AppendLine( "\t\t\t\t\t\tflip_bone_forward = false" );
		builder.AppendLine( "\t\t\t\t\t\tswap_left_and_right_bones = false" );
		builder.AppendLine( "\t\t\t\t\t}," );
		builder.AppendLine( "\t\t\t\t]" );
		builder.AppendLine( "\t\t\t}," );
	}

	private static void AppendMaterialGroupList( System.Text.StringBuilder builder )
	{
		builder.AppendLine( "\t\t\t{" );
		builder.AppendLine( "\t\t\t\t_class = \"MaterialGroupList\"" );
		builder.AppendLine( "\t\t\t\tchildren = " );
		builder.AppendLine( "\t\t\t\t[" );
		builder.AppendLine( "\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t_class = \"Prefab\"" );
		builder.AppendLine( $"\t\t\t\t\t\tnote = \"{MaterialGroupNote}\"" );
		builder.AppendLine( "\t\t\t\t\t\ttarget_file = \"models/citizen/prefabs/citizen_materialgrouplist.vmdl_prefab\"" );
		builder.AppendLine( "\t\t\t\t\t}," );
		builder.AppendLine( "\t\t\t\t]" );
		builder.AppendLine( "\t\t\t}," );
	}

	private static void AppendAnimationList( System.Text.StringBuilder builder, IReadOnlyList<GeneratedAnimationSource> clips )
	{
		builder.AppendLine( "\t\t\t{" );
		builder.AppendLine( "\t\t\t\t_class = \"AnimationList\"" );
		builder.AppendLine( "\t\t\t\tchildren = " );
		builder.AppendLine( "\t\t\t\t[" );

		foreach ( var prefabPath in AnimationPrefabs )
		{
			builder.AppendLine( "\t\t\t\t\t{" );
			builder.AppendLine( "\t\t\t\t\t\t_class = \"Prefab\"" );
			builder.AppendLine( $"\t\t\t\t\t\ttarget_file = \"{prefabPath}\"" );
			builder.AppendLine( "\t\t\t\t\t}," );
		}

		foreach ( var clip in clips.OrderBy( entry => entry.SequenceName, StringComparer.OrdinalIgnoreCase ) )
		{
			builder.AppendLine( "\t\t\t\t\t{" );
			builder.AppendLine( "\t\t\t\t\t\t_class = \"AnimFile\"" );
			builder.AppendLine( $"\t\t\t\t\t\tname = \"{EscapeKv3String( clip.SequenceName )}\"" );
			builder.AppendLine( "\t\t\t\t\t\tactivity_name = \"\"" );
			builder.AppendLine( "\t\t\t\t\t\tactivity_weight = 1" );
			builder.AppendLine( "\t\t\t\t\t\tweight_list_name = \"\"" );
			builder.AppendLine( "\t\t\t\t\t\tfade_in_time = 0.2" );
			builder.AppendLine( "\t\t\t\t\t\tfade_out_time = 0.2" );
			builder.AppendLine( $"\t\t\t\t\t\tlooping = {(clip.Looping ? "true" : "false")}" );
			builder.AppendLine( "\t\t\t\t\t\tdelta = false" );
			builder.AppendLine( "\t\t\t\t\t\tworldSpace = false" );
			builder.AppendLine( "\t\t\t\t\t\thidden = false" );
			builder.AppendLine( "\t\t\t\t\t\tanim_markup_ordered = false" );
			builder.AppendLine( "\t\t\t\t\t\tdisable_compression = false" );
			builder.AppendLine( "\t\t\t\t\t\tdisable_interpolation = false" );
			builder.AppendLine( "\t\t\t\t\t\tenable_scale = false" );
			builder.AppendLine( $"\t\t\t\t\t\tsource_filename = \"{EscapeKv3String( clip.ResourcePath )}\"" );
			AppendAnimFileFrameRange( builder, clip );
			builder.AppendLine( "\t\t\t\t\t\tframerate = -1.0" );
			builder.AppendLine( "\t\t\t\t\t\ttake = 0" );
			builder.AppendLine( "\t\t\t\t\t\treverse = false" );
			builder.AppendLine( "\t\t\t\t\t}," );
		}

		builder.AppendLine( "\t\t\t\t]" );
		builder.AppendLine( "\t\t\t\tdefault_root_bone_name = \"pelvis\"" );
		builder.AppendLine( "\t\t\t}," );
	}

	private static void AppendSourcePreviewRenderMeshList( System.Text.StringBuilder builder, GeneratedAnimationSource clip )
	{
		builder.AppendLine( "\t\t\t{" );
		builder.AppendLine( "\t\t\t\t_class = \"RenderMeshList\"" );
		builder.AppendLine( "\t\t\t\tchildren = " );
		builder.AppendLine( "\t\t\t\t[" );
		builder.AppendLine( "\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t_class = \"RenderMeshFile\"" );
		builder.AppendLine( $"\t\t\t\t\t\tname = \"{EscapeKv3String( clip.SequenceName )}\"" );
		builder.AppendLine( $"\t\t\t\t\t\tfilename = \"{EscapeKv3String( clip.ResourcePath )}\"" );
		builder.AppendLine( "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]" );
		builder.AppendLine( "\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]" );
		builder.AppendLine( "\t\t\t\t\t\timport_scale = 1.0" );
		builder.AppendLine( "\t\t\t\t\t\talign_origin_x_type = \"None\"" );
		builder.AppendLine( "\t\t\t\t\t\talign_origin_y_type = \"None\"" );
		builder.AppendLine( "\t\t\t\t\t\talign_origin_z_type = \"None\"" );
		builder.AppendLine( "\t\t\t\t\t\tparent_bone = \"\"" );
		builder.AppendLine( "\t\t\t\t\t\timport_filter = " );
		builder.AppendLine( "\t\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t\texclude_by_default = false" );
		builder.AppendLine( "\t\t\t\t\t\t\texception_list = [  ]" );
		builder.AppendLine( "\t\t\t\t\t\t}" );
		builder.AppendLine( "\t\t\t\t\t}," );
		builder.AppendLine( "\t\t\t\t]" );
		builder.AppendLine( "\t\t\t}," );
	}

	private static void AppendSourcePreviewAnimationList( System.Text.StringBuilder builder, GeneratedAnimationSource clip )
	{
		builder.AppendLine( "\t\t\t{" );
		builder.AppendLine( "\t\t\t\t_class = \"AnimationList\"" );
		builder.AppendLine( "\t\t\t\tchildren = " );
		builder.AppendLine( "\t\t\t\t[" );
		builder.AppendLine( "\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t_class = \"AnimFile\"" );
		builder.AppendLine( $"\t\t\t\t\t\tname = \"{EscapeKv3String( clip.SequenceName )}\"" );
		builder.AppendLine( "\t\t\t\t\t\tactivity_name = \"\"" );
		builder.AppendLine( "\t\t\t\t\t\tactivity_weight = 1" );
		builder.AppendLine( "\t\t\t\t\t\tweight_list_name = \"\"" );
		builder.AppendLine( "\t\t\t\t\t\tfade_in_time = 0.2" );
		builder.AppendLine( "\t\t\t\t\t\tfade_out_time = 0.2" );
		builder.AppendLine( $"\t\t\t\t\t\tlooping = {(clip.Looping ? "true" : "false")}" );
		builder.AppendLine( "\t\t\t\t\t\tdelta = false" );
		builder.AppendLine( "\t\t\t\t\t\tworldSpace = false" );
		builder.AppendLine( "\t\t\t\t\t\thidden = false" );
		builder.AppendLine( "\t\t\t\t\t\tanim_markup_ordered = false" );
		builder.AppendLine( "\t\t\t\t\t\tdisable_compression = false" );
		builder.AppendLine( "\t\t\t\t\t\tdisable_interpolation = false" );
		builder.AppendLine( "\t\t\t\t\t\tenable_scale = false" );
		builder.AppendLine( $"\t\t\t\t\t\tsource_filename = \"{EscapeKv3String( clip.ResourcePath )}\"" );
		AppendAnimFileFrameRange( builder, clip );
		builder.AppendLine( "\t\t\t\t\t\tframerate = -1.0" );
		builder.AppendLine( "\t\t\t\t\t\ttake = 0" );
		builder.AppendLine( "\t\t\t\t\t\treverse = false" );
		builder.AppendLine( "\t\t\t\t\t}," );
		builder.AppendLine( "\t\t\t\t]" );
		builder.AppendLine( "\t\t\t\tdefault_root_bone_name = \"\"" );
		builder.AppendLine( "\t\t\t}," );
	}

	private static void AppendPrefabList( System.Text.StringBuilder builder, string nodeClass, string prefabPath, string? trailingProperty = null )
	{
		builder.AppendLine( "\t\t\t{" );
		builder.AppendLine( $"\t\t\t\t_class = \"{nodeClass}\"" );
		builder.AppendLine( "\t\t\t\tchildren = " );
		builder.AppendLine( "\t\t\t\t[" );
		builder.AppendLine( "\t\t\t\t\t{" );
		builder.AppendLine( "\t\t\t\t\t\t_class = \"Prefab\"" );
		builder.AppendLine( $"\t\t\t\t\t\ttarget_file = \"{prefabPath}\"" );
		builder.AppendLine( "\t\t\t\t\t}," );
		builder.AppendLine( "\t\t\t\t]" );
		if ( !string.IsNullOrWhiteSpace( trailingProperty ) )
			builder.AppendLine( trailingProperty );
		builder.AppendLine( "\t\t\t}," );
	}

	private static List<GeneratedAnimationSource> EnumerateSourcesFromFolderInternal( string outputAnimationFolder, string sequencePrefix, IReadOnlyList<string> extensions )
	{
		var absoluteAnimationFolder = CitizenRetargetPaths.GetAssetAbsolutePath( outputAnimationFolder );
		Directory.CreateDirectory( absoluteAnimationFolder );
		var primaryGeneratedFolder = Path.Combine( absoluteAnimationFolder, "anims" );
		var allowedExtensions = extensions is { Count: > 0 }
			? new HashSet<string>( extensions.Select( NormalizeExtension ), StringComparer.OrdinalIgnoreCase )
			: new HashSet<string>( [".fbx"], StringComparer.OrdinalIgnoreCase );

		var searchRoots = Directory.Exists( primaryGeneratedFolder )
			? new[] { primaryGeneratedFolder }
			: new[] { absoluteAnimationFolder };

		return searchRoots
			.SelectMany( root => Directory.GetFiles( root, "*.*", SearchOption.TopDirectoryOnly ) )
			.Where( path => allowedExtensions.Contains( NormalizeExtension( Path.GetExtension( path ) ) ) )
			.Where( path => string.IsNullOrWhiteSpace( sequencePrefix )
				|| Path.GetFileNameWithoutExtension( path ).StartsWith( sequencePrefix, StringComparison.OrdinalIgnoreCase ) )
			.Select( path => new GeneratedAnimationSource
			{
				SequenceName = Path.GetFileNameWithoutExtension( path ),
				ResourcePath = CitizenRetargetPaths.GetAssetResourcePath( path ),
				Looping = Path.GetFileNameWithoutExtension( path ).Contains( "loop", StringComparison.OrdinalIgnoreCase )
			} )
			.ToList();
	}

	private static string NormalizeExtension( string extension )
	{
		if ( string.IsNullOrWhiteSpace( extension ) )
			return string.Empty;

		return extension.StartsWith( '.' ) ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";
	}

	private static void AppendAnimFileFrameRange( System.Text.StringBuilder builder, GeneratedAnimationSource clip )
	{
		var useCustom = clip.StartFrame >= 0 && clip.EndFrame >= 0 && clip.EndFrame >= clip.StartFrame;
		if ( useCustom )
		{
			builder.AppendLine( $"\t\t\t\t\t\tstart_frame = {clip.StartFrame}" );
			builder.AppendLine( $"\t\t\t\t\t\tend_frame = {clip.EndFrame}" );
		}
		else
		{
			builder.AppendLine( "\t\t\t\t\t\tstart_frame = -1" );
			builder.AppendLine( "\t\t\t\t\t\tend_frame = -1" );
		}
	}

	private static string EscapeKv3String( string value )
	{
		return value
			.Replace( "\\", "\\\\" )
			.Replace( "\"", "\\\"" );
	}
}
