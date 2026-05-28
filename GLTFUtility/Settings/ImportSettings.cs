using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Events;


namespace Siccity.GLTFUtility {
	[Serializable]
	public class ImportSettings {

		public bool materials = true;
		/* [FormerlySerializedAs removed for IL2CPP build] */
		public ShaderSettings shaderOverrides = new ShaderSettings();
		public AnimationSettings animationSettings = new AnimationSettings();
		public bool generateLightmapUVs;
		/* [Range removed for IL2CPP build] */
		public float hardAngle = 88;

		/* [Range removed for IL2CPP build] */
		public float angleError = 8;

		/* [Range removed for IL2CPP build] */
		public float areaError = 15;

		/* [Range removed for IL2CPP build] */
		public float packMargin = 4;

		/* [Tooltip removed for IL2CPP build] */
		public GLTFExtrasProcessor extrasProcessor;
	}
}
