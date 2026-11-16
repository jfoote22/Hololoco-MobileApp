// Loading a drop's 3D model (glTF/GLB): a ready-made object from the website, or
// the business's upload. Used by the AR reveal and the bag's preview.

using System.Threading.Tasks;
using GLTFast;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Hololoco
{
    public static class Models
    {
        /// <summary>The GLB for a drop as drop_to_json shows it (a nearby drop, or an item's snapshot).</summary>
        public static string UrlFor(JToken drop)
        {
            var asset = (string)drop.At("asset").At("model");
            var objectId = (string)drop.At("objectId") ?? "gift";
            return asset != null
                ? $"{Config.SupabaseUrl}/storage/v1/object/public/drop-assets/{asset}"
                : $"{Config.SiteUrl}/library/{objectId}.glb";
        }

        /// <summary>
        /// Loads a model under `parent`, centred, standing on its base, scaled so its
        /// widest side is `size` metres. Puts it on `layer`. Returns its height, or -1 if it failed.
        /// </summary>
        public static async Task<float> LoadInto(string url, Transform parent, float size, int layer)
        {
            var root = new GameObject("model") { layer = layer }.transform;
            var inner = new GameObject("geometry") { layer = layer }.transform;
            inner.SetParent(root, false);
            var import = new GltfImport();
            var ok = await import.Load(url) && await import.InstantiateMainSceneAsync(inner);
            if (parent == null)
            {
                Object.Destroy(root.gameObject);
                import.Dispose();
                return -1;
            }
            if (!ok)
            {
                Debug.LogWarning($"HOLOLOCO couldn't load {url}");
                Object.Destroy(root.gameObject);
                import.Dispose();
                return -1;
            }

            var bounds = new Bounds();
            var first = true;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                r.gameObject.layer = layer;
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);
            }
            var widest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            var scale = size / Mathf.Max(widest, 0.001f);
            inner.localPosition = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            root.localScale = Vector3.one * scale;
            root.SetParent(parent, false);
            // The import owns the meshes and textures: free them with the model.
            root.gameObject.AddComponent<ImportOwner>().Import = import;
            return bounds.size.y * scale;
        }

        class ImportOwner : MonoBehaviour
        {
            public GltfImport Import;
            void OnDestroy() => Import?.Dispose();
        }
    }
}
