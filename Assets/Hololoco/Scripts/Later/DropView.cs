// One drop in the camera view: its 3D model, sized to its real width (`size`
// metres across), standing on its base, with a collider so a tap can capture it.

using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace Hololoco
{
    public class DropView : MonoBehaviour
    {
        public DropInfo Drop;
        /// <summary>Placed with a Geospatial anchor (accurate) rather than from GPS and compass.</summary>
        public bool Anchored;
        /// <summary>How tall the model is once loaded, in metres.</summary>
        public float ModelHeight;

        /// <summary>Where its label goes: just above the top of the model.</summary>
        public Vector3 LabelPoint => transform.position + transform.up * (Drop.Height + ModelHeight + 0.3f);

        GltfImport import;
        Transform model;

        public async Task Load()
        {
            // Build the model at the world origin, measure it, then move it under this drop.
            var root = new GameObject("model").transform;
            var inner = new GameObject("geometry").transform;
            inner.SetParent(root, false);

            import = new GltfImport();
            var loaded = await import.Load(Drop.ModelUrl) && await import.InstantiateMainSceneAsync(inner);
            if (this == null)
            {
                Destroy(root.gameObject);
                return;
            }
            if (!loaded)
            {
                // The model couldn't load: show a plain marker instead.
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(cube.GetComponent<Collider>());
                cube.transform.SetParent(inner, false);
                cube.transform.localPosition = new Vector3(0, 0.5f, 0);
            }

            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            var first = true;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);
            }
            var across = Mathf.Max(bounds.size.x, bounds.size.z);
            if (across < 0.001f) across = Mathf.Max(bounds.size.y, 1f);
            var scale = Mathf.Clamp(Drop.Size, 0.5f, 30f) / across;

            inner.localPosition = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            root.localScale = Vector3.one * scale;
            root.SetParent(transform, false);
            root.localPosition = Vector3.up * Drop.Height;
            model = root;

            ModelHeight = bounds.size.y * scale;
            Debug.Log($"HOLOLOCO model {Drop.Id} {(loaded ? "loaded" : "failed, showing a marker")}: {bounds.size} scaled x{scale:0.00} to {Drop.Size} m across, {Drop.Height} m up");

            var box = gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0, Drop.Height + bounds.size.y * scale / 2, 0);
            box.size = new Vector3(bounds.size.x * scale, bounds.size.y * scale, bounds.size.z * scale) * 1.2f;
        }

        void Update()
        {
            // Things to collect turn slowly; photos and messages keep facing the way they were placed.
            if (model != null && Drop.Type != "message" && Drop.AssetKind != "photo")
                model.Rotate(Vector3.up, 20f * Time.deltaTime, Space.Self);
        }

        void OnDestroy()
        {
            import?.Dispose();
        }
    }
}
