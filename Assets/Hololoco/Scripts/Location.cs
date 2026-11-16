// The phone's GPS position and compass heading, after asking for permission.

using System;
using System.Collections;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Hololoco
{
    public class Location : MonoBehaviour
    {
        public double Lat, Lng;
        public float Accuracy = 9999f;
        public float Heading;
        public bool Denied;

        public bool HasFix => Input.location.status == LocationServiceStatus.Running && Input.location.lastData.timestamp > 0;

        IEnumerator Start()
        {
#if UNITY_ANDROID
            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
            {
                Permission.RequestUserPermission(Permission.FineLocation);
                var waited = 0f;
                while (!Permission.HasUserAuthorizedPermission(Permission.FineLocation) && waited < 30f)
                {
                    waited += 0.5f;
                    yield return new WaitForSeconds(0.5f);
                }
                if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
                {
                    Denied = true;
                    yield break;
                }
            }
#endif
            Input.compass.enabled = true;
            Input.location.Start(5f, 2f);
            yield break;
        }

        void Update()
        {
            if (!HasFix) return;
            var data = Input.location.lastData;
            Lat = data.latitude;
            Lng = data.longitude;
            Accuracy = data.horizontalAccuracy;
            Heading = Input.compass.trueHeading;
        }
    }

    public static class Geo
    {
        const double EarthRadius = 6371008.8;

        static double Rad(double degrees) => degrees * Math.PI / 180.0;

        public static float Distance(double lat1, double lng1, double lat2, double lng2)
        {
            var dLat = Rad(lat2 - lat1);
            var dLng = Rad(lng2 - lng1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                    + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return (float)(2 * EarthRadius * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)));
        }

        /// <summary>Metres east (x) and north (y) from the first point to the second.</summary>
        public static Vector2 Offset(double lat1, double lng1, double lat2, double lng2)
        {
            var north = Rad(lat2 - lat1) * EarthRadius;
            var east = Rad(lng2 - lng1) * EarthRadius * Math.Cos(Rad(lat1));
            return new Vector2((float)east, (float)north);
        }
    }
}
