using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

// Raid-scoped trigger footprints, captured once. No scene queries or physics casts per bot/frame.
internal static class DirectionalMineSafety
{
    private sealed class Footprint
    {
        internal MineDirectional Mine;
        internal Collider Trigger;
        internal Matrix4x4 WorldToLocal;
        internal Vector3 Center, Half;
        internal Bounds Broad;
        internal GameObject Cutter;
    }
    private static readonly List<Footprint> entries = new();
    private static int routeRejects;

    internal static void Clear()
    {
        foreach(var e in entries)if(e.Cutter!=null)UnityEngine.Object.Destroy(e.Cutter);
        entries.Clear();routeRejects=0;
    }
    internal static void Refresh()
    {
        Clear();var seen=new HashSet<int>();
        foreach(var mine in MineDirectional.Mines)
        {
            if(mine==null||!mine.gameObject.activeInHierarchy)continue;
            foreach(var col in mine.GetComponentsInChildren<Collider>())
            {
                if(col==null||!col.enabled||!col.isTrigger||!seen.Add(col.GetInstanceID()))continue;
                // Only colliders actually forwarding a mine trigger, not explosion/visual colliders.
                if(col.gameObject!=mine.gameObject&&col.GetComponent<MineDirectionalColliders>()==null)continue;
                var e=new Footprint{Mine=mine,Trigger=col};
                if(col is BoxCollider box)
                {
                    e.WorldToLocal=col.transform.worldToLocalMatrix;e.Center=box.center;
                    var scale=col.transform.lossyScale;
                    e.Half=box.size*.5f+new Vector3(.4f/Mathf.Max(.0001f,Mathf.Abs(scale.x)),
                        .8f/Mathf.Max(.0001f,Mathf.Abs(scale.y)),.4f/Mathf.Max(.0001f,Mathf.Abs(scale.z)));
                }
                else {e.WorldToLocal=Matrix4x4.identity;e.Center=col.bounds.center;e.Half=col.bounds.extents+new Vector3(.4f,.8f,.4f);}
                e.Broad=col.bounds;e.Broad.Expand(new Vector3(1.2f,1.6f,1.2f));
                e.Cutter=new GameObject("ORBIT directional mine avoidance");
                e.Cutter.transform.SetParent(col.transform,false);
                var obstacle=e.Cutter.AddComponent<NavMeshObstacle>();obstacle.shape=NavMeshObstacleShape.Box;
                if(col is BoxCollider b){obstacle.center=b.center;obstacle.size=e.Half*2;}
                else
                {
                    // Conservative world box for a non-box trigger; never treat its mesh bounds as empty.
                    e.Cutter.transform.SetParent(mine.transform,false);e.Cutter.transform.position=col.bounds.center;
                    e.Cutter.transform.rotation=Quaternion.identity;
                    e.Cutter.transform.localScale=Vector3.one;
                    var s=e.Cutter.transform.lossyScale;
                    obstacle.size=new Vector3(e.Broad.size.x/Mathf.Abs(s.x),e.Broad.size.y/Mathf.Abs(s.y),e.Broad.size.z/Mathf.Abs(s.z));
                }
                obstacle.carving=true;obstacle.carveOnlyStationary=false;entries.Add(e);
            }
        }
        Log.Info($"Directional mine safety: {entries.Count} trigger footprints registered; route checks enabled");
    }
    private static bool Active(Footprint e)
    {
        bool active=e.Mine!=null&&e.Mine.gameObject.activeInHierarchy&&e.Trigger!=null&&e.Trigger.enabled&&e.Trigger.gameObject.activeInHierarchy;
        if(e.Cutter!=null&&e.Cutter.activeSelf!=active)e.Cutter.SetActive(active);
        return active;
    }
    internal static bool IsInside(Vector3 feet)
    {
        var p=feet+Vector3.up*.8f;
        foreach(var e in entries)if(Active(e)&&e.Broad.Contains(p)&&SegmentBox(p,p,e))return true;
        return false;
    }
    private static bool SegmentBox(Vector3 a,Vector3 b,Footprint e)
        => MineSegmentBox.Intersects(e.WorldToLocal.MultiplyPoint3x4(a)-e.Center,e.WorldToLocal.MultiplyPoint3x4(b)-e.Center,e.Half);
    internal static bool Blocks(Vector3 origin,Vector3[] path,out string mine)
    {
        mine="";if(path==null)return false;
        foreach(var e in entries)
        {
            if(!Active(e))continue;
            var a=origin+Vector3.up*.8f;
            foreach(var corner in path)
            {
                var b=corner+Vector3.up*.8f;
                var broad=new Bounds(a,Vector3.zero);broad.Encapsulate(b);
                if(broad.Intersects(e.Broad)&&SegmentBox(a,b,e))
                {
                    mine=e.Mine.name;
                    if(++routeRejects<=25)Log.Info($"Directional mine route rejected: mine={mine} from={origin} segment={a}->{b}");
                    return true;
                }
                a=b;
            }
        }
        return false;
    }
}
