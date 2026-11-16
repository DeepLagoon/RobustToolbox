using NUnit.Framework;
using Robust.Client;
using Robust.Client.Console;
using Robust.Client.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Utility;

namespace Robust.UnitTesting.Client.Sprite;

[TestFixture]
public sealed class SpriteErrorLoggingTest : RobustIntegrationTest
{
    [Test]
    public async Task MissingRsiErrorsAreDeduplicatedAndBounded()
    {
        var client = StartClient(new ClientIntegrationOptions { FailureLogLevel = LogLevel.Fatal });
        await client.WaitIdleAsync();
        await client.WaitPost(() =>
        {
            client.Resolve<IBaseClient>().StartSinglePlayer();
            var sprites = client.System<SpriteSystem>();
            var console = client.Resolve<IClientConsoleHost>();
            var logs = new List<string>();
            console.AddFormatted += CaptureRsiError;

            try
            {
                var missing = new SpriteSpecifier.Rsi(new ResPath("missing-test.rsi"), "icon");
                var fallback = sprites.GetFallbackState();
                for (var i = 0; i < 100_000; i++)
                {
                    if (!ReferenceEquals(sprites.GetState(missing), fallback))
                        Assert.Fail("A missing RSI should keep returning the fallback state.");
                }

                Assert.That(logs, Has.Count.EqualTo(1));
                Assert.That(logs[0], Does.Contain("icon"));

                sprites.GetState(new SpriteSpecifier.Rsi(missing.RsiPath, "other"));
                Assert.That(logs, Has.Count.EqualTo(2));

                for (var i = 0; i < 256; i++)
                    sprites.GetState(new SpriteSpecifier.Rsi(missing.RsiPath, $"missing-state-{i}"));

                Assert.That(logs, Has.Count.EqualTo(128));

                var valid = new SpriteSpecifier.Rsi(new ResPath("debugRotation.rsi"), "direction1");
                Assert.That(sprites.GetState(valid), Is.Not.SameAs(fallback));
            }
            finally
            {
                console.AddFormatted -= CaptureRsiError;
            }

            void CaptureRsiError(object? sender, AddFormattedMessageArgs args)
            {
                var line = args.Message.ToString();
                if (line.Contains("system.sprite: Failed to load RSI") && line.Contains("missing-test.rsi"))
                    logs.Add(line);
            }
        });
    }
}
