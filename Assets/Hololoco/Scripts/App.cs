// The Hololoco app. Its look comes from the skin (Skin.cs and Resources/Skins);
// Daylight is the default, and people pick another under Me > Look.
//
//   Map (home)  a tilted 3D map with your avatar and a pin for each drop nearby,
//               cards for the closest ones, and a drop's card when you tap one:
//               "Open in AR" when you're close, or "Walk there". Filters along the
//               top choose the pins: All, Events (games, shows and giveaways around
//               town that Hololoco found), Businesses, Quests, or Mine.
//   Quests      the quest you're on, stop by stop, and quests starting nearby.
//   Scan        opens AR on the closest drop you can reach.
//   AR          the item's own 3D model, standing on the ground and staying put while
//               you walk around it: from the map, at its spot on the map (GPS and
//               compass); from the bag, right in front of you. A chip says which way
//               it is. Capture it (or pick up an item someone left), leave it, take a photo.
//   Bag         what you've collected: Redeem (a code for the counter), View in AR,
//               Leave it here (for someone else), Throw away.
//   Me          level and XP, the Look (skin), settings, your trading name, sign out.
//   +           leave a message where you're standing.
//
// Everything goes through the database functions in the website's docs/app-api.md.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using UnityEngine.XR.ARFoundation;

namespace Hololoco
{
    public class App : MonoBehaviour
    {
        public UIDocument Document;
        public Location Location;
        public MapView Map;
        public Camera MapCamera;
        public Camera ArCamera;
        public ARSession Session;
        public Encounter Encounter;
        public Preview Preview;

        enum Mode { Map, Encounter, ViewItem }
        Mode mode = Mode.Map;
        float arSince;
        string panel; // null (just the map), "quests", "bag", "me" or "drop"

        VisualElement root, pinLayer, top, tools, nav, page, card, arUi, banner, levelFill;
        readonly Dictionary<string, (VisualElement pin, DropInfo drop)> pins = new();
        string selectedPin;
        float pinsSortedAt;
        ScrollView nearbyStrip, pageScroll;
        Label status, toast, levelText, avatarLetter, countPill;
        Icon compassIcon;
        StyleSheet skinSheet;
        float toastUntil, bannerUntil;

        List<DropInfo> nearby = new();
        /// <summary>Approved events and giveaways across town (app_found_nearby), farther than the player can see.</summary>
        List<DropInfo> aroundTown = new();
        double foundLat = double.NaN, foundLng;
        float foundAt = -999f;
        bool fetchingFound;

        /// <summary>What the map shows: everything, or only events, business drops, quests or your own.</summary>
        static readonly (string id, string label)[] Filters =
        {
            ("all", "All"), ("events", "Events"), ("business", "Businesses"), ("quests", "Quests"), ("mine", "Mine"),
        };
        string pinFilter = "all";
        ScrollView filters;

        /// <summary>Under Events: how far, and what kind. Null and 0 mean any.</summary>
        string eventType;
        float eventWithin;
        static readonly float[] EventDistances = { 0f, 1000f, 3000f, 10000f };
        ScrollView eventFilters;

        /// <summary>What's at each spot on the map: drops within about 10 m share a pin.</summary>
        readonly Dictionary<string, List<DropInfo>> spots = new();
        JArray bag = new();
        JArray myQuests = new();
        JArray nearbyQuests = new();
        JObject player;
        DropInfo selectedDrop;
        DropInfo encounterDrop;
        string bagFilter = "all";
        double lastLat, lastLng;
        float lastFetch = -999f;
        bool fetching;
        HashSet<string> alerted = new();
        DropInfo bannerDrop;

        static readonly (string id, string name)[] Looks =
        {
            ("note", "Speech bubble"), ("heart", "Heart"), ("star", "Star"), ("gift", "Gift box"), ("cup", "Coffee cup"), ("pizza", "Pizza slice"),
        };

        // ================================================================ start

        void Start()
        {
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Skin.Load();
            root = Document.rootVisualElement;
            var layout = Resources.Load<StyleSheet>("hololoco");
            if (layout != null) root.styleSheets.Add(layout);
            root.AddToClassList("app");
            root.pickingMode = PickingMode.Ignore;
            LoadAlerted();
            BuildChrome();
            ApplySkin(Skin.Current);
            Skin.Changed += ApplySkin;
            Map.IsOverUi = OverUi;
            Map.SetBuildings(PlayerPrefs.GetInt("hl.buildings", 1) == 1);
            SetMode(Mode.Map);
            if (!Supabase.SignedIn) ShowSignIn();
            _ = RefreshPlayer();
        }

        void OnDestroy() => Skin.Changed -= ApplySkin;

        /// <summary>Swaps the skin's style sheet and recolours the map and gems.</summary>
        void ApplySkin(Skin skin)
        {
            if (skinSheet != null) root.styleSheets.Remove(skinSheet);
            skinSheet = Resources.Load<StyleSheet>(skin.Sheet);
            if (skinSheet != null) root.styleSheets.Add(skinSheet);
            foreach (var s in Skin.All) root.EnableInClassList("skin-" + s.Id, s == skin);
            Map.SetLook(skin);
            Icon.Repaint(root);
            if (panel != null) RenderPanel();
        }

        void BuildChrome()
        {
            // Pins for the drops, under everything else.
            pinLayer = Div("pins");
            pinLayer.pickingMode = PickingMode.Ignore;
            root.Add(pinLayer);

            // Top: your level (opens Me), how much is nearby, and a compass that points the map north.
            top = Div("top");
            var pill = Div("level-pill");
            pill.AddManipulator(new Clickable(() => OpenPanel("me")));
            avatarLetter = Text("?", "avatar");
            pill.Add(avatarLetter);
            var column = Div("level-col");
            levelText = Text("Level 1", "strong small");
            column.Add(levelText);
            var levelBar = Div("level-bar");
            levelFill = Div("level-fill");
            levelBar.Add(levelFill);
            column.Add(levelBar);
            pill.Add(column);
            top.Add(pill);
            countPill = Text("", "count-pill");
            countPill.style.display = DisplayStyle.None;
            top.Add(countPill);
            top.Add(Div("top-spacer"));
            var compass = Round("compass", () => Map.Bearing = 0f);
            compassIcon = compass.Q<Icon>();
            top.Add(compass);
            root.Add(top);

            // Which pins the map shows. The choice is kept between visits.
            pinFilter = PlayerPrefs.GetString("hl.pins", "all");
            if (Filters.All(f => f.id != pinFilter)) pinFilter = "all";
            filters = new ScrollView(ScrollViewMode.Horizontal)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Hidden,
            };
            filters.AddToClassList("filters");
            foreach (var (id, label) in Filters)
            {
                var chip = new Tap(label, () => SetFilter(id));
                chip.AddToClassList("filter");
                chip.name = id;
                filters.Add(chip);
            }
            MarkFilter();
            root.Add(filters);

            eventFilters = new ScrollView(ScrollViewMode.Horizontal)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Hidden,
            };
            eventFilters.AddToClassList("filters");
            eventFilters.AddToClassList("subfilters");
            root.Add(eventFilters);

            tools = Div("tools");
            tools.Add(Round("recenter", () => { ClosePanel(); Map.Recenter(); }));
            tools.Add(Round("plus", () => OpenPanel("drop")));
            root.Add(tools);

            status = Text("", "status");
            status.pickingMode = PickingMode.Ignore;
            root.Add(status);

            banner = Div("banner");
            banner.style.display = DisplayStyle.None;
            banner.AddManipulator(new Clickable(() =>
            {
                banner.style.display = DisplayStyle.None;
                if (bannerDrop != null) SelectDrop(bannerDrop);
            }));
            root.Add(banner);

            nearbyStrip = new ScrollView(ScrollViewMode.Horizontal)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Hidden,
            };
            nearbyStrip.AddToClassList("nearby");
            root.Add(nearbyStrip);

            page = Div("page");
            pageScroll = new ScrollView(ScrollViewMode.Vertical)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Hidden,
            };
            pageScroll.AddToClassList("page-scroll");
            pageScroll.contentContainer.AddToClassList("page-body");
            page.Add(pageScroll);
            root.Add(page);

            // The bottom bar: Map, Quests, Scan, Bag, Me.
            nav = Div("nav");
            foreach (var (id, icon, label) in new[] { ("map", "map", "Map"), ("quests", "quests", "Quests"), ("scan", "scan", "Scan"), ("bag", "bag", "Bag"), ("me", "me", "Me") })
            {
                var item = Div("nav-item");
                item.name = id;
                if (id == "scan")
                {
                    item.AddToClassList("nav-scan");
                    var disc = Div("scan-disc");
                    disc.pickingMode = PickingMode.Ignore;
                    disc.Add(new Icon("scan", 28));
                    item.Add(disc);
                }
                else
                {
                    var pillBox = Div("nav-pill");
                    pillBox.pickingMode = PickingMode.Ignore;
                    pillBox.Add(new Icon(icon, 24));
                    item.Add(pillBox);
                }
                var words = new Label(label) { pickingMode = PickingMode.Ignore };
                words.AddToClassList("nav-label");
                item.Add(words);
                item.AddManipulator(new Clickable(() => NavTapped(id)));
                nav.Add(item);
            }
            root.Add(nav);

            card = Div("card-sheet");
            card.style.display = DisplayStyle.None;
            root.Add(card);

            arUi = Div("ar");
            arUi.pickingMode = PickingMode.Ignore;
            arUi.style.display = DisplayStyle.None;
            root.Add(arUi);

            toast = Text("", "toast");
            toast.pickingMode = PickingMode.Ignore;
            toast.style.display = DisplayStyle.None;
            root.Add(toast);

            SetNavSelected("map");
        }

        bool OverUi(Vector2 screen)
        {
            if (root?.panel == null) return false;
            var p = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y));
            var picked = root.panel.Pick(p);
            return picked != null && picked != root;
        }

        // ================================================================ modes and what's showing

        void SetMode(Mode next)
        {
            var ar = next != Mode.Map;
            if (ar && mode == Mode.Map) arSince = Time.time;
            mode = next;
            Session.enabled = ar;
            ArCamera.enabled = ar;
            MapCamera.enabled = !ar;
            if (ar) RenderSettings.fog = false;
            if (ar)
            {
                card.style.display = DisplayStyle.None;
                Preview.SetVisible(false);
            }
            else
            {
                Encounter.Clear();
                if (panel != null) RenderPanel();
            }
            UpdateChrome();
        }

        /// <summary>Shows what belongs on screen for the mode, the open page and the open card.</summary>
        void UpdateChrome()
        {
            var onMap = mode == Mode.Map;
            var cardOpen = card.style.display == DisplayStyle.Flex;
            var mapClear = onMap && panel == null;
            Show(top, mapClear);
            Show(filters, mapClear);
            Show(eventFilters, mapClear && pinFilter == "events");
            root.EnableInClassList("with-subfilters", mapClear && pinFilter == "events");
            Show(pinLayer, mapClear);
            Show(tools, mapClear && !cardOpen);
            Show(nearbyStrip, mapClear && !cardOpen && nearbyStrip.childCount > 0);
            Show(page, onMap && panel != null);
            Show(nav, onMap && !cardOpen);
            Show(arUi, !onMap);
            if (!mapClear) banner.style.display = DisplayStyle.None;
        }

        static void Show(VisualElement e, bool show) => e.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;

        void NavTapped(string id)
        {
            switch (id)
            {
                case "map":
                    CloseCard();
                    ClosePanel();
                    break;
                case "scan":
                    Scan();
                    break;
                default:
                    OpenPanel(id);
                    break;
            }
        }

        void SetNavSelected(string id)
        {
            foreach (var item in nav.Children()) item.EnableInClassList("selected", item.name == id);
            Icon.Repaint(nav);
        }

        void OpenPanel(string id)
        {
            panel = id;
            card.style.display = DisplayStyle.None;
            Preview.SetVisible(false);
            SetNavSelected(id == "drop" ? "map" : id);
            RenderPanel();
            pageScroll.scrollOffset = Vector2.zero;
            UpdateChrome();
            if (id == "bag") _ = RefreshBag();
            if (id == "me") _ = RefreshPlayer();
            if (id == "quests") _ = RefreshQuests();
        }

        void ClosePanel()
        {
            panel = null;
            Preview.SetVisible(false);
            SetNavSelected("map");
            UpdateChrome();
        }

        void CloseCard()
        {
            card.style.display = DisplayStyle.None;
            Map.SetRoute(null);
            MarkSelectedPin(null);
            UpdateChrome();
        }

        /// <summary>The middle button: AR on the closest drop within reach, or the way to the nearest one.</summary>
        void Scan()
        {
            if (!Location.HasFix)
            {
                ShowToast("Finding your location…");
                return;
            }
            var open = nearby.Where(d => !d.Mine && !(d.Captured && d.QuestName == null)).OrderBy(DistanceTo).ToList();
            var reachable = open.FirstOrDefault(CloseEnough);
            if (reachable != null)
            {
                ClosePanel();
                OpenInAr(reachable);
                return;
            }
            if (open.Count == 0)
            {
                ShowToast("Nothing to scan around here yet.");
                return;
            }
            ClosePanel();
            SelectDrop(open[0]);
            ShowToast($"Nothing close enough yet. The nearest is {Words.Distance(DistanceTo(open[0]))} away.");
        }

        // ================================================================ every frame

        void Update()
        {
            if (Location.HasFix) Map.SetPlayer(Location.Lat, Location.Lng, Location.Heading);
            status.text = Location.Denied ? "Hololoco needs your location to show what's around."
                : !Location.HasFix ? "Finding your location…" : "";
            Show(status, status.text.Length > 0 && mode == Mode.Map && panel == null);
            if (toast.style.display == DisplayStyle.Flex && Time.time > toastUntil) toast.style.display = DisplayStyle.None;
            if (banner.style.display == DisplayStyle.Flex && Time.time > bannerUntil) banner.style.display = DisplayStyle.None;
            if (compassIcon != null) compassIcon.style.rotate = new Rotate(new Angle(-Map.Bearing, AngleUnit.Degree));

            if (Location.HasFix)
            {
                var moved = Geo.Distance(lastLat, lastLng, Location.Lat, Location.Lng);
                if (!fetching && (moved > 25f || Time.time - lastFetch > 30f)) _ = RefreshNearby(Location.Lat, Location.Lng);
                var movedFar = double.IsNaN(foundLat) ? float.MaxValue : Geo.Distance(foundLat, foundLng, Location.Lat, Location.Lng);
                if (!fetchingFound && (movedFar > 1000f || Time.time - foundAt > 300f)) _ = RefreshFound(Location.Lat, Location.Lng);
            }

            if (mode == Mode.Map && card.style.display == DisplayStyle.Flex && selectedDrop != null) RenderCardDistance();
            if (mode == Mode.Map && panel == null) PlacePins();
            if (mode != Mode.Map) UpdateAr();
            HandleArTap();
        }

        /// <summary>In AR, tapping the item captures it (or picks it up), the same as the button.</summary>
        void HandleArTap()
        {
            if (mode != Mode.Encounter || Time.time - arSince < 0.5f || Input.touchCount != 1) return;
            var touch = Input.GetTouch(0);
            if (touch.phase != TouchPhase.Ended || OverUi(touch.position)) return;
            if (Physics.Raycast(ArCamera.ScreenPointToRay(touch.position), out var hit, 40f, ~(1 << MapView.Layer | 1 << Preview.Layer))
                && Encounter.IsPart(hit.collider) && CanTake(encounterDrop))
                _ = TakeInAr();
        }

        float arWordsAt;

        void UpdateAr()
        {
            if (Time.time - arWordsAt < 0.25f) return;
            arWordsAt = Time.time;
            if (Encounter.Finding && findingWords != null) findingWords.text = Encounter.FindingWords;
            if (arChipWords != null && Encounter.Position != null) arChipWords.text = WhichWay();
        }

        /// <summary>Where the item is from here: "Right here", "12 m ahead", "To your left · 12 m"…</summary>
        string WhichWay()
        {
            var at = Encounter.Position;
            if (at == null) return "";
            var cam = ArCamera.transform;
            var to = Vector3.ProjectOnPlane(at.Value - cam.position, Vector3.up);
            var metres = to.magnitude;
            var ahead = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            var angle = ahead.sqrMagnitude > 0.001f ? Vector3.SignedAngle(ahead, to, Vector3.up) : 0f;
            var far = Words.Distance(metres);
            if (Mathf.Abs(angle) <= 30f) return metres < 4f ? "Right here" : $"{far} ahead";
            if (Mathf.Abs(angle) >= 140f) return $"Behind you · {far}";
            return angle > 0 ? $"To your right · {far}" : $"To your left · {far}";
        }

        // ================================================================ pins on the map

        static string SpotKey(DropInfo d) =>
            d.Lat.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "," +
            d.Lng.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

        static string PlaceName(DropInfo d) =>
            d.Found != null && !string.IsNullOrEmpty(d.Found.Venue) ? d.Found.Venue : "Here";

        /// <summary>
        /// One pin per spot: drops within about 10 m share a pin, which shows how many.
        /// Tapping a shared pin lists everything there.
        /// </summary>
        void SyncPins(List<DropInfo> shown)
        {
            spots.Clear();
            foreach (var drop in shown)
            {
                var key = SpotKey(drop);
                if (!spots.TryGetValue(key, out var here)) spots[key] = here = new List<DropInfo>();
                here.Add(drop);
            }
            foreach (var key in pins.Keys.Where(k => !spots.ContainsKey(k)).ToList())
            {
                pins[key].pin.RemoveFromHierarchy();
                pins.Remove(key);
            }
            foreach (var (key, here) in spots)
            {
                var first = here[0];
                var count = here.Count;
                var label = count == 1 ? first.Title : $"{PlaceName(first)} · {count}";
                if (pins.TryGetValue(key, out var entry))
                {
                    // The same pin while it says the same thing; drawn again when it changes.
                    if (entry.pin.userData as string == label) { pins[key] = (entry.pin, first); continue; }
                    entry.pin.RemoveFromHierarchy();
                    pins.Remove(key);
                }
                var pin = Div("pin " + RarityClass("pin", first.Type, first.Rarity));
                pin.userData = label;
                if (here.All(d => d.Mine)) pin.AddToClassList("mine");
                if (first.Found != null) pin.AddToClassList("pin-event");
                var words = Text(label, "pin-label strong one-line");
                words.pickingMode = PickingMode.Ignore;
                pin.Add(words);
                var disc = Div("pin-disc");
                disc.pickingMode = PickingMode.Ignore;
                disc.Add(new Icon(Icon.For(first.Type, first.ObjectId), 22));
                pin.Add(disc);
                var tail = Div("pin-tail");
                tail.pickingMode = PickingMode.Ignore;
                pin.Add(tail);
                if (count > 1)
                {
                    var badge = Text(count > 99 ? "99+" : count.ToString(), "pin-count");
                    badge.pickingMode = PickingMode.Ignore;
                    pin.Add(badge);
                }
                var spot = key;
                pin.AddManipulator(new Clickable(() => OpenSpot(spot)));
                pinLayer.Add(pin);
                pins[key] = (pin, first);
            }
            MarkSelectedPin(selectedPin);
            Icon.Repaint(pinLayer);
        }

        void OpenSpot(string key)
        {
            if (!spots.TryGetValue(key, out var here) || here.Count == 0) return;
            if (here.Count == 1) SelectDrop(here[0]);
            else ShowSpot(key, here);
        }

        /// <summary>Everything at one spot, in a list to scroll: events soonest first. A row opens its card.</summary>
        void ShowSpot(string key, List<DropInfo> here)
        {
            selectedDrop = null;
            MarkSelectedPin(key);
            Map.SetRoute(null);
            panel = null;
            SetNavSelected("map");
            card.Clear();
            card.style.display = DisplayStyle.Flex;

            card.Add(Div("handle"));
            var first = here[0];
            var head = Div("spot-head");
            head.Add(Text($"{here.Count} here", "chip chip-plain"));
            head.Add(Text(PlaceName(first) == "Here" ? "Things at this spot" : PlaceName(first), "display"));
            if (first.Found != null && !string.IsNullOrEmpty(first.Found.Address)) head.Add(Text(first.Found.Address, "muted mt-2"));
            card.Add(head);
            var close = Div("close-x");
            close.Add(new Icon("close", 20));
            close.AddManipulator(new Clickable(CloseCard));
            card.Add(close);

            var list = new ScrollView(ScrollViewMode.Vertical)
            {
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Hidden,
            };
            list.AddToClassList("spot-list");
            foreach (var drop in here.OrderBy(d => d.Found?.Start ?? "").ThenBy(d => d.Title))
            {
                var row = Div("spot-row");
                row.Add(Thumb(drop.Type, drop.Rarity, drop.ObjectId));
                var words = Div("near-text");
                words.Add(drop.Found != null ? EventChip(drop.Found) : Chip(drop.Type, drop.Rarity));
                words.Add(Text(drop.Title, "strong lead one-line"));
                words.Add(Text(drop.Found != null ? Words.When(drop.Found) : drop.OwnerName, "muted small one-line mt-2"));
                row.Add(words);
                row.Add(new Icon("chevron", 18));
                var chosen = drop;
                row.AddManipulator(new Clickable(() => SelectDrop(chosen)));
                list.Add(row);
            }
            card.Add(list);
            Icon.Repaint(card);
            UpdateChrome();
        }

        /// <summary>Moves each pin to its drop's spot on screen; nearer pins drawn on top.</summary>
        void PlacePins()
        {
            if (root.panel == null || pins.Count == 0) return;
            var depths = new List<(float z, VisualElement pin)>();
            foreach (var (pin, drop) in pins.Values)
            {
                if (!Map.ScreenOf(drop, out var s))
                {
                    pin.style.visibility = Visibility.Hidden;
                    continue;
                }
                var p = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(s.x, Screen.height - s.y));
                pin.style.visibility = Visibility.Visible;
                pin.style.translate = new Translate(p.x - 26f, p.y - 64f);
                depths.Add((s.z, pin));
            }
            if (Time.time - pinsSortedAt < 0.5f) return;
            pinsSortedAt = Time.time;
            foreach (var (_, pin) in depths.OrderByDescending(d => d.z)) pin.BringToFront();
            if (selectedPin != null && pins.TryGetValue(selectedPin, out var chosen)) chosen.pin.BringToFront();
        }

        void MarkSelectedPin(string id)
        {
            selectedPin = id;
            foreach (var (key, entry) in pins) entry.pin.EnableInClassList("selected", key == id);
        }

        // ================================================================ data

        async Task RefreshNearby(double lat, double lng)
        {
            fetching = true;
            lastFetch = Time.time;
            try
            {
                var result = await Supabase.Rpc("app_nearby", new JObject { ["lat"] = lat, ["lng"] = lng, ["radius_m"] = 1500 });
                lastLat = lat;
                lastLng = lng;
                var drops = ((JArray)result["drops"]).Select(DropInfo.From);
                var loose = (result["items"] as JArray ?? new JArray()).Select(LooseOrNull).Where(d => d != null);
                nearby = drops.Concat(loose).ToList();
                nearbyQuests = result["quests"] as JArray ?? new JArray();
                Map.SightRadius = (float?)result["radius"] ?? Map.SightRadius;
                if (result["player"] is JObject p) SetPlayer(p);
                ApplyFilter();
                AlertIfNear();
            }
            catch (Exception e)
            {
                if (nearby.Count == 0) ShowToast(Words.For(e));
            }
            finally
            {
                fetching = false;
            }
        }

        /// <summary>Events and giveaways around town: a wider look than app_nearby, refreshed every few minutes.</summary>
        async Task RefreshFound(double lat, double lng)
        {
            fetchingFound = true;
            foundAt = Time.time;
            try
            {
                var result = await Supabase.Rpc("app_found_nearby", new JObject { ["lat"] = lat, ["lng"] = lng, ["radius_m"] = 15000 });
                foundLat = lat;
                foundLng = lng;
                aroundTown = (result as JArray ?? new JArray()).Select(DropInfo.From).ToList();
                ApplyFilter();
            }
            catch (Exception)
            {
                // Keep what was there; the next refresh tries again.
            }
            finally
            {
                fetchingFound = false;
            }
        }

        // ================================================================ map filters

        void SetFilter(string id)
        {
            if (id == pinFilter) return;
            pinFilter = id;
            PlayerPrefs.SetString("hl.pins", id);
            MarkFilter();
            CloseCard();
            ApplyFilter();
        }

        void MarkFilter()
        {
            foreach (var chip in filters.Children()) chip.EnableInClassList("selected", chip.name == pinFilter);
        }

        /// <summary>Everything worth a pin: what's in sight, plus events around town not already in it.</summary>
        List<DropInfo> Everything()
        {
            var ids = new HashSet<string>(nearby.Select(d => d.Id));
            return nearby.Concat(aroundTown.Where(d => !ids.Contains(d.Id))).ToList();
        }

        bool InReach(DropInfo d) => eventWithin <= 0f || Metres(d) <= eventWithin;

        bool Passes(DropInfo d) => pinFilter switch
        {
            "events" => d.Found != null && InReach(d) && (eventType == null || d.Found.Category == eventType),
            "business" => d.Found == null && !d.Loose && d.OwnerKind == "business",
            "quests" => d.QuestName != null,
            "mine" => d.Mine,
            _ => true,
        };

        /// <summary>Pins, the map's drops and the cards along the bottom, for the chosen filter.</summary>
        void ApplyFilter()
        {
            var everything = Everything();
            var shown = everything.Where(Passes).ToList();
            if (pinFilter == "events") RenderEventFilters(everything.Where(d => d.Found != null && InReach(d)).ToList());
            Map.ShowDrops(shown);
            SyncPins(shown);
            RenderNearby(shown);
        }

        /// <summary>Distance (tap to change) and the kinds of event there are, with how many of each.</summary>
        void RenderEventFilters(List<DropInfo> events)
        {
            eventFilters.Clear();
            var distance = new Tap(eventWithin <= 0f ? "Any distance" : $"Within {eventWithin / 1000f:0} km", () =>
            {
                var at = Array.IndexOf(EventDistances, eventWithin);
                eventWithin = EventDistances[(at + 1) % EventDistances.Length];
                ApplyFilter();
            });
            distance.AddToClassList("filter");
            distance.EnableInClassList("selected", eventWithin > 0f);
            eventFilters.Add(distance);

            var any = new Tap("Any type", () =>
            {
                eventType = null;
                ApplyFilter();
            });
            any.AddToClassList("filter");
            any.EnableInClassList("selected", eventType == null);
            eventFilters.Add(any);

            foreach (var kind in events.GroupBy(d => d.Found.Category).OrderByDescending(g => g.Count()))
            {
                var category = kind.Key;
                var chip = new Tap($"{Words.Category(category)}  {kind.Count()}", () =>
                {
                    eventType = eventType == category ? null : category;
                    ApplyFilter();
                });
                chip.AddToClassList("filter");
                chip.EnableInClassList("selected", eventType == category);
                eventFilters.Add(chip);
            }
        }

        static DropInfo LooseOrNull(JToken j)
        {
            try
            {
                return DropInfo.FromLoose(j);
            }
            catch (Exception)
            {
                return null;
            }
        }

        async Task RefreshBag()
        {
            if (!Supabase.SignedIn) return;
            try
            {
                bag = (JArray)await Supabase.Rpc("my_inventory") ?? new JArray();
                if (await Supabase.Rpc("my_player") is JObject p) SetPlayer(p);
                if (panel == "bag") RenderPanel();
            }
            catch (Exception e)
            {
                ShowToast(Words.For(e));
            }
        }

        async Task RefreshQuests()
        {
            if (!Supabase.SignedIn) return;
            try
            {
                myQuests = await Supabase.Rpc("my_quests") as JArray ?? new JArray();
                if (panel == "quests") RenderPanel();
            }
            catch (Exception e)
            {
                ShowToast(Words.For(e));
            }
        }

        async Task RefreshPlayer()
        {
            if (!Supabase.SignedIn) return;
            try
            {
                if (await Supabase.Rpc("my_player") is JObject p) SetPlayer(p);
            }
            catch (Exception)
            {
                // The level pill stays as it was.
            }
        }

        void SetPlayer(JObject p)
        {
            player = p;
            var level = (int?)p["level"] ?? 1;
            levelText.text = $"Level {level}";
            levelFill.style.width = Length.Percent(LevelProgress() * 100f);
            avatarLetter.text = Initial();
            Map.SightRadius = (float?)p["sightMetres"] ?? Map.SightRadius;
            if (panel == "me") RenderPanel();
        }

        float LevelProgress()
        {
            if (player == null) return 0;
            var xp = (int?)player["xp"] ?? 0;
            var from = (int?)player["levelXp"] ?? 0;
            var to = (int?)player["nextLevelXp"] ?? 100;
            return Mathf.Clamp01((xp - from) / (float)Mathf.Max(to - from, 1));
        }

        string Initial()
        {
            var name = (string)player?["handle"];
            if (string.IsNullOrEmpty(name)) name = Supabase.Email;
            return string.IsNullOrEmpty(name) ? "?" : char.ToUpperInvariant(name[0]).ToString();
        }

        float DistanceTo(DropInfo d) => Geo.Distance(Location.Lat, Location.Lng, d.Lat, d.Lng);
        bool CloseEnough(DropInfo d) => Location.HasFix && DistanceTo(d) <= 40f + Mathf.Min(Location.Accuracy, 30f);
        float Metres(DropInfo d) => Location.HasFix ? DistanceTo(d) : d.Distance;
        static string Walk(float metres) => $"{Mathf.Max(1, Mathf.RoundToInt(metres / 80f))} min walk";

        // ================================================================ nearby cards

        void RenderNearby(List<DropInfo> shown)
        {
            nearbyStrip.Clear();
            // Your own drops only under Mine; elsewhere they're on the map but not in the cards.
            var others = shown.Where(d => pinFilter == "mine" || !d.Mine).ToList();
            // Events soonest first; everything else nearest first.
            var ordered = pinFilter == "events"
                ? others.OrderBy(d => d.Found?.Start ?? "").ThenBy(Metres)
                : others.OrderBy(Metres);
            foreach (var drop in ordered.Take(pinFilter == "events" ? 12 : 6))
            {
                var c = Div("near-card");
                c.Add(Thumb(drop.Type, drop.Rarity, drop.ObjectId));
                var words = Div("near-text");
                words.Add(drop.Found != null ? EventChip(drop.Found) : Chip(drop.Type, drop.Rarity));
                words.Add(Text(drop.Title, "strong lead one-line"));
                words.Add(Text(drop.Found != null
                    ? $"{Words.When(drop.Found)} · {Words.Distance(Metres(drop))}"
                    : $"{drop.OwnerName} · {Walk(Metres(drop))}", "muted small one-line mt-2"));
                c.Add(words);
                var chosen = drop;
                c.AddManipulator(new Clickable(() => SelectDrop(chosen)));
                nearbyStrip.Add(c);
            }
            if (others.Count == 0 && pinFilter != "all")
            {
                var empty = Div("near-card near-empty");
                empty.Add(Text(pinFilter switch
                {
                    "events" => "No events around here yet. Hololoco is finding events in Austin first.",
                    "business" => "No business drops nearby yet.",
                    "quests" => "No quests start near here yet.",
                    _ => "Nothing of yours nearby. Tap + to leave a message.",
                }, "muted"));
                nearbyStrip.Add(empty);
            }
            // "All" counts what's in sight; the events around town have their own filter.
            var count = pinFilter == "all" ? others.Count(d => d.Found == null || Metres(d) <= Map.SightRadius) : others.Count;
            countPill.text = pinFilter switch
            {
                "events" => count == 1 ? "1 event around town" : $"{count} events around town",
                "business" => count == 1 ? "1 business drop" : $"{count} business drops",
                "quests" => count == 1 ? "1 quest stop" : $"{count} quest stops",
                "mine" => count == 1 ? "1 of yours" : $"{count} of yours",
                _ => count == 1 ? "1 thing nearby" : $"{count} things nearby",
            };
            Show(countPill, count > 0);
            UpdateChrome();
        }

        static Label EventChip(FoundInfo found) =>
            Text(found.Free ? $"{Words.Category(found.Category)} · Free" : Words.Category(found.Category), "chip chip-warm");

        // ================================================================ near-a-drop alerts

        void LoadAlerted()
        {
            var key = "hl.alerted." + DateTime.Now.ToString("yyyyMMdd");
            alerted = new HashSet<string>(PlayerPrefs.GetString(key, "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        }

        void AlertIfNear()
        {
            if (PlayerPrefs.GetInt("hl.alerts", 1) != 1 || mode != Mode.Map || panel != null) return;
            var near = nearby.FirstOrDefault(d => !d.Mine && !d.Captured && !alerted.Contains(d.Id) && DistanceTo(d) <= 50f);
            if (near == null) return;
            alerted.Add(near.Id);
            PlayerPrefs.SetString("hl.alerted." + DateTime.Now.ToString("yyyyMMdd"), string.Join(",", alerted));
#if UNITY_ANDROID || UNITY_IOS
            Handheld.Vibrate();
#endif
            banner.Clear();
            banner.Add(new Icon("pin", 26));
            var words = Div("grow ml-12");
            words.Add(Text($"You're near {near.Title}", "strong"));
            words.Add(Text(near.Savings ?? "Tap to have a look.", "muted small mt-2"));
            banner.Add(words);
            bannerDrop = near;
            banner.style.display = DisplayStyle.Flex;
            bannerUntil = Time.time + 8f;
        }

        // ================================================================ a drop's card

        Label cardDistance, cardWalk;

        void SelectDrop(DropInfo drop)
        {
            selectedDrop = drop;
            var spotKey = SpotKey(drop);
            MarkSelectedPin(spotKey);
            Map.SetRoute(null);
            panel = null;
            SetNavSelected("map");
            card.Clear();
            card.style.display = DisplayStyle.Flex;

            card.Add(Div("handle"));
            // From a shared pin: the way back to everything there.
            if (spots.TryGetValue(spotKey, out var neighbours) && neighbours.Count > 1)
                card.Add(Action($"‹ All {neighbours.Count} at {PlaceName(neighbours[0])}", () => ShowSpot(spotKey, neighbours), "link back-link"));
            var head = Div("row");
            head.Add(Thumb(drop.Type, drop.Rarity, drop.ObjectId, true));
            var info = Div("head-text");
            info.Add(drop.Found != null ? EventChip(drop.Found) : Chip(drop.Type, drop.Rarity));
            info.Add(Text(drop.Title, "display"));
            info.Add(Text(drop.Found != null ? drop.Found.Venue : drop.OwnerName, "muted mt-2"));
            head.Add(info);
            card.Add(head);
            var close = Div("close-x");
            close.Add(new Icon("close", 20));
            close.AddManipulator(new Clickable(CloseCard));
            card.Add(close);

            if (drop.Savings != null) card.Add(Text(drop.Savings, "strong lead mt-16"));
            if (drop.Found != null)
            {
                var when = Words.When(drop.Found);
                if (!string.IsNullOrEmpty(drop.Found.TimeNote)) when += $"\n{drop.Found.TimeNote}";
                card.Add(Notice("calendar", when, "accent"));
                var where = string.IsNullOrEmpty(drop.Found.Price) ? drop.Found.Address : $"{drop.Found.Address}\n{drop.Found.Price}";
                if (!string.IsNullOrEmpty(where)) card.Add(Text(where, "muted mt-8"));
            }
            if (!string.IsNullOrEmpty(drop.Description)) card.Add(Text(drop.Description, "muted mt-8"));

            var stats = Div("stats");
            cardDistance = Stat(stats, "", "away");
            cardWalk = Stat(stats, "", "walk");
            if (drop.ValueNowCents >= 0) Stat(stats, Words.Money(drop.ValueNowCents), "worth now");
            card.Add(stats);

            if (drop.ValueNowCents >= 0 && drop.DecayPercent > 0)
                card.Add(Notice("clock", Words.Value(drop.ValueNowCents, drop.DecayPercent, drop.DecayDays), "warm"));
            if (drop.QuestName != null)
                card.Add(Notice("quests", $"Stop {drop.QuestStage} of {drop.QuestStages} of {drop.QuestName}", "accent"));

            var buttons = Div("stack mt-4");
            buttons.name = "buttons";
            card.Add(buttons);
            RenderCardDistance(true);
            UpdateChrome();
        }

        bool lastClose;
        void RenderCardDistance(bool force = false)
        {
            var drop = selectedDrop;
            var close = CloseEnough(drop);
            if (Location.HasFix)
            {
                var metres = DistanceTo(drop);
                cardDistance.text = Words.Distance(metres);
                cardWalk.text = $"{Mathf.Max(1, Mathf.RoundToInt(metres / 80f))} min";
            }
            else
            {
                cardDistance.text = "…";
                cardWalk.text = "…";
            }
            if (!force && close == lastClose) return;
            lastClose = close;
            var buttons = card.Q("buttons");
            buttons.Clear();
            if (drop.Loose) buttons.Add(Text(drop.Mine ? "You left this here. Pick it back up any time." : "Someone left this here for anyone to pick up.", "muted mt-12"));
            else if (drop.Mine) buttons.Add(Text("This one is yours.", "muted mt-12"));
            else if (drop.Captured && drop.QuestName == null) buttons.Add(Text("You've collected this one.", "muted mt-12"));
            if (close) buttons.Add(Action(CanTake(drop) ? "Open in AR" : "See it in AR", () => OpenInAr(drop), drop.Rarity == "rare" ? "rare" : "primary"));
            else
            {
                buttons.Add(Action("Walk there", () => _ = WalkTo(drop), "primary"));
                buttons.Add(Text("Get within about 40 m to open it in AR.", "muted small center mt-8"));
            }
            // An event's own page, and where to get tickets, open in the browser.
            var page = drop.Found?.Url;
            var tickets = drop.Found?.TicketUrl;
            if (page != null) buttons.Add(Action("Event page", () => Application.OpenURL(page), "secondary"));
            if (tickets != null && tickets != page) buttons.Add(Action("Tickets", () => Application.OpenURL(tickets), "secondary"));
        }

        async Task WalkTo(DropInfo drop)
        {
            if (!Location.HasFix) return;
            var url = $"https://api.mapbox.com/directions/v5/mapbox/walking/{Location.Lng},{Location.Lat};{drop.Lng},{drop.Lat}" +
                      $"?geometries=geojson&overview=full&access_token={Config.MapboxToken}";
            using var request = UnityWebRequest.Get(url);
            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (request.result != UnityWebRequest.Result.Success)
            {
                ShowToast("Couldn't find a walking route.");
                return;
            }
            var route = JObject.Parse(request.downloadHandler.text)["routes"]?.FirstOrDefault();
            if (route == null)
            {
                ShowToast("Couldn't find a walking route.");
                return;
            }
            var points = ((JArray)route["geometry"]["coordinates"]).Select(c => new Vector2((float)(double)c[1], (float)(double)c[0])).ToList();
            Map.SetRoute(points);
            Map.Recenter();
            var minutes = Mathf.Max(1, Mathf.RoundToInt((float)(double)route["duration"] / 60f));
            ShowToast($"{minutes} min walk · {Words.Distance((float)(double)route["distance"])}");
        }

        // ================================================================ AR

        void OpenInAr(DropInfo drop)
        {
            if (!Supabase.SignedIn)
            {
                ShowSignIn();
                return;
            }
            encounterDrop = drop;
            card.style.display = DisplayStyle.None;
            Map.SetRoute(null);
            SetMode(Mode.Encounter);
            RenderArLooking();
            StartCoroutine(ShowEncounter(drop));
        }

        IEnumerator ShowEncounter(DropInfo drop)
        {
            // A first find goes at its spot on the map; one that's yours or already collected
            // goes right in front of you, like the bag's View in AR.
            yield return Encounter.Show(drop, CanTake(drop));
            if (mode == Mode.Encounter && encounterDrop == drop) RenderArChoice();
        }

        /// <summary>Whether this drop can go in your bag from AR: a capture, or picking up a left item.</summary>
        static bool CanTake(DropInfo d) => d != null && (d.Loose || (!d.Mine && !(d.Captured && d.QuestName == null)));

        Label arChipWords;

        VisualElement ArTop(Action close, string chipIcon = null, string chipText = null)
        {
            var row = Div("ar-top");
            row.pickingMode = PickingMode.Ignore;
            row.Add(Round("close", close));
            arChipWords = null;
            if (chipText != null)
            {
                var chip = Div("ar-chip");
                chip.Add(new Icon(chipIcon, 18));
                arChipWords = Text(chipText, "strong");
                chip.Add(arChipWords);
                row.Add(chip);
            }
            return row;
        }

        Label findingWords;

        /// <summary>The moment before it appears: AR tracking, the floor and the compass.</summary>
        void RenderArLooking()
        {
            arUi.Clear();
            arUi.Add(ArTop(() => SetMode(Mode.Map)));
            var hint = Div("ar-hint");
            hint.pickingMode = PickingMode.Ignore;
            hint.Add(new Icon("scan", 40));
            findingWords = Text("Starting the camera…", "strong center mt-12");
            findingWords.pickingMode = PickingMode.Ignore;
            hint.Add(findingWords);
            var help = Text("It'll appear in a moment and stay right there while you walk around it.", "muted small center mt-4");
            help.pickingMode = PickingMode.Ignore;
            hint.Add(help);
            arUi.Add(hint);
            arUi.Add(Spacer());
        }

        void RenderArChoice()
        {
            arUi.Clear();
            var drop = encounterDrop;
            findingWords = null;
            var take = CanTake(drop);
            arUi.Add(ArTop(() => { if (take) StartCoroutine(LeaveInAr()); else SetMode(Mode.Map); }, "pin", WhichWay()));
            arUi.Add(Spacer());
            var box = Div("ar-card");
            box.Add(Chip(drop.Type, drop.Rarity));
            box.Add(Text(drop.Title, "display"));
            box.Add(Text(drop.Savings ?? drop.OwnerName, "muted mt-4"));
            var verb = drop.Loose ? "Pick it up" : "Capture";
            box.Add(Text(take ? $"It stays right there. Walk up to it, then tap it or {verb}."
                : drop.Mine ? "This one is yours. It stays right there; walk around it."
                : "You've collected this one. It stays right there; walk around it.", "muted small mt-8"));
            var buttons = Div("buttons");
            if (take)
            {
                buttons.Add(Action("Leave it", () => StartCoroutine(LeaveInAr()), "secondary"));
                buttons.Add(Action(verb, () => _ = TakeInAr(), drop.Rarity == "rare" ? "rare" : "primary"));
            }
            else
            {
                buttons.Add(Action("Photo", TakePhoto, "secondary"));
                buttons.Add(Action("Done", () => SetMode(Mode.Map), "primary"));
            }
            box.Add(buttons);
            arUi.Add(box);
        }

        bool capturing;

        Task TakeInAr() => encounterDrop?.Loose == true ? PickUpInAr() : CaptureInAr();

        /// <summary>Picks up an item left on the map (someone else's, or your own back).</summary>
        async Task PickUpInAr()
        {
            if (capturing || encounterDrop?.ItemId == null) return;
            capturing = true;
            var drop = encounterDrop;
            try
            {
                var result = await Supabase.Rpc("pick_up_item", new JObject
                {
                    ["item_id"] = drop.ItemId, ["lat"] = Location.Lat, ["lng"] = Location.Lng,
                    ["accuracy_m"] = Mathf.Max(Location.Accuracy, 1f),
                });
                if (result["player"] is JObject p) SetPlayer(p);
                arUi.Clear();
                StartCoroutine(AfterCapture(drop, result));
                lastFetch = -999f;
            }
            catch (Exception e)
            {
                ShowToast(Words.For(e));
            }
            finally
            {
                capturing = false;
            }
        }

        async Task CaptureInAr()
        {
            if (capturing || encounterDrop == null) return;
            capturing = true;
            var drop = encounterDrop;
            try
            {
                var result = await Supabase.Rpc("capture", new JObject
                {
                    ["drop_id"] = drop.Id, ["lat"] = Location.Lat, ["lng"] = Location.Lng,
                    ["accuracy_m"] = Mathf.Max(Location.Accuracy, 1f),
                });
                if (result["player"] is JObject p) SetPlayer(p);
                arUi.Clear();
                StartCoroutine(AfterCapture(drop, result));
                lastFetch = -999f;
            }
            catch (Exception e)
            {
                ShowToast(Words.For(e));
            }
            finally
            {
                capturing = false;
            }
        }

        IEnumerator AfterCapture(DropInfo drop, JToken result)
        {
            yield return Encounter.Captured(drop.Json);
            arUi.Clear();
            arUi.Add(ArTop(() => SetMode(Mode.Map)));
            arUi.Add(Spacer());
            var box = Div("ar-card");
            var kept = result["item"]?.Type == JTokenType.Object;
            var xp = (int?)result["xp"] ?? 0;
            box.Add(Text(drop.Loose && drop.Mine ? "Back in your bag" : kept ? "Added to your bag" : "Collected", "strong accent"));
            if (xp > 0) box.Add(Text($"+{xp} XP", drop.Rarity == "rare" ? "big rare-ink" : "big"));
            box.Add(Text(drop.Title, "display"));
            if (result["quest"] is JObject q)
                box.Add(Notice("quests", (bool?)q["completed"] == true
                    ? $"Quest complete · +{(int?)result["points"] ?? 0} points"
                    : $"Quest stop {(int?)q["stage"]} of {(int?)q["stageCount"]} done", "accent"));
            foreach (var e in (JArray)result["events"] ?? new JArray())
                box.Add(Notice("pin", (bool?)e["completed"] == true ? "Event finished" : $"Event stop {(int?)e["found"]} of {(int?)e["total"]}", "accent"));
            if (((JArray)result["rewards"])?.Count > 0) box.Add(Notice("gift", "You got a reward too. It's in your bag.", "rare"));
            var buttons = Div("buttons");
            buttons.Add(Action("Photo", TakePhoto, "secondary"));
            buttons.Add(Action("Done", () => SetMode(Mode.Map), "primary"));
            box.Add(buttons);
            arUi.Add(box);
        }

        IEnumerator LeaveInAr()
        {
            arUi.Clear();
            yield return Encounter.Left();
            SetMode(Mode.Map);
        }

        void ViewItemInAr(JToken item)
        {
            SetMode(Mode.ViewItem);
            RenderArLooking();
            StartCoroutine(ShowItemInAr(item));
        }

        IEnumerator ShowItemInAr(JToken item)
        {
            yield return Encounter.ShowItem(item["drop"]);
            if (mode != Mode.ViewItem) yield break;
            arUi.Clear();
            findingWords = null;
            arUi.Add(ArTop(() => SetMode(Mode.Map), "pin", WhichWay()));
            arUi.Add(Spacer());
            var box = Div("ar-card");
            box.Add(Text((string)item["title"], "display"));
            box.Add(Text("Walk around it and take a photo.", "muted mt-4"));
            var buttons = Div("buttons");
            buttons.Add(Action("Photo", TakePhoto, "secondary"));
            buttons.Add(Action("Done", () => SetMode(Mode.Map), "primary"));
            box.Add(buttons);
            arUi.Add(box);
        }

        void TakePhoto()
        {
            StartCoroutine(Share.Photo(
                () => root.style.display = DisplayStyle.None,
                () => root.style.display = DisplayStyle.Flex,
                ShowToast));
        }

        // ================================================================ pages

        void RenderPanel()
        {
            pageScroll.Clear();
            switch (panel)
            {
                case "quests": RenderQuests(); break;
                case "bag": RenderBag(); break;
                case "drop": RenderDrop(); break;
                case "me": RenderMe(); break;
            }
        }

        void P(VisualElement e) => pageScroll.Add(e);

        void SignInPrompt(string why)
        {
            var box = Div("card");
            box.Add(Text(why, "muted"));
            var buttons = Div("stack");
            buttons.Add(Action("Sign in", ShowSignIn, "primary"));
            box.Add(buttons);
            P(box);
        }

        // ---------------------------------------------------------------- quests

        static bool Missing(JToken t) => t == null || t.Type == JTokenType.Null;

        void RenderQuests()
        {
            P(Text("Quests", "h1"));
            if (!Supabase.SignedIn)
            {
                SignInPrompt("Sign in to follow quests and keep your progress.");
                return;
            }

            var active = myQuests.FirstOrDefault(q => Missing(q.At("progress").At("completedAt")));
            if (active != null) P(ActiveQuest(active));
            else
            {
                var none = Div("card");
                none.Add(Text("You're not on a quest yet", "strong lead"));
                none.Add(Text("Collect the first stop of a quest to start it. Quests near you are below.", "muted mt-4"));
                P(none);
            }

            var startedIds = new HashSet<string>(myQuests.Select(q => (string)q["id"]));
            var near = nearbyQuests.Where(q => !startedIds.Contains((string)q["id"])).ToList();
            if (near.Count > 0)
            {
                P(Text("More quests nearby", "strong section-title"));
                foreach (var q in near)
                {
                    var stages = q["stages"] as JArray ?? new JArray();
                    var first = stages.FirstOrDefault()?.At("drop");
                    var startsAt = (string)first.At("owner").At("name");
                    P(QuestRow((string)q["name"], $"{stages.Count} stops{(startsAt != null ? $" · starts at {startsAt}" : "")}", () => ShowQuestStop(first)));
                }
            }

            var finished = myQuests.Where(q => !Missing(q.At("progress").At("completedAt"))).ToList();
            if (finished.Count > 0)
            {
                P(Text("Finished", "strong section-title"));
                foreach (var q in finished)
                    P(QuestRow((string)q["name"], $"{(q["stages"] as JArray)?.Count ?? 0} stops · done", null));
            }
        }

        VisualElement ActiveQuest(JToken quest)
        {
            var stages = quest["stages"] as JArray ?? new JArray();
            var done = (int?)quest.At("progress").At("stage") ?? 0;
            var box = Div("card");
            box.Add(Text("You're on", "strong small accent"));
            box.Add(Text((string)quest["name"], "display mt-2"));
            box.Add(Text($"Stop {Mathf.Min(done + 1, stages.Count)} of {stages.Count} · finish for 300 XP", "muted mt-4"));

            var steps = Div("mt-16");
            for (var i = 0; i < stages.Count; i++)
            {
                var stage = stages[i];
                var drop = stage.At("drop");
                var last = i == stages.Count - 1;
                var row = Div("step");
                var rail = Div("step-rail");
                var node = Div("step-node");
                if (i < done)
                {
                    node.AddToClassList("done");
                    node.Add(new Icon("check", 16));
                }
                else if (last && (string)stage["rarity"] == "rare")
                {
                    node.AddToClassList("prize");
                    node.Add(new Icon("star", 14));
                }
                if (i == done) node.AddToClassList("current");
                rail.Add(node);
                if (!last)
                {
                    var line = Div("step-line");
                    if (i < done) line.AddToClassList("done");
                    rail.Add(line);
                }
                row.Add(rail);
                var words = Div("step-text");
                var name = (string)drop.At("owner").At("name") ?? "A stop that isn't open now";
                words.Add(Text(name, i == done ? "strong" : "muted"));
                if (i == done && !Missing(drop))
                {
                    var d = DropInfo.From(drop);
                    words.Add(Text(Location.HasFix ? $"Next stop · {Walk(DistanceTo(d))}" : "Next stop", "small accent mt-2"));
                }
                row.Add(words);
                steps.Add(row);
            }
            box.Add(steps);

            if (done < stages.Count)
            {
                var next = stages[done].At("drop");
                var buttons = Div("stack");
                buttons.Add(Action("Show me the way", () => ShowQuestStop(next), "primary"));
                box.Add(buttons);
            }
            return box;
        }

        VisualElement QuestRow(string name, string detail, Action tapped)
        {
            var row = Div("list-row");
            var words = Div("grow");
            words.Add(Text(name, "strong"));
            words.Add(Text(detail, "muted small mt-2"));
            row.Add(words);
            if (tapped != null)
            {
                row.Add(new Icon("chevron", 20));
                row.AddManipulator(new Clickable(tapped));
            }
            return row;
        }

        void ShowQuestStop(JToken drop)
        {
            if (Missing(drop))
            {
                ShowToast("That stop isn't open right now.");
                return;
            }
            var d = DropInfo.From(drop);
            ClosePanel();
            SelectDrop(d);
            _ = WalkTo(d);
        }

        // ---------------------------------------------------------------- bag

        void RenderBag()
        {
            var used = (int?)player?["bagUsed"] ?? bag.Count(i => (string)i["status"] == "held");
            var slots = (int?)player?["bagSlots"] ?? 10;
            var head = Div("row");
            head.Add(Text("Your bag", "h1 grow"));
            if (Supabase.SignedIn)
            {
                var right = new VisualElement();
                right.style.alignItems = Align.FlexEnd;
                right.Add(Text($"{used} of {slots} spaces", "strong small"));
                var dots = Div("dots");
                for (var i = 0; i < slots && i < 20; i++) dots.Add(Div(i < used ? "dot" : "dot empty"));
                right.Add(dots);
                head.Add(right);
            }
            P(head);
            if (!Supabase.SignedIn)
            {
                SignInPrompt("Sign in to start collecting.");
                return;
            }

            var segments = Div("segments");
            foreach (var (id, label) in new[] { ("all", "All"), ("offer", "Offers"), ("object", "Objects") })
            {
                var s = new Tap(label, () => { bagFilter = id; RenderPanel(); });
                s.AddToClassList("segment");
                s.EnableInClassList("selected", bagFilter == id);
                segments.Add(s);
            }
            P(segments);

            var held = bag.Where(i => (string)i["status"] != "discarded")
                .Where(i => bagFilter == "all" || (bagFilter == "offer" ? (string)i["kind"] == "offer" : (string)i["kind"] != "offer"))
                .ToList();
            if (held.Count == 0)
            {
                var empty = Div("card");
                empty.Add(Text(bag.Count == 0 ? "Nothing yet. Walk to something on the map and capture it." : "Nothing here.", "muted"));
                P(empty);
            }
            else
            {
                var grid = Div("grid");
                foreach (var item in held) grid.Add(BagTile(item));
                P(grid);
            }
            if (slots - used > 0) P(Text(slots - used == 1 ? "Room for 1 more find" : $"Room for {slots - used} more finds", "room"));
        }

        VisualElement BagTile(JToken item)
        {
            var kind = (string)item["kind"];
            var rarity = (string)item["rarity"];
            var drop = item["drop"];
            var status = (string)item["status"];
            var tile = Div("tile");
            var inner = Div("tile-inner");
            var art = Div("tile-art " + RarityClass("thumb", kind, rarity));
            art.Add(new Icon(Icon.For(kind, (string)drop.At("objectId")), 48));
            var badge = Text(status == "redeemed" ? "Redeemed" : status == "dropped" ? "On the map"
                : kind == "offer" ? Words.Capital(rarity ?? "common") : kind == "message" ? "Message" : "3D object",
                "tile-badge " + RarityClass("chip", kind, rarity));
            art.Add(badge);
            inner.Add(art);
            inner.Add(Text((string)item["title"], "strong tile-name"));
            inner.Add(Text((string)drop.At("owner").At("name") ?? "", "muted small tile-sub one-line"));
            tile.Add(inner);
            tile.AddManipulator(new Clickable(() => ShowItem(item)));
            return tile;
        }

        /// <summary>One item from the bag, turning in 3D, with what you can do with it.</summary>
        void ShowItem(JToken item)
        {
            var box = Overlay("at-bottom");
            var overlay = box.parent;
            void Close()
            {
                overlay.RemoveFromHierarchy();
                Preview.SetVisible(false);
            }

            var preview = Div("preview");
            preview.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(Preview.Texture));
            box.Add(preview);
            Preview.Show(item["drop"]);
            Preview.SetVisible(true);

            var kind = (string)item["kind"];
            var status = (string)item["status"];
            var offer = item["drop"].At("offer");
            var chip = Chip(kind, (string)item["rarity"]);
            chip.AddToClassList("mt-16");
            box.Add(chip);
            box.Add(Text((string)item["title"], "display"));
            var savings = (string)offer.At("savings");
            if (savings != null) box.Add(Text(savings, "strong lead mt-8"));
            var worth = Words.Value((int?)item["valueNowCents"] ?? -1, (int?)offer.At("decayPercent") ?? 0, (int?)offer.At("decayDays") ?? 7);
            if (worth != null) box.Add(Text(worth, "muted mt-4"));

            var actions = Div("stack mt-4");
            if (status == "held")
            {
                if (kind == "offer") actions.Add(Action("Redeem", () => { Close(); _ = Redeem(item); }, "primary"));
                actions.Add(Action("View in AR", () => { Close(); ViewItemInAr(item); }, "secondary"));
                if ((bool?)item["tradeable"] ?? true) actions.Add(Action("Leave it here for someone", () => { Close(); _ = LeaveHere(item); }, "secondary"));
                actions.Add(Action("Throw away", () => { Close(); Confirm($"Throw away {(string)item["title"]}?", () => _ = ThrowAway(item)); }, "danger"));
            }
            else
            {
                box.Add(Text(status == "redeemed" ? "Redeemed." : "Left on the map. Go back to it to pick it up again.", "muted mt-12"));
                actions.Add(Action("View in AR", () => { Close(); ViewItemInAr(item); }, "secondary"));
            }
            actions.Add(Action("Close", Close, "link"));
            box.Add(actions);
        }

        // ---------------------------------------------------------------- leave a message

        void RenderDrop()
        {
            P(Text("Leave a message", "h1"));
            if (!Supabase.SignedIn)
            {
                SignInPrompt("Sign in to leave messages for people who come by.");
                return;
            }
            P(Text("It goes right where you're standing, for anyone who comes by.", "muted mt-8"));
            var title = new TextField("Title") { maxLength = 70 };
            var message = new TextField("Message") { multiline = true, maxLength = 400 };
            message.AddToClassList("multiline");
            P(title);
            P(message);
            P(Text("How it looks", "strong small mt-16"));
            var chosen = "note";
            var looks = Div("looks-picker");
            foreach (var (id, name) in Looks)
            {
                var b = new Tap(name, null);
                b.AddToClassList("look-chip");
                b.clicked += () =>
                {
                    chosen = id;
                    foreach (var other in looks.Children()) other.EnableInClassList("selected", other == b);
                };
                b.EnableInClassList("selected", id == chosen);
                looks.Add(b);
            }
            P(looks);
            var error = Text("", "error mt-8");
            var buttons = Div("stack");
            buttons.Add(Action("Drop it here", () => _ = PostMessage(title.value, message.value, chosen, error), "primary"));
            buttons.Add(Action("Cancel", ClosePanel, "link"));
            P(buttons);
            P(error);
        }

        async Task PostMessage(string title, string message, string look, Label error)
        {
            error.text = "";
            if (string.IsNullOrWhiteSpace(title))
            {
                error.text = "Give it a title.";
                return;
            }
            if (!Location.HasFix)
            {
                error.text = "Finding your location…";
                return;
            }
            var payload = new JObject
            {
                ["type"] = "message", ["title"] = title.Trim(), ["description"] = message?.Trim() ?? "",
                ["objectId"] = look, ["assetId"] = null,
                ["placement"] = new JObject { ["lat"] = Location.Lat, ["lng"] = Location.Lng, ["height"] = 0, ["size"] = 2, ["turn"] = Mathf.Round(Location.Heading) % 360 },
                ["timeZone"] = TimeZoneId(), ["timing"] = new JObject { ["kind"] = "always" }, ["audience"] = "everyone",
            };
            try
            {
                await Supabase.Rpc("save_drop", new JObject { ["payload"] = payload });
                ShowToast("Dropped. People nearby can find it now.");
                ClosePanel();
                lastFetch = -999f;
            }
            catch (HololocoError e) when (e.Code == "profile_incomplete")
            {
                ShowProfileForm(() => _ = PostMessage(title, message, look, error));
            }
            catch (Exception e)
            {
                error.text = e is HololocoError h && h.Code.StartsWith("limit_")
                    ? "You've reached your plan's limit for drops. Delete one on hololoco.com, or try tomorrow."
                    : Words.For(e);
            }
        }

        static string TimeZoneId()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var zone = new AndroidJavaClass("java.util.TimeZone");
            using var current = zone.CallStatic<AndroidJavaObject>("getDefault");
            return current.Call<string>("getID");
#else
            return TimeZoneInfo.Local.Id;
#endif
        }

        // ---------------------------------------------------------------- me

        void RenderMe()
        {
            var profile = Div("profile");
            var avatar = Text(Initial(), "avatar avatar-big");
            profile.Add(avatar);
            var who = Div("grow ml-12");
            var handle = (string)player?["handle"];
            who.Add(Text(!string.IsNullOrEmpty(handle) ? "@" + handle : Supabase.SignedIn ? "You" : "Not signed in", "display"));
            if (player != null) who.Add(Text($"Level {(int?)player["level"] ?? 1} · {(int?)player["xp"] ?? 0} XP", "muted mt-2"));
            profile.Add(who);
            P(profile);

            if (!Supabase.SignedIn) SignInPrompt("Sign in to capture drops, keep them, and redeem offers.");
            else if (player != null)
            {
                var xp = (int?)player["xp"] ?? 0;
                var to = (int?)player["nextLevelXp"] ?? 100;
                var stats = Div("stats on-page");
                Stat(stats, $"{Mathf.Max(to - xp, 0)}", $"XP to level {((int?)player["level"] ?? 1) + 1}");
                Stat(stats, $"{(int?)player["points"] ?? 0}", "points");
                Stat(stats, $"{(int?)player["captures"] ?? 0}", "collected");
                P(stats);
                var bar = Div("progress mt-12");
                var fill = Div("progress-fill");
                fill.style.width = Length.Percent(LevelProgress() * 100f);
                bar.Add(fill);
                P(bar);
            }

            // The look: one card per skin.
            P(Text("Look", "strong section-title"));
            var looks = Div("looks-row");
            foreach (var skin in Skin.All)
            {
                var choice = Div("look");
                choice.EnableInClassList("selected", skin == Skin.Current);
                choice.Add(Text(skin.Name, "strong small"));
                var swatches = Div("swatches");
                foreach (var colour in new[] { skin.Page, skin.Accent, skin.Rare })
                {
                    var swatch = Div("swatch");
                    swatch.style.backgroundColor = colour;
                    swatches.Add(swatch);
                }
                choice.Add(swatches);
                var chosen = skin;
                choice.AddManipulator(new Clickable(() => { if (Skin.Current != chosen) Skin.Set(chosen); }));
                looks.Add(choice);
            }
            P(looks);
            P(Text(Skin.Current.Blurb, "muted small mt-8"));

            P(Text("Settings", "strong section-title"));
            P(Toggle("Tell me when I'm near a drop", "hl.alerts", null));
            P(Toggle("3D buildings on the map", "hl.buildings", on => Map.SetBuildings(on)));

            if (Supabase.SignedIn && player != null)
            {
                P(Text("Trading name", "strong section-title"));
                var name = new TextField("People trade with you by this name") { value = (string)player["handle"] ?? "" };
                P(name);
                var save = Div("stack");
                save.Add(Action("Save name", async () =>
                {
                    try
                    {
                        if (await Supabase.Rpc("set_handle", new JObject { ["handle"] = name.value }) is JObject p) SetPlayer(p);
                        ShowToast("Saved.");
                    }
                    catch (Exception e) { ShowToast(Words.For(e)); }
                }, "secondary"));
                P(save);
                P(Text($"Signed in as {Supabase.Email}", "muted small mt-24"));
                var signOut = Div("stack");
                signOut.Add(Action("Sign out", () =>
                {
                    Supabase.SignOut();
                    player = null;
                    bag = new JArray();
                    myQuests = new JArray();
                    levelText.text = "Level 1";
                    levelFill.style.width = 0;
                    avatarLetter.text = "?";
                    RenderPanel();
                }, "link"));
                P(signOut);
            }
        }

        VisualElement Toggle(string label, string key, Action<bool> changed)
        {
            var toggle = new UnityEngine.UIElements.Toggle(label) { value = PlayerPrefs.GetInt(key, 1) == 1 };
            toggle.AddToClassList("switch");
            toggle.RegisterValueChangedCallback(e =>
            {
                PlayerPrefs.SetInt(key, e.newValue ? 1 : 0);
                PlayerPrefs.Save();
                changed?.Invoke(e.newValue);
            });
            return toggle;
        }

        // ================================================================ bag actions

        async Task Redeem(JToken item)
        {
            try
            {
                var answer = await Supabase.Rpc("start_redemption", new JObject { ["item_id"] = (string)item["id"] });
                ShowRedeem(item, answer);
            }
            catch (Exception e) { ShowToast(Words.For(e)); }
        }

        async Task LeaveHere(JToken item)
        {
            if (!Location.HasFix)
            {
                ShowToast("Finding your location…");
                return;
            }
            try
            {
                await Supabase.Rpc("drop_item", new JObject { ["item_id"] = (string)item["id"], ["lat"] = Location.Lat, ["lng"] = Location.Lng });
                ShowToast("Left here for someone else. It comes back to you in 7 days if nobody picks it up.");
                await RefreshBag();
            }
            catch (Exception e) { ShowToast(Words.For(e)); }
        }

        async Task ThrowAway(JToken item)
        {
            try
            {
                await Supabase.Rpc("discard_item", new JObject { ["item_id"] = (string)item["id"] });
                await RefreshBag();
            }
            catch (Exception e) { ShowToast(Words.For(e)); }
        }

        // ================================================================ overlays

        VisualElement Overlay(string where = null)
        {
            var overlay = Div("overlay");
            if (where != null) overlay.AddToClassList(where);
            var box = Div("dialog");
            overlay.Add(box);
            root.Add(overlay);
            toast.BringToFront();
            return box;
        }

        void ShowSignIn()
        {
            root.Query(className: "overlay").ForEach(o => o.RemoveFromHierarchy());
            var box = Overlay("at-top");
            var overlay = box.parent;
            box.Add(Text("Sign in to Hololoco", "display"));
            box.Add(Text("Use the same account as the Hololoco website. We'll email you a 6-digit code.", "muted mt-8"));
            var email = new TextField("Email") { value = Supabase.Email, keyboardType = TouchScreenKeyboardType.EmailAddress };
            var code = new TextField("Code from the email") { keyboardType = TouchScreenKeyboardType.NumberPad };
            code.style.display = DisplayStyle.None;
            var error = Text("", "error mt-8");
            Tap send = null, verify = null;
            send = Action("Email me a code", async () =>
            {
                error.text = "";
                send.SetEnabled(false);
                try
                {
                    await Supabase.SendCode(email.value);
                    code.style.display = DisplayStyle.Flex;
                    verify.style.display = DisplayStyle.Flex;
                    send.text = "Send a new code";
                    send.RemoveFromClassList("primary");
                    send.AddToClassList("secondary");
                }
                catch (Exception e) { error.text = Words.For(e); }
                send.SetEnabled(true);
            }, "primary");
            verify = Action("Sign in", async () =>
            {
                error.text = "";
                verify.SetEnabled(false);
                try
                {
                    await Supabase.VerifyCode(email.value, code.value);
                    overlay.RemoveFromHierarchy();
                    await RefreshPlayer();
                    lastFetch = -999f;
                    if (panel != null) RenderPanel();
                }
                catch (Exception e) { error.text = Words.For(e); }
                verify.SetEnabled(true);
            }, "primary");
            verify.style.display = DisplayStyle.None;
            box.Add(email);
            box.Add(code);
            var buttons = Div("stack");
            buttons.Add(send);
            buttons.Add(verify);
            buttons.Add(Action("Not now", () => overlay.RemoveFromHierarchy(), "link"));
            box.Add(buttons);
            box.Add(error);
        }

        /// <summary>Posting needs a name shown on drops and an 18+ confirmation, as on the website.</summary>
        void ShowProfileForm(Action then)
        {
            var box = Overlay("at-top");
            var overlay = box.parent;
            box.Add(Text("One more thing", "display"));
            box.Add(Text("Before you post, choose the name shown on your drops.", "muted mt-8"));
            var name = new TextField("Name shown on your drops") { maxLength = 40 };
            var adult = new UnityEngine.UIElements.Toggle("I'm 18 or older");
            adult.AddToClassList("switch");
            var error = Text("", "error mt-8");
            box.Add(name);
            box.Add(adult);
            var buttons = Div("stack");
            buttons.Add(Action("Save", async () =>
            {
                if (string.IsNullOrWhiteSpace(name.value) || !adult.value)
                {
                    error.text = "Add a name and confirm you're 18 or older.";
                    return;
                }
                try
                {
                    await Supabase.UpdateProfile(new JObject { ["display_name"] = name.value.Trim(), ["adult_confirmed_at"] = DateTime.UtcNow.ToString("o") });
                    overlay.RemoveFromHierarchy();
                    then();
                }
                catch (Exception e) { error.text = Words.For(e); }
            }, "primary"));
            buttons.Add(Action("Cancel", () => overlay.RemoveFromHierarchy(), "link"));
            box.Add(buttons);
            box.Add(error);
        }

        /// <summary>The code to show at the counter, until the business confirms it or it runs out.</summary>
        void ShowRedeem(JToken item, JToken answer)
        {
            var box = Overlay("at-bottom");
            var overlay = box.parent;
            box.Add(Text("Show this at the counter", "strong small accent center"));
            box.Add(Text((string)item["drop"].At("offer").At("savings") ?? (string)item["title"], "display center mt-8"));
            box.Add(Text($"at {(string)answer["business"]}", "muted center mt-2"));
            var code = (string)answer["code"];
            box.Add(Text($"{code.Substring(0, 3)} {code.Substring(3)}", "code"));
            var bar = Div("progress");
            var fill = Div("progress-fill");
            bar.Add(fill);
            box.Add(bar);
            var expires = DateTime.Parse((string)answer["expiresAt"]).ToUniversalTime();
            var left = Text("", "muted center mt-8");
            box.Add(left);
            box.Add(Text("They type the code at hololoco.com/redeem. It leaves your bag once they confirm.", "muted small center mt-12"));
            var terms = (string)item["drop"].At("offer").At("terms");
            if (!string.IsNullOrEmpty(terms)) box.Add(Text("Terms: " + terms, "muted small center mt-8"));
            var buttons = Div("stack");
            buttons.Add(Action("Close", () => overlay.RemoveFromHierarchy(), "secondary"));
            box.Add(buttons);

            var itemId = (string)item["id"];
            var total = Mathf.Max((float)(expires - DateTime.UtcNow).TotalSeconds, 1f);
            var done = false;
            overlay.schedule.Execute(async () =>
            {
                if (done) return;
                var remaining = expires - DateTime.UtcNow;
                left.text = remaining.TotalSeconds > 0 ? $"Good for {remaining.Minutes}:{remaining.Seconds:00}" : "This code has run out. Close and tap Redeem again.";
                fill.style.width = Length.Percent(Mathf.Clamp01((float)remaining.TotalSeconds / total) * 100f);
                if ((int)remaining.TotalSeconds % 4 != 0) return;
                try
                {
                    var items = (JArray)await Supabase.Rpc("my_inventory");
                    if ((string)items?.FirstOrDefault(i => (string)i["id"] == itemId).At("status") != "redeemed") return;
                    done = true;
                    box.Clear();
                    box.Add(Text("Redeemed", "big accent center"));
                    box.Add(Text("Enjoy it.", "muted center mt-8"));
                    var close = Div("stack");
                    close.Add(Action("Close", () => { overlay.RemoveFromHierarchy(); _ = RefreshBag(); }, "primary"));
                    box.Add(close);
                }
                catch (Exception)
                {
                    // Try again on the next tick.
                }
            }).Every(1000);
        }

        void Confirm(string question, Action yes)
        {
            var box = Overlay();
            var overlay = box.parent;
            box.Add(Text(question, "display"));
            var buttons = Div("buttons");
            buttons.Add(Action("No", () => overlay.RemoveFromHierarchy(), "secondary"));
            buttons.Add(Action("Yes", () => { overlay.RemoveFromHierarchy(); yes(); }, "primary"));
            box.Add(buttons);
        }

        void ShowToast(string text)
        {
            toast.text = text;
            toast.style.display = DisplayStyle.Flex;
            toast.BringToFront();
            toastUntil = Time.time + 4f;
        }

        // ================================================================ small helpers

        /// <summary>A tappable label: the app's buttons, free of the default theme's button styles.</summary>
        class Tap : Label
        {
            public event Action clicked;

            public Tap(string text, Action onClick) : base(text)
            {
                if (onClick != null) clicked += onClick;
                this.AddManipulator(new Clickable(() => clicked?.Invoke()));
            }
        }

        /// <summary>Empty room in the AR layout that lets taps through to AR.</summary>
        static VisualElement Spacer()
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.style.flexGrow = 1;
            return e;
        }

        static VisualElement Div(string classes = null)
        {
            var element = new VisualElement();
            AddClasses(element, classes);
            return element;
        }

        static void AddClasses(VisualElement element, string classes)
        {
            if (classes == null) return;
            foreach (var c in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) element.AddToClassList(c);
        }

        static Label Text(string text, string classes = null)
        {
            var label = new Label(text);
            label.AddToClassList("text");
            AddClasses(label, classes);
            return label;
        }

        static Tap Action(string text, Action onClick, string classes = null)
        {
            var button = new Tap(text, onClick);
            button.AddToClassList("action");
            AddClasses(button, classes);
            return button;
        }

        static VisualElement Round(string icon, Action onClick)
        {
            var button = Div("round");
            button.Add(new Icon(icon, 22));
            button.AddManipulator(new Clickable(onClick));
            return button;
        }

        static string RarityClass(string prefix, string type, string rarity) =>
            type == "offer" ? $"{prefix}-{(rarity is "rare" or "uncommon" ? rarity : "common")}" : $"{prefix}-plain";

        static VisualElement Thumb(string type, string rarity, string objectId, bool big = false)
        {
            var thumb = Div("thumb " + RarityClass("thumb", type, rarity) + (big ? " thumb-big" : ""));
            thumb.Add(new Icon(Icon.For(type, objectId), big ? 42 : 34));
            return thumb;
        }

        static Label Chip(string type, string rarity)
        {
            var words = type == "offer" ? Words.Capital(rarity ?? "common") : type == "message" ? "Message" : "3D object";
            var chip = Text(words, "chip " + RarityClass("chip", type, rarity));
            return chip;
        }

        static Label Stat(VisualElement stats, string value, string label)
        {
            var box = Div("stat");
            var number = Text(value, "stat-value");
            box.Add(number);
            box.Add(Text(label, "muted small"));
            stats.Add(box);
            return number;
        }

        static VisualElement Notice(string icon, string words, string tone)
        {
            var notice = Div("notice notice-" + tone);
            notice.Add(new Icon(icon, 20));
            notice.Add(Text(words, "strong"));
            return notice;
        }
    }
}
