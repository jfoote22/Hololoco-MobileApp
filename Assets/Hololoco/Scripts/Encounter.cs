// The AR encounter, like old Coupemon's AR_Coupin: once the player is near a drop
// and opens it, it appears about 1.5 m in front of them. An offer is a gem in its
// rarity colour that breaks open to reveal the business's item when captured;
// objects and messages appear as themselves. Placement doesn't depend on GPS
// accuracy, so it works indoors too.

using System.Collections;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Hololoco
{
    public class Encounter : MonoBehaviour
    {
        public Camera Camera;
        public ARSession Session;

        Transform root;
        Gem gem;
        Transform item;
        float itemSpin = 40f;

        public bool Showing => root != null;

        /// <summary>Waits briefly for tracking, then puts the drop in front of the camera.</summary>
        public IEnumerator Show(DropInfo drop)
        {
            Clear();
            var waited = 0f;
            while (ARSession.state < ARSessionState.SessionTracking && waited < 3f)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            yield return null;

            root = new GameObject("encounter").transform;
            var cam = Camera.transform;
            var ahead = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            if (ahead.sqrMagnitude < 0.01f) ahead = Vector3.ProjectOnPlane(cam.up, Vector3.up);
            ahead.Normalize();
            root.position = cam.position + ahead * 1.5f + Vector3.down * 0.35f;
            root.rotation = Quaternion.LookRotation(-ahead, Vector3.up);

            if (drop.Type == "offer")
            {
                gem = Gem.Create(drop, root, 0.45f, 0);
                gem.Bob = 0.08f;
            }
            else
            {
                yield return Reveal(drop.Json, 0.5f, false);
            }
        }

        /// <summary>The capture animation: the gem breaks open and the item pops out, or the item is collected.</summary>
        public IEnumerator Captured(JToken drop)
        {
            if (root == null) yield break;
            if (gem != null)
            {
                yield return gem.Dissolve(1.1f, true);
                gem = null;
                yield return Reveal(drop, 0.45f, true);
            }
            else if (item != null)
            {
                // Pulled into the bag: shrink and drift toward the camera.
                var start = item.position;
                var startScale = item.localScale;
                for (var t = 0f; t < 0.6f; t += Time.deltaTime)
                {
                    var k = t / 0.6f;
                    item.position = Vector3.Lerp(start, Camera.transform.position + Vector3.down * 0.3f, k * k);
                    item.localScale = startScale * (1 - k);
                    yield return null;
                }
                Destroy(item.gameObject);
                item = null;
            }
        }

        /// <summary>Leaving it: the gem or item fades away.</summary>
        public IEnumerator Left()
        {
            if (gem != null) yield return gem.Dissolve(0.8f, true);
            else if (item != null)
                for (var t = 0f; t < 0.4f && item != null; t += Time.deltaTime)
                {
                    item.localScale *= 0.9f;
                    yield return null;
                }
            Clear();
        }

        /// <summary>For the bag's "View in AR": the item itself, in front of the camera.</summary>
        public IEnumerator ShowItem(JToken drop)
        {
            yield return Show(new DropInfo { Id = "item", Type = "object", Json = drop });
        }

        IEnumerator Reveal(JToken drop, float size, bool pop)
        {
            var holder = new GameObject("item").transform;
            holder.SetParent(root, false);
            item = holder;
            var task = Models.LoadInto(Models.UrlFor(drop), holder, size, 0);
            while (!task.IsCompleted) yield return null;
            if (holder == null) yield break;
            if (pop)
            {
                for (var t = 0f; t < 0.35f; t += Time.deltaTime)
                {
                    var k = t / 0.35f;
                    holder.localScale = Vector3.one * (1f + Mathf.Sin(k * Mathf.PI) * 0.25f) * k;
                    yield return null;
                }
                holder.localScale = Vector3.one;
            }
        }

        void Update()
        {
            if (item != null) item.Rotate(Vector3.up, itemSpin * Time.deltaTime, Space.Self);
        }

        public void Clear()
        {
            if (root != null) Destroy(root.gameObject);
            root = null;
            gem = null;
            item = null;
        }
    }
}
