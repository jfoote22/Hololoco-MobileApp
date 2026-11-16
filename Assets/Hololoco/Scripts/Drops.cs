// What the database returns, in the shapes the app uses, and the words it shows.

using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Hololoco
{
    /// <summary>A drop from app_nearby (drop_to_json plus distance, captured, quest and events).</summary>
    public class DropInfo
    {
        public string Id, Type, Title, Description, OwnerName, ObjectId, AssetModel, AssetKind;
        public double Lat, Lng;
        public float Height, Size, Turn, Distance;
        public bool Captured, Mine;
        public string Savings, Terms, Rarity;
        public int Remaining;
        /// <summary>What the offer is worth now in cents, by the database's clock, or -1 for no value.</summary>
        public int ValueNowCents = -1;
        public int DecayPercent, DecayDays;
        public string QuestName;
        public int QuestStage, QuestStages;
        public JToken Json;

        public static DropInfo From(JToken j)
        {
            var p = j["placement"];
            var offer = j["offer"];
            var quest = j["quest"];
            return new DropInfo
            {
                Json = j,
                Id = (string)j["id"],
                Type = (string)j["type"],
                Title = (string)j["title"],
                Description = (string)j["description"] ?? "",
                OwnerName = (string)j["owner"].At("name") ?? "Someone",
                ObjectId = (string)j["objectId"],
                AssetModel = (string)j["asset"].At("model"),
                AssetKind = (string)j["asset"].At("kind"),
                Lat = (double)p["lat"],
                Lng = (double)p["lng"],
                Height = (float?)p["height"] ?? 0,
                Size = (float?)p["size"] ?? 4,
                Turn = (float?)p["turn"] ?? 0,
                Distance = (float?)j["distance"] ?? 0,
                Captured = (bool?)j["captured"] ?? false,
                Mine = (bool?)j["mine"] ?? false,
                Savings = offer != null && offer.Type == JTokenType.Object ? (string)offer["savings"] : null,
                Terms = offer != null && offer.Type == JTokenType.Object ? (string)offer["terms"] : null,
                Rarity = offer != null && offer.Type == JTokenType.Object ? (string)offer["rarity"] : null,
                Remaining = offer != null && offer.Type == JTokenType.Object ? (int?)offer["remaining"] ?? 0 : 0,
                ValueNowCents = offer != null && offer.Type == JTokenType.Object ? (int?)offer["valueNowCents"] ?? -1 : -1,
                DecayPercent = offer != null && offer.Type == JTokenType.Object ? (int?)offer["decayPercent"] ?? 0 : 0,
                DecayDays = offer != null && offer.Type == JTokenType.Object ? (int?)offer["decayDays"] ?? 7 : 7,
                QuestName = quest != null && quest.Type == JTokenType.Object ? (string)quest["name"] : null,
                QuestStage = quest != null && quest.Type == JTokenType.Object ? (int?)quest["position"] ?? 0 : 0,
                QuestStages = quest != null && quest.Type == JTokenType.Object ? (int?)quest["stageCount"] ?? 0 : 0,
            };
        }

        /// <summary>The GLB to show: a ready-made object from the website, or the uploaded model.</summary>
        public string ModelUrl => AssetModel != null
            ? $"{Config.SupabaseUrl}/storage/v1/object/public/drop-assets/{AssetModel}"
            : $"{Config.SiteUrl}/library/{ObjectId ?? "gift"}.glb";
    }

    /// <summary>JSON nulls are values, not C# nulls: asking one for a field throws. This doesn't.</summary>
    public static class Json
    {
        public static JToken At(this JToken token, string key) => token is JObject o ? o[key] : null;
    }

    public static class Words
    {
        public static string Distance(float metres) =>
            metres < 1000 ? $"{Mathf.RoundToInt(metres)} m" : $"{metres / 1000f:0.0} km";

        public static string Kind(DropInfo d) => d.Type switch
        {
            "offer" => d.Rarity == null ? "Offer" : $"{Capital(d.Rarity)} offer",
            "message" => "Message",
            _ => "Object",
        };

        public static string Money(int cents) => cents % 100 == 0 ? $"${cents / 100}" : $"${cents / 100f:0.00}";

        /// <summary>"Worth $8.10 now, losing 10% every week", or null without a value.</summary>
        public static string Value(int nowCents, int decayPercent, int decayDays)
        {
            if (nowCents < 0) return null;
            if (decayPercent <= 0) return $"Worth {Money(nowCents)}";
            var every = decayDays == 1 ? "every day" : decayDays == 7 ? "every week" : $"every {decayDays} days";
            return $"Worth {Money(nowCents)} now, losing {decayPercent}% {every}";
        }

        public static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        /// <summary>Plain words for the database's and sign-in's error codes.</summary>
        public static string For(Exception e)
        {
            if (e is not HololocoError h) return "Something went wrong. Try again.";
            return h.Code switch
            {
                "offline" => h.Message,
                "otp_expired" => "That code didn't work or has expired. Ask for a new one.",
                "over_email_send_rate_limit" => "Too many codes asked for. Wait a minute, then try again.",
                "validation_failed" => "Check the email address.",
                "sign_in_needed" => "Sign in first.",
                "too_far" => "Get a little closer to capture it.",
                "location_unclear" => "Your location isn't clear yet. Wait a moment outside, then try again.",
                "moving_too_fast" => "You're moving too fast. Stop for a moment and try again.",
                "limit_captures" => "That's 100 captures today. Come back tomorrow.",
                "quest_locked" => "Finish the quest's earlier stages first.",
                "not_available" => "That isn't here right now.",
                "own_drop" => "That's one of yours.",
                "sold_out" => "That offer has run out.",
                "already_captured" => "You've already captured that one.",
                "not_tradeable" => "The business doesn't allow trading this one.",
                "limit_dropped" => "You have 20 items out on the map already.",
                "item_not_found" or "item_not_available" => "That item isn't in your bag anymore.",
                "not_an_offer" => "Only offers can be redeemed.",
                "offer_gone" => "That business has left Hololoco, so this can't be redeemed.",
                "bad_handle" => "Use 3 to 20 letters, numbers or _.",
                "handle_taken" => "Someone already has that name.",
                "bad_location" => "Your location isn't available.",
                "bag_full" => "Your bag is full. Redeem, leave or throw something away to make room.",
                "their_bag_full" => "Their bag is full, so the trade can't happen yet.",
                _ => "Something went wrong. Try again.",
            };
        }
    }
}
