using Robust.Shared.GameObjects;

namespace Robust.Shared.Physics.Events;

/// <summary>
/// Server-only notification that an unsafe fixture was rejected and its entity queued for deletion.
/// This is a local event; it does not change the network protocol or the official client contract.
/// </summary>
[ByRefEvent]
public readonly record struct InvalidPhysicsFixtureEvent(EntityUid Entity, string FixtureId, string Reason);
