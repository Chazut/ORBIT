namespace Orbit.Interop;

/// <summary>
/// String-backed enum mirroring the BSG brain names used as keys in
/// <c>BrainManager.AddCustomLayer</c> / <c>RemoveLayer</c>. Using
/// <c>nameof(BsgBrain.PMC)</c> at call sites instead of raw strings means
/// a typo or rename surfaces as a compile error rather than a silent
/// no-op at runtime.
/// </summary>
public enum BsgBrain
{
    ArenaFighter,
    BossBully,
    BossGluhar,
    BossBoar,
    BossPartisan,
    Knight,
    BossKojaniy,
    BossSanitar,
    BossKolontay,
    Tagilla,
    TagillaAgro,
    BossTest,

    Obdolbs,
    ExUsec,
    BigPipe,
    BirdEye,
    FollowerBully,
    FollowerGluharAssault,
    FollowerGluharProtect,
    FollowerGluharScout,
    FollowerKojaniy,
    FollowerSanitar,
    FollowerBoar,
    FollowerBoarClose1,
    FollowerBoarClose2,
    BossBoarSniper,
    FollowerKolontayAssault,
    FollowerKolontaySecurity,
    TagillaFollower,
    TagillaHelperAgro,

    Gifter,
    Killa,
    KillaAgro,
    Marksman,
    PMC,
    SectantPriest,
    SectantWarrior,
    CursAssault,
    Assault,
    PmcBear,
    PmcUsec,
    FlBoarCl,
    FlBoarSt,
    FlKlnAslt,
    KolonSec,
}
