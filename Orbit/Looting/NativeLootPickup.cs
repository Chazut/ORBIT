using System;
using EFT;
using UnityEngine;

namespace Orbit.Looting;

// Pickup on MovePlayerState is a no-op. Wait for the native Idle transition,
// then for its animation callback before allowing an inventory transaction.
internal sealed class NativeLootPickup : IDisposable
{
    private readonly Player _player;
    private object _requestedFrom;
    private Action _callback;
    private int _generation;
    private bool _disposed;
    internal bool Started { get; private set; }
    internal bool Ready { get; private set; }

    internal NativeLootPickup(Player player) { _player = player; }

    internal void Tick()
    {
        if (_disposed || Ready) return;
        var movement = _player.MovementContext;
        if (Started && !ReferenceEquals(movement.OverridenState, movement.PickUpState))
        {
            Started = false;
            _requestedFrom = null;
            _generation++;
        }
        if (Started) return;
        _player.Move(Vector2.zero);
        var current = _player.CurrentManagedState;
        if (movement.OverridenState != null || ReferenceEquals(current, _requestedFrom)) return;
        _requestedFrom = current;
        int generation = ++_generation;
        _callback = () => { if (!_disposed && generation == _generation) Ready = true; };
        current.Pickup(true, _callback);
        Started = ReferenceEquals(movement.OverridenState, movement.PickUpState);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        var movement = _player.MovementContext;
        bool ownsCallback = _callback != null && ReferenceEquals(movement.PickupAction, _callback);
        if (Started && ReferenceEquals(movement.OverridenState, movement.PickUpState)
            && (ownsCallback || Ready && movement.PickupAction == null))
        {
            _player.CurrentManagedState.Pickup(false, null);
            _player.UpdateInteractionCast();
        }
        if (ownsCallback) movement.PickupAction = null;
    }
}
