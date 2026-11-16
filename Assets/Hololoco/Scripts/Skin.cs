// Skins: the app's look, swappable without touching its screens. A skin is a
// style sheet in Resources/Skins (colours, fonts, corner shapes, and where the
// bottom bar sits) plus the colours the 3D map and gems need, which are set here.
// Daylight is the default. People choose another under Me > Look.
//
// To add a skin: copy one of the style sheets, change its values, and add an
// entry to All below.

using System;
using System.Linq;
using UnityEngine;

namespace Hololoco
{
    public class Skin
    {
        public string Id, Name, Blurb;
        /// <summary>The dark Mapbox map (navigation-night) instead of the light one (streets).</summary>
        public bool DarkMap;
        public Color MapBackground, BuildingWall, BuildingTop, Accent;
        public Color Common, Uncommon, Rare, Message, Object;
        /// <summary>Three colours for the Look picker: the page, the accent and the rare colour.</summary>
        public Color Page;

        public string Sheet => "Skins/" + Id;

        public static readonly Skin Daylight = new()
        {
            Id = "daylight", Name = "Daylight", Blurb = "Bright and easy to read outdoors.",
            DarkMap = false,
            Page = Hex("#f2f5f7"),
            MapBackground = Hex("#e8ece9"), BuildingWall = Hex("#d6dde5"), BuildingTop = Hex("#ffffff"),
            Accent = Hex("#00788a"),
            Common = Hex("#8796a3"), Uncommon = Hex("#2fa36a"), Rare = Hex("#f2b13a"),
            Message = Hex("#00788a"), Object = Hex("#3d7bd9"),
        };

        public static readonly Skin SoftNight = new()
        {
            Id = "soft-night", Name = "Soft night", Blurb = "Calm and dark, with glowing pins.",
            DarkMap = true,
            Page = Hex("#0b1020"),
            MapBackground = Hex("#0b1020"), BuildingWall = Hex("#1d2643"), BuildingTop = Hex("#2a3556"),
            Accent = Hex("#7fe3f2"),
            Common = Hex("#cdd5e4"), Uncommon = Hex("#86e3b0"), Rare = Hex("#f7a8f0"),
            Message = Hex("#7fe3f2"), Object = Hex("#a8b8ff"),
        };

        public static readonly Skin HoloHud = new()
        {
            Id = "holo-hud", Name = "Holo HUD", Blurb = "Sharp, technical, a little cyberpunk.",
            DarkMap = true,
            Page = Hex("#080b10"),
            MapBackground = Hex("#080b10"), BuildingWall = Hex("#161f28"), BuildingTop = Hex("#233140"),
            Accent = Hex("#3fe0ff"),
            Common = Hex("#a9b8c4"), Uncommon = Hex("#4be38c"), Rare = Hex("#ff5cb8"),
            Message = Hex("#3fe0ff"), Object = Hex("#ffb547"),
        };

        public static readonly Skin[] All = { Daylight, SoftNight, HoloHud };

        public static Skin Current { get; private set; } = Daylight;
        public static event Action<Skin> Changed;

        const string Key = "hl.skin";

        public static void Load() => Current = All.FirstOrDefault(s => s.Id == PlayerPrefs.GetString(Key, Daylight.Id)) ?? Daylight;

        public static void Set(Skin skin)
        {
            Current = skin;
            PlayerPrefs.SetString(Key, skin.Id);
            PlayerPrefs.Save();
            Changed?.Invoke(skin);
        }

        public Color RarityColour(string rarity) => rarity switch
        {
            "rare" => Rare,
            "uncommon" => Uncommon,
            _ => Common,
        };

        static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.magenta;
    }
}
