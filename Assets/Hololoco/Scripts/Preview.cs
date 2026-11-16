// The bag's 3D preview: the selected item turning slowly, rendered by its own
// camera far from everything else into a texture the bag screen shows.

using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Hololoco
{
    public class Preview : MonoBehaviour
    {
        public const int Layer = 9;
        public RenderTexture Texture { get; private set; }

        Camera cam;
        Transform stage;
        Transform current;
        string currentUrl;

        void Awake()
        {
            transform.position = new Vector3(0, -2000, 0);
            Texture = new RenderTexture(512, 512, 24) { antiAliasing = 2 };
            var camObject = new GameObject("preview camera");
            camObject.transform.SetParent(transform, false);
            camObject.transform.localPosition = new Vector3(0, 0.55f, -1.6f);
            camObject.transform.localRotation = Quaternion.Euler(12, 0, 0);
            cam = camObject.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.1f, 0.16f, 1f);
            cam.cullingMask = 1 << Layer;
            cam.targetTexture = Texture;
            cam.fieldOfView = 35;
            cam.enabled = false;
            stage = new GameObject("stage").transform;
            stage.SetParent(transform, false);
        }

        public void SetVisible(bool visible) => cam.enabled = visible;

        public async void Show(JToken drop)
        {
            var url = Models.UrlFor(drop);
            if (url == currentUrl) return;
            currentUrl = url;
            if (current != null) Destroy(current.gameObject);
            var holder = new GameObject("item") { layer = Layer }.transform;
            holder.SetParent(stage, false);
            current = holder;
            await Models.LoadInto(url, holder, 0.9f, Layer);
        }

        void Update()
        {
            if (cam.enabled) stage.Rotate(Vector3.up, 35f * Time.deltaTime, Space.Self);
        }

        void OnDestroy()
        {
            if (Texture != null) Texture.Release();
        }
    }
}
