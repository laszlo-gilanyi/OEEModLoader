using System;
using System.Collections;
using UnityEngine;

namespace Siccity.GLTFUtility {
	public static class Extensions {

		// Async coroutine entry-points are unused in our synchronous plugin flow;
		// stub the runner so it stays out of the IL2CPP MonoBehaviour-injection
		// path (a regular C# IEnumerator can't be passed to MonoBehaviour
		// StartCoroutine in the IL2CPP-wrapped Unity API).
		public static object RunCoroutine(this IEnumerator ienum) {
			throw new NotSupportedException("Async GLB loading is not supported in this build; use LoadFromBytes (sync) instead.");
		}

		public static T[] SubArray<T>(this T[] data, int index, int length) {
			T[] result = new T[length];
			Array.Copy(data, index, result, 0, length);
			return result;
		}

		public static void UnpackTRS(this Matrix4x4 trs, ref Vector3 position, ref Quaternion rotation, ref Vector3 scale) {
			position = trs.GetColumn(3);
			position.x = -position.x;
			rotation = trs.rotation;
			rotation = new Quaternion(rotation.x, -rotation.y, -rotation.z, rotation.w);
			scale = trs.lossyScale;
		}
	}
}