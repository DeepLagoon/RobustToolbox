using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.UnitTesting.Client.UserInterface;

[TestFixture]
public sealed class DebugConsoleTest : RobustIntegrationTest
{
    [Test]
    public async Task HiddenConsoleKeepsOnlyRecentMessages()
    {
        var client = StartClient();
        await client.WaitIdleAsync();
        await client.WaitPost(() =>
        {
            var ui = client.Resolve<IUserInterfaceManager>();
            var config = client.Resolve<IConfigurationManager>();
            var console = new DebugConsole { Visible = false };
            ui.RootControl.AddChild(console);
            console.Clear();

            Parallel.For(0, 100_000, i => console.AddFormattedLine(FormattedMessage.FromUnformatted($"Message {i}")));
            Assert.That(console.PendingMessageCount, Is.EqualTo(CVars.ConMaxEntries.DefaultValue));

            config.SetCVar(CVars.ConMaxEntries, 2);
            Assert.That(console.PendingMessageCount, Is.EqualTo(2));

            console.AddFormattedLine(FormattedMessage.FromUnformatted("First retained message"));
            console.AddFormattedLine(FormattedMessage.FromUnformatted("Last retained message"));
            console.Visible = true;
            console.DoFrameUpdateRecursive(new FrameEventArgs(0));

            var output = console.FindControl<OutputPanel>("Output");
            Assert.That(output.EntryCount, Is.EqualTo(2));
            Assert.That(output.GetMessage(0).ToString(), Is.EqualTo("First retained message"));
            Assert.That(output.GetMessage(1).ToString(), Is.EqualTo("Last retained message"));
            Assert.That(console.PendingMessageCount, Is.Zero);

            var message = FormattedMessage.FromUnformatted("Pending error");
            console.Visible = false;
            console.AddFormattedLine(message);
            console.Clear();
            Assert.That(console.PendingMessageCount, Is.Zero);
            Assert.That(output.EntryCount, Is.Zero);

            config.SetCVar(CVars.ConMaxEntries, 0);
            console.AddFormattedLine(message);
            Assert.That(console.PendingMessageCount, Is.Zero);
            console.Orphan();
        });
    }
}
