using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using Orbit.Core;
using Orbit.Navigation;
using Orbit.Settings;
using Orbit.Systems;
using SPT.Common.Http;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace Orbit.Helpers;

// Authoring only: no scene scans, physics queries or network work until the capture key is pressed.
internal static class DoorCaptureTool
{
    private static Task<MarkedDoorCaptureReply> _pending;
    private static string _doorId;
    private static string _status = "Enable capture, bind the shortcut, then face a door until its interaction prompt appears.";
    private static float _toastUntil;
    private static float _nextCapture;
    private static GUIStyle _toastStyle;
    internal static string Status => _status;

    internal static void Update()
    {
        if (_pending != null && _pending.IsCompleted)
        {
            try
            {
                var reply = _pending.GetAwaiter().GetResult();
                var message = reply.Success
                    ? $"{(reply.Created ? "Room added" : "Already configured")} in {reply.PresetName}. {reply.Message}"
                    : "Door ID copied. Could not confirm the save: " + reply.Message;
                Notice(message);
                Log.Always($"DOOR CAPTURE: {(reply.Success ? reply.Created ? "saved" : "duplicate" : "failed")} door={_doorId} preset={reply.PresetName} point={reply.PointId} review={reply.NeedsReview} detail={reply.Message}");
            }
            catch (Exception ex)
            {
                Notice("Door ID copied. Save not confirmed: " + ex.Message + " Capture again to check; existing points are kept.");
                Log.Warning($"DOOR CAPTURE: failed door={_doorId} detail={ex.Message}");
            }
            finally { _pending = null; }
        }
        if (Plugin.DoorCaptureEnabled?.Value != true || Plugin.DoorCaptureKey == null
            || _pending != null || Time.unscaledTime < _nextCapture || !Plugin.DoorCaptureKey.Value.IsDown()) return;
        _nextCapture = Time.unscaledTime + 1;
        if (Cursor.visible) return; // Never capture while editing F12 bindings or using a menu.
        try { Capture(); }
        catch (Exception ex)
        {
            Notice("Door capture failed: " + ex.Message);
            Log.Warning($"DOOR CAPTURE: failed detail={ex.Message}");
        }
    }

    private static void Capture()
    {
        var world = Singleton<GameWorld>.Instance;
        var player = world?.MainPlayer;
        if (player == null || !player.IsYourPlayer || player.IsAI || player.HealthController?.IsAlive != true)
        { Notice("Enter a raid with your player to capture a door."); return; }
        // Use EFT's current interaction target, including reader proxies. A nearby door through
        // a wall or outside the player's aim must never be selected by a nearest-object search.
        var target = player.InteractableObject;
        var door = target as Door; // EFT already resolves an InteractiveProxy to its linked object.
        if (door == null || string.IsNullOrWhiteSpace(door.Id)
            || Vector3.Distance(player.Position, door.GetInteractionParameters(player.Position).InteractionPosition) > 5f)
        { Notice("Face a nearby door until its interaction prompt appears, then press the capture key."); return; }
        GUIUtility.systemCopyBuffer = door.Id;
        _doorId = door.Id;
        if (!MultiStepAccess.CanForceUnlock(door))
        { Notice("Door ID copied. This powered door requires its access sequence and cannot use Marked Rush."); return; }

        var map = Singleton<OrbitManager>.Instance?.ZoneKey
            ?? MapVariants.ZoneKey(world.LocationId, MapVariants.Detect(world.LocationId));
        if (!RushDefaults.IsMarkedRushTarget(map, door.Id))
        { Notice("Door ID copied. This room's access is not supported by Marked Rush."); return; }
        var approach = door.GetInteractionParameters(player.Position).InteractionPosition;
        var approachConfirmed = TryFloor(approach, player.Position.y, out var floor);
        approach = approachConfirmed ? floor : player.Position;
        var forward = door.transform.position - player.Position;
        forward.y = 0;
        forward = forward.sqrMagnitude > .01f ? forward.normalized : player.LookDirection;
        forward.y = 0;
        var loot = new Vector3(door.transform.position.x, approach.y, door.transform.position.z) + forward.normalized * 2;
        var radius = 5f;
        var room = FindRoom(door);
        var roomConfirmed = false;
        if (room != null)
        {
            var scope = new MarkedRoomScope(loot, radius, room);
            // Sample the room centre first, then nearby interior points. Keep the captured floor.
            var center = scope.Center;
            for (var i = 0; i < 9 && !roomConfirmed; i++)
            {
                var seed = i == 0 ? center : center + new Vector3(Mathf.Cos(i * Mathf.PI / 4), 0, Mathf.Sin(i * Mathf.PI / 4)) * 1.5f;
                if (TryFloor(seed, approach.y, out var sample) && scope.Contains(sample))
                { loot = sample; roomConfirmed = true; }
            }
            radius = Mathf.Clamp(scope.Radius, 1, 50);
        }
        if (!roomConfirmed && TryFloor(loot, approach.y, out var fallback)) loot = fallback;
        var capture = new MarkedDoorCapture { MapId = map, DoorId = door.Id,
            X = approach.x, Y = approach.y, Z = approach.z, LootX = loot.x, LootY = loot.y, LootZ = loot.z,
            Radius = radius, PlacementConfirmed = approachConfirmed && roomConfirmed };
        Log.Always($"DOOR CAPTURE: selected map={map} door={door.Id} position={door.transform.position} approach={approach} loot={loot} confirmed={capture.PlacementConfirmed}");
        Notice("Door ID copied. Saving room to the active preset...");
        _pending = Task.Run(() => Save(capture));
    }

    private static bool TryFloor(Vector3 point, float height, out Vector3 result)
    {
        point.y = height;
        if (NavMesh.SamplePosition(point, out var hit, 1.5f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - height) <= 1f)
        { result = hit.position; return true; }
        result = point;
        return false;
    }

    private static SpatialAudioRoom FindRoom(Door door)
    {
        // Same interior selection as the runtime Marked Rush scope. Only scan at the user's request.
        SpatialAudioRoom selected = null;
        foreach (var portal in Object.FindObjectsOfType<Audio.SpatialSystem.SpatialAudioPortal>(true))
        {
            if (portal.DoorID != door.Id || (portal.transform.position - door.transform.position).sqrMagnitude >= 225) continue;
            foreach (var room in new[] { portal.FrontRoom, portal.BackRoom })
                if (room != null && !room.IsOutdoor && room.Bounds.size.sqrMagnitude > 0
                    && (selected == null || room.RoomSize < selected.RoomSize)) selected = room;
        }
        return selected;
    }

    private static async Task<MarkedDoorCaptureReply> Save(MarkedDoorCapture capture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context = await Send("/orbit/tools/door/context", null, timeout.Token).ConfigureAwait(false);
        if (!context.Success) return context;
        capture.ExpectedPresetId = context.PresetId;
        return await Send("/orbit/tools/door/capture", new { Payload = capture }, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<MarkedDoorCaptureReply> Send(string path, object body, CancellationToken cancel)
    {
        var client = RequestHandler.HttpClient;
        using var request = client.CreateNewHttpRequest(body == null ? HttpMethod.Get : HttpMethod.Post, path);
        if (body != null)
            request.Content = new ByteArrayContent(SPT.Common.Utils.Zlib.Compress(
                Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body)), SPT.Common.Utils.ZlibCompression.Maximum));
        using var response = await client.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cancel).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 32768) throw new InvalidDataException("Invalid door capture response.");
            buffer.Write(chunk, 0, read);
        }
        var data = buffer.ToArray();
        if (SPT.Common.Utils.Zlib.IsCompressed(data)) data = SPT.Common.Utils.Zlib.Decompress(data);
        if (data.Length > 32768) throw new InvalidDataException("Invalid door capture response.");
        var json = Newtonsoft.Json.Linq.JObject.Parse(Encoding.UTF8.GetString(data));
        var reply = json.ToObject<MarkedDoorCaptureReply>();
        // Require an explicit version marker, including when an older server returns an empty object.
        if (reply == null || !json.TryGetValue("Format", out var format) || (string)format != MarkedDoorCaptureReply.FormatName)
            throw new InvalidDataException("Update the ORBIT server mod to use door capture.");
        return reply;
    }

    private static void Notice(string message)
    { _status = message; _toastUntil = Time.unscaledTime + 12; }

    internal static void DrawStatus()
    {
        if (Time.unscaledTime >= _toastUntil) return;
        _toastStyle ??= new GUIStyle(GUI.skin.box) { fontSize = 18, wordWrap = true, alignment = TextAnchor.MiddleCenter };
        GUI.Box(new Rect((Screen.width - Math.Min(800, Screen.width - 40)) / 2f, 60,
            Math.Min(800, Screen.width - 40), 120), "ORBIT door capture\n" + _doorId + "\n" + _status, _toastStyle);
    }
}
