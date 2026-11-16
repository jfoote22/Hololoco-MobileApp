// The app's icons: strokes on a 24-unit grid with round caps and joins, drawn
// from SVG path data in the element's text colour, so they follow the skin.
// After changing a class that changes an icon's colour, call Icon.Repaint on a
// parent so the icons draw again with the new colour.

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hololoco
{
    public class Icon : VisualElement
    {
        /// <summary>Each icon is one or more paths; a path starting with "F:" is filled, the rest are stroked.</summary>
        static readonly Dictionary<string, string[]> Shapes = new()
        {
            ["map"] = new[] { "M9 4 3 6v14l6-2 6 2 6-2V4l-6 2-6-2Z M9 4v14 M15 6v14" },
            ["quests"] = new[] { "M5 21V4 M5 4h12l-2.5 4L17 12H5" },
            ["bag"] = new[] { "M5.5 9h13l-1 11h-11l-1-11Z M9 9V7a3 3 0 0 1 6 0v2" },
            ["me"] = new[] { "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z M4.5 20.5a7.5 7.5 0 0 1 15 0" },
            ["scan"] = new[] { "M4 8V5h3 M17 5h3v3 M20 16v3h-3 M7 19H4v-3 M15 12a3 3 0 1 1-6 0 3 3 0 1 1 6 0" },
            ["compass"] = new[] { "F:M12 3 16 13l-4-2-4 2 4-10Z", "M12 15v5" },
            ["recenter"] = new[] { "M12 3v3 M12 18v3 M3 12h3 M18 12h3 M18 12a6 6 0 1 1-12 0 6 6 0 1 1 12 0" },
            ["plus"] = new[] { "M12 5v14 M5 12h14" },
            ["chevron"] = new[] { "M9 6l6 6-6 6" },
            ["back"] = new[] { "M15 6l-6 6 6 6" },
            ["close"] = new[] { "M6 6l12 12 M18 6 6 18" },
            ["clock"] = new[] { "M21 12a9 9 0 1 1-18 0 9 9 0 1 1 18 0 M12 7v5l3 2" },
            ["pin"] = new[] { "M12 21s-6-5.3-6-10a6 6 0 0 1 12 0c0 4.7-6 10-6 10Z M14 11a2 2 0 1 1-4 0 2 2 0 1 1 4 0" },
            ["star"] = new[] { "F:M12 3l2 5.5L19.5 10 14 12l-2 6-2-6-5.5-2L10 8.5 12 3Z" },
            ["cup"] = new[] { "M7 9h10l-1.2 11H8.2L7 9Z M6 6.5h12V9H6Z M13 6.5 14.5 2.5" },
            ["ticket"] = new[] { "M4 7h16v3a2 2 0 0 0 0 4v3H4v-3a2 2 0 0 0 0-4V7Z" },
            ["cube"] = new[] { "M12 3 4 7.5v9L12 21l8-4.5v-9L12 3Z M4 7.5 12 12l8-4.5 M12 12v9" },
            ["chat"] = new[] { "M5 5h14v10H10l-5 4V5Z" },
            ["heart"] = new[] { "M12 20s-7-4.4-7-10a4 4 0 0 1 7-2.6A4 4 0 0 1 19 10c0 5.6-7 10-7 10Z" },
            ["gift"] = new[] { "M4 10h16v10H4Z M3 7h18v3H3Z M12 7v13 M12 7c-1.5-3-5-3.5-5-1.2S9.5 7 12 7c2.5 0 5-.5 5-1.2S13.5 4 12 7" },
            ["pizza"] = new[] { "M12 3 21 18c-5 3-13 3-18 0L12 3Z M11 10h.01 M14 14h.01 M9 15h.01" },
            ["camera"] = new[] { "M4 8h3l2-3h6l2 3h3v11H4Z M15.5 13a3.5 3.5 0 1 1-7 0 3.5 3.5 0 1 1 7 0" },
            ["check"] = new[] { "M5 12.5 10 17.5 19 7" },
            ["walk"] = new[] { "M13 4.5a1.5 1.5 0 1 1-3 0 1.5 1.5 0 1 1 3 0 M10 21l2-7 3 3v5 M8 12l2-4 4 1 2 3" },
        };

        string shape;

        public Icon(string shape, float size = 24f)
        {
            this.shape = shape;
            AddToClassList("icon");
            style.width = size;
            style.height = size;
            style.flexShrink = 0;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ => MarkDirtyRepaint());
        }

        public string Shape
        {
            get => shape;
            set { shape = value; MarkDirtyRepaint(); }
        }

        /// <summary>Redraws every icon under an element, after a class change altered their colour.</summary>
        public static void Repaint(VisualElement under) => under?.Query<Icon>().ForEach(i => i.MarkDirtyRepaint());

        /// <summary>An icon for a drop or an item: its ready-made look if it has one, else its kind.</summary>
        public static string For(string type, string objectId) => objectId switch
        {
            "cup" => "cup",
            "heart" => "heart",
            "star" => "star",
            "gift" => "gift",
            "pizza" => "pizza",
            "note" => "chat",
            _ => type switch { "message" => "chat", "object" => "cube", _ => "ticket" },
        };

        void Draw(MeshGenerationContext context)
        {
            if (shape == null || !Shapes.TryGetValue(shape, out var paths)) return;
            var scale = Mathf.Min(contentRect.width, contentRect.height) / 24f;
            if (scale <= 0) return;
            var painter = context.painter2D;
            var colour = resolvedStyle.color;
            painter.strokeColor = colour;
            painter.fillColor = colour;
            painter.lineWidth = 2f * scale;
            painter.lineCap = LineCap.Round;
            painter.lineJoin = LineJoin.Round;
            foreach (var path in paths)
            {
                var fill = path.StartsWith("F:", StringComparison.Ordinal);
                painter.BeginPath();
                Trace(painter, fill ? path.Substring(2) : path, scale);
                if (fill) painter.Fill();
                else painter.Stroke();
            }
        }

        // ------------------------------------------------------------------ SVG path data

        /// <summary>Follows SVG path data (M L H V C S Q Z A, absolute and relative) with the painter.</summary>
        static void Trace(Painter2D p, string d, float k)
        {
            var tokens = Tokens(d);
            var i = 0;
            var command = 'M';
            Vector2 current = Vector2.zero, start = Vector2.zero, lastControl = Vector2.zero;
            var lastWasCurve = false;
            float Num() => tokens[i++].number;
            Vector2 At(Vector2 v) => v * k;

            while (i < tokens.Count)
            {
                if (tokens[i].isCommand) command = tokens[i++].command;
                var relative = char.IsLower(command);
                var origin = relative ? current : Vector2.zero;
                switch (char.ToUpperInvariant(command))
                {
                    case 'M':
                        current = origin + new Vector2(Num(), Num());
                        start = current;
                        p.MoveTo(At(current));
                        command = relative ? 'l' : 'L'; // further pairs are lines
                        lastWasCurve = false;
                        break;
                    case 'L':
                        current = origin + new Vector2(Num(), Num());
                        p.LineTo(At(current));
                        lastWasCurve = false;
                        break;
                    case 'H':
                        current = new Vector2((relative ? current.x : 0) + Num(), current.y);
                        p.LineTo(At(current));
                        lastWasCurve = false;
                        break;
                    case 'V':
                        current = new Vector2(current.x, (relative ? current.y : 0) + Num());
                        p.LineTo(At(current));
                        lastWasCurve = false;
                        break;
                    case 'C':
                    {
                        var c1 = origin + new Vector2(Num(), Num());
                        var c2 = origin + new Vector2(Num(), Num());
                        var end = origin + new Vector2(Num(), Num());
                        p.BezierCurveTo(At(c1), At(c2), At(end));
                        lastControl = c2;
                        current = end;
                        lastWasCurve = true;
                        break;
                    }
                    case 'S':
                    {
                        var c1 = lastWasCurve ? current * 2 - lastControl : current;
                        var c2 = origin + new Vector2(Num(), Num());
                        var end = origin + new Vector2(Num(), Num());
                        p.BezierCurveTo(At(c1), At(c2), At(end));
                        lastControl = c2;
                        current = end;
                        lastWasCurve = true;
                        break;
                    }
                    case 'Q':
                    {
                        var c = origin + new Vector2(Num(), Num());
                        var end = origin + new Vector2(Num(), Num());
                        p.QuadraticCurveTo(At(c), At(end));
                        current = end;
                        lastWasCurve = false;
                        break;
                    }
                    case 'A':
                    {
                        var rx = Num();
                        var ry = Num();
                        Num(); // rotation: the icons use circles only
                        var large = Num() != 0;
                        var sweep = Num() != 0;
                        var end = origin + new Vector2(Num(), Num());
                        Arc(p, current, end, (rx + ry) / 2f, large, sweep, k);
                        current = end;
                        lastWasCurve = false;
                        break;
                    }
                    case 'Z':
                        p.ClosePath();
                        current = start;
                        lastWasCurve = false;
                        command = '\0'; // numbers straight after Z aren't valid: stop there
                        break;
                    default:
                        return; // something this reader doesn't know: stop rather than draw nonsense
                }
            }
        }

        /// <summary>An SVG arc between two points, as a circle arc around its centre (SVG spec F.6.5).</summary>
        static void Arc(Painter2D p, Vector2 from, Vector2 to, float r, bool large, bool sweep, float k)
        {
            if (r <= 0 || from == to)
            {
                p.LineTo(to * k);
                return;
            }
            var half = (from - to) / 2f;
            var lambda = half.sqrMagnitude / (r * r);
            if (lambda > 1) r *= Mathf.Sqrt(lambda);
            var squared = r * r;
            var numerator = squared * squared - squared * half.y * half.y - squared * half.x * half.x;
            var denominator = squared * half.y * half.y + squared * half.x * half.x;
            var coefficient = (large == sweep ? -1 : 1) * Mathf.Sqrt(Mathf.Max(0, numerator / Mathf.Max(denominator, 1e-6f)));
            var centrePrime = new Vector2(coefficient * half.y, -coefficient * half.x);
            var centre = centrePrime + (from + to) / 2f;
            var startAngle = Mathf.Atan2((half.y - centrePrime.y) / r, (half.x - centrePrime.x) / r);
            var endAngle = Mathf.Atan2((-half.y - centrePrime.y) / r, (-half.x - centrePrime.x) / r);
            var delta = endAngle - startAngle;
            if (!sweep && delta > 0) delta -= 2 * Mathf.PI;
            else if (sweep && delta < 0) delta += 2 * Mathf.PI;
            p.Arc(centre * k, r * k, Angle.Radians(startAngle), Angle.Radians(startAngle + delta),
                sweep ? ArcDirection.Clockwise : ArcDirection.CounterClockwise);
        }

        struct Token
        {
            public bool isCommand;
            public char command;
            public float number;
        }

        static List<Token> Tokens(string d)
        {
            var list = new List<Token>();
            var n = 0;
            while (n < d.Length)
            {
                var c = d[n];
                if (char.IsLetter(c))
                {
                    list.Add(new Token { isCommand = true, command = c });
                    n++;
                    continue;
                }
                if (c == ' ' || c == ',' || c == '\n' || c == '\t')
                {
                    n++;
                    continue;
                }
                // A number: sign, digits, at most one point, an optional exponent.
                var begin = n;
                if (d[n] == '-' || d[n] == '+') n++;
                var seenPoint = false;
                while (n < d.Length)
                {
                    var ch = d[n];
                    if (char.IsDigit(ch)) n++;
                    else if (ch == '.' && !seenPoint)
                    {
                        seenPoint = true;
                        n++;
                    }
                    else if ((ch == 'e' || ch == 'E') && n + 1 < d.Length && (char.IsDigit(d[n + 1]) || d[n + 1] == '-'))
                    {
                        n += 2;
                    }
                    else break;
                }
                if (n == begin)
                {
                    n++; // not a number either: skip it
                    continue;
                }
                list.Add(new Token { number = float.Parse(d.Substring(begin, n - begin), NumberStyles.Float, CultureInfo.InvariantCulture) });
            }
            return list;
        }
    }
}
