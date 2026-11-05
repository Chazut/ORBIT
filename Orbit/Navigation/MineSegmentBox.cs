using System;
using UnityEngine;
namespace Orbit.Navigation;

internal static class MineSegmentBox
{
    // Closed segment against an expanded trigger box, in the box's local space.
    internal static bool Intersects(Vector3 a,Vector3 b,Vector3 half)
    {
        float lo=0,hi=1;
        return Axis(a.x,b.x,half.x,ref lo,ref hi)&&Axis(a.y,b.y,half.y,ref lo,ref hi)&&Axis(a.z,b.z,half.z,ref lo,ref hi);
    }
    private static bool Axis(float a,float b,float half,ref float lo,ref float hi)
    {
        if(float.IsNaN(a)||float.IsInfinity(a)||float.IsNaN(b)||float.IsInfinity(b)||half<0)return false;
        float d=b-a;if(Math.Abs(d)<.000001f)return Math.Abs(a)<=half;
        float first=(-half-a)/d,last=(half-a)/d;if(first>last)(first,last)=(last,first);
        lo=Math.Max(lo,first);hi=Math.Min(hi,last);return lo<=hi;
    }
}
