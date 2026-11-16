// AR photos: takes a picture of the camera view (with the drop, without the
// buttons), saves it to the phone's Pictures/Hololoco album, and opens Android's
// share sheet. Saving through MediaStore needs no storage permission on Android 10+.

using System;
using System.Collections;
using UnityEngine;

namespace Hololoco
{
    public static class Share
    {
        /// <summary>Hide the interface, capture the frame, show it again, then save and share.</summary>
        public static IEnumerator Photo(Action hideUi, Action showUi, Action<string> done)
        {
            hideUi();
            yield return null;
            yield return new WaitForEndOfFrame();
            var shot = ScreenCapture.CaptureScreenshotAsTexture();
            showUi();
            var png = shot.EncodeToPNG();
            UnityEngine.Object.Destroy(shot);
            string message;
            try
            {
                message = SaveAndShare(png, $"Hololoco_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"HOLOLOCO photo: {e.Message}");
                message = "Couldn't save the photo.";
            }
            done(message);
        }

        static string SaveAndShare(byte[] png, string name)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var resolver = activity.Call<AndroidJavaObject>("getContentResolver");
            using var values = new AndroidJavaObject("android.content.ContentValues");
            values.Call("put", "_display_name", name);
            values.Call("put", "mime_type", "image/png");
            values.Call("put", "relative_path", "Pictures/Hololoco");
            using var media = new AndroidJavaClass("android.provider.MediaStore$Images$Media");
            using var collection = media.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");
            using var uri = resolver.Call<AndroidJavaObject>("insert", collection, values);
            using (var stream = resolver.Call<AndroidJavaObject>("openOutputStream", uri))
            {
                stream.Call("write", png);
                stream.Call("close");
            }

            using var intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.SEND");
            intent.Call<AndroidJavaObject>("setType", "image/png");
            intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.STREAM", uri);
            intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.TEXT", "Found on Hololoco");
            intent.Call<AndroidJavaObject>("addFlags", 1); // FLAG_GRANT_READ_URI_PERMISSION
            using var intents = new AndroidJavaClass("android.content.Intent");
            using var chooser = intents.CallStatic<AndroidJavaObject>("createChooser", intent, "Share your photo");
            activity.Call("startActivity", chooser);
            return "Saved to Pictures/Hololoco.";
#else
            var path = System.IO.Path.Combine(Application.persistentDataPath, name);
            System.IO.File.WriteAllBytes(path, png);
            return $"Saved to {path}.";
#endif
        }
    }
}
