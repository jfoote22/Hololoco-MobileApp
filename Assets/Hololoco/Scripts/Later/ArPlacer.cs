// Puts nearby drops into the camera view at their real places.
//
// Outdoors where Google has Street View imagery, ARCore's Geospatial API knows
// where the phone is to a metre or two, and each drop gets a terrain anchor at
// its latitude and longitude. Until then (or indoors), drops are placed from
// GPS and the compass, which is rougher; they move to an anchor once it's ready.

using System.Collections;
using System.Collections.Generic;
using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Hololoco
{
    public class ArPlacer : MonoBehaviour
    {
        public ARAnchorManager AnchorManager;
        public AREarthManager Earth;
        public Camera Camera;
        public Location Location;

        /// <summary>Drops farther than this aren't drawn.</summary>
        public float ShowWithin = 150f;

        readonly Dictionary<string, DropView> views = new();
        readonly HashSet<string> resolving = new();
        float nextRough;

        /// <summary>The Geospatial API has a good fix: accurate placement and positions.</summary>
        public bool Geospatial =>
            Earth != null && Earth.EarthTrackingState == TrackingState.Tracking
            && Earth.CameraGeospatialPose.HorizontalAccuracy < 15
            && Earth.CameraGeospatialPose.OrientationYawAccuracy < 15;

        /// <summary>Where the player is: from the Geospatial API when it has a good fix, else GPS.</summary>
        public bool TryGetPosition(out double lat, out double lng, out float accuracy)
        {
            if (Geospatial)
            {
                var pose = Earth.CameraGeospatialPose;
                lat = pose.Latitude;
                lng = pose.Longitude;
                accuracy = (float)pose.HorizontalAccuracy;
                return true;
            }
            lat = Location.Lat;
            lng = Location.Lng;
            accuracy = Location.Accuracy;
            return Location.HasFix;
        }

        /// <summary>A line for the top of the screen about how well placement works right now.</summary>
        public string Status
        {
            get
            {
                if (Earth != null && Earth.EarthState != EarthState.Enabled)
                    return Earth.EarthState == EarthState.ErrorNotAuthorized
                        ? "Placement isn't set up (ARCore key). Drops are approximate."
                        : "Approximate placement (GPS and compass).";
                if (Geospatial) return "Located. Drops are in their real places.";
                if (!Location.HasFix) return "Finding your location…";
                return "Look around at buildings and streets to place drops precisely.";
            }
        }

        public DropView ViewFor(Collider collider) => collider ? collider.GetComponentInParent<DropView>() : null;

        /// <summary>The drops placed in the camera view right now.</summary>
        public IEnumerable<DropView> Placed
        {
            get
            {
                foreach (var view in views.Values)
                    if (view != null && view.isActiveAndEnabled) yield return view;
            }
        }

        public void Show(IList<DropInfo> drops)
        {
            var keep = new HashSet<string>();
            foreach (var drop in drops)
            {
                if (drop.Distance > ShowWithin) continue;
                keep.Add(drop.Id);
                if (views.TryGetValue(drop.Id, out var existing))
                {
                    existing.Drop = drop;
                    continue;
                }
                var go = new GameObject("drop " + drop.Id);
                go.SetActive(false);
                var view = go.AddComponent<DropView>();
                view.Drop = drop;
                views[drop.Id] = view;
                _ = view.Load();
            }
            foreach (var id in new List<string>(views.Keys))
                if (!keep.Contains(id)) Remove(id);
        }

        void Remove(string id)
        {
            if (!views.TryGetValue(id, out var view)) return;
            views.Remove(id);
            if (view == null) return;
            var anchor = view.Anchored ? view.transform.parent : null;
            Destroy(view.gameObject);
            if (anchor != null) Destroy(anchor.gameObject);
        }

        void Update()
        {
            if (Geospatial)
            {
                foreach (var view in views.Values)
                    if (view != null && !view.Anchored && !resolving.Contains(view.Drop.Id))
                        StartCoroutine(Anchor(view));
            }
            else if (Time.time > nextRough && Location.HasFix)
            {
                nextRough = Time.time + 2f;
                foreach (var view in views.Values)
                    if (view != null && !view.Anchored) PlaceRoughly(view);
            }
        }

        IEnumerator Anchor(DropView view)
        {
            var drop = view.Drop;
            resolving.Add(drop.Id);
            // A clockwise angle from north, as the Geospatial API expects it.
            var rotation = Quaternion.AngleAxis(180f - drop.Turn, Vector3.up);
            var promise = AnchorManager.ResolveAnchorOnTerrainAsync(drop.Lat, drop.Lng, 0, rotation);
            yield return promise;
            resolving.Remove(drop.Id);
            var result = promise.Result;
            if (result == null || result.TerrainAnchorState != TerrainAnchorState.Success || result.Anchor == null) yield break;
            if (view == null)
            {
                Destroy(result.Anchor.gameObject);
                yield break;
            }
            view.transform.SetParent(result.Anchor.transform, false);
            view.transform.localPosition = Vector3.zero;
            view.transform.localRotation = Quaternion.identity;
            view.Anchored = true;
            view.gameObject.SetActive(true);
            Debug.Log($"HOLOLOCO anchored {drop.Id} with Geospatial");
        }

        /// <summary>Places a drop from GPS and compass: right direction, roughly the right distance, on the ground.</summary>
        void PlaceRoughly(DropView view)
        {
            var cam = Camera.transform;
            // Which way is north in the camera's world: the camera's heading minus the compass heading.
            var heading = Earth != null && Earth.EarthTrackingState == TrackingState.Tracking
                ? (float)Earth.CameraGeospatialPose.Heading
                : Location.Heading;
            var northYaw = cam.eulerAngles.y - heading;
            var north = Quaternion.Euler(0, northYaw, 0) * Vector3.forward;
            var east = Quaternion.Euler(0, northYaw + 90f, 0) * Vector3.forward;
            var offset = Geo.Offset(Location.Lat, Location.Lng, view.Drop.Lat, view.Drop.Lng);
            var target = cam.position + east * offset.x + north * offset.y;
            target.y = cam.position.y - 1.4f;

            var t = view.transform;
            if (!view.gameObject.activeSelf || Vector3.Distance(t.position, target) > 25f)
                t.position = target;
            else
                t.position = Vector3.Lerp(t.position, target, 0.3f);
            t.rotation = Quaternion.Euler(0, northYaw + view.Drop.Turn, 0);
            if (!view.gameObject.activeSelf)
                Debug.Log($"HOLOLOCO placed {view.Drop.Id} roughly: {offset.x:0} m east, {offset.y:0} m north of you (GPS ±{Location.Accuracy:0} m, heading {heading:0}°)");
            view.gameObject.SetActive(true);
        }
    }
}
