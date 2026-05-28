using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Scripting;

namespace Siccity.GLTFUtility {
	// https://github.com/KhronosGroup/glTF/blob/master/specification/2.0/README.md#image
	/* [Preserve removed for IL2CPP build] */ public class GLTFImage {
		/// <summary>
		/// The uri of the image.
		/// Relative paths are relative to the .gltf file.
		/// Instead of referencing an external file, the uri can also be a data-uri.
		/// The image format must be jpg or png.
		/// </summary>
		public string uri;
		/// <summary> Either "image/jpeg" or "image/png" </summary>
		public string mimeType;
		public int? bufferView;
		public string name;

		public class ImportResult {
			public byte[] bytes;
			public string path;

			public ImportResult(byte[] bytes, string path = null) {
				this.bytes = bytes;
				this.path = path;
			}

			public IEnumerator CreateTextureAsync(bool linear, Action<Texture2D> onFinish, Action<float> onProgress = null) {
				// The original code loaded textures over UnityWebRequest when the
				// image referenced an on-disk URI. BepInEx IL2CPP plugins ship
				// without UnityWebRequest references and the GLBs we handle always
				// embed their images in the binary buffer, so the only path we
				// ever exercise is the embedded-bytes case below.
				Texture2D tex = new Texture2D(2, 2, TextureFormat.ARGB32, true, linear);
				if (bytes == null || !ImageConversion.LoadImage(tex, bytes)) {
					Debug.Log("GLTFImage: mimeType not supported or no embedded bytes");
					yield break;
				}
				onFinish(tex);
			}
		}

		public class ImportTask : Importer.ImportTask<ImportResult[]> {
			public ImportTask(List<GLTFImage> images, string directoryRoot, GLTFBufferView.ImportTask bufferViewTask) : base(bufferViewTask) {
				task = new Task(() => {
					// No images
					if (images == null) return;

					Result = new ImportResult[images.Count];
					for (int i = 0; i < images.Count; i++) {
						string fullUri = directoryRoot + images[i].uri;
						if (!string.IsNullOrEmpty(images[i].uri)) {
							if (File.Exists(fullUri)) {
								// If the file is found at fullUri, read it
								byte[] bytes = File.ReadAllBytes(fullUri);
								Result[i] = new ImportResult(bytes, fullUri);
							} else if (images[i].uri.StartsWith("data:")) {
								// If the image is embedded, find its Base64 content and save as byte array
								string content = images[i].uri.Split(',').Last();
								byte[] imageBytes = Convert.FromBase64String(content);
								Result[i] = new ImportResult(imageBytes);
							}
						} else if (images[i].bufferView.HasValue && !string.IsNullOrEmpty(images[i].mimeType)) {
							GLTFBufferView.ImportResult view = bufferViewTask.Result[images[i].bufferView.Value];
							byte[] bytes = new byte[view.byteLength];
							view.stream.Position = view.byteOffset;
							view.stream.Read(bytes, 0, view.byteLength);
							Result[i] = new ImportResult(bytes);
						} else {
							Debug.Log("Couldn't find texture at " + fullUri);
						}
					}
				});
			}
		}
	}
}