using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Robust.Server.ViewVariables;

/// <summary>
/// Raised locally after a successful VV edit or addcomp/rmcomp operation on an entity.
/// Carries the owner through sessions for nested component data. Never sent over the network.
/// </summary>
public sealed class ViewVariablesModifiedEvent(EntityUid entity, NetUserId user) : EntityEventArgs
{
    public EntityUid Entity { get; } = entity;
    public NetUserId User { get; } = user;
}
