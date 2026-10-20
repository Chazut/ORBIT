using System;
using System.Reflection;
using System.Collections;
using EFT;
using EFT.Interactive;
using Orbit.Api;
using UnityEngine;

namespace Orbit.Systems;

// A door animation can outlive the synchronous call. Retain its actor until it finishes,
// and recover only the transition still owned by this operation.
internal sealed class OperationInteraction
{
    private static readonly FieldInfo StateField = typeof(WorldInteractiveObject).GetField("_interaction",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    private static readonly PropertyInfo BreakProperty = StateField?.FieldType.GetProperty("Break");
    private static readonly PropertyInfo ResultProperty = StateField?.FieldType.GetProperty("ResultState");
    internal enum Result { Ready, Waiting, Failed }
    private WorldInteractiveObject _target;
    private Player _actor;
    private EDoorState _before;
    private float _angle, _started, _retryAt, _busySince = -1;
    private ulong _revision;
    private Coroutine _unlock;
    private bool _unlockRunning, _cleanupFailed;
    internal int Attempts { get; private set; }
    internal string Detail { get; private set; }
    internal bool RecoveryFailed => _cleanupFailed;

    private bool Owns => _target != null && ReferenceEquals(_target.InteractingPlayer, _actor)
        && (_target is not Door door || OrbitDoorEvents.Revision(door) == _revision);

    // Poll independently of reach: a displaced operator must still release its native context.
    internal void Poll()
    {
        if (_target == null) return;
        var now = Time.time;
        if (!Owns)
        {
            // Stop only our unlock coroutine. Leave the new actor's state and context intact.
            Log.Info($"MULTISTEP INTERACTION: target={_target.Id} result=superseded attempts={Attempts}");
            Release(true);
            Detail = "interaction ownership changed";
            return;
        }
        var settled = !_unlockRunning && _target.DoorState != EDoorState.Interacting;
        if (_target is Door door && door.DoorState == EDoorState.Open)
            settled &= Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, door.GetAngle(EDoorState.Open))) < 5f;
        if (settled)
        {
            Release(false);
            _retryAt = Mathf.Max(_retryAt, _started + 5f);
        }
        else if (now - _started >= 12f)
        {
            Detail = "owned interaction timed out";
            Log.Warning($"MULTISTEP INTERACTION: target={_target.Id} result=timeout attempts={Attempts} state={_target.DoorState}");
            Release(true);
            _retryAt = now + 2f;
        }
    }

    internal Result Check(WorldInteractiveObject target)
    {
        Poll();
        if (_cleanupFailed) return Result.Failed;
        if (_target != null) return Result.Waiting;
        var now = Time.time;
        if (Attempts >= 3)
        { Detail ??= "interaction attempts exhausted"; return Result.Failed; }
        if (now < _retryAt) return Result.Waiting;
        if (target.DoorState == EDoorState.Interacting || target.InteractingPlayer != null || !target.Operatable)
        {
            if (_busySince < 0) _busySince = now;
            if (now - _busySince < 15f) return Result.Waiting;
            Detail = "external or unavailable interaction timed out";
            return Result.Failed;
        }
        _busySince = -1;
        return Result.Ready;
    }

    internal void Begin(WorldInteractiveObject target, Player actor)
    {
        _target = target; _actor = actor;
        _before = target.DoorState;
        _angle = target.CurrentAngle;
        _started = Time.time;
        _revision = target is Door door ? OrbitDoorEvents.Revision(door) : 0;
        Attempts++;
        Detail = null;
        // Native trigger callbacks may dereference InteractingPlayer.ProfileId, including
        // during the unlock coroutine. Ghost movement does not supply this context itself.
        target.SetUser(actor);
    }

    internal void Unlock(Door door)
    {
        _unlockRunning = true;
        _unlock = door.StartCoroutine(RunUnlock(door.UnlockCoroutine()));
    }

    private IEnumerator RunUnlock(IEnumerator routine)
    {
        while (true)
        {
            bool next;
            try { next = routine.MoveNext(); }
            catch (Exception error)
            {
                _unlockRunning = false;
                Log.Warning($"MULTISTEP INTERACTION: target={_target?.Id} result=unlock-exception attempts={Attempts} error={error}");
                Fault();
                yield break;
            }
            if (!next) break;
            yield return routine.Current;
        }
        _unlockRunning = false;
    }

    internal void Fault()
    {
        Detail = "native interaction exception";
        Release(true);
        _retryAt = Time.time + 2f;
    }

    internal void Reset()
    {
        Release(true);
        Attempts = 0; _busySince = -1; _retryAt = 0; Detail = null; _cleanupFailed = false;
    }

    private void Release(bool cancel)
    {
        var target = _target;
        try
        {
            if (target != null && _unlock != null) target.StopCoroutine(_unlock);
            if (Owns)
            {
                // Switch animations can own delayed chains. Let those native effects finish;
                // only a door transition can be restored here.
                if (cancel && target is Door && (target.DoorState == EDoorState.Interacting || _unlockRunning))
                {
                    // Stop our animation before restoring its pre-interaction state. Never
                    // force Open, reset another actor's transition or consume a credential.
                    var state = StateField?.GetValue(target);
                    if (state == null || BreakProperty?.CanWrite != true || ResultProperty?.CanWrite != true)
                        throw new InvalidOperationException("Native interaction recovery fields unavailable");
                    BreakProperty.SetValue(state, true);
                    ResultProperty.SetValue(state, _before);
                    target.CurrentAngle = _angle;
                    target.DoorState = _before;
                    Log.Info($"MULTISTEP INTERACTION: target={target.Id} result=restored attempts={Attempts} state={target.DoorState} reason={Detail ?? "cancelled"}");
                }
            }
        }
        catch (Exception error)
        {
            _cleanupFailed = true;
            Detail = "interaction recovery failed";
            Log.Warning($"MULTISTEP INTERACTION: target={target?.Id} result=recovery-failed error={error}");
        }
        finally
        {
            try { if (Owns) target.SetUser(null); }
            catch (Exception error)
            {
                _cleanupFailed = true;
                Detail = "interaction actor release failed";
                Log.Warning($"MULTISTEP INTERACTION: target={target?.Id} result=release-failed error={error}");
            }
            _target = null; _actor = null; _unlock = null; _unlockRunning = false;
        }
    }
}
