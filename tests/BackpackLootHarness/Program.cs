using EFT.Interactive;
using EFT.InventoryLogic;
using Orbit.Looting;

static void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
static (InventoryController IC, Grid Bag, Grid Pouch, Item Source, Grid World, OrbitLootHandler Bot) SetUp(int w, int h)
{
    var ic = new InventoryController();
    Grid Equip(EquipmentSlot key,int width,int height)
    {
        var bag = new CompoundItem();
        var slot = new Slot { ParentItem = ic.Equipment, ContainedItem = bag };
        ic.Equipment.Slots[key]=slot;
        bag.CurrentAddress = new ItemAddress(slot);
        var grid = new Grid { ParentItem=bag, Owner=ic, GridWidth=width, GridHeight=height };
        bag.Grids=[grid];
        return grid;
    }
    var pouch = Equip(EquipmentSlot.SecuredContainer,4,4);
    var bag = Equip(EquipmentSlot.Backpack,w,h);
    var painting = new Item {Name="Mona Lisa Replica",W=3,H=4,Price=4_500_000};
    var world = new Grid {ParentItem=new CompoundItem(),GridWidth=4,GridHeight=4};
    world.Put(painting,new(0,0,ItemRotation.Horizontal));
    var bot = new OrbitLootHandler {CurrentTargetKind=LootKind.Item,CurrentTarget=new LootItem {Item=painting}};
    bot._player.InventoryController = ic;
    return (ic,bag,pouch,painting,world,bot);
}
static Item Put(Grid bag,int x,int y,float value=1000)
{
    var item = new Item {Price=value};
    bag.Put(item,new(x,y,ItemRotation.Horizontal));
    return item;
}

var empty = SetUp(3,4);
var direct = await empty.Bot.Prepare(empty.Source,empty.IC);
Check(direct.Succeeded && ((MoveResult)direct.Value).To.Container == empty.Bag, "secure preferred by native pickup, safe bag used instead");
var inside = Put(empty.Pouch,0,0);
Check(RaidLootPlacement.GoesIntoSecureSlot(new MergeResult(inside),empty.IC)
    && RaidLootPlacement.GoesIntoSecureSlot(new TransferResult(inside),empty.IC), "stack merges and partial transfers into secure storage rejected");
var innerBag = new CompoundItem();
empty.Pouch.Put(innerBag,new(1,0,ItemRotation.Horizontal));
var inner = new Grid { ParentItem=innerBag };
Check(RaidLootPlacement.InSecureSlot(inner,empty.IC) && RaidLootPlacement.GoesIntoSecureSlot(new SwapResult(direct.Value is MoveResult m ? m.To : null,new ItemAddress(inner)),empty.IC), "nested grids and reverse swap destination protected");

var fragmented = SetUp(4,4);
for (int row=0;row<4;row++) Put(fragmented.Bag,row,row);
var planned = await fragmented.Bot.Prepare(fragmented.Source,fragmented.IC);
Check(planned.Succeeded && fragmented.IC.Dropped.Count == 0 && fragmented.Bag.Items.Count == 4, "3x4 painting fits fragmented 4x4 bag after moving residents");
Check((await fragmented.IC.TryRunNetworkTransaction(planned,null)).Succeed && fragmented.Source.CurrentAddress.Container == fragmented.Bag, "planned pickup really fits after rearrangement");

var full = SetUp(4,4);
for(int y=0;y<4;y++) for(int x=0;x<4;x++) Put(full.Bag,x,y,x==0?2_000_000:1000);
full.Bot.CurrentTargetKind=LootKind.Container;
full.Bot._currentSourceRoot=full.World.ParentItem;
var clock = System.Diagnostics.Stopwatch.StartNew();
var dropTimes = new List<long>();
full.IC.AfterThrow = () => dropTimes.Add(clock.ElapsedMilliseconds);
var replacement = full.Bot.Prepare(full.Source,full.IC);
Check(dropTimes.Count == 1 && !replacement.IsCompleted, "first drop is immediate and the next drop yields");
var exchange = await replacement;
var delays = UnityEngine.Random.Delays;
Check(delays.Count == dropTimes.Count - 1 && delays.Distinct().Count() == 2
    && delays.Select((ms, i) => dropTimes[i + 1] - dropTimes[i] >= ms - 20).All(ok => ok),
    "twelve confirmed drops have eleven variable gaps and no wait after the last");
Check(exchange.Succeeded && full.IC.Dropped.Count == 12 && full.IC.Dropped.Sum(i=>i.Price)==12000 && full.Bag.Items.Count == 4, "full container pickup displaces cheapest profitable rectangle only");
Check(full.Bot.Stats.TotalGained == -12000 && full.IC.Dropped.TrueForAll(i=>i.CurrentAddress==null), "drops confirmed, deducted, and available outside the bag");
var expensive = SetUp(3,4);
var stack = Put(expensive.Bag,0,0,500_000); stack.StackObjectsCount=9;
var rejected = await expensive.Bot.Prepare(expensive.Source,expensive.IC);
Check(!rejected.Succeeded && expensive.IC.Dropped.Count==0 && expensive.Bag.Items.Contains(stack), "equal stack value kept and secure slot unused");

var nested = SetUp(3,4);
var caseItem = new CompoundItem {Price=100};
nested.Bag.Put(caseItem,new(0,0,ItemRotation.Horizontal));
var caseGrid = new Grid { ParentItem=caseItem }; caseItem.Grids=[caseGrid];
Put(caseGrid,0,0,5_000_000);
Check(!(await nested.Bot.Prepare(nested.Source,nested.IC)).Succeeded && nested.IC.Dropped.Count==0, "full contents of a cheap case count toward replacement cost");
var ghost = SetUp(3,4);
var resident=Put(ghost.Bag,0,0);
ghost.IC.ForbiddenDrop=resident;
Check(!(await ghost.Bot.Prepare(ghost.Source,ghost.IC)).Succeeded && ghost.IC.Dropped.Count==0,"discard limits protect resident");
ghost.IC.ForbiddenDrop=null; ghost.Bot.DormantMode=true;
try { await ghost.Bot.Prepare(ghost.Source,ghost.IC); throw new Exception("expected wake"); }
catch (LootInventoryBusyException) {}
Check(ghost.Bot.NeedsAwakeInventory && ghost.IC.Dropped.Count==0,"ghost requests a wake before physical drop");
ghost.Bot.DormantMode=false; ghost.IC.Reject=ghost.Source;
Check(!(await ghost.Bot.Prepare(ghost.Source,ghost.IC)).Succeeded && ghost.IC.Dropped.Count==0 && resident.CurrentAddress.Container==ghost.Bag,"failed native pickup preflight rolls back grid before any drop");
ghost.IC.Reject=null; resident.PinLockState=EItemPinLockState.Locked;
Check(!(await ghost.Bot.Prepare(ghost.Source,ghost.IC)).Succeeded && ghost.IC.Dropped.Count==0,"pinned item retained");
var small=SetUp(2,4);
Check(!(await small.Bot.Prepare(small.Source,small.IC)).Succeeded && small.Pouch.Items.Count==0,"too narrow in both orientations: leave the painting at source");
var split = SetUp(2,4);
var splitBag = (CompoundItem)split.Bag.ParentItem;
splitBag.Grids=[split.Bag,new Grid {ParentItem=splitBag,Owner=split.IC,GridWidth=2,GridHeight=4}];
Check(!(await split.Bot.Prepare(split.Source,split.IC)).Succeeded,"sixteen free cells in two narrow compartments cannot fit one painting");
var sort = SetUp(4,5);
var a=Put(sort.Bag,1,0); a.W=a.H=2;
var b=Put(sort.Bag,1,2); b.W=b.H=2;
void Layout(int ax,int ay,int bx,int by)
{
    sort.Bag.Remove(a,false); sort.Bag.Remove(b,false);
    sort.Bag.Put(a,new(ax,ay,ItemRotation.Horizontal)); sort.Bag.Put(b,new(bx,by,ItemRotation.Horizontal));
}
int sorts=0;
sort.IC.Sort=()=>{
    Layout(0,0,2,0);
    return new SortResult(()=>Layout(1,0,1,2),()=>{ sorts++; Layout(0,0,2,0); });
};
Check((await sort.Bot.Prepare(sort.Source,sort.IC)).Succeeded && sorts==1 && sort.IC.Dropped.Count==0,
    "full native sort packs two crates together before sacrificing anything");
var rotated=SetUp(4,3);
Check((await rotated.Bot.Prepare(rotated.Source,rotated.IC)).Succeeded,"rotated painting fits");
var lost=SetUp(3,4);
Put(lost.Bag,0,0); Put(lost.Bag,1,0);
lost.IC.AfterThrow=()=>lost.World.Remove(lost.Source,false);
Check(!(await lost.Bot.Prepare(lost.Source,lost.IC)).Succeeded && lost.IC.Dropped.Count==1,"source taken during a network operation stops further drops");
var canceled=SetUp(3,4);
Put(canceled.Bag,0,0); Put(canceled.Bag,1,0);
using var cancel=new CancellationTokenSource();
canceled.IC.AfterThrow=()=>cancel.Cancel();
try { await canceled.Bot.Prepare(canceled.Source,canceled.IC,cancel.Token); throw new Exception("expected cancellation"); }
catch(OperationCanceledException){}
Check(canceled.IC.Dropped.Count==1 && canceled.Pouch.Items.Count==0,"cancelled loot session stops after the in-flight drop");

static async Task ExpectCancelled(Task sorting)
{
    try { await sorting; }
    catch (OperationCanceledException) { return; }
    throw new Exception("expected interrupted sort");
}

foreach (bool underFire in new[] { false, true })
{
    var contact = SetUp(3,4);
    Put(contact.Bag,0,0); Put(contact.Bag,1,0);
    var sorting = contact.Bot.Prepare(contact.Source,contact.IC);
    // Threat arrives in the gap, before the action layer has cancelled the token.
    contact.Bot._bot.Memory.HaveEnemy = !underFire;
    contact.Bot._bot.Memory.IsUnderFire = underFire;
    await ExpectCancelled(sorting);
    Check(contact.IC.Dropped.Count == 1 && contact.Bot.Stats.TotalGained == -1000
        && contact.Bag.Items.Count == 1 && contact.World.Items.Contains(contact.Source),
        "enemy or incoming fire during the pause preserves remaining cargo and leaves the target at source");
    contact.Bot._bot.Memory.HaveEnemy = contact.Bot._bot.Memory.IsUnderFire = false;
    Check((await contact.Bot.Prepare(contact.Source,contact.IC)).Succeeded && contact.IC.Dropped.Count == 2,
        "a fresh session after combat replans from the actual inventory");
}

var blocked = SetUp(3,4);
Put(blocked.Bag,0,0);
blocked.Bot._bot.Memory.IsUnderFire = true;
await ExpectCancelled(blocked.Bot.Prepare(blocked.Source,blocked.IC));
Check(blocked.IC.Dropped.Count == 0, "combat before the first drop keeps the whole bag");

var gap = SetUp(3,4);
Put(gap.Bag,0,0); Put(gap.Bag,1,0);
using var gapCancel = new CancellationTokenSource();
var waiting = gap.Bot.Prepare(gap.Source,gap.IC,gapCancel.Token);
gapCancel.Cancel();
await ExpectCancelled(waiting);
Check(gap.IC.Dropped.Count == 1 && gap.World.Items.Contains(gap.Source), "action handoff cancels a pending gap");

foreach (var late in new[] { "accepted", "refused", "retired" })
{
    var inFlight = SetUp(3,4);
    Put(inFlight.Bag,0,0); Put(inFlight.Bag,1,0);
    Action<bool> reply = null;
    inFlight.IC.SubmitThrow = callback => reply = callback;
    using var handoff = new CancellationTokenSource();
    var sorting = inFlight.Bot.Prepare(inFlight.Source,inFlight.IC,handoff.Token);
    var transaction = LootActorGuard.LastTransaction;
    handoff.Cancel();
    await ExpectCancelled(sorting);
    Check(reply != null && !transaction.IsCompleted && inFlight.Bot.Stats.TotalGained == 0,
        "combat hands back control before the pending throw is acknowledged");
    inFlight.Bot.HasLiveOwner = late != "retired";
    reply(late != "refused");
    await transaction;
    Check(inFlight.IC.Dropped.Count == (late == "refused" ? 0 : 1)
        && inFlight.Bot.Stats.TotalGained == (late == "accepted" ? -1000 : 0)
        && inFlight.World.Items.Contains(inFlight.Source), "late " + late + " reply cannot resume sorting");
}

var freed = SetUp(3,4);
Put(freed.Bag,0,0); Put(freed.Bag,1,0);
var recheck = freed.Bot.Prepare(freed.Source,freed.IC);
var extraGrid = new Grid { ParentItem=freed.Bag.ParentItem, Owner=freed.IC, GridWidth=3, GridHeight=4 };
((CompoundItem)freed.Bag.ParentItem).Grids = [freed.Bag, extraGrid];
Check((await recheck).Succeeded && freed.IC.Dropped.Count == 1, "new space during the pause avoids another drop");
