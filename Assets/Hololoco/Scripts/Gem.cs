// A drop's gem: a hexagonal crystal in its rarity colour that turns and bobs,
// and breaks apart along the old Coupemon rarity pattern when collected or left.

using System.Collections;
using UnityEngine;

namespace Hololoco
{
    public class Gem : MonoBehaviour
    {
        public float Spin = 45f;
        public float Bob = 0.06f;

        Material material;
        Transform body;
        float phase;

        static Mesh mesh;

        /// <summary>Colour for a rarity, or for a drop that isn't an offer.</summary>
        public static Color ColourFor(DropInfo drop) => drop.Type switch
        {
            "offer" => drop.Rarity switch
            {
                "rare" => new Color(0.78f, 0.48f, 1f),
                "uncommon" => new Color(0.3f, 0.9f, 0.6f),
                _ => new Color(0.55f, 0.83f, 1f),
            },
            "message" => new Color(0.95f, 0.95f, 1f),
            _ => new Color(1f, 0.8f, 0.4f),
        };

        static string PatternFor(DropInfo drop) => drop.Rarity switch
        {
            "rare" => "Art/dissolve_rare",
            "uncommon" => "Art/dissolve_uncommon",
            _ => "Art/dissolve_common",
        };

        /// <summary>Makes a gem for a drop, `height` metres tall, on the given layer.</summary>
        public static Gem Create(DropInfo drop, Transform parent, float height, int layer)
        {
            var go = new GameObject("gem " + drop.Id) { layer = layer };
            go.transform.SetParent(parent, false);
            var gem = go.AddComponent<Gem>();
            var bodyObject = new GameObject("body") { layer = layer };
            bodyObject.transform.SetParent(go.transform, false);
            bodyObject.transform.localScale = Vector3.one * height;
            bodyObject.AddComponent<MeshFilter>().sharedMesh = Mesh();
            var renderer = bodyObject.AddComponent<MeshRenderer>();
            gem.material = new Material(Resources.Load<Material>("Materials/Gem"));
            gem.material.SetColor("_Color", ColourFor(drop));
            gem.material.SetTexture("_DissolveTex", Resources.Load<Texture2D>(PatternFor(drop)));
            gem.material.SetFloat("_Dissolve", 0);
            renderer.sharedMaterial = gem.material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var collider = go.AddComponent<SphereCollider>();
            collider.radius = height * 0.6f;
            collider.center = new Vector3(0, height * 0.5f, 0);
            gem.body = bodyObject.transform;
            gem.phase = Random.value * 10f;
            return gem;
        }

        void Update()
        {
            if (body == null) return;
            body.Rotate(Vector3.up, Spin * Time.deltaTime, Space.Self);
            body.localPosition = new Vector3(0, Mathf.Sin(Time.time * 2f + phase) * Bob * body.localScale.y, 0);
        }

        /// <summary>Breaks the gem apart over `seconds`, then (if asked) removes it.</summary>
        public IEnumerator Dissolve(float seconds, bool destroy)
        {
            for (var t = 0f; t < seconds; t += Time.deltaTime)
            {
                material.SetFloat("_Dissolve", t / seconds);
                Spin = 45f + 400f * (t / seconds);
                yield return null;
            }
            material.SetFloat("_Dissolve", 1f);
            if (destroy) Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (material != null) Destroy(material);
        }

        /// <summary>A hexagonal bipyramid, 1 unit tall, standing on its point, with UVs that wrap around it.</summary>
        static Mesh Mesh()
        {
            if (mesh != null) return mesh;
            const int sides = 6;
            const float waist = 0.62f, radius = 0.42f;
            var vertices = new System.Collections.Generic.List<Vector3>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            var triangles = new System.Collections.Generic.List<int>();
            var bottom = new Vector3(0, 0, 0);
            var top = new Vector3(0, 1, 0);
            for (var i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2 / sides, a1 = (i + 1) * Mathf.PI * 2 / sides;
                var p0 = new Vector3(Mathf.Cos(a0) * radius, waist, Mathf.Sin(a0) * radius);
                var p1 = new Vector3(Mathf.Cos(a1) * radius, waist, Mathf.Sin(a1) * radius);
                float u0 = (float)i / sides, u1 = (float)(i + 1) / sides;
                // Upper face (flat shaded: own vertices).
                var v = vertices.Count;
                vertices.AddRange(new[] { p0, top, p1 });
                uvs.AddRange(new[] { new Vector2(u0, waist), new Vector2((u0 + u1) / 2, 1), new Vector2(u1, waist) });
                triangles.AddRange(new[] { v, v + 1, v + 2 });
                // Lower face.
                v = vertices.Count;
                vertices.AddRange(new[] { p1, bottom, p0 });
                uvs.AddRange(new[] { new Vector2(u1, waist), new Vector2((u0 + u1) / 2, 0), new Vector2(u0, waist) });
                triangles.AddRange(new[] { v, v + 1, v + 2 });
            }
            mesh = new Mesh { name = "Gem" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
