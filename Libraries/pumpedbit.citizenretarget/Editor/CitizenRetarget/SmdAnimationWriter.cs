using System.Globalization;
namespace Editor.CitizenRetarget;

using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

[Obsolete( "Deprecated diagnostic-only SMD writer. Production retarget output uses the template-FBX path.", false )]
internal sealed class SmdAnimationWriter : IRetargetOutputWriter
{
	private static readonly NQuaternion RootYUpCorrection = RetargetMath.Normalize(
		NQuaternion.CreateFromAxisAngle( NVector3.UnitX, -MathF.PI * 0.5f ) );
	private static readonly NQuaternion ValveLegacyCorrection = RetargetMath.Normalize(
		NQuaternion.CreateFromAxisAngle( NVector3.UnitY, MathF.PI * 0.5f ) *
		NQuaternion.CreateFromAxisAngle( NVector3.UnitZ, MathF.PI * 0.5f ) );

	public RetargetOutputFormat Format => RetargetOutputFormat.SmdDebug;
	public string FileExtension => ".smd";

	public string WriteClip( RetargetedClip clip, string outputAnimationFolder, SmdWriteOptions options = null )
	{
		options ??= new SmdWriteOptions();
		var absoluteFolder = CitizenRetargetPaths.GetAssetAbsolutePath( outputAnimationFolder );
		Directory.CreateDirectory( absoluteFolder );

		var absolutePath = Path.Combine( absoluteFolder, $"{clip.SequenceName}.smd" );
		var builder = new System.Text.StringBuilder();
		builder.AppendLine( "version 1" );
		builder.AppendLine( "nodes" );
		foreach ( var bone in clip.Bones )
		{
			builder.AppendLine( $"{bone.Id} \"{bone.Name}\" {bone.ParentId}" );
		}

		builder.AppendLine( "end" );
		builder.AppendLine( "skeleton" );

		var previousEuler = new Dictionary<string, NVector3>( StringComparer.OrdinalIgnoreCase );
		foreach ( var frame in clip.Frames )
		{
			builder.AppendLine( $"time {frame.Index}" );
			for ( var boneIndex = 0; boneIndex < clip.Bones.Count; ++boneIndex )
			{
				var bone = clip.Bones[boneIndex];
				var transform = SerializeTransform( bone, frame.BoneTransforms[boneIndex], options.RotationContract );
				var euler = QuaternionToEulerXyz( transform.Rotation );
				if ( previousEuler.TryGetValue( bone.Name, out var previous ) )
					euler = UnwrapEuler( euler, previous );

				previousEuler[bone.Name] = euler;
				builder.Append( bone.Id.ToString( CultureInfo.InvariantCulture ) );
				builder.Append( ' ' );
				builder.Append( FormatFloat( transform.Translation.X ) );
				builder.Append( ' ' );
				builder.Append( FormatFloat( transform.Translation.Y ) );
				builder.Append( ' ' );
				builder.Append( FormatFloat( transform.Translation.Z ) );
				builder.Append( ' ' );
				builder.Append( FormatFloat( euler.X ) );
				builder.Append( ' ' );
				builder.Append( FormatFloat( euler.Y ) );
				builder.Append( ' ' );
				builder.AppendLine( FormatFloat( euler.Z ) );
			}
		}

		builder.AppendLine( "end" );
		File.WriteAllText( absolutePath, builder.ToString() );
		return absolutePath;
	}

	string IRetargetOutputWriter.WriteClip( RetargetedClip clip, string outputFolder )
	{
		return WriteClip( clip, outputFolder );
	}

	private static BoneTransform SerializeTransform( RetargetedBone bone, BoneTransform transform, SmdRotationContract contract )
	{
		transform = new BoneTransform( transform.Translation, RetargetMath.Normalize( transform.Rotation ) );
		var serialized = contract switch
		{
			SmdRotationContract.RawLocal => transform,
			SmdRotationContract.InverseLocal => new BoneTransform(
				transform.Translation,
				RetargetMath.Normalize( NQuaternion.Inverse( transform.Rotation ) ) ),
			SmdRotationContract.LegacyValve => new BoneTransform(
				transform.Translation,
				RetargetMath.Normalize( transform.Rotation * ValveLegacyCorrection ) ),
			SmdRotationContract.LegacyValveInverse => new BoneTransform(
				transform.Translation,
				RetargetMath.Normalize( NQuaternion.Inverse( transform.Rotation * ValveLegacyCorrection ) ) ),
			SmdRotationContract.LegacyValveBasis => ApplyBasisConjugation( transform, ValveLegacyCorrection, invertRotation: false ),
			SmdRotationContract.LegacyValveBasisInverse => ApplyBasisConjugation( transform, ValveLegacyCorrection, invertRotation: true ),
			SmdRotationContract.RawLocalRootYUp => transform,
			SmdRotationContract.InverseLocalRootYUp => new BoneTransform(
				transform.Translation,
				RetargetMath.Normalize( NQuaternion.Inverse( transform.Rotation ) ) ),
			_ => transform
		};

		if ( bone.ParentId < 0 && (contract == SmdRotationContract.RawLocalRootYUp || contract == SmdRotationContract.InverseLocalRootYUp) )
			serialized = ApplyRootYUpCorrection( serialized );

		return serialized;
	}

	private static BoneTransform ApplyBasisConjugation( BoneTransform transform, NQuaternion correction, bool invertRotation )
	{
		var inverseCorrection = RetargetMath.Normalize( NQuaternion.Inverse( correction ) );
		var remappedTranslation = NVector3.Transform( transform.Translation, inverseCorrection );
		var remappedRotation = RetargetMath.Normalize( inverseCorrection * transform.Rotation * correction );
		if ( invertRotation )
			remappedRotation = RetargetMath.Normalize( NQuaternion.Inverse( remappedRotation ) );

		return new BoneTransform( remappedTranslation, remappedRotation );
	}

	private static BoneTransform ApplyRootYUpCorrection( BoneTransform transform )
	{
		return new BoneTransform(
			NVector3.Transform( transform.Translation, RootYUpCorrection ),
			RetargetMath.Normalize( RootYUpCorrection * transform.Rotation ) );
	}

	private static string FormatFloat( float value ) => value.ToString( "0.000000", CultureInfo.InvariantCulture );

	private static NVector3 QuaternionToEulerXyz( NQuaternion rotation )
	{
		rotation = RetargetMath.Normalize( rotation );

		var xx = rotation.X * rotation.X;
		var yy = rotation.Y * rotation.Y;
		var zz = rotation.Z * rotation.Z;
		var xy = rotation.X * rotation.Y;
		var xz = rotation.X * rotation.Z;
		var yz = rotation.Y * rotation.Z;
		var wx = rotation.W * rotation.X;
		var wy = rotation.W * rotation.Y;
		var wz = rotation.W * rotation.Z;

		var m11 = 1f - 2f * (yy + zz);
		var m12 = 2f * (xy - wz);
		var m13 = 2f * (xz + wy);
		var m23 = 2f * (yz - wx);
		var m33 = 1f - 2f * (xx + yy);
		var m32 = 2f * (yz + wx);
		var m22 = 1f - 2f * (xx + zz);

		var y = MathF.Asin( Math.Clamp( m13, -1f, 1f ) );
		float x;
		float z;

		if ( MathF.Abs( m13 ) < 0.999999f )
		{
			x = MathF.Atan2( -m23, m33 );
			z = MathF.Atan2( -m12, m11 );
		}
		else
		{
			x = MathF.Atan2( m32, m22 );
			z = 0f;
		}

		return new NVector3( x, y, z );
	}

	private static NVector3 UnwrapEuler( NVector3 current, NVector3 previous )
	{
		return new NVector3(
			UnwrapAngle( current.X, previous.X ),
			UnwrapAngle( current.Y, previous.Y ),
			UnwrapAngle( current.Z, previous.Z ) );
	}

	private static float UnwrapAngle( float value, float previous )
	{
		const float TwoPi = MathF.PI * 2f;
		while ( value - previous > MathF.PI )
			value -= TwoPi;
		while ( previous - value > MathF.PI )
			value += TwoPi;
		return value;
	}
}
