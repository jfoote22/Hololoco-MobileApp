// Labels and arrows over the camera view, so people can find drops: a tag with
// the drop's name and distance floats just above each one, and when a drop is
// off screen (behind you, to the side, or high up), its tag sits at the edge of
// the view with an arrow pointing the way to turn.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hololoco
{
    public class Markers
    {
        readonly VisualElement layer;
        readonly ArPlacer placer;
        readonly VisualElement sheet;
        readonly Dictionary<string, (VisualElement box, VisualElement arrow, Label text)> tags = new();

        public Markers(VisualElement root, VisualElement sheet, ArPlacer placer)
        {
            this.placer = placer;
            this.sheet = sheet;
            layer = new VisualElement { pickingMode = PickingMode.Ignore };
            layer.AddToClassList("markers");
            // Under the sheet and overlays, over the camera.
            root.Insert(0, layer);
        }

        public void Update()
        {
            var panel = layer.panel;
            if (panel == null || float.IsNaN(layer.layout.width)) return;
            var camera = placer.Camera;
            placer.TryGetPosition(out var lat, out var lng, out _);

            // The part of the screen tags can use: below the status line, above the sheet.
            var width = layer.layout.width;
            var top = 120f;
            var bottom = Mathf.Max(top + 60f, sheet.layout.yMin - 30f);
            var area = Rect.MinMaxRect(70f, top, width - 70f, bottom);
            var centre = area.center;

            var seen = new HashSet<string>();
            foreach (var view in placer.Placed)
            {
                var drop = view.Drop;
                seen.Add(drop.Id);
                if (!tags.TryGetValue(drop.Id, out var tag))
                {
                    tag = Create();
                    tags[drop.Id] = tag;
                }

                var screen = camera.WorldToScreenPoint(view.LabelPoint);
                var behind = screen.z < 0;
                var point = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.x, Screen.height - screen.y));
                // A point behind the camera projects mirrored; flip it so the arrow points the right way.
                if (behind) point = centre - (point - centre);

                var distance = Geo.Distance(lat, lng, drop.Lat, drop.Lng);
                var words = $"{drop.Title} · {Words.Distance(distance)}";
                if (drop.Height >= 5f) words += $" · {Mathf.RoundToInt(drop.Height)} m up";

                if (!behind && area.Contains(point))
                {
                    tag.arrow.style.display = DisplayStyle.None;
                    tag.text.text = words;
                    Place(tag.box, point);
                }
                else
                {
                    var direction = (point - centre).normalized;
                    if (direction.sqrMagnitude < 0.01f) direction = Vector2.down;
                    // Walk from the centre toward the drop until reaching the edge of the area.
                    var reachX = Mathf.Abs(direction.x) > 0.001f ? (area.width / 2f) / Mathf.Abs(direction.x) : float.MaxValue;
                    var reachY = Mathf.Abs(direction.y) > 0.001f ? (area.height / 2f) / Mathf.Abs(direction.y) : float.MaxValue;
                    var edge = centre + direction * Mathf.Min(reachX, reachY);
                    var angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
                    tag.arrow.style.display = DisplayStyle.Flex;
                    tag.arrow.style.rotate = new Rotate(new Angle(angle + 45f, AngleUnit.Degree));
                    tag.text.text = words;
                    Place(tag.box, edge);
                }
            }

            foreach (var id in new List<string>(tags.Keys))
            {
                if (seen.Contains(id)) continue;
                tags[id].box.RemoveFromHierarchy();
                tags.Remove(id);
            }
        }

        (VisualElement, VisualElement, Label) Create()
        {
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.AddToClassList("tag");
            var arrow = new VisualElement { pickingMode = PickingMode.Ignore };
            arrow.AddToClassList("tag-arrow");
            var text = new Label { pickingMode = PickingMode.Ignore };
            text.AddToClassList("tag-text");
            box.Add(arrow);
            box.Add(text);
            layer.Add(box);
            return (box, arrow, text);
        }

        static void Place(VisualElement box, Vector2 point)
        {
            box.style.left = point.x;
            box.style.top = point.y;
        }
    }
}
