// The AR encounter. When the player opens a drop nearby, the app looks for a
// surface ahead (ARCore's horizontal planes: the floor, a table), anchors the drop
// there, and shows the item's own 3D model standing on a ring in its rarity colour.
// The anchor keeps it locked to that spot as the player walks around it. If no
// surface turns up within a few seconds, the item appears in view about 1.2 m ahead
// while the app keeps looking; as soon as a surface is found it settles onto it and
// locks there.
//
// This places the drop where the player is, not at its exact map position: that
// needs ARCore Geospatial (Scripts/Later).

using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Hololoco
{
    public class Encounter : MonoBehaviour
    {
        public Camera Camera;
        public ARSession Session;
        public ARRaycastManager Raycasts;
        public ARPlaneManager Planes;

        /// <summary>How long to look for a surface before showing the drop in front of the player.</summary>
        public float SearchSeconds = 4f;
        /// <summary>How long to keep looking after that, to settle it onto a surface.</summary>
        public float KeepLookingSeconds = 20f;
        const float NearestSurface = 0.25f, FarthestSurface = 10f;

        Transform root;
        Transform item;
        Transform ring;
        Material ringMaterial;
        float itemSpin = 20f;
        readonly List<ARRaycastHit> hits = new();

        public bool Showing => root != null;
        /// <summary>True while looking for the ground, before the drop appears.</summary>
        public bool Searching { get; private set; }
        /// <summary>True when the drop is anchored to a surface ARCore found.</summary>
        public bool Locked { get; private set; }

        /// <summary>Whether a collider belongs to the drop on show (for tap to capture).</summary>
        public bool IsPart(Collider c) => root != null && c != null && c.transform.IsChildOf(root);

        /// <summary>Finds the ground, anchors the drop there and shows its model.</summary>
        public IEnumerator Show(DropInfo drop)
        {
            Clear();
            if (Planes != null) Planes.requestedDetectionMode = PlaneDetectionMode.Horizontal;
            var waited = 0f;
            while (ARSession.state < ARSessionState.SessionTracking && waited < 3f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Debug.Log($"HOLOLOCO AR session {ARSession.state}, planes so far {(Planes != null ? Planes.trackables.count : -1)}");

            // Look for a surface a little below the middle of the screen.
            Searching = true;
            Pose? ground = null;
            for (var t = 0f; t < SearchSeconds && ground == null; t += Time.deltaTime)
            {
                ground = FindSurface();
                yield return null;
            }
            Searching = false;

            var cam = Camera.transform;
            var ahead = Ahead();
            var position = ground?.position ?? cam.position + ahead * 1.2f + Vector3.down * 0.5f;
            var anchorObject = new GameObject("encounter");
            anchorObject.transform.SetPositionAndRotation(position, Facing(position));
            root = anchorObject.transform;
            if (ground != null) Lock();
            Debug.Log($"HOLOLOCO AR placed {(Locked ? "on a surface" : "in front")} {Vector3.Distance(position, cam.position):0.0} m away; planes {(Planes != null ? Planes.trackables.count : -1)}");

            ring = Ring(root, Gem.ColourFor(drop));
            if (!Locked) StartCoroutine(KeepLooking(root));
            yield return Reveal(drop.Json, SizeFor(drop), true);
        }

        /// <summary>A surface under a point a little below the middle of the screen, at a sensible distance.</summary>
        Pose? FindSurface()
        {
            if (Raycasts == null) return null;
            var point = new Vector2(Screen.width * 0.5f, Screen.height * 0.4f);
            if (!Raycasts.Raycast(point, hits, TrackableType.PlaneWithinPolygon | TrackableType.PlaneEstimated)) return null;
            foreach (var hit in hits)
            {
                var distance = Vector3.Distance(hit.pose.position, Camera.transform.position);
                if (distance >= NearestSurface && distance <= FarthestSurface) return hit.pose;
            }
            return null;
        }

        Vector3 Ahead()
        {
            var cam = Camera.transform;
            var ahead = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            if (ahead.sqrMagnitude < 0.01f) ahead = Vector3.ProjectOnPlane(cam.up, Vector3.up);
            return ahead.normalized;
        }

        /// <summary>Turned to face the player.</summary>
        Quaternion Facing(Vector3 position)
        {
            var toPlayer = Vector3.ProjectOnPlane(Camera.transform.position - position, Vector3.up);
            return toPlayer.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toPlayer.normalized, Vector3.up) : Quaternion.identity;
        }

        void Lock()
        {
            if (root == null || Locked) return;
            root.gameObject.AddComponent<ARAnchor>();
            Locked = true;
        }

        /// <summary>After showing the drop in front of the player, settles it onto the first surface found.</summary>
        IEnumerator KeepLooking(Transform placed)
        {
            for (var t = 0f; t < KeepLookingSeconds && root == placed && !Locked; t += Time.deltaTime)
            {
                var surface = FindSurface();
                if (surface != null)
                {
                    var from = root.position;
                    var to = surface.Value.position;
                    for (var k = 0f; k < 1f && root == placed; k += Time.deltaTime / 0.4f)
                    {
                        root.position = Vector3.Lerp(from, to, k * k * (3 - 2 * k));
                        yield return null;
                    }
                    if (root != placed) yield break;
                    root.SetPositionAndRotation(to, Facing(to));
                    Lock();
                    Debug.Log($"HOLOLOCO AR settled onto a surface {Vector3.Distance(to, Camera.transform.position):0.0} m away");
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>A model's size in AR, in metres: offers and messages hand-sized, objects as placed but kept walkable.</summary>
        static float SizeFor(DropInfo drop) => drop.Type switch
        {
            "object" => Mathf.Clamp(drop.Size * 0.5f, 0.5f, 1.4f),
            "message" => 0.45f,
            _ => 0.55f,
        };

        /// <summary>The capture: the item shrinks toward the camera, into the bag, and the ring fades.</summary>
        public IEnumerator Captured(JToken drop)
        {
            if (root == null) yield break;
            if (item != null)
            {
                var start = item.position;
                var startScale = item.localScale;
                for (var t = 0f; t < 0.6f && item != null; t += Time.deltaTime)
                {
                    var k = t / 0.6f;
                    item.position = Vector3.Lerp(start, Camera.transform.position + Vector3.down * 0.3f, k * k);
                    item.localScale = startScale * (1 - k);
                    FadeRing(1 - k);
                    yield return null;
                }
                if (item != null) Destroy(item.gameObject);
                item = null;
            }
            FadeRing(0);
        }

        /// <summary>Leaving it: the item sinks into its ring and fades away.</summary>
        public IEnumerator Left()
        {
            if (item != null)
            {
                var startScale = item.localScale;
                for (var t = 0f; t < 0.45f && item != null; t += Time.deltaTime)
                {
                    var k = t / 0.45f;
                    item.localScale = startScale * (1 - k);
                    FadeRing(1 - k);
                    yield return null;
                }
            }
            Clear();
        }

        /// <summary>For the bag's "View in AR": the item itself, placed the same way.</summary>
        public IEnumerator ShowItem(JToken drop)
        {
            yield return Show(new DropInfo { Id = "item", Type = (string)drop.At("type") ?? "object", Rarity = (string)drop.At("offer").At("rarity"), Size = 1.2f, Json = drop });
        }

        IEnumerator Reveal(JToken drop, float size, bool pop)
        {
            var holder = new GameObject("item").transform;
            holder.SetParent(root, false);
            item = holder;
            var task = Models.LoadInto(Models.UrlFor(drop), holder, size, 0);
            while (!task.IsCompleted) yield return null;
            if (holder == null) yield break;
            Debug.Log($"HOLOLOCO AR model {(task.Result > 0 ? $"loaded, {task.Result:0.00} m tall" : "failed")}");

            // Something to tap: a box around the model.
            var height = Mathf.Max(task.Result, 0.2f);
            var box = holder.gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0, height / 2f, 0);
            box.size = new Vector3(size, height, size);

            if (pop)
            {
                for (var t = 0f; t < 0.35f && holder != null; t += Time.deltaTime)
                {
                    var k = t / 0.35f;
                    holder.localScale = Vector3.one * (1f + Mathf.Sin(k * Mathf.PI) * 0.2f) * k;
                    yield return null;
                }
                if (holder != null) holder.localScale = Vector3.one;
            }
        }

        /// <summary>A flat ring on the ground under the drop, in its rarity colour.</summary>
        Transform Ring(Transform parent, Color colour)
        {
            var go = new GameObject("ring");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0, 0.005f, 0);
            var mesh = new Mesh();
            const float h = 0.45f;
            mesh.SetVertices(new[] { new Vector3(-h, 0, -h), new Vector3(-h, 0, h), new Vector3(h, 0, h), new Vector3(h, 0, -h) });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            ringMaterial = new Material(Resources.Load<Material>("Materials/Glow")) { mainTexture = MapView.RingTexture() };
            ringMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            ringMaterial.SetColor("_Color", colour);
            go.AddComponent<MeshRenderer>().sharedMaterial = ringMaterial;
            return go.transform;
        }

        void FadeRing(float alpha)
        {
            if (ringMaterial == null) return;
            var c = ringMaterial.GetColor("_Color");
            c.a = alpha;
            ringMaterial.SetColor("_Color", c);
        }

        void Update()
        {
            if (item != null) item.Rotate(Vector3.up, itemSpin * Time.deltaTime, Space.Self);
            // A slow breath on the ring so it reads as a marker, not a sticker.
            if (ring != null) ring.localScale = Vector3.one * (1f + Mathf.Sin(Time.time * 2f) * 0.04f);
        }

        public void Clear()
        {
            if (root != null) Destroy(root.gameObject);
            if (ringMaterial != null) Destroy(ringMaterial);
            root = null;
            item = null;
            ring = null;
            ringMaterial = null;
            Searching = false;
            Locked = false;
        }
    }
}
