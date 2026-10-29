using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Shared.Map;

namespace Robust.UnitTesting.Client.Graphics;

[TestFixture]
public sealed class MouseButtonPositionTest
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [TestCase(1f, 1f)]
    [TestCase(1.5f, 2f)]
    public void AbsoluteClickUpdatesPositionBeforeButtonWithoutMotion(float scaleX, float scaleY)
    {
        var clydeType = typeof(IClyde).Assembly.GetType("Robust.Client.Graphics.Clyde.Clyde", true)!;
        var clyde = (IClyde) Activator.CreateInstance(clydeType, true)!;
        var implType = clydeType.GetNestedType("Sdl3WindowingImpl", BindingFlags.NonPublic)!;
        var impl = RuntimeHelpers.GetUninitializedObject(implType);
        implType.GetField("_clyde", Fields)!.SetValue(impl, clyde);
        var windowType = implType.GetNestedType("Sdl3WindowReg", BindingFlags.NonPublic)!;
        var window = Activator.CreateInstance(windowType, true)!;
        windowType.GetField("WindowId", Fields)!.SetValue(window, 1u);
        windowType.GetField("PixelRatio", Fields)!.SetValue(window, new Vector2(scaleX, scaleY));
        windowType.GetField("LastMousePos", Fields)!.SetValue(window, new Vector2(10, 20));
        ((IList) clydeType.GetField("_windows", Fields)!.GetValue(clyde)!).Add(window);
        var events = new List<string>();
        clyde.MouseMove += args => events.Add("move");
        clydeType.GetEvent("KeyDown")!.AddEventHandler(clyde, new Action<KeyEventArgs>(args =>
        {
            events.Add("down");
            Assert.That(args.Key, Is.EqualTo(Keyboard.Key.MouseLeft));
            Assert.That(((ScreenCoordinates) clydeType.GetProperty("MouseScreenPosition")!.GetValue(clyde)!).Position, Is.EqualTo(new Vector2(200 * scaleX, 160 * scaleY)));
        }));
        var eventType = implType.GetNestedType("EventMouseButton", BindingFlags.NonPublic)!;
        var button = Activator.CreateInstance(eventType, true)!;
        eventType.GetField("WindowId", Fields)!.SetValue(button, 1u);
        eventType.GetField("Button", Fields)!.SetValue(button, (byte) 1);
        eventType.GetField("X", Fields)!.SetValue(button, 200f);
        eventType.GetField("Y", Fields)!.SetValue(button, 160f);
        var typeField = eventType.GetField("Type", Fields)!;
        typeField.SetValue(button, Enum.Parse(typeField.FieldType, "SDL_EVENT_MOUSE_BUTTON_DOWN"));
        implType.GetMethod("ProcessEventMouseButton", Fields)!.Invoke(impl, new[] { button });
        clydeType.GetMethod("DispatchEvents", Fields)!.Invoke(clyde, null);
        Assert.That(events, Is.EqualTo(new[] { "move", "down" }));
    }
}
