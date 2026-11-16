// Talking to Hololoco's Supabase project: signing in with an emailed code, and
// calling the database functions listed in the website's docs/app-api.md.
// The database enforces every rule; a refusal comes back as a short code
// (too_far, sold_out, …) that Words.For turns into plain words.

using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Hololoco
{
    public class HololocoError : Exception
    {
        public readonly string Code;
        public HololocoError(string code, string message) : base(message) { Code = code; }
    }

    [Serializable]
    class ConfigJson
    {
        public string supabaseUrl;
        public string publishableKey;
        public string siteUrl;
        public string mapboxToken;
    }

    /// <summary>Resources/hololoco-config.json: the project's address and its public (publishable) key.</summary>
    public static class Config
    {
        static ConfigJson loaded;

        static ConfigJson Loaded =>
            loaded ??= JsonUtility.FromJson<ConfigJson>(Resources.Load<TextAsset>("hololoco-config").text);

        public static string SupabaseUrl => Loaded.supabaseUrl.TrimEnd('/');
        public static string Key => Loaded.publishableKey;
        public static string SiteUrl => Loaded.siteUrl.TrimEnd('/');
        /// <summary>Mapbox's public (pk.) token, for map tiles, buildings and walking directions.</summary>
        public static string MapboxToken => Loaded.mapboxToken;
    }

    public static class Supabase
    {
        const string AccessKey = "hl.access", RefreshKey = "hl.refresh", ExpiresKey = "hl.expires", EmailKey = "hl.email", UserKey = "hl.user";

        public static bool SignedIn => !string.IsNullOrEmpty(PlayerPrefs.GetString(RefreshKey, ""));
        public static string Email => PlayerPrefs.GetString(EmailKey, "");
        public static string UserId => PlayerPrefs.GetString(UserKey, "");

        /// <summary>Emails a sign-in code. New addresses get an account.</summary>
        public static Task SendCode(string email) =>
            Send("POST", "/auth/v1/otp", new JObject { ["email"] = email.Trim(), ["create_user"] = true }, false);

        public static async Task VerifyCode(string email, string code)
        {
            var session = await Send("POST", "/auth/v1/verify",
                new JObject { ["type"] = "email", ["email"] = email.Trim(), ["token"] = code.Trim() }, false);
            Store(session);
            PlayerPrefs.SetString(EmailKey, email.Trim());
            PlayerPrefs.Save();
        }

        public static void SignOut()
        {
            PlayerPrefs.DeleteKey(AccessKey);
            PlayerPrefs.DeleteKey(RefreshKey);
            PlayerPrefs.DeleteKey(ExpiresKey);
            PlayerPrefs.DeleteKey(EmailKey);
            PlayerPrefs.DeleteKey(UserKey);
            PlayerPrefs.Save();
        }

        /// <summary>Updates the signed-in person's own profile (name, age confirmation). Row-level security allows only their own.</summary>
        public static Task UpdateProfile(JObject changes) =>
            Send("PATCH", "/rest/v1/profiles?id=eq." + UserId, changes, true);

        /// <summary>Calls a database function (POST /rest/v1/rpc/name) as the signed-in player, or as a visitor.</summary>
        public static Task<JToken> Rpc(string name, JObject args = null) =>
            Send("POST", "/rest/v1/rpc/" + name, args ?? new JObject(), true);

        static void Store(JToken session)
        {
            PlayerPrefs.SetString(AccessKey, (string)session["access_token"]);
            PlayerPrefs.SetString(RefreshKey, (string)session["refresh_token"]);
            var expiresIn = (long?)session["expires_in"] ?? 3600;
            PlayerPrefs.SetString(ExpiresKey, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn).ToString());
            var user = (string)session["user"]?["id"];
            if (!string.IsNullOrEmpty(user)) PlayerPrefs.SetString(UserKey, user);
            PlayerPrefs.Save();
        }

        /// <summary>The access token, refreshed a minute before it runs out. Null when signed out.</summary>
        static async Task<string> AccessToken()
        {
            if (!SignedIn) return null;
            long.TryParse(PlayerPrefs.GetString(ExpiresKey, "0"), out var expires);
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires - 60)
            {
                try
                {
                    Store(await Send("POST", "/auth/v1/token?grant_type=refresh_token",
                        new JObject { ["refresh_token"] = PlayerPrefs.GetString(RefreshKey) }, false));
                }
                catch (HololocoError e) when (e.Code != "offline")
                {
                    // The refresh token was revoked or expired: sign in again.
                    SignOut();
                    return null;
                }
            }
            return PlayerPrefs.GetString(AccessKey);
        }

        static async Task<JToken> Send(string method, string path, JToken body, bool asPlayer)
        {
            using var request = new UnityWebRequest(Config.SupabaseUrl + path, method);
            if (body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("apikey", Config.Key);
            if (asPlayer)
            {
                var token = await AccessToken();
                if (token != null) request.SetRequestHeader("Authorization", "Bearer " + token);
            }
            request.timeout = 20;

            var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();

            var text = request.downloadHandler.text;
            if (request.result == UnityWebRequest.Result.ConnectionError)
                throw new HololocoError("offline", "Couldn't reach Hololoco. Check your connection.");
            if (request.responseCode >= 400)
            {
                string code = "error", message = text;
                try
                {
                    var json = JObject.Parse(text);
                    message = (string)(json["message"] ?? json["msg"] ?? json["error_description"] ?? json["error"]) ?? text;
                    // Auth errors carry error_code; database rules raise their code as the message.
                    code = (string)json["error_code"] ?? message;
                }
                catch (JsonException)
                {
                    // Not JSON: keep the raw text.
                }
                throw new HololocoError(code, message);
            }
            return string.IsNullOrWhiteSpace(text) ? null : JToken.Parse(text);
        }
    }
}
