namespace Editor.CitizenRetarget;

internal static class RetargetSampleNormalizer
{
	public static void NormalizeToInternalCentimeters( NativeSampleResult sample )
	{
		var sourceScale = RetargetMath.CentimetersPerUnit( sample.SourceOutputUnitMeters );
		var targetScale = RetargetMath.CentimetersPerUnit( sample.TargetOutputUnitMeters );

		foreach ( var bone in sample.SourceBones )
			ScaleTranslationArray( bone.Translation, sourceScale );
		foreach ( var bone in sample.TargetBones )
			ScaleTranslationArray( bone.Translation, targetScale );
		foreach ( var frame in sample.Frames )
		{
			foreach ( var translation in frame.Translations )
				ScaleTranslationArray( translation, sourceScale );
		}
	}

	private static void ScaleTranslationArray( float[] values, float scale )
	{
		if ( values is null || values.Length < 3 || MathF.Abs( scale - 1f ) < 0.000001f )
			return;

		values[0] *= scale;
		values[1] *= scale;
		values[2] *= scale;
	}
}
