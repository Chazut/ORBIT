using EFT;
using Orbit.Helpers;
using System.Text.Json;
using Newtonsoft.Json;
using Orbit.Config;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Server.Zones;
using Orbit.Server.Web;
using System.Text;
using System.Xml.Linq;
using Orbit.Systems;
using Orbit.Zones;
using SPTarkov.Common.Models.Logging;
using UnityEngine;
using Json = System.Text.Json.JsonSerializer;

var checks=0;
void Check(bool ok,string name) { if(!ok)throw new Exception(name);checks++; }
bool Floor(string map,string floor,float x,float y,float z)=>FloorCatalog.Matches(map,floor,x,y,z);
Check(Floor("factory4_day","base",0,0,0),"Factory ground");
Check(Floor("factory4_day","2nd-floor",0,3,0),"Lower bound belongs to upper floor");
Check(!Floor("factory4_day","2nd-floor",0,6,0),"Upper bound does not overlap");
Check(Floor("factory4_day","3rd-floor",0,6,0),"Factory third floor");
Check(Floor("factory4_day","tunnels",0,-2,0),"Factory tunnels");
Check(!Floor("factory4_day","base",0,6,0),"Base does not include upper floor");
Check(Floor("bigmap","3rd-floor",200,6,150),"Dorms overlapping metadata resolves to upper floor");
Check(!Floor("bigmap","2nd-floor",200,6,150),"Dorms lower overlapping extent excluded");
Check(!Floor("bigmap","3rd-floor",0,6,0),"Local building bounds respected");
Check(Floor("bigmap","base",0,6,0),"Outside building stays base");
Check(Floor("Sandbox","garage",80,20,100),"Garage inside bounds");
Check(!Floor("Sandbox","garage",0,20,0),"Garage outside bounds");
Check(Floor("Interchange@rework","2nd-floor",0,28,0),"Rework mall floor");
Check(!Floor("Interchange","2nd-floor",300,28,0),"Mall bounds respected");
Check(Floor("laboratory","technical",-150,-2,-320),"Labs technical");
Check(Floor("unknown",null,0,99,0),"Legacy unknown map all floors");
Check(!Floor("unknown","base",0,0,0),"Unknown scoped floor fails closed");
Check(!Floor("factory4_day","future-floor",0,0,0),"Unknown floor fails closed");
Check(FloorCatalog.For("laboratory").Find("technical").TilePath!=null,"Labs has tile render");
Check(FloorCatalog.For("Interchange@rework").Find("2nd-floor").HideLayers.Length==2,"Rework groups isolate the floor");
Check(FloorCatalog.For("Interchange").Find("2nd-floor").Svg!=FloorCatalog.For("Interchange@rework").Find("2nd-floor").Svg,"Vanilla and rework renders remain distinct");
var svgFixture="""
<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" xml:space="preserve" viewBox="0 0 1127 947" width="1127" height="947">
  <defs><path id="outline" d="M0 0h10v10z" /></defs>
  <g id="Ground_Level"><use xlink:href="#outline" /></g>
  <g id="First_Floor" style="opacity:0.8"><use xlink:href="#outline" /></g>
  <g id="Second_Floor"><use xlink:href="#outline" /></g>
</svg>
""";
XElement SvgDocument(MapRenders.InlineRender render)=>XElement.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(render.ImageUrl.Split(',')[1])));
XElement SvgLayer(XElement svg,string id)=>svg.Descendants().Single(e=>(string)e.Attribute("id")==id);
var groundSvg=SvgDocument(MapRenders.PrepareSvg(svgFixture,["First_Floor","Second_Floor"]));
Check(groundSvg.GetNamespaceOfPrefix("xlink")=="http://www.w3.org/1999/xlink"
    && groundSvg.Descendants().First(e=>e.Name.LocalName=="use").Attribute(XName.Get("href","http://www.w3.org/1999/xlink")).Value=="#outline",
    "Filtered SVG stays a valid standalone document with xlink references");
Check((string)groundSvg.Attribute("viewBox")=="0 0 1127 947" && (string)groundSvg.Attribute("width")=="1127"
    && (string)groundSvg.Attribute(XNamespace.Xml+"space")=="preserve","Filtering retains root geometry and XML attributes");
Check(SvgLayer(groundSvg,"Ground_Level").Attribute("style")==null
    && ((string)SvgLayer(groundSvg,"First_Floor").Attribute("style")).Contains("display:none")
    && ((string)SvgLayer(groundSvg,"Second_Floor").Attribute("style")).Contains("display:none"),"Ground render hides only the two upper floors");
Check(((string)SvgLayer(groundSvg,"First_Floor").Attribute("style")).Contains("opacity:0.8"),"Filtering preserves existing floor styles");
var upperSvg=SvgDocument(MapRenders.PrepareSvg(svgFixture,["Ground_Level","Second_Floor"]));
Check((string)SvgLayer(upperSvg,"First_Floor").Attribute("style")=="opacity:0.8"
    && ((string)SvgLayer(upperSvg,"Ground_Level").Attribute("style")).Contains("display:none"),"Each floor image filters the original independently");
foreach(var mapId in new[]{"laboratory","Labyrinth"})
    Check(FloorCatalog.For(mapId).Floors[0].Svg==null && FloorCatalog.For(mapId).Floors[0].TilePath!=null,
        "Convergence background has a base tile layer for "+mapId);
foreach(var entry in FloorCatalog.Maps)
{
    Check(entry.Value.Floors.Select(f=>f.Id).Distinct().Count()==entry.Value.Floors.Length,"Unique floor IDs: "+entry.Key);
    Check(entry.Value.Floors.All(f=>f.Extents.All(e=>e.MinY<e.MaxY)),"Ordered extents: "+entry.Key);
}
var all=new ZoneScope();
Check(all.Allows("Other"),"Missing type filter includes custom bots");
var pmc=new ZoneScope{BotTypes=["PMC"],FloorId="2nd-floor"};
Check(pmc.Allows("PMC")&&!pmc.Allows("Scav"),"Type allow list");
Check(!new ZoneScope{BotTypes=[]}.Allows("PMC"),"Empty type filter disables zone");
var copy=pmc.Copy();copy.BotTypes[0]="Scav";
Check(pmc.BotTypes[0]=="PMC","Scope copy is deep");

var roles=new (WildSpawnType Role,string Expected)[] {
    (WildSpawnType.pmcUSEC,"PMC"),(WildSpawnType.pmcBEAR,"PMC"),(WildSpawnType.assault,"Scav"),
    (WildSpawnType.pmcBot,"Raider"),(WildSpawnType.exUsec,"Rogue"),(WildSpawnType.bossKnight,"Goons"),
    (WildSpawnType.bossKilla,"Boss"),(WildSpawnType.followerBully,"Follower"),(WildSpawnType.sectantPriest,"Cultist"),
    (WildSpawnType.arenaFighter,"Bloodhound"),(WildSpawnType.untarTest,"UNTAR"),(WildSpawnType.ruafTest,"RUAF"),
    (WildSpawnType.remnantTest,"RUAF"),(WildSpawnType.blackDivTest,"BlackDivision"),(WildSpawnType.ISBTest,"ISB"),
    (WildSpawnType.CombineTest,"Combine"),(WildSpawnType.unclassifiedTest,"Other")
};
foreach(var (role,expected) in roles)
{
    var bot=new BotOwner();bot.Profile.Info.Settings.Role=role;
    Check(ZoneBotType.For(bot)==expected,"Role classification: "+role);
}
var playerScav=new BotOwner();playerScav.Profile.Info.Settings.Role=WildSpawnType.assault;
playerScav.Profile.Info.MainProfileNickname="test-player";
Check(ZoneBotType.For(playerScav)=="PlayerScav","PlayerScav distinguished from assault role");
var system=new WaypointSystem("factory4_day");
var squad=new Squad{Leader=new Agent{Position=new Vector3(10,0,10)}};
system.Add(new WaypointSystem.Zone(new Vector2(4,4),100,1,1,pmc,new Vector3(40,0,40),true));
Check(system.Attraction(squad,new(1,1)).magnitude>0,"Upstairs zone attracts from downstairs");
squad.Leader.Bot.Profile.Info.Settings.Role=WildSpawnType.assault;
Check(system.Attraction(squad,new(1,1)).magnitude==0,"Wrong type gets no attraction");
Check(system.GetPositiveForceZoneAnchors(squad).Count==0,"Wrong type gets no kill main");
squad.Leader.Bot.Profile.Info.Settings.Role=WildSpawnType.pmcUSEC;
Check(system.Floor(squad,new(4,4))=="2nd-floor","Target cell commits to zone floor");
var below=new Waypoint{Category=WaypointCategory.Synthetic,Position=new(40,0,40)};
var upstairs=new Waypoint{Category=WaypointCategory.Synthetic,Position=new(40,4,40)};
Check(!system.AllowsFloor("2nd-floor",below)&&system.AllowsFloor("2nd-floor",upstairs),"Candidate height filter");
Check(!system.HasReachedZoneFloor(squad,upstairs,below.Position),"Arrival directly below rejected");
Check(system.HasReachedZoneFloor(squad,upstairs,upstairs.Position),"Arrival on target floor accepted");
var smallZone=new WaypointSystem("factory4_day");
smallZone.Add(new WaypointSystem.Zone(new Vector2(4.45f,4.45f),1,1,1,pmc,new Vector3(49.5f,0,49.5f)));
Check(smallZone.Floor(squad,new(4,4))=="2nd-floor","Small zone near cell corner keeps its floor");
Check(smallZone.Floor(squad,new(6,6))==null,"Small zone does not constrain distant cells");
system.Add(below);system.Add(upstairs);
var anchors=system.GetPositiveForceZoneAnchors(squad);
Check(anchors.Count==1&&anchors[0].Position.y==4&&anchors[0].FloorId=="2nd-floor","Kill main anchors to actual floor POI");
upstairs.Reachable=false;
Check(system.GetPositiveForceZoneAnchors(squad).Count==0,"Unreachable upper floor never falls back downstairs");
squad.ExtractRequested=true;
Check(system.Attraction(squad,new(1,1)).magnitude==0,"Extraction bypasses scoped attraction");
Check(system.Floor(squad,new(4,4))==null,"Extraction bypasses floor preference");
Check(system.HasReachedZoneFloor(squad,upstairs,below.Position),"Extraction arrival unrestricted");
squad.ExtractRequested=false;
Check(system.AllowsFloor("2nd-floor",new Waypoint{Category=WaypointCategory.Quest,Position=below.Position}),"Precise quest destinations retain priority");
var repel=new WaypointSystem("factory4_day");
repel.Add(new WaypointSystem.Zone(new Vector2(4,4),100,-2,1,pmc,new Vector3(40,0,40)));
Check(repel.Attraction(squad,new(1,1)).magnitude==0,"Upper floor repeller leaves lower floor alone");
squad.Leader.Position=new(10,4,10);
Check(repel.Attraction(squad,new(1,1)).magnitude>0,"Repeller applies on selected floor");
Check(repel.Weight(squad,upstairs)<repel.Weight(squad,below),"Repeller lowers upstairs destination weight");
Check(repel.Weight(squad,upstairs)>0,"Repeller is a preference, not a hard wall");

var serverZone=new CustomZoneModel{Position=new(){X=20,Y=40},BotTypes=["PMC","RUAF"],FloorId="2nd-floor"};
var wire=Json.Serialize(serverZone);
var clientZone=JsonConvert.DeserializeObject<WaypointConfig.CustomZone>(wire);
Check(clientZone.Position.x==20&&clientZone.Position.y==40,"XZ contract unchanged");
Check(clientZone.Allows("RUAF")&&!clientZone.Allows("Scav")&&clientZone.FloorId=="2nd-floor","Server/client scoped contract");
var roundtrip=Json.Deserialize<CustomZoneModel>(JsonConvert.SerializeObject(clientZone));
Check(roundtrip.FloorId==serverZone.FloorId&&roundtrip.BotTypes.SequenceEqual(serverZone.BotTypes),"Client/server roundtrip");
var legacy=JsonConvert.DeserializeObject<WaypointConfig.CustomZone>("{\"Position\":{\"x\":0,\"y\":0},\"Radius\":{\"Min\":100,\"Max\":150},\"Force\":{\"Min\":1,\"Max\":1},\"Decay\":1}");
Check(legacy.BotTypes==null&&legacy.FloorId==null&&legacy.KillMains,"Legacy client JSON remains all types and floors");
var store=new ZoneStoreService(new TestLogger<ZoneStoreService>());
var model=new MapZoneModel{CustomZones=[serverZone],BuiltinZones=new(){["test"]=new(){BotTypes=["Scav"],FloorId="base"}}};
var pack=Json.Serialize(new ZonePackModel{Maps=new(){["Sandbox"]=model}});
store.ImportPack(pack);
store.CopyWorking("Sandbox","Sandbox_high");
store.GetWorking("Sandbox_high").CustomZones[0].BotTypes[0]="Scav";
Check(store.GetWorking("Sandbox").CustomZones[0].BotTypes[0]=="PMC","Sibling copy isolates arrays");
var snapshot=store.SnapshotWorking();
store.GetWorking("Sandbox").CustomZones[0].FloorId="base";
store.RestoreWorking(snapshot.ToDictionary(p=>p.Key,p=>p.Value.Current));
Check(store.GetWorking("Sandbox").CustomZones[0].FloorId=="2nd-floor","History restore retains floor");
var exported=Json.Deserialize<ZonePackModel>(store.ExportPack(["Sandbox"],"scoped","tester",""));
Check(exported.Maps["Sandbox"].CustomZones[0].BotTypes.SequenceEqual(new[]{"PMC","RUAF"}),"Pack export retains types");
Check(exported.Maps["Sandbox"].BuiltinZones["test"].FloorId==null,"Unknown built-in does not retain a manually imposed floor");
Check(exported.Maps["Sandbox"].BuiltinZones["test"].BotTypes.SequenceEqual(new[]{"Scav"}),"Builtin bot types survive migration and export");

Check(NativeFloorResolver.Resolve("RezervBase",[new(-104,-14.5f,33),new(-99,-14.3f,27)])=="bunkers","Native underground points choose Bunkers");
Check(NativeFloorResolver.Resolve("factory4_day",[new(0,8,0),new(0,-3,0),new(0,0,0),new(0,4,0)])=="base|2nd-floor|3rd-floor|tunnels","Native multi-floor union follows catalogue order");
Check(NativeFloorResolver.Resolve("RezervBase",[])==null,"Missing native points stay unknown");
var sceneSystem=new WaypointSystem("factory4_day");
var nativeReport=sceneSystem.NativeMetadata(new NativeBotZone
{
    name="fixture",SpawnPoints=[new(){Position=new(0,0,0)}],
    PatrolWays=[new(){Points=[new(){Position=new(0,4,0),SubPoints=[new(){Position=new(0,8,0)}]},null]}]
});
Check(nativeReport["fixture"]=="base|2nd-floor|3rd-floor","Actual collector includes spawn, patrol and subpoint floors");
Check(SPT.Common.Http.RequestHandler.LastUrl=="/orbit/zones/native-floors"&&SPT.Common.Http.RequestHandler.LastJson.Contains("base|2nd-floor|3rd-floor"),"Client reports scene floors to editor endpoint");
Check(FloorSelection.Contains("base|bunkers","bunkers")&&!FloorSelection.Contains("base|bunkers","bunker"),"Floor union matches whole IDs");
Check(FloorSelection.IsKnown("RezervBase","base|bunkers")&&!FloorSelection.IsKnown("RezervBase","base|future"),"Union validation checks every member");
var multiSystem=new WaypointSystem("factory4_day");
multiSystem.Add(new WaypointSystem.Zone(new(4,4),100,1,1,new ZoneScope{FloorId="base|2nd-floor"},new(40,0,40),true));
multiSystem.Add(below);upstairs.Reachable=true;multiSystem.Add(upstairs);
Check(multiSystem.AllowsFloor("base|2nd-floor",below)&&multiSystem.AllowsFloor("base|2nd-floor",upstairs),"Native routing retains both occupied floors");
Check(!multiSystem.HasReachedZoneFloor(squad,upstairs,below.Position),"Multi-floor arrival cannot validate upstairs from below");
Check(multiSystem.HasReachedZoneFloor(squad,upstairs,upstairs.Position),"Multi-floor arrival accepts the target floor");
Check(!multiSystem.GetPositiveForceZoneAnchors(squad)[0].FloorId.Contains('|'),"Kill anchor commits to its actual native floor");

var zoneDir=Path.Combine(AppContext.BaseDirectory,"zones");
Directory.CreateDirectory(zoneDir);
var fixturePaths=new[]{Path.Combine(zoneDir,"RezervBase.json"),Path.Combine(zoneDir,"RezervBase.json.pre-native-floors.bak"),Path.Combine(zoneDir,"native-floors.json")};
var originals=fixturePaths.ToDictionary(p=>p,p=>File.Exists(p)?File.ReadAllBytes(p):null);
try
{
    foreach(var p in fixturePaths) if(File.Exists(p))File.Delete(p);
    var oldFile="""
    {"BuiltinZones":{"ZoneSubCommand":{"Radius":{"Min":45,"Max":99},"Force":{"Min":2,"Max":3},"Decay":2,"KillMains":false,"FloorId":"base","BotTypes":["PMC"]}},"CustomZones":[{"Name":"Legacy custom","Position":{"x":10,"y":20},"Radius":{"Min":40,"Max":90},"Force":{"Min":1,"Max":2},"Decay":1},{"Name":"Scoped custom","FloorId":"base","BotTypes":["Scav"]}]}
    """;
    File.WriteAllText(fixturePaths[0],oldFile);
    var migratedStore=new ZoneStoreService(new TestLogger<ZoneStoreService>());
    migratedStore.MigrateExistingZoneFiles();
    Check(Json.Deserialize<MapZoneModel>(File.ReadAllText(fixturePaths[0])).BuiltinZones["ZoneSubCommand"].FloorId=="bunkers","Startup migration runs before opening the editor");
    var migrated=migratedStore.GetWorking("RezervBase");
    var built=migrated.BuiltinZones["ZoneSubCommand"];
    Check(migrated.SchemaVersion==ZoneStoreService.CurrentZoneSchema&&built.FloorId=="bunkers","Legacy file migrates to automatic Bunkers");
    Check(built.Radius.Min==45&&built.Radius.Max==99&&built.Force.Min==2&&built.Force.Max==3&&built.Decay==2&&!built.KillMains&&built.BotTypes.SequenceEqual(new[]{"PMC"}),"Migration preserves all built-in tuning except floor");
    Check(migrated.CustomZones[0].FloorId==null&&migrated.CustomZones[0].Name=="Legacy custom"&&migrated.CustomZones[0].Position.X==10,"Legacy custom stays all floors at its position");
    Check(migrated.CustomZones[1].FloorId=="base"&&migrated.CustomZones[1].BotTypes[0]=="Scav","Scoped custom stays untouched");
    Check(File.ReadAllText(fixturePaths[1])==oldFile,"Original zone file backed up byte for byte");
    var firstSave=File.ReadAllText(fixturePaths[0]);
    migratedStore.GetZones("RezervBase");
    Check(firstSave==File.ReadAllText(fixturePaths[0])&&oldFile==File.ReadAllText(fixturePaths[1]),"Migration is idempotent and preserves first backup");
    Check(migratedStore.GetPendingMaps().Count==0,"Automatic migration does not create unsaved user edits");
    built.Force.Max=8;
    migratedStore.RecordNativeFloors("RezervBase",new(){["ZoneSubCommand"]="base|bunkers"});
    Check(built.FloorId=="base|bunkers"&&built.Force.Max==8,"Live scene update preserves pending sliders");
    Check(migratedStore.GetPendingMaps().SequenceEqual(new[]{"RezervBase"}),"User edits remain pending after scene update");
    migratedStore.DiscardAllPending();
    Check(migratedStore.GetWorking("RezervBase").BuiltinZones["ZoneSubCommand"].Force.Max==3,"Discard retains saved force after scene update");
    Check(migratedStore.GetWorking("RezervBase").BuiltinZones["ZoneSubCommand"].FloorId=="base|bunkers","Discard cannot restore obsolete native floor");
    var reopened=new ZoneStoreService(new TestLogger<ZoneStoreService>());
    Check(reopened.NativeFloorsFor("RezervBase","ZoneSubCommand")=="base|bunkers"&&reopened.HasLiveNativeFloors("RezervBase","ZoneSubCommand"),"Scene metadata persists across server restart");
    var import=new ZonePackModel{Maps=new(){["RezervBase"]=new(){BuiltinZones=new(){["ZoneSubCommand"]=new(){FloorId="future",BotTypes=["Scav"]}}}}};
    reopened.ImportPack(Json.Serialize(import));
    Check(reopened.GetWorking("RezervBase").BuiltinZones["ZoneSubCommand"].FloorId=="base|bunkers","Old pack cannot override automatic native floors");
    reopened.RestoreWorking(new Dictionary<string,string>{{"RezervBase",oldFile}});
    Check(reopened.GetWorking("RezervBase").BuiltinZones["ZoneSubCommand"].FloorId=="base|bunkers","Undo snapshots cannot override automatic native floors");
    var rejected=false;
    try{reopened.RecordNativeFloors("../bad",new());}catch(InvalidDataException){rejected=true;}
    Check(rejected,"Native report rejects invalid map IDs");
    rejected=false;
    try{reopened.RecordNativeFloors("RezervBase",new(){["ZoneSubCommand"]="future"});}catch(InvalidDataException){rejected=true;}
    Check(rejected,"Native report rejects unknown floors");
    // An unwritable backup must leave the original file intact and keep personal settings in memory.
    File.WriteAllText(fixturePaths[0],oldFile);
    File.Delete(fixturePaths[1]);
    Directory.CreateDirectory(fixturePaths[1]);
    try
    {
        var blockedSave=reopened.GetZones("RezervBase");
        Check(blockedSave.BuiltinZones["ZoneSubCommand"].Force.Max==3&&blockedSave.CustomZones.Count==2,"Failed migration save never falls back to default tuning");
        Check(File.ReadAllText(fixturePaths[0])==oldFile,"Failed backup leaves the original config untouched");
    }
    finally { Directory.Delete(fixturePaths[1]); }
}
finally
{
    foreach(var (path,bytes) in originals)
        if(bytes==null){if(File.Exists(path))File.Delete(path);}else File.WriteAllBytes(path,bytes);
}
Console.WriteLine($"PASS: {checks} zone scope checks (engine doubles, no Unity raid)");
