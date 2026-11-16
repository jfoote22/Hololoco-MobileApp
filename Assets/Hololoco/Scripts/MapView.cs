// The home screen's map: a tilted, dark Mapbox map of the streets around the
// player, with 3D buildings, the player's avatar and pulsing hexagon rings, and a
// gem for each nearby drop. Drag to pan, pinch to zoom, twist to rotate; tap a gem
// to select it. Everything here is on the map layer, seen only by the map camera.
//
// Coordinates: metres east (x) and north (z) of an origin near the player, from
// Web Mercator scaled by cos(latitude), which is accurate over a few kilometres.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Hololoco
{
    public class MapView : MonoBehaviour
    {
        public const int Layer = 8;
        const int Zoom = 16;
        const double EarthRadius = 6378137.0;
        /// <summary>Mapbox's classic look by default; the dark "game map" when switched on.</summary>
        const string LightStyle = "mapbox/streets-v12", DarkStyle = "mapbox/navigation-night-v1";
        public bool GameMap { get; private set; }
        string Style => GameMap ? DarkStyle : LightStyle;

        public Camera Camera;
        public bool ShowBuildings = true;
        /// <summary>Set by the app: whether a screen point is over its interface.</summary>
        public Func<Vector2, bool> IsOverUi = _ => false;
        public event Action<DropInfo> Tapped;

        // Camera
        public float Distance = 320f;
        /// <summary>How far the player can see drops, in metres. The pulse shows it.</summary>
        public float SightRadius = 300f;
        public float Pitch = 52f;
        public float Bearing;
        bool following = true;
        Vector3 focus;

        // The world
        bool hasOrigin;
        double originX, originY, scale;
        // Where GPS and the compass say the player is, and the smoothed values the map shows.
        Vector3 player, playerTarget;
        float heading, headingTarget;
        bool hasPlayer;
        Transform avatar, pointer;
        readonly List<Transform> rings = new();
        readonly Dictionary<(int, int), GameObject> ground = new();
        readonly Dictionary<(int, int), GameObject> buildings = new();
        readonly Dictionary<string, (Gem gem, DropInfo drop)> gems = new();
        LineRenderer route;
        (int, int) centreTile = (-1, -1);

        // Gestures
        Vector2 gestureStart;
        float gestureStartTime;
        bool gestureOnUi, moved;

        Material groundMaterial, buildingMaterial, glowMaterial, avatarMaterial;
        string token;

        void Awake()
        {
            groundMaterial = Resources.Load<Material>("Materials/Tile");
            buildingMaterial = new Material(Resources.Load<Material>("Materials/Building"));
            glowMaterial = new Material(Resources.Load<Material>("Materials/Glow"));
            token = Config.MapboxToken;
            BuildAvatar();
        }

        // ------------------------------------------------------------------ positions

        static (double, double) Mercator(double lat, double lng) =>
            (lng * Math.PI / 180.0 * EarthRadius,
             Math.Log(Math.Tan(Math.PI / 4.0 + lat * Math.PI / 360.0)) * EarthRadius);

        public Vector3 World(double lat, double lng)
        {
            var (mx, my) = Mercator(lat, lng);
            return new Vector3((float)((mx - originX) * scale), 0, (float)((my - originY) * scale));
        }

        (double, double) MercatorOf(Vector3 world) => (originX + world.x / scale, originY + world.z / scale);

        static (int, int) TileOf(double mx, double my)
        {
            var n = 1 << Zoom;
            var x = (int)Math.Floor((mx + Math.PI * EarthRadius) / (2 * Math.PI * EarthRadius) * n);
            var y = (int)Math.Floor((Math.PI * EarthRadius - my) / (2 * Math.PI * EarthRadius) * n);
            return (x, y);
        }

        (double minX, double minY, double size) TileBounds(int x, int y)
        {
            var size = 2 * Math.PI * EarthRadius / (1 << Zoom);
            var minX = -Math.PI * EarthRadius + x * size;
            var maxY = Math.PI * EarthRadius - y * size;
            return (minX, maxY - size, size);
        }

        // ------------------------------------------------------------------ the player and drops

        public void SetPlayer(double lat, double lng, float compassHeading)
        {
            if (!hasOrigin)
            {
                hasOrigin = true;
                (originX, originY) = Mercator(lat, lng);
                scale = Math.Cos(lat * Math.PI / 180.0);
                focus = Vector3.zero;
            }
            playerTarget = World(lat, lng);
            // Ignore the compass's small wobbles; follow real turns.
            if (Mathf.Abs(Mathf.DeltaAngle(headingTarget, compassHeading)) > 3f) headingTarget = compassHeading;
            if (!hasPlayer)
            {
                hasPlayer = true;
                player = playerTarget;
                heading = headingTarget = compassHeading;
            }
            if (playerTarget.magnitude > 5000f) Reorigin(lat, lng);
        }

        void Reorigin(double lat, double lng)
        {
            hasOrigin = false;
            foreach (var g in ground.Values) Destroy(g);
            foreach (var b in buildings.Values) Destroy(b);
            ground.Clear();
            buildings.Clear();
            centreTile = (-1, -1);
            hasPlayer = false;
            SetPlayer(lat, lng, headingTarget);
            foreach (var (gem, drop) in gems.Values) gem.transform.position = World(drop.Lat, drop.Lng);
            SetRoute(null);
        }

        public void ShowDrops(IList<DropInfo> drops)
        {
            if (!hasOrigin) return;
            var keep = new HashSet<string>();
            foreach (var drop in drops)
            {
                keep.Add(drop.Id);
                if (!gems.TryGetValue(drop.Id, out var entry))
                {
                    var gem = Gem.Create(drop, transform, 1f, Layer);
                    entry = (gem, drop);
                }
                entry.drop = drop;
                entry.gem.transform.position = World(drop.Lat, drop.Lng);
                gems[drop.Id] = entry;
            }
            foreach (var id in new List<string>(gems.Keys))
            {
                if (keep.Contains(id)) continue;
                Destroy(gems[id].gem.gameObject);
                gems.Remove(id);
            }
        }

        public void Recenter()
        {
            following = true;
        }

        /// <summary>Draws a walking route (lat, lng pairs), or clears it.</summary>
        public void SetRoute(IList<Vector2> latLngs)
        {
            if (latLngs == null || latLngs.Count < 2)
            {
                if (route != null) route.positionCount = 0;
                return;
            }
            if (route == null)
            {
                var go = new GameObject("route") { layer = Layer };
                go.transform.SetParent(transform, false);
                route = go.AddComponent<LineRenderer>();
                route.sharedMaterial = glowMaterial; // coloured by the map look
                route.textureMode = LineTextureMode.Stretch;
                route.alignment = LineAlignment.View;
                route.numCapVertices = 4;
                route.numCornerVertices = 4;
                route.startColor = route.endColor = Color.white;
            }
            route.positionCount = latLngs.Count;
            for (var i = 0; i < latLngs.Count; i++)
                route.SetPosition(i, World(latLngs[i].x, latLngs[i].y) + Vector3.up * 1.5f);
        }

        // ------------------------------------------------------------------ every frame

        void Update()
        {
            if (!hasOrigin || Camera == null || !Camera.enabled) return;
            HandleGestures();
            // Glide to each new GPS reading over about a second, and turn the arrow with a little lag,
            // so the avatar doesn't jitter. A big jump (a fresh fix far away) is taken at once.
            if ((playerTarget - player).magnitude > 150f) player = playerTarget;
            else player = Vector3.Lerp(player, playerTarget, 1f - Mathf.Exp(-Time.deltaTime / 0.9f));
            heading = Mathf.LerpAngle(heading, headingTarget, 1f - Mathf.Exp(-Time.deltaTime / 0.35f));
            if (following) focus = Vector3.Lerp(focus, player, 1f - Mathf.Exp(-6f * Time.deltaTime));

            var rotation = Quaternion.Euler(Pitch, Bearing, 0);
            Camera.transform.position = focus - rotation * Vector3.forward * Distance;
            Camera.transform.rotation = rotation;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Camera.backgroundColor;
            RenderSettings.fogStartDistance = Distance * 1.3f;
            RenderSettings.fogEndDistance = Distance * 3.6f;

            // Things on the map keep a readable size at any zoom.
            var unit = Mathf.Clamp(Distance * 0.045f, 4f, 45f);
            avatar.position = player;
            avatar.localScale = Vector3.one * unit;
            pointer.rotation = Quaternion.Euler(0, heading, 0);
            // One pulse every two seconds, sweeping out to the edge of what the player can see.
            for (var i = 0; i < rings.Count; i++)
            {
                var t = Mathf.Repeat(Time.time / 2f, 1f);
                var radius = Mathf.Lerp(unit * 0.6f, SightRadius, 1f - (1f - t) * (1f - t));
                rings[i].localScale = Vector3.one * (2f * radius / unit);
                var ringColour = GameMap ? new Color(0.3f, 0.95f, 1f) : new Color(0f, 0.55f, 0.7f);
                ringColour.a = (1 - t) * (GameMap ? 0.8f : 0.7f);
                rings[i].GetComponent<Renderer>().material.SetColor("_Color", ringColour);
            }
            foreach (var (gem, _) in gems.Values) gem.transform.localScale = Vector3.one * unit * 1.4f;
            if (route != null) route.widthMultiplier = unit * 0.35f;

            var (mx, my) = MercatorOf(focus);
            var tile = TileOf(mx, my);
            if (tile != centreTile)
            {
                centreTile = tile;
                StartCoroutine(LoadAround(tile));
            }
        }

        void HandleGestures()
        {
            if (Input.touchCount == 0) return;
            var t0 = Input.GetTouch(0);
            if (t0.phase == TouchPhase.Began && Input.touchCount == 1)
            {
                gestureStart = t0.position;
                gestureStartTime = Time.time;
                gestureOnUi = IsOverUi(t0.position);
                moved = false;
            }
            if (gestureOnUi) return;

            if (Input.touchCount == 1)
            {
                // One finger orbits the camera around the player (or the point being looked at):
                // sideways turns it, up and down tilts it.
                if (t0.phase == TouchPhase.Moved && (moved || (t0.position - gestureStart).magnitude > 18f))
                {
                    moved = true;
                    Bearing += t0.deltaPosition.x * 0.25f;
                    Pitch = Mathf.Clamp(Pitch - t0.deltaPosition.y * 0.12f, 25f, 80f);
                }
                if (t0.phase == TouchPhase.Ended && !moved && Time.time - gestureStartTime < 0.5f) Tap(t0.position);
            }
            else if (Input.touchCount == 2)
            {
                // Two fingers pan across the ground and pinch to zoom.
                moved = true;
                var t1 = Input.GetTouch(1);
                var before0 = t0.position - t0.deltaPosition;
                var before1 = t1.position - t1.deltaPosition;
                var span = (t0.position - t1.position).magnitude;
                var spanBefore = (before0 - before1).magnitude;
                if (spanBefore > 1f) Distance = Mathf.Clamp(Distance * spanBefore / Mathf.Max(span, 1f), 60f, 1200f);
                var middle = (t0.deltaPosition + t1.deltaPosition) / 2f;
                if (middle.sqrMagnitude > 1f)
                {
                    following = false;
                    var metresPerPixel = 2f * Distance * Mathf.Tan(Camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height;
                    var right = Camera.transform.right;
                    var forward = Vector3.ProjectOnPlane(Camera.transform.forward, Vector3.up).normalized;
                    focus -= (right * middle.x + forward * middle.y / Mathf.Sin(Pitch * Mathf.Deg2Rad)) * metresPerPixel;
                }
            }
        }

        void Tap(Vector2 screen)
        {
            var ray = Camera.ScreenPointToRay(screen);
            if (Physics.Raycast(ray, out var hit, 5000f, 1 << Layer))
            {
                var gem = hit.collider.GetComponentInParent<Gem>();
                foreach (var (g, drop) in gems.Values)
                    if (g == gem) { Tapped?.Invoke(drop); return; }
            }
        }

        // ------------------------------------------------------------------ tiles

        IEnumerator LoadAround((int x, int y) centre)
        {
            for (var radius = 0; radius <= 2; radius++)
                for (var dx = -radius; dx <= radius; dx++)
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != radius) continue;
                        var key = (centre.x + dx, centre.y + dy);
                        if (!ground.ContainsKey(key)) StartCoroutine(LoadGround(key));
                        if (ShowBuildings && radius <= 1 && !buildings.ContainsKey(key)) StartCoroutine(LoadBuildings(key));
                    }
            yield return null;
            // Forget tiles well out of view.
            foreach (var key in new List<(int, int)>(ground.Keys))
                if (Mathf.Max(Mathf.Abs(key.Item1 - centre.x), Mathf.Abs(key.Item2 - centre.y)) > 3)
                {
                    if (ground[key] != null) Destroy(ground[key]);
                    ground.Remove(key);
                }
            foreach (var key in new List<(int, int)>(buildings.Keys))
                if (Mathf.Max(Mathf.Abs(key.Item1 - centre.x), Mathf.Abs(key.Item2 - centre.y)) > 2)
                {
                    if (buildings[key] != null) Destroy(buildings[key]);
                    buildings.Remove(key);
                }
        }

        IEnumerator LoadGround((int x, int y) key)
        {
            ground[key] = null;
            var url = $"https://api.mapbox.com/styles/v1/{Style}/tiles/512/{Zoom}/{key.x}/{key.y}@2x?access_token={token}";
            var bytes = default(byte[]);
            var style = Style;
            yield return Fetch(url, $"ground/{style.Replace('/', '_')}/{Zoom}_{key.x}_{key.y}.png", b => bytes = b);
            if (style != Style) yield break; // the look changed while it downloaded
            if (bytes == null || !ground.ContainsKey(key)) yield break;
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, true);
            texture.LoadImage(bytes);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.anisoLevel = 4;

            var (minX, minY, size) = TileBounds(key.x, key.y);
            var a = new Vector3((float)((minX - originX) * scale), 0, (float)((minY - originY) * scale));
            var s = (float)(size * scale);
            var mesh = new Mesh();
            mesh.SetVertices(new[] { a, a + new Vector3(0, 0, s), a + new Vector3(s, 0, s), a + new Vector3(s, 0, 0) });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            var go = new GameObject($"tile {key.x},{key.y}") { layer = Layer };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(groundMaterial) { mainTexture = texture };
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            go.AddComponent<Cleanup>().Owned = new UnityEngine.Object[] { mesh, material, texture };
            ground[key] = go;
        }

        IEnumerator LoadBuildings((int x, int y) key)
        {
            buildings[key] = null;
            var url = $"https://api.mapbox.com/v4/mapbox.mapbox-streets-v8/{Zoom}/{key.x}/{key.y}.mvt?access_token={token}";
            var bytes = default(byte[]);
            yield return Fetch(url, $"buildings/{Zoom}_{key.x}_{key.y}.mvt", b => bytes = b);
            if (bytes == null || !buildings.ContainsKey(key)) yield break;
            List<Mvt.Building> found;
            int extent;
            try { (found, extent) = Mvt.Buildings(bytes); }
            catch (Exception e)
            {
                Debug.LogWarning($"HOLOLOCO buildings {key}: {e.Message}");
                yield break;
            }
            var (minX, minY, size) = TileBounds(key.x, key.y);
            var maxY = minY + size;
            var mesh = Mvt.BuildMesh(found, p => new Vector2(
                (float)((minX + p.x / extent * size - originX) * scale),
                (float)((maxY - p.y / extent * size - originY) * scale)));
            var go = new GameObject($"buildings {key.x},{key.y}") { layer = Layer };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = buildingMaterial;
            go.AddComponent<Cleanup>().Owned = new UnityEngine.Object[] { mesh };
            buildings[key] = go;
        }

        /// <summary>Downloads a tile, or reads it from this phone's cache (kept for a week).</summary>
        static IEnumerator Fetch(string url, string cacheName, Action<byte[]> done)
        {
            var path = Path.Combine(Application.temporaryCachePath, "map", cacheName);
            if (File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays < 7)
            {
                done(File.ReadAllBytes(path));
                yield break;
            }
            using var request = UnityWebRequest.Get(url);
            request.timeout = 20;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"HOLOLOCO map tile failed ({request.responseCode}): {cacheName}");
                yield break;
            }
            var data = request.downloadHandler.data;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, data);
            }
            catch (IOException)
            {
                // No room to cache: fine, it's downloaded again next time.
            }
            done(data);
        }

        /// <summary>Switches between Mapbox's classic look and the dark game map.</summary>
        public void SetGameMap(bool game)
        {
            var changed = game != GameMap;
            GameMap = game;
            Camera.backgroundColor = game ? new Color(0.035f, 0.06f, 0.11f) : new Color(0.93f, 0.93f, 0.91f);
            buildingMaterial.SetColor("_Color", game ? new Color(0.11f, 0.15f, 0.23f) : new Color(0.78f, 0.8f, 0.84f));
            buildingMaterial.SetColor("_Top", game ? new Color(0.2f, 0.27f, 0.38f) : new Color(0.95f, 0.95f, 0.96f));
            glowMaterial.SetColor("_Color", game ? new Color(0.3f, 0.95f, 1f) : new Color(0f, 0.55f, 0.7f));
            glowMaterial.SetFloat("_DstBlend", game ? (float)UnityEngine.Rendering.BlendMode.One : (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r.sharedMaterial != null && r.sharedMaterial.shader == glowMaterial.shader)
                {
                    r.sharedMaterial.SetFloat("_DstBlend", glowMaterial.GetFloat("_DstBlend"));
                    r.sharedMaterial.SetColor("_Color", glowMaterial.GetColor("_Color")); // rings re-colour themselves each frame
                }
            if (!changed) return;
            foreach (var g in ground.Values) if (g != null) Destroy(g);
            ground.Clear();
            centreTile = (-1, -1);
        }

        public void SetBuildings(bool show)
        {
            ShowBuildings = show;
            foreach (var b in buildings.Values) if (b != null) b.SetActive(show);
            if (show) centreTile = (-1, -1);
        }

        // ------------------------------------------------------------------ the avatar

        void BuildAvatar()
        {
            avatar = new GameObject("avatar") { layer = Layer }.transform;
            avatar.SetParent(transform, false);

            // A teal gem for the player, a pointer showing which way they face, and pulsing hexagons.
            var body = Gem.Create(new DropInfo { Id = "me", Type = "offer", Rarity = "common" }, avatar, 1.1f, Layer);
            body.Spin = 25f;
            Destroy(body.GetComponent<Collider>());

            var hex = Resources.Load<Texture2D>("Art/hex_outline");
            // An arrowhead in front of the avatar, turned to the compass heading.
            var arrow = new GameObject("pointer") { layer = Layer };
            arrow.transform.SetParent(avatar, false);
            var arrowMesh = new Mesh();
            arrowMesh.SetVertices(new[] { new Vector3(-0.28f, 0.06f, 0.75f), new Vector3(0, 0.06f, 1.25f), new Vector3(0.28f, 0.06f, 0.75f) });
            arrowMesh.SetUVs(0, new[] { Vector2.zero, Vector2.one, Vector2.right });
            arrowMesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            arrowMesh.RecalculateBounds();
            arrow.AddComponent<MeshFilter>().sharedMesh = arrowMesh;
            arrow.AddComponent<MeshRenderer>().sharedMaterial = new Material(glowMaterial) { mainTexture = Texture2D.whiteTexture };
            pointer = arrow.transform;
            var ring = Quad("ring", avatar, hex, 1f);
            ring.localPosition = new Vector3(0, 0.08f, 0);
            ring.localRotation = Quaternion.Euler(90, 0, 0);
            rings.Add(ring);
        }

        Transform Quad(string name, Transform parent, Texture texture, float size)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(parent, false);
            var mesh = new Mesh();
            var h = size / 2f;
            mesh.SetVertices(new[] { new Vector3(-h, -h, 0), new Vector3(-h, h, 0), new Vector3(h, h, 0), new Vector3(h, -h, 0) });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(glowMaterial) { mainTexture = texture };
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            go.transform.localRotation = Quaternion.Euler(90, 0, 0);
            return go.transform;
        }

        /// <summary>Frees a tile's mesh, material and texture with it.</summary>
        class Cleanup : MonoBehaviour
        {
            public UnityEngine.Object[] Owned;
            void OnDestroy()
            {
                if (Owned == null) return;
                foreach (var o in Owned) if (o != null) Destroy(o);
            }
        }
    }
}
