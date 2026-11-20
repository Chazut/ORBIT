using BepInEx.Bootstrap;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Orbit.Looting;
using PackNStrap.Core.Items;

var checks = 0;
void Check(bool value, string reason)
{
    if (!value) throw new Exception(reason);
    checks++;
}

var world = Singleton<GameWorld>.Instance = new GameWorld();
world.Victims["host"] = new Player();
world.Victims["remote-scav"] = new Player();
world.Victims["bot"] = new Player { IsAI = true };
var gear = new InventoryEquipment();
var owner = new CorpseItemController { RootItem = gear };
gear.Controller = owner;
var belt = new CustomBeltItemClass { Parent = gear };
gear.ArmBand.ContainedItem = belt;
var pouch = new CompoundItem { Parent = belt };
var ammo = new Item { Parent = pouch };
var backpack = new CompoundItem { Parent = gear };
var queuedPickup = new Pickup(ammo);
bool Blocked(Item item) => PackNStrapCompat.TakesProtectedItem(new Pickup(item));

Check(!Blocked(ammo) && PackNStrapCompat.CorpseBelt(gear) == null, "optional plugin absent");
Chainloader.PluginInfos.Add("com.wtt.packnstrap", new object());
foreach (var id in new[] { "host", "remote-scav", "unknown", "", null })
{
    owner.KilledProfileID = id;
    Check(!PackNStrapCompat.IsBotCorpse(owner), "only a confirmed bot can be searched");
    Check(PackNStrapCompat.ProtectedBelt(gear) == belt, "omit belt from available ammo");
    Check(Blocked(belt) && Blocked(pouch) && Blocked(ammo), "protect the full subtree");
    Check(!Blocked(backpack), "other corpse slots still lootable");
    Check(PackNStrapCompat.TakesProtectedItem(new SwapResult(ammo, backpack)), "swap first operand");
    Check(PackNStrapCompat.TakesProtectedItem(new SwapResult(backpack, ammo)), "swap second operand");
}
ammo.Parent = backpack;
Check(!PackNStrapCompat.TakesProtectedItem(queuedPickup), "can take an item in the backpack");
ammo.Parent = pouch;
Check(PackNStrapCompat.TakesProtectedItem(queuedPickup), "item moved into belt during search");
owner.KilledProfileID = "bot";
Check(PackNStrapCompat.IsBotCorpse(owner), "bot with retained raid identity");
Check(PackNStrapCompat.ProtectedBelt(gear) == null && !Blocked(ammo), "bot belt contents still lootable");
Singleton<GameWorld>.Instance = null;
Check(Blocked(ammo), "missing world protects the belt");
gear.ArmBand.ContainedItem = new CompoundItem { Parent = gear };
Check(PackNStrapCompat.CorpseBelt(gear) == null && !Blocked(gear.ArmBand.ContainedItem), "ordinary armband");
Console.WriteLine($"PASS {checks} corpse belt checks");
