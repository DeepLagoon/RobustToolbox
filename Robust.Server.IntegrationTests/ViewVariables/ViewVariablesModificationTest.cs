using Moq;
using NUnit.Framework;
using Robust.Server.ViewVariables;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using Robust.Shared.ViewVariables;

namespace Robust.UnitTesting.Server.ViewVariables;

[TestFixture]
public sealed class ViewVariablesModificationTest
{
    [Test]
    public void SuccessfulEditsRetainEntityAndAuthorButFailedEditsDoNotEmit()
    {
        var user = new NetUserId(Guid.NewGuid());
        var entity = new EntityUid(42);
        var host = new Mock<IServerViewVariablesInternal>();
        host.Setup(h => h.TraitIdsFor(typeof(Editable))).Returns(new object[] { ViewVariablesTraits.Members });
        var serializer = new Mock<IRobustSerializer>();
        serializer.Setup(s => s.CanSerialize(It.IsAny<Type>())).Returns(true);
        var bus = new Mock<IEventBus>();
        var entities = new Mock<IEntityManager>();
        entities.SetupGet(e => e.EventBus).Returns(bus.Object);
        var value = new Editable();
        // A nested object is not a component itself, so the owning session supplies the entity.
        var session = new ViewVariablesSession(user, value, null, 1, host.Object,
            serializer.Object, entities.Object, Mock.Of<ISawmill>(), entity);
        var members = (ViewVariablesBlobMembers) session.DataRequest(new ViewVariablesRequestMembers())!;
        var fields = members.MemberGroups.SelectMany(g => g.groupMembers).ToDictionary(m => m.Name);

        session.Modify(new object[] { new ViewVariablesMemberSelector(fields[nameof(Editable.Value)].PropertyIndex) }, 7);
        Assert.That(value.Value, Is.EqualTo(7));
        bus.Verify(b => b.RaiseEvent(EventSource.Local,
            It.Is<ViewVariablesModifiedEvent>(e => e.Entity == entity && e.User == user)), Times.Once);

        session.Modify(new object[] { new ViewVariablesMemberSelector(fields[nameof(Editable.Throws)].PropertyIndex) }, 7);
        session.Modify(new object[] { new ViewVariablesMemberSelector(int.MaxValue) }, 7);
        bus.Verify(b => b.RaiseEvent(EventSource.Local, It.IsAny<ViewVariablesModifiedEvent>()), Times.Once);
    }

    private sealed class Editable
    {
        [ViewVariables(VVAccess.ReadWrite)] public int Value { get; set; }
        [ViewVariables(VVAccess.ReadWrite)] public int Throws
        {
            get => 0;
            set => throw new InvalidOperationException();
        }
    }
}
