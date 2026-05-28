using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Siccity.GLTFUtility {
	/// <summary> Defines which shaders to use in the gltf import process </summary>
	[Serializable]
	public class ShaderSettings {
		/* [SerializeField removed for IL2CPP build] */ private Shader metallic;
		public Shader Metallic { get { return metallic != null ? metallic : GetDefaultMetallic(); } }

		/* [SerializeField removed for IL2CPP build] */ private Shader metallicBlend;
		public Shader MetallicBlend { get { return metallicBlend != null ? metallicBlend : GetDefaultMetallicBlend(); } }

		/* [SerializeField removed for IL2CPP build] */ private Shader specular;
		public Shader Specular { get { return specular != null ? specular : GetDefaultSpecular(); } }

		/* [SerializeField removed for IL2CPP build] */ private Shader specularBlend;
		public Shader SpecularBlend { get { return specularBlend != null ? specularBlend : GetDefaultSpecularBlend(); } }

		/// <summary> Caches default shaders so that async import won't try to search for them while on a separate thread </summary>
		public void CacheDefaultShaders() {
			metallic = Metallic;
			metallicBlend = MetallicBlend;
			specular = Specular;
			specularBlend = SpecularBlend;
		}

		// In a BepInEx IL2CPP plugin the GLTFUtility-bundled shaders are not
		// available and the host game ships its own pipeline (often URP plus
		// custom shaders), so the GLTFUtility lookup names return null. Falling
		// back through a few common shader names keeps Material(...) from
		// throwing; the plugin later overwrites every imported Material with a
		// clone of the vanilla unit's material anyway, so the exact fallback
		// shader is cosmetic only.
		private static Shader Fallback() {
			Shader s = Shader.Find("Universal Render Pipeline/Lit");
			if (s != null) return s;
			s = Shader.Find("Standard");
			if (s != null) return s;
			s = Shader.Find("Sprites/Default");
			if (s != null) return s;
			// Last resort: pick any loaded shader; Resources returns at least the
			// fallback/internal error shader from the runtime.
			var all = Resources.FindObjectsOfTypeAll<Shader>();
			if (all != null && all.Length > 0) return all[0];
			throw new Exception("ShaderSettings.Fallback: no shaders found in runtime.");
		}

		public Shader GetDefaultMetallic()      => Shader.Find("GLTFUtility/Standard (Metallic)") ?? Fallback();
		public Shader GetDefaultMetallicBlend() => Shader.Find("GLTFUtility/Standard Transparent (Metallic)") ?? Fallback();
		public Shader GetDefaultSpecular()      => Shader.Find("GLTFUtility/Standard (Specular)") ?? Fallback();
		public Shader GetDefaultSpecularBlend() => Shader.Find("GLTFUtility/Standard Transparent (Specular)") ?? Fallback();
	}
}