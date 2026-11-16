// The Hololoco app, following old Coupemon's flow with today's tools:
//
//   Map (home)  a tilted dark map with your avatar and a gem for each drop nearby.
//               Tap a gem for its card: "Open in AR" when you're close, or "Walk there".
//   AR          "Hold your phone up": the drop appears in front of you. Capture it
//               (an offer's gem breaks open to reveal the item) or Leave it. Take a photo.
//   Bag         what you've collected, turning in 3D: Redeem (a code for the counter),
//               View in AR, Leave here (for someone else), Throw away.
//   Drop        leave a message where you're standing.
//   Me          level, XP, your trading name, settings, sign out.
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
        string panel; // null (just the map), "bag", "drop" or "me"

        VisualElement root, top, bar, sheet, card, arUi, banner;
        Label status, chip, toast;
        float toastUntil, bannerUntil;

        List<DropInfo> nearby = new();
        JArray bag = new();
        JObject player;
        JToken selectedItem;
        DropInfo selectedDrop;
        DropInfo encounterDrop;
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
            root = Document.rootVisualElement;
            var style = Resources.Load<StyleSheet>("hololoco");
            if (style != null) root.styleSheets.Add(style);
            root.AddToClassList("app");
            root.pickingMode = PickingMode.Ignore;
            LoadAlerted();
            BuildChrome();
            Map.IsOverUi = OverUi;
            Map.Tapped += SelectDrop;
            Map.SetBuildings(PlayerPrefs.GetInt("hl.buildings", 1) == 1);
            Map.SetGameMap(PlayerPrefs.GetInt("hl.gamemap", 0) == 1);
            SetMode(Mode.Map);
            if (!Supabase.SignedIn) ShowSignIn();
            _ = RefreshPlayer();
        }

        void BuildChrome()
        {
            top = Div("top");
            status = new Label { pickingMode = PickingMode.Ignore };
            status.AddToClassList("status");
            chip = new Label();
            chip.AddToClassList("chip");
            chip.style.display = DisplayStyle.None;
            chip.RegisterCallback<ClickEvent>(_ => OpenPanel("me"));
            top.Add(chip);
            root.Add(top);
            root.Add(status);

            banner = Div("banner");
            banner.style.display = DisplayStyle.None;
            banner.RegisterCallback<ClickEvent>(_ =>
            {
                banner.style.display = DisplayStyle.None;
                if (bannerDrop != null) SelectDrop(bannerDrop);
            });
            root.Add(banner);

            toast = new Label { pickingMode = PickingMode.Ignore };
            toast.AddToClassList("toast");
            toast.style.display = DisplayStyle.None;
            root.Add(toast);

            sheet = Div("sheet");
            sheet.style.display = DisplayStyle.None;
            root.Add(sheet);

            card = Div("card-sheet");
            card.style.display = DisplayStyle.None;
            root.Add(card);

            bar = Div("bar");
            foreach (var (id, icon) in new[] { ("map", "icon_map"), ("bag", "icon_bag"), ("recenter", "icon_recenter"), ("drop", "icon_drop"), ("me", "icon_me") })
            {
                var button = new Button(() => BarTapped(id));
                button.AddToClassList("hex-icon");
                if (id == "recenter") button.AddToClassList("hex-icon-big");
                button.style.backgroundImage = new StyleBackground(Resources.Load<Texture2D>("Art/" + icon));
                button.name = id;
                bar.Add(button);
            }
            root.Add(bar);

            arUi = Div("ar");
            arUi.style.display = DisplayStyle.None;
            root.Add(arUi);
        }

        bool OverUi(Vector2 screen)
        {
            if (root?.panel == null) return false;
            var p = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y));
            var picked = root.panel.Pick(p);
            return picked != null && picked != root;
        }

        // ================================================================ modes

        void SetMode(Mode next)
        {
            mode = next;
            var ar = next != Mode.Map;
            Session.enabled = ar;
            ArCamera.enabled = ar;
            MapCamera.enabled = !ar;
            if (ar) RenderSettings.fog = false;
            top.style.display = ar ? DisplayStyle.None : DisplayStyle.Flex;
            status.style.visibility = ar ? Visibility.Hidden : Visibility.Visible;
            bar.style.display = ar ? DisplayStyle.None : DisplayStyle.Flex;
            arUi.style.display = ar ? DisplayStyle.Flex : DisplayStyle.None;
            if (ar)
            {
                sheet.style.display = DisplayStyle.None;
                card.style.display = DisplayStyle.None;
                Preview.SetVisible(false);
            }
            else
            {
                Encounter.Clear();
                if (panel != null) OpenPanel(panel);
            }
        }

        void BarTapped(string id)
        {
            switch (id)
            {
                case "map": ClosePanel(); break;
                case "recenter": ClosePanel(); Map.Recenter(); break;
                default:
                    if (panel == id) ClosePanel();
                    else OpenPanel(id);
                    break;
            }
        }

        void OpenPanel(string id)
        {
            panel = id;
            card.style.display = DisplayStyle.None;
            sheet.style.display = DisplayStyle.Flex;
            foreach (var b in bar.Children()) b.EnableInClassList("selected", b.name == id);
            Preview.SetVisible(id == "bag");
            RenderPanel();
            if (id == "bag") _ = RefreshBag();
            if (id == "me") _ = RefreshPlayer();
        }

        void ClosePanel()
        {
            panel = null;
            sheet.style.display = DisplayStyle.None;
            Preview.SetVisible(false);
            foreach (var b in bar.Children()) b.EnableInClassList("selected", b.name == "map");
        }

        // ================================================================ every frame

        void Update()
        {
            if (Location.HasFix) Map.SetPlayer(Location.Lat, Location.Lng, Location.Heading);
            status.text = Location.Denied ? "Hololoco needs your location to show what's around."
                : !Location.HasFix ? "Finding your location…" : "";
            status.style.display = status.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (toast.style.display == DisplayStyle.Flex && Time.time > toastUntil) toast.style.display = DisplayStyle.None;
            if (banner.style.display == DisplayStyle.Flex && Time.time > bannerUntil) banner.style.display = DisplayStyle.None;

            if (Location.HasFix)
            {
                var moved = Geo.Distance(lastLat, lastLng, Location.Lat, Location.Lng);
                if (!fetching && (moved > 25f || Time.time - lastFetch > 30f)) _ = RefreshNearby(Location.Lat, Location.Lng);
            }

            if (mode == Mode.Map && card.style.display == DisplayStyle.Flex && selectedDrop != null) RenderCardDistance();
            HandleArTap();
        }

        /// <summary>In AR, tapping the gem captures it too.</summary>
        void HandleArTap()
        {
            if (mode != Mode.Encounter || Input.touchCount != 1) return;
            var touch = Input.GetTouch(0);
            if (touch.phase != TouchPhase.Ended || OverUi(touch.position)) return;
            if (Physics.Raycast(ArCamera.ScreenPointToRay(touch.position), out var hit, 20f, ~(1 << MapView.Layer | 1 << Preview.Layer))
                && hit.collider.GetComponentInParent<Gem>() != null)
                _ = CaptureInAr();
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
                nearby = ((JArray)result["drops"]).Select(DropInfo.From).ToList();
                Map.SightRadius = (float?)result["radius"] ?? Map.SightRadius;
                Map.ShowDrops(nearby);
                if (result["player"] is JObject p) SetPlayer(p);
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

        async Task RefreshPlayer()
        {
            if (!Supabase.SignedIn) return;
            try
            {
                if (await Supabase.Rpc("my_player") is JObject p) SetPlayer(p);
            }
            catch (Exception)
            {
                // The chip stays as it was.
            }
        }

        void SetPlayer(JObject p)
        {
            player = p;
            chip.text = $"Lv {(int?)p["level"] ?? 1}  ·  {(int?)p["xp"] ?? 0} XP";
            Map.SightRadius = (float?)p["sightMetres"] ?? Map.SightRadius;
            chip.style.display = DisplayStyle.Flex;
            if (panel == "me") RenderPanel();
        }

        float DistanceTo(DropInfo d) => Geo.Distance(Location.Lat, Location.Lng, d.Lat, d.Lng);
        bool CloseEnough(DropInfo d) => Location.HasFix && DistanceTo(d) <= 40f + Mathf.Min(Location.Accuracy, 30f);

        // ================================================================ near-a-drop alerts

        void LoadAlerted()
        {
            var key = "hl.alerted." + DateTime.Now.ToString("yyyyMMdd");
            alerted = new HashSet<string>(PlayerPrefs.GetString(key, "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        }

        void AlertIfNear()
        {
            if (PlayerPrefs.GetInt("hl.alerts", 1) != 1 || mode != Mode.Map) return;
            var near = nearby.FirstOrDefault(d => !d.Mine && !d.Captured && !alerted.Contains(d.Id) && DistanceTo(d) <= 50f);
            if (near == null) return;
            alerted.Add(near.Id);
            PlayerPrefs.SetString("hl.alerted." + DateTime.Now.ToString("yyyyMMdd"), string.Join(",", alerted));
#if UNITY_ANDROID || UNITY_IOS
            Handheld.Vibrate();
#endif
            banner.Clear();
            banner.Add(Text($"You're near {near.Title}", "title"));
            banner.Add(Text(near.Savings ?? "Tap to have a look.", "muted"));
            bannerDrop = near;
            banner.style.display = DisplayStyle.Flex;
            bannerUntil = Time.time + 8f;
        }

        // ================================================================ a drop's card

        void SelectDrop(DropInfo drop)
        {
            selectedDrop = drop;
            Map.SetRoute(null);
            ClosePanel();
            card.Clear();
            card.style.display = DisplayStyle.Flex;

            var head = Div("row");
            var gemDot = Div("gem-dot");
            gemDot.style.backgroundColor = Gem.ColourFor(drop);
            head.Add(gemDot);
            var info = Div("grow");
            info.Add(Text(drop.Title, "title"));
            info.Add(Text($"{Words.Kind(drop)} · {drop.OwnerName}", "muted"));
            head.Add(info);
            head.Add(Action("×", () => { card.style.display = DisplayStyle.None; Map.SetRoute(null); }, "close"));
            card.Add(head);
            if (drop.Savings != null) card.Add(Text(drop.Savings, "savings"));
            var worth = Words.Value(drop.ValueNowCents, drop.DecayPercent, drop.DecayDays);
            if (worth != null) card.Add(Text(worth, "muted"));
            if (!string.IsNullOrEmpty(drop.Description)) card.Add(Text(drop.Description, "muted"));
            if (drop.QuestName != null) card.Add(Text($"Quest stage {drop.QuestStage} of {drop.QuestStages}: {drop.QuestName}", "muted"));
            var distance = Text("", "distance");
            distance.name = "distance";
            card.Add(distance);
            var buttons = Div("buttons-row");
            buttons.name = "buttons";
            card.Add(buttons);
            RenderCardDistance(true);
        }

        bool lastClose;
        void RenderCardDistance(bool force = false)
        {
            var drop = selectedDrop;
            var close = CloseEnough(drop) && !drop.Mine && !(drop.Captured && drop.QuestName == null);
            card.Q<Label>("distance").text = Location.HasFix ? $"{Words.Distance(DistanceTo(drop))} away" : "Finding your location…";
            if (!force && close == lastClose) return;
            lastClose = close;
            var buttons = card.Q("buttons");
            buttons.Clear();
            if (drop.Mine) buttons.Add(Text("This one is yours.", "muted"));
            else if (drop.Captured && drop.QuestName == null) buttons.Add(Text("You've captured this one.", "muted"));
            else if (close) buttons.Add(Action("Open in AR", () => OpenInAr(drop), "hex-primary"));
            else
            {
                buttons.Add(Action("Walk there", () => _ = WalkTo(drop), "hex-primary"));
                buttons.Add(Text("Get within about 40 m to open it in AR.", "muted"));
            }
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
            yield return Encounter.Show(drop);
            if (mode == Mode.Encounter && encounterDrop == drop) RenderArChoice();
        }

        void RenderArLooking()
        {
            arUi.Clear();
            var hint = Div("ar-hint");
            var picture = Div("ar-hint-picture");
            picture.style.backgroundImage = new StyleBackground(Resources.Load<Texture2D>("Art/hold_phone_up"));
            hint.Add(picture);
            hint.Add(Text("HOLD YOUR PHONE UP AND LOOK STRAIGHT AHEAD", "ar-hint-text"));
            arUi.Add(hint);
            var bottom = Div("ar-bottom");
            bottom.Add(Action("Back to the map", () => SetMode(Mode.Map), "hex-secondary"));
            arUi.Add(bottom);
        }

        void RenderArChoice()
        {
            arUi.Clear();
            var drop = encounterDrop;
            var head = Div("ar-head");
            head.Add(Text(drop.Title, "ar-title"));
            head.Add(Text(drop.Savings ?? Words.Kind(drop), "ar-sub"));
            arUi.Add(head);
            var bottom = Div("ar-bottom");
            bottom.Add(Action("CAPTURE", () => _ = CaptureInAr(), "hex-primary"));
            bottom.Add(Action("LEAVE", () => StartCoroutine(LeaveInAr()), "hex-secondary"));
            arUi.Add(bottom);
        }

        bool capturing;
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
            var lines = new List<string>();
            if (result["quest"] is JObject q)
                lines.Add((bool?)q["completed"] == true ? $"Quest complete! +{(int?)result["points"] ?? 0} points"
                    : $"Quest stage {(int?)q["stage"]} of {(int?)q["stageCount"]}");
            foreach (var e in (JArray)result["events"] ?? new JArray())
                lines.Add((bool?)e["completed"] == true ? "Event finished!" : $"Event stop {(int?)e["found"]} of {(int?)e["total"]}");
            if (((JArray)result["rewards"])?.Count > 0) lines.Add("You got a reward too. It's in your bag.");

            arUi.Clear();
            var head = Div("ar-head");
            head.Add(Text($"+{(int?)result["xp"] ?? 0} XP", "ar-xp"));
            head.Add(Text(result["item"]?.Type == JTokenType.Object ? $"{drop.Title} is in your bag." : drop.Title, "ar-title"));
            foreach (var line in lines) head.Add(Text(line, "ar-sub"));
            arUi.Add(head);
            var bottom = Div("ar-bottom");
            bottom.Add(Action("PHOTO", TakePhoto, "hex-secondary"));
            bottom.Add(Action("DONE", () => SetMode(Mode.Map), "hex-primary"));
            arUi.Add(bottom);
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
            arUi.Clear();
            var head = Div("ar-head");
            head.Add(Text((string)item["title"], "ar-title"));
            arUi.Add(head);
            var bottom = Div("ar-bottom");
            bottom.Add(Action("PHOTO", TakePhoto, "hex-secondary"));
            bottom.Add(Action("DONE", () => SetMode(Mode.Map), "hex-primary"));
            arUi.Add(bottom);
            StartCoroutine(Encounter.ShowItem(item["drop"]));
        }

        void TakePhoto()
        {
            StartCoroutine(Share.Photo(
                () => root.style.display = DisplayStyle.None,
                () => root.style.display = DisplayStyle.Flex,
                ShowToast));
        }

        // ================================================================ panels

        void RenderPanel()
        {
            sheet.Clear();
            switch (panel)
            {
                case "bag": RenderBag(); break;
                case "drop": RenderDrop(); break;
                case "me": RenderMe(); break;
            }
        }

        void RenderBag()
        {
            var slots = player != null ? $"  ·  {(int?)player["bagUsed"] ?? 0} / {(int?)player["bagSlots"] ?? 10}" : "";
            sheet.Add(Text("YOUR BAG" + slots, "sheet-title"));
            if (!Supabase.SignedIn)
            {
                sheet.Add(Text("Sign in to start collecting.", "muted"));
                sheet.Add(Action("Sign in", ShowSignIn, "hex-primary"));
                return;
            }
            var held = bag.Where(i => (string)i["status"] != "discarded").ToList();
            if (held.Count == 0)
            {
                sheet.Add(Text("Nothing yet. Walk to a gem on the map and capture it.", "muted"));
                return;
            }
            selectedItem ??= held[0];
            if (!held.Contains(selectedItem)) selectedItem = held[0];

            var preview = Div("preview");
            preview.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(Preview.Texture));
            sheet.Add(preview);
            Preview.Show(selectedItem["drop"]);

            var actions = Div("buttons-row");
            var kind = (string)selectedItem["kind"];
            var status = (string)selectedItem["status"];
            sheet.Add(Text((string)selectedItem["title"], "title"));
            var savings = (string)selectedItem["drop"].At("offer").At("savings");
            if (savings != null) sheet.Add(Text(savings, "savings"));
            var offer = selectedItem["drop"].At("offer");
            var worth = Words.Value((int?)selectedItem["valueNowCents"] ?? -1, (int?)offer.At("decayPercent") ?? 0, (int?)offer.At("decayDays") ?? 7);
            if (worth != null) sheet.Add(Text(worth, "muted"));
            if (status == "held")
            {
                var item = selectedItem;
                if (kind == "offer") actions.Add(Action("Redeem", () => _ = Redeem(item), "hex-primary"));
                actions.Add(Action("View in AR", () => ViewItemInAr(item), "hex-secondary"));
                if ((bool?)item["tradeable"] ?? true) actions.Add(Action("Leave here", () => _ = LeaveHere(item), "hex-secondary"));
                actions.Add(Action("Throw away", () => Confirm($"Throw away {(string)item["title"]}?", () => _ = ThrowAway(item)), "link"));
            }
            else sheet.Add(Text(status == "redeemed" ? "Redeemed." : "Left on the map for someone else.", "muted"));
            sheet.Add(actions);

            var list = new ScrollView();
            list.AddToClassList("list");
            foreach (var item in held)
            {
                var row = Div("item-row");
                row.EnableInClassList("selected", item == selectedItem);
                var dot = Div("gem-dot");
                dot.style.backgroundColor = Gem.ColourFor(new DropInfo { Type = (string)item["kind"], Rarity = (string)item["rarity"] });
                row.Add(dot);
                var info = Div("grow");
                info.Add(Text((string)item["title"], "item-title"));
                var words = (string)item["kind"] == "offer" ? $"{Words.Capital((string)item["rarity"] ?? "")} offer".Trim() : Words.Capital((string)item["kind"]);
                if ((string)item["status"] == "redeemed") words += " · redeemed";
                if ((string)item["status"] == "dropped") words += " · on the map";
                info.Add(Text(words, "muted"));
                row.Add(info);
                var chosen = item;
                row.RegisterCallback<ClickEvent>(_ => { selectedItem = chosen; RenderPanel(); });
                list.Add(row);
            }
            sheet.Add(list);
        }

        void RenderDrop()
        {
            sheet.Add(Text("DROP A MESSAGE", "sheet-title"));
            if (!Supabase.SignedIn)
            {
                sheet.Add(Text("Sign in to leave messages for people who come by.", "muted"));
                sheet.Add(Action("Sign in", ShowSignIn, "hex-primary"));
                return;
            }
            sheet.Add(Text("It goes right where you're standing, for anyone who comes by.", "muted"));
            var title = new TextField("Title") { maxLength = 70 };
            var message = new TextField("Message") { multiline = true, maxLength = 400 };
            message.AddToClassList("multiline");
            sheet.Add(title);
            sheet.Add(message);
            var chosen = "note";
            var looks = Div("looks");
            foreach (var (id, name) in Looks)
            {
                var b = Action(name, null, "look");
                b.clicked += () =>
                {
                    chosen = id;
                    foreach (var other in looks.Children()) other.EnableInClassList("selected", other == b);
                };
                b.EnableInClassList("selected", id == chosen);
                looks.Add(b);
            }
            sheet.Add(looks);
            var error = Text("", "error");
            sheet.Add(Action("Drop it here", () => _ = PostMessage(title.value, message.value, chosen, error), "hex-primary"));
            sheet.Add(error);
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
                ShowToast("Dropped! People nearby can find it now.");
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

        void RenderMe()
        {
            sheet.Add(Text("YOU", "sheet-title"));
            if (!Supabase.SignedIn)
            {
                sheet.Add(Text("Sign in to capture drops, keep them, and redeem offers.", "muted"));
                sheet.Add(Action("Sign in", ShowSignIn, "hex-primary"));
                return;
            }
            if (player != null)
            {
                var level = (int?)player["level"] ?? 1;
                var xp = (int?)player["xp"] ?? 0;
                var from = (int?)player["levelXp"] ?? 0;
                var to = (int?)player["nextLevelXp"] ?? 100;
                sheet.Add(Text($"Level {level}", "big"));
                var back = Div("bar-back");
                var fill = Div("bar-fill");
                fill.style.width = Length.Percent(Mathf.Clamp01((xp - from) / (float)Mathf.Max(to - from, 1)) * 100f);
                back.Add(fill);
                sheet.Add(back);
                sheet.Add(Text($"{xp} XP · {to - xp} to level {level + 1}", "muted"));
                sheet.Add(Text($"{(int?)player["captures"] ?? 0} captured · {(int?)player["questsDone"] ?? 0} quests · {(int?)player["points"] ?? 0} points", "muted"));

                var handle = new TextField("Your name for trading") { value = (string)player["handle"] ?? "" };
                sheet.Add(handle);
                sheet.Add(Action("Save name", async () =>
                {
                    try
                    {
                        if (await Supabase.Rpc("set_handle", new JObject { ["handle"] = handle.value }) is JObject p) SetPlayer(p);
                        ShowToast("Saved.");
                    }
                    catch (Exception e) { ShowToast(Words.For(e)); }
                }, "hex-secondary"));
            }
            sheet.Add(Toggle("Tell me when I'm near a drop", "hl.alerts", null));
            sheet.Add(Toggle("3D buildings on the map", "hl.buildings", on => Map.SetBuildings(on)));
            var game = new UnityEngine.UIElements.Toggle("Dark game map") { value = Map.GameMap };
            game.AddToClassList("switch");
            game.RegisterValueChangedCallback(e => SetGameMap(e.newValue));
            sheet.Add(game);
            sheet.Add(Text($"Signed in as {Supabase.Email}", "muted"));
            sheet.Add(Action("Sign out", () =>
            {
                Supabase.SignOut();
                player = null;
                bag = new JArray();
                chip.style.display = DisplayStyle.None;
                RenderPanel();
            }, "link"));
        }

        void SetGameMap(bool on)
        {
            PlayerPrefs.SetInt("hl.gamemap", on ? 1 : 0);
            PlayerPrefs.Save();
            Map.SetGameMap(on);
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
                selectedItem = null;
                await RefreshBag();
            }
            catch (Exception e) { ShowToast(Words.For(e)); }
        }

        // ================================================================ overlays

        VisualElement Overlay(bool atTop = false)
        {
            var overlay = Div("overlay");
            if (atTop) overlay.AddToClassList("at-top");
            var box = Div("dialog");
            overlay.Add(box);
            root.Add(overlay);
            return box;
        }

        void ShowSignIn()
        {
            root.Query(className: "overlay").ForEach(o => o.RemoveFromHierarchy());
            var box = Overlay(true);
            var overlay = box.parent;
            box.Add(Text("SIGN IN TO HOLOLOCO", "sheet-title"));
            box.Add(Text("Use the same account as the Hololoco website. We'll email you a 6-digit code.", "muted"));
            var email = new TextField("Email") { value = Supabase.Email, keyboardType = TouchScreenKeyboardType.EmailAddress };
            var code = new TextField("Code from the email") { keyboardType = TouchScreenKeyboardType.NumberPad };
            code.style.display = DisplayStyle.None;
            var error = Text("", "error");
            Button send = null, verify = null;
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
                }
                catch (Exception e) { error.text = Words.For(e); }
                send.SetEnabled(true);
            }, "hex-primary");
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
            }, "hex-primary");
            verify.style.display = DisplayStyle.None;
            box.Add(email);
            box.Add(send);
            box.Add(code);
            box.Add(verify);
            box.Add(error);
            box.Add(Action("Not now", () => overlay.RemoveFromHierarchy(), "link"));
        }

        /// <summary>Posting needs a name shown on drops and an 18+ confirmation, as on the website.</summary>
        void ShowProfileForm(Action then)
        {
            var box = Overlay(true);
            var overlay = box.parent;
            box.Add(Text("ONE MORE THING", "sheet-title"));
            box.Add(Text("Before you post, choose the name shown on your drops.", "muted"));
            var name = new TextField("Name shown on your drops") { maxLength = 40 };
            var adult = new UnityEngine.UIElements.Toggle("I'm 18 or older");
            adult.AddToClassList("switch");
            var error = Text("", "error");
            box.Add(name);
            box.Add(adult);
            box.Add(Action("Save", async () =>
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
            }, "hex-primary"));
            box.Add(error);
            box.Add(Action("Cancel", () => overlay.RemoveFromHierarchy(), "link"));
        }

        /// <summary>The code to show at the counter, until the business confirms it or it runs out.</summary>
        void ShowRedeem(JToken item, JToken answer)
        {
            var box = Overlay();
            var overlay = box.parent;
            box.Add(Text((string)item["drop"].At("offer").At("savings") ?? (string)item["title"], "title"));
            box.Add(Text($"Show this code at {(string)answer["business"]}. They type it in to redeem it.", "muted"));
            var code = (string)answer["code"];
            box.Add(Text($"{code.Substring(0, 3)} {code.Substring(3)}", "code"));
            var expires = DateTime.Parse((string)answer["expiresAt"]).ToUniversalTime();
            var left = Text("", "muted");
            box.Add(left);
            var terms = (string)item["drop"].At("offer").At("terms");
            if (!string.IsNullOrEmpty(terms)) box.Add(Text("Terms: " + terms, "muted"));
            box.Add(Action("Close", () => overlay.RemoveFromHierarchy(), "hex-secondary"));

            var itemId = (string)item["id"];
            var done = false;
            overlay.schedule.Execute(async () =>
            {
                if (done) return;
                var remaining = expires - DateTime.UtcNow;
                left.text = remaining.TotalSeconds > 0 ? $"Good for {remaining.Minutes}:{remaining.Seconds:00}" : "This code has run out. Close and tap Redeem again.";
                if ((int)remaining.TotalSeconds % 4 != 0) return;
                try
                {
                    var items = (JArray)await Supabase.Rpc("my_inventory");
                    if ((string)items?.FirstOrDefault(i => (string)i["id"] == itemId).At("status") != "redeemed") return;
                    done = true;
                    box.Clear();
                    box.Add(Text("REDEEMED!", "big"));
                    box.Add(Text("Enjoy it.", "muted"));
                    box.Add(Action("Close", () => { overlay.RemoveFromHierarchy(); _ = RefreshBag(); }, "hex-primary"));
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
            box.Add(Text(question, "title"));
            box.Add(Action("Yes", () => { overlay.RemoveFromHierarchy(); yes(); }, "hex-primary"));
            box.Add(Action("No", () => overlay.RemoveFromHierarchy(), "link"));
        }

        void ShowToast(string text)
        {
            toast.text = text;
            toast.style.display = DisplayStyle.Flex;
            toastUntil = Time.time + 4f;
        }

        // ================================================================ small helpers

        static VisualElement Div(string cls)
        {
            var element = new VisualElement();
            element.AddToClassList(cls);
            return element;
        }

        static Label Text(string text, string cls = null)
        {
            var label = new Label(text);
            label.AddToClassList("text");
            if (cls != null) label.AddToClassList(cls);
            return label;
        }

        static Button Action(string text, Action onClick, string cls = null)
        {
            var button = onClick != null ? new Button(onClick) { text = text } : new Button { text = text };
            button.AddToClassList("action");
            if (cls != null) button.AddToClassList(cls);
            return button;
        }
    }
}
