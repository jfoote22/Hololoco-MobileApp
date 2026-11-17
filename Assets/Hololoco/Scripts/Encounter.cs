// The AR encounter. The item's own 3D model stands on the ground in the real world, on
// a ring in its rarity colour. It is placed once, in world space, and never moved after
// that: AR tracking moves the camera as the player walks, and the item stays put.
//
//   A first find   (Open in AR on the map) goes where it is on the map. Its direction and
//                  distance come from GPS, turned into the AR world with the compass, and
//                  a beam of light rises from it so it can be spotted from afar. When it's
//                  closer than GPS can be wrong (indoors that's 10 m or more), it goes
//                  right in front of the player instead. (ARCore Geospatial, in
//                  Scripts/Later, will make this precise once there's an ARCore key.)
//   Anything else  (the bag's View in AR, or See it in AR on your own or collected drops)
//                  goes right in front of the player.
//
// First it waits a moment for AR tracking, the floor and (from the map) the compass,
// asking the player to point the phone at the ground a few steps ahead.

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
        public ARPlaneManager Planes;
        public Location Location;

        /// <summary>How far ahead an item goes when it's placed in front of the player.</summary>
        const float FrontDistance = 1.6f;
        /// <summary>Where the floor is assumed to be, below the phone, when no floor has been found.</summary>
        const float EyeToFloor = 1.3f;
        /// <summary>Farther than this, it's brought in to this distance (same direction) so it can be seen.</summary>
        const float Farthest = 30f;

        Transform root;
        Transform item;
        Transform ring;
        Material ringMaterial;
        Transform beam;
        Material beamMaterial;
        DropInfo drop;
        int run;
        Vector3 lastLogged;

        public bool Showing => root != null || Finding;
        /// <summary>Getting ready: waiting for AR tracking, the floor and (from the map) the compass.</summary>
        public bool Finding { get; private set; }
        /// <summary>What to tell the player while finding.</summary>
        public string FindingWords { get; private set; } = "Starting the camera…";
        /// <summary>Where the item stands in the world, once it's placed.</summary>
        public Vector3? Position => root != null ? root.position : null;

        /// <summary>Whether a collider belongs to the drop on show (for tap to capture).</summary>
        public bool IsPart(Collider c) => root != null && c != null && c.transform.IsChildOf(root);

        /// <summary>Shows a drop: at its place on the map (atItsSpot), or right in front of the player.</summary>
        public IEnumerator Show(DropInfo shown, bool atItsSpot)
        {
            Clear();
            var mine = run;
            drop = shown;
            Finding = true;
            if (Planes != null) Planes.requestedDetectionMode = PlaneDetectionMode.Horizontal;

            // 1. AR tracking: the camera has to follow the phone before anything goes in the world.
            FindingWords = "Starting the camera…";
            var started = Time.time;
            while (!Tracking() && Time.time - started < 8f)
            {
                yield return null;
                if (mine != run) yield break;
            }
            if (!Tracking()) Debug.Log($"HOLOLOCO AR not tracking after 8 s ({ARSession.state}, {ARSession.notTrackingReason}); placing anyway");

            // 2. The floor, and which way north is in the AR world. Both come quickest with
            // the phone pointed at the ground a few steps ahead.
            FindingWords = "Point your phone at the ground a few steps ahead";
            var norths = new List<float>();
            float? floor = null;
            started = Time.time;
            var wait = atItsSpot ? 6f : 2.5f;
            while (Time.time - started < wait)
            {
                floor = FloorHeight();
                if (atItsSpot) SampleNorth(norths);
                var ready = floor != null && (!atItsSpot || norths.Count >= 20);
                if (ready && Time.time - started > 0.5f) break;
                yield return null;
                if (mine != run) yield break;
            }

            // 3. Place it, once.
            var cam = Camera.transform;
            Vector3 spot;
            string how;
            var offset = atItsSpot && Location != null && Location.HasFix
                ? Geo.Offset(Location.Lat, Location.Lng, drop.Lat, drop.Lng)
                : Vector2.zero;
            // GPS can be this far out (indoors, ±10 m is normal). A drop closer than that could
            // be in any direction, so it goes in front: "it's right around here" is all GPS knows.
            var unsure = Location != null ? Mathf.Max(Location.Accuracy, 5f) * 1.5f : float.MaxValue;
            if (atItsSpot && offset.magnitude > unsure)
            {
                var northYaw = norths.Count > 0 ? MeanAngle(norths) : Mathf.DeltaAngle(0f, Yaw(cam) - Input.compass.trueHeading);
                var north = Quaternion.Euler(0f, northYaw, 0f) * Vector3.forward;
                var east = Quaternion.Euler(0f, northYaw + 90f, 0f) * Vector3.forward;
                var flat = east * offset.x + north * offset.y;
                if (flat.magnitude > Farthest) flat *= Farthest / flat.magnitude;
                spot = cam.position + flat;
                how = $"at its spot: {offset.x:0.0} m east, {offset.y:0.0} m north (GPS ±{Location.Accuracy:0} m, {norths.Count} compass readings)";
            }
            else
            {
                spot = cam.position + Ahead() * FrontDistance;
                how = atItsSpot ? $"in front (its spot is {offset.magnitude:0.0} m away, within GPS error of {unsure:0} m)" : "in front";
            }
            spot.y = floor ?? cam.position.y - EyeToFloor;

            root = new GameObject("encounter").transform;
            root.SetPositionAndRotation(spot, Facing(spot));
            ring = Ring(root, Gem.ColourFor(drop), out ringMaterial, 0.45f);
            beam = Beam(root, Gem.ColourFor(drop), out beamMaterial);
            Finding = false;
            lastLogged = cam.position;
            Debug.Log($"HOLOLOCO AR placed {how}, {Vector3.Distance(spot, cam.position):0.0} m from the camera, " +
                      $"{(floor != null ? "on the floor" : "floor not found")}; camera at {cam.position:F2}, tracking {ARSession.state}");
            yield return Reveal(drop.Json, SizeFor(drop), true);
        }

        /// <summary>For the bag's "View in AR": the item itself, right in front of the player.</summary>
        public IEnumerator ShowItem(JToken shown)
        {
            yield return Show(new DropInfo { Id = "item", Type = (string)shown.At("type") ?? "object", Rarity = (string)shown.At("offer").At("rarity"), Size = 1.2f, Json = shown }, false);
        }

        /// <summary>AR is tracking and has moved the camera (it starts exactly at the origin).</summary>
        bool Tracking() => ARSession.state == ARSessionState.SessionTracking
                           && (Camera.transform.localPosition != Vector3.zero || Camera.transform.localRotation != Quaternion.identity);

        /// <summary>The height of the lowest horizontal surface well below the phone: the floor or the ground.</summary>
        float? FloorHeight()
        {
            if (Planes == null) return null;
            float? floor = null;
            var eye = Camera.transform.position.y;
            foreach (var plane in Planes.trackables)
            {
                if (plane.alignment != PlaneAlignment.HorizontalUp || plane.trackingState != TrackingState.Tracking) continue;
                var y = plane.transform.position.y;
                if (eye - y < 0.5f) continue; // a table or a shelf, not the floor
                if (floor == null || y < floor) floor = y;
            }
            return floor;
        }

        /// <summary>
        /// One reading of which way north is in the AR world: the camera's heading in the AR
        /// world minus its compass heading. The compass reads the way the top of the phone
        /// points, which matches the camera only with the phone tilted back (camera looking
        /// down); upright or face down it's unreliable, so those moments are skipped.
        /// </summary>
        void SampleNorth(List<float> norths)
        {
            if (!Input.compass.enabled) Input.compass.enabled = true;
            if (Input.compass.timestamp <= 0) return;
            var cam = Camera.transform;
            var fromDown = Vector3.Angle(cam.forward, Vector3.down);
            if (fromDown < 10f || fromDown > 75f) return;
            norths.Add(Mathf.DeltaAngle(0f, Yaw(cam) - Input.compass.trueHeading));
        }

        static float Yaw(Transform t) => Mathf.Atan2(t.forward.x, t.forward.z) * Mathf.Rad2Deg;

        static float MeanAngle(List<float> angles)
        {
            float s = 0, c = 0;
            foreach (var a in angles)
            {
                s += Mathf.Sin(a * Mathf.Deg2Rad);
                c += Mathf.Cos(a * Mathf.Deg2Rad);
            }
            return Mathf.Atan2(s, c) * Mathf.Rad2Deg;
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

        /// <summary>A model's size in AR, in metres: offers and messages hand-sized, objects as placed but kept walkable.</summary>
        static float SizeFor(DropInfo d) => d.Type switch
        {
            "object" => Mathf.Clamp(d.Size * 0.5f, 0.5f, 1.4f),
            "message" => 0.45f,
            _ => 0.55f,
        };

        /// <summary>The capture: the item shrinks toward the camera, into the bag, and the ring fades.</summary>
        public IEnumerator Captured(JToken captured)
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

        IEnumerator Reveal(JToken json, float size, bool pop)
        {
            var holder = new GameObject("item").transform;
            holder.SetParent(root, false);
            item = holder;
            var task = Models.LoadInto(Models.UrlFor(json), holder, size, 0);
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
        Transform Ring(Transform parent, Color colour, out Material material, float halfSize)
        {
            var go = new GameObject("ring");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0, 0.005f, 0);
            var mesh = new Mesh();
            var h = halfSize;
            mesh.SetVertices(new[] { new Vector3(-h, 0, -h), new Vector3(-h, 0, h), new Vector3(h, 0, h), new Vector3(h, 0, -h) });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            material = new Material(Resources.Load<Material>("Materials/Glow")) { mainTexture = MapView.RingTexture() };
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetColor("_Color", colour);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go.transform;
        }

        /// <summary>
        /// A tall column of light rising from the item, in its rarity colour, so it can be
        /// spotted from across a room or a street. It fades away as the player gets close.
        /// </summary>
        Transform Beam(Transform parent, Color colour, out Material material)
        {
            var go = new GameObject("beam");
            go.transform.SetParent(parent, false);
            const float w = 0.14f, h = 4f;
            var mesh = new Mesh();
            mesh.SetVertices(new[] { new Vector3(-w, 0, 0), new Vector3(-w, h, 0), new Vector3(w, h, 0), new Vector3(w, 0, 0) });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            material = new Material(Resources.Load<Material>("Materials/Glow")) { mainTexture = BeamTexture() };
            colour.a = 0f;
            material.SetColor("_Color", colour);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go.transform;
        }

        static Texture2D beamTexture;

        /// <summary>White, bright at the bottom and the middle, fading to nothing at the top and sides.</summary>
        static Texture2D BeamTexture()
        {
            if (beamTexture != null) return beamTexture;
            const int width = 16, height = 64;
            beamTexture = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var across = 1f - Mathf.Pow(Mathf.Abs((x + 0.5f) / width * 2f - 1f), 2f);
                var up = Mathf.Pow(1f - y / (height - 1f), 1.6f);
                beamTexture.SetPixel(x, y, new Color(1, 1, 1, across * up));
            }
            beamTexture.Apply();
            return beamTexture;
        }

        void FadeRing(float alpha)
        {
            if (ringMaterial == null) return;
            var c = ringMaterial.GetColor("_Color");
            c.a = alpha;
            ringMaterial.SetColor("_Color", c);
            if (alpha < 1f && beam != null) beam.gameObject.SetActive(false);
        }

        void Update()
        {
            if (item != null) item.Rotate(Vector3.up, 20f * Time.deltaTime, Space.Self);
            // A slow breath on the ring so it reads as a marker, not a sticker.
            if (ring != null) ring.localScale = Vector3.one * (1f + Mathf.Sin(Time.time * 2f) * 0.04f);
            // The beam turns to face the camera, and shows only from a few metres away.
            if (beam != null && beamMaterial != null)
            {
                var toCamera = Vector3.ProjectOnPlane(beam.position - Camera.transform.position, Vector3.up);
                if (toCamera.sqrMagnitude > 0.01f) beam.rotation = Quaternion.LookRotation(toCamera.normalized, Vector3.up);
                var c = beamMaterial.GetColor("_Color");
                c.a = Mathf.Clamp01((toCamera.magnitude - 3f) / 3f) * 0.8f;
                beamMaterial.SetColor("_Color", c);
            }

            // For testing on the phone: the camera should move as the player walks while the
            // item's position stays the same.
            if (root != null && Vector3.Distance(Camera.transform.position, lastLogged) > 0.75f)
            {
                lastLogged = Camera.transform.position;
                Debug.Log($"HOLOLOCO AR camera {lastLogged:F2}, item {root.position:F2}, {Vector3.Distance(lastLogged, root.position):0.0} m apart");
            }
        }

        public void Clear()
        {
            run++;
            if (root != null) Destroy(root.gameObject);
            if (ringMaterial != null) Destroy(ringMaterial);
            if (beamMaterial != null) Destroy(beamMaterial);
            root = null;
            item = null;
            ring = null;
            ringMaterial = null;
            beam = null;
            beamMaterial = null;
            Finding = false;
        }
    }
}
