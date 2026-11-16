// Reads the "building" layer of a Mapbox vector tile (mapbox-streets-v8, Mapbox
// Vector Tile format: protobuf, https://github.com/mapbox/vector-tile-spec) and
// turns each footprint into a 3D block: walls from min_height to height, and a flat roof.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hololoco
{
    public static class Mvt
    {
        public struct Building
        {
            public List<Vector2> Ring; // tile coordinates, 0..extent, y down
            public float Height, MinHeight;
        }

        /// <summary>The building footprints in a tile, and the tile's coordinate extent.</summary>
        public static (List<Building>, int) Buildings(byte[] data)
        {
            if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b) data = Gunzip(data);
            var buildings = new List<Building>();
            var extent = 4096;
            var tile = new Reader(data, 0, data.Length);
            while (tile.Next(out var field, out var wire))
            {
                if (field == 3 && wire == 2)
                {
                    var (start, end) = tile.Bytes();
                    var layer = ReadLayer(data, start, end, out var name, out var layerExtent);
                    if (name == "building")
                    {
                        extent = layerExtent;
                        buildings.AddRange(layer);
                    }
                }
                else tile.Skip(wire);
            }
            return (buildings, extent);
        }

        static List<Building> ReadLayer(byte[] data, int start, int end, out string name, out int extent)
        {
            name = null;
            extent = 4096;
            var keys = new List<string>();
            var values = new List<object>();
            var features = new List<(int, int)>();
            var r = new Reader(data, start, end);
            while (r.Next(out var field, out var wire))
            {
                switch (field)
                {
                    case 1 when wire == 2: name = r.String(); break;
                    case 2 when wire == 2: features.Add(r.Bytes()); break;
                    case 3 when wire == 2: keys.Add(r.String()); break;
                    case 4 when wire == 2: var (vs, ve) = r.Bytes(); values.Add(ReadValue(data, vs, ve)); break;
                    case 5 when wire == 0: extent = (int)r.Varint(); break;
                    default: r.Skip(wire); break;
                }
            }
            var result = new List<Building>();
            if (name != "building") return result;
            foreach (var (fs, fe) in features) ReadFeature(data, fs, fe, keys, values, result);
            return result;
        }

        static void ReadFeature(byte[] data, int start, int end, List<string> keys, List<object> values, List<Building> into)
        {
            var type = 0;
            List<uint> tags = null, geometry = null;
            var r = new Reader(data, start, end);
            while (r.Next(out var field, out var wire))
            {
                if (field == 2 && wire == 2) tags = r.Packed();
                else if (field == 3 && wire == 0) type = (int)r.Varint();
                else if (field == 4 && wire == 2) geometry = r.Packed();
                else r.Skip(wire);
            }
            if (type != 3 || geometry == null) return; // polygons only

            float height = 6, minHeight = 0;
            var extrude = true;
            if (tags != null)
                for (var i = 0; i + 1 < tags.Count; i += 2)
                {
                    var key = keys[(int)tags[i]];
                    var value = values[(int)tags[i + 1]];
                    if (key == "height") height = Convert.ToSingle(value);
                    else if (key == "min_height") minHeight = Convert.ToSingle(value);
                    else if (key == "extrude") extrude = value is string s ? s == "true" : Convert.ToBoolean(value);
                    else if (key == "underground" && (value as string) == "true") extrude = false;
                }
            if (!extrude || height <= minHeight) return;

            // Geometry commands: MoveTo(1), LineTo(2), ClosePath(7); parameters are zigzag deltas.
            int x = 0, y = 0;
            List<Vector2> ring = null;
            for (var i = 0; i < geometry.Count;)
            {
                var command = geometry[i] & 0x7;
                var count = (int)(geometry[i] >> 3);
                i++;
                if (command == 7)
                {
                    if (ring != null && ring.Count >= 3 && Area(ring) > 0)
                        into.Add(new Building { Ring = ring, Height = height, MinHeight = minHeight });
                    ring = null;
                    continue;
                }
                for (var c = 0; c < count && i + 1 < geometry.Count; c++)
                {
                    x += Zigzag(geometry[i++]);
                    y += Zigzag(geometry[i++]);
                    if (command == 1) ring = new List<Vector2>();
                    ring?.Add(new Vector2(x, y));
                }
            }
        }

        /// <summary>Positive for an outer ring in tile coordinates (y down), as the spec defines it.</summary>
        static float Area(List<Vector2> ring)
        {
            float sum = 0;
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                sum += a.x * b.y - b.x * a.y;
            }
            return sum / 2f;
        }

        static object ReadValue(byte[] data, int start, int end)
        {
            var r = new Reader(data, start, end);
            object value = null;
            while (r.Next(out var field, out var wire))
            {
                switch (field)
                {
                    case 1: value = r.String(); break;
                    case 2: value = r.Fixed32(); break;
                    case 3: value = r.Fixed64(); break;
                    case 4: value = (long)r.Varint(); break;
                    case 5: value = (long)r.Varint(); break;
                    case 6: value = (long)Zigzag64(r.Varint()); break;
                    case 7: value = r.Varint() != 0; break;
                    default: r.Skip(wire); break;
                }
            }
            return value ?? 0;
        }

        static int Zigzag(uint n) => (int)(n >> 1) ^ -(int)(n & 1);
        static long Zigzag64(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);

        static byte[] Gunzip(byte[] data)
        {
            using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        /// <summary>A protobuf reader over part of a byte array.</summary>
        struct Reader
        {
            readonly byte[] data;
            int position;
            readonly int end;

            public Reader(byte[] data, int start, int end)
            {
                this.data = data;
                position = start;
                this.end = end;
            }

            public bool Next(out int field, out int wire)
            {
                field = wire = 0;
                if (position >= end) return false;
                var key = Varint();
                field = (int)(key >> 3);
                wire = (int)(key & 0x7);
                return true;
            }

            public ulong Varint()
            {
                ulong result = 0;
                var shift = 0;
                while (position < end)
                {
                    var b = data[position++];
                    result |= (ulong)(b & 0x7f) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                return result;
            }

            public (int, int) Bytes()
            {
                var length = (int)Varint();
                var start = position;
                position += length;
                return (start, Math.Min(position, end));
            }

            public string String()
            {
                var (s, e) = Bytes();
                return System.Text.Encoding.UTF8.GetString(data, s, e - s);
            }

            public List<uint> Packed()
            {
                var (s, e) = Bytes();
                var list = new List<uint>();
                var inner = new Reader(data, s, e);
                while (inner.position < e) list.Add((uint)inner.Varint());
                return list;
            }

            public float Fixed32()
            {
                var v = BitConverter.ToSingle(data, position);
                position += 4;
                return v;
            }

            public double Fixed64()
            {
                var v = BitConverter.ToDouble(data, position);
                position += 8;
                return v;
            }

            public void Skip(int wire)
            {
                switch (wire)
                {
                    case 0: Varint(); break;
                    case 1: position += 8; break;
                    case 2: Bytes(); break;
                    case 5: position += 4; break;
                    default: position = end; break;
                }
            }
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>One mesh for all the tile's buildings. toWorld maps tile coordinates to map metres (x east, z north).</summary>
        public static Mesh BuildMesh(List<Building> buildings, Func<Vector2, Vector2> toWorld)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var b in buildings)
            {
                var points = new List<Vector2>(b.Ring.Count);
                foreach (var p in b.Ring) points.Add(toWorld(p));
                if (points.Count > 1 && (points[0] - points[^1]).sqrMagnitude < 0.0001f) points.RemoveAt(points.Count - 1);
                if (points.Count < 3) continue;

                // Walls.
                for (var i = 0; i < points.Count; i++)
                {
                    var a = points[i];
                    var c = points[(i + 1) % points.Count];
                    var along = c - a;
                    if (along.sqrMagnitude < 0.0001f) continue;
                    var normal = new Vector3(along.y, 0, -along.x).normalized;
                    var v = vertices.Count;
                    vertices.Add(new Vector3(a.x, b.MinHeight, a.y));
                    vertices.Add(new Vector3(c.x, b.MinHeight, c.y));
                    vertices.Add(new Vector3(c.x, b.Height, c.y));
                    vertices.Add(new Vector3(a.x, b.Height, a.y));
                    for (var k = 0; k < 4; k++) normals.Add(normal);
                    triangles.AddRange(new[] { v, v + 1, v + 2, v, v + 2, v + 3 });
                }

                // Roof.
                var roof = Triangulate(points);
                var r0 = vertices.Count;
                foreach (var p in points)
                {
                    vertices.Add(new Vector3(p.x, b.Height, p.y));
                    normals.Add(Vector3.up);
                }
                foreach (var index in roof) triangles.Add(r0 + index);
            }

            var mesh = new Mesh { indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Ear clipping for a simple polygon. Returns triangle indices into points.</summary>
        static List<int> Triangulate(List<Vector2> points)
        {
            var result = new List<int>();
            var indices = new List<int>();
            for (var i = 0; i < points.Count; i++) indices.Add(i);
            // Work counter-clockwise.
            float area = 0;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                area += a.x * b.y - b.x * a.y;
            }
            if (area < 0) indices.Reverse();

            var guard = 0;
            while (indices.Count > 3 && guard++ < 10000)
            {
                var clipped = false;
                for (var i = 0; i < indices.Count; i++)
                {
                    var ia = indices[(i + indices.Count - 1) % indices.Count];
                    var ib = indices[i];
                    var ic = indices[(i + 1) % indices.Count];
                    var a = points[ia];
                    var b = points[ib];
                    var c = points[ic];
                    if (Cross(b - a, c - b) <= 0) continue; // reflex corner
                    var inside = false;
                    foreach (var j in indices)
                    {
                        if (j == ia || j == ib || j == ic) continue;
                        if (InTriangle(points[j], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    result.AddRange(new[] { ia, ib, ic });
                    indices.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break; // not simple: give up on the rest of this roof
            }
            if (indices.Count == 3) result.AddRange(indices);
            return result;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var d1 = Cross(b - a, p - a);
            var d2 = Cross(c - b, p - b);
            var d3 = Cross(a - c, p - c);
            return d1 >= 0 && d2 >= 0 && d3 >= 0;
        }
    }
}
