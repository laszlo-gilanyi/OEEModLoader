using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Siccity.GLTFUtility {
	/// <summary> Defines how animations are imported </summary>
	[Serializable]
	public class AnimationSettings {
		public bool looping;
		/* [Tooltip removed for IL2CPP build] */
		public float frameRate = 24;
		/* [Tooltip removed for IL2CPP build] */
		public InterpolationMode interpolationMode = InterpolationMode.ImportFromFile;
		/* [Tooltip removed for IL2CPP build] */
		public bool compressBlendShapeKeyFrames = true;
		/* [Tooltip removed for IL2CPP build] */
		public bool useLegacyClips;
	}
}
