using System.Collections.Generic;

namespace Orbit.Systems;

internal sealed class AmbushSearchBudget(int remaining = 24)
{
    internal int Remaining = remaining;
    internal int NativeQueries, NativeDataMissing, NativePoints, NativeHeightRejected, NativeRangeRejected, NativeTypeRejected;
    internal int GroundMisses, GroundFloorRejected, CachedCandidates, NativeCandidates, GeometryProbes, GeometryCandidates, CandidatesTested;
    internal string Source = "none";
    internal readonly Dictionary<string, int> Rejected = new();
    internal string Rejections => Rejected.Count != 0 ? string.Join(",", Rejected)
        : CandidatesTested > 0 ? "none" : "no cover candidates";
    internal string Diagnostics => $"source={Source} cached={CachedCandidates} nativeQueries={NativeQueries} dataMissing={NativeDataMissing} nativePoints={NativePoints} heightFiltered={NativeHeightRejected} rangeFiltered={NativeRangeRejected} typeFiltered={NativeTypeRejected} groundMisses={GroundMisses} groundFloorRejected={GroundFloorRejected} nativeCandidates={NativeCandidates} geometryProbes={GeometryProbes} geometryCandidates={GeometryCandidates} tested={CandidatesTested} pathsRemaining={Remaining}";
    internal bool Reject(string reason)
    { Rejected.TryGetValue(reason, out var count); Rejected[reason] = count + 1; return false; }
}
