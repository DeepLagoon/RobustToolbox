using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Shared.Maths;

namespace Robust.Client.Tests.UserInterface;

[TestFixture]
public sealed class UIScaleTest
{
    [TestCase(0f, 0.5f)]
    [TestCase(-1f, 0.5f)]
    [TestCase(float.NaN, 0.5f)]
    [TestCase(float.PositiveInfinity, 0.5f)]
    [TestCase(0.25f, 0.5f)]
    [TestCase(1f, float.NaN)]
    [TestCase(1f, -1f)]
    public void InvalidDpiAndMinimumProduceFinitePositiveScale(float dpi, float minimum)
    {
        var result = UserInterfaceManager.CalculateAutoScale(new Vector2i(1280, 720), dpi,
            new Vector2i(1024, 768), new Vector2i(1920, 1080), minimum);
        Assert.That(float.IsFinite(result), Is.True);
        Assert.That(result, Is.GreaterThan(0f));
        if (dpi == 0.25f)
            Assert.That(result, Is.EqualTo(dpi));
    }

    [TestCase(1920, 1080, 1f)]
    [TestCase(1024, 768, 0.5f)]
    [TestCase(0, 0, 1f)]
    public void ValidResolutionScalingIsPreserved(int width, int height, float expected)
    {
        Assert.That(UserInterfaceManager.CalculateAutoScale(new Vector2i(width, height), 1f,
            new Vector2i(1024, 768), new Vector2i(1920, 1080), 0.5f), Is.EqualTo(expected));
    }

    [TestCase(1024, 768)]
    [TestCase(800, 600)]
    public void InvalidResolutionRangeDoesNotDivideByZero(int upperX, int upperY)
    {
        Assert.That(UserInterfaceManager.CalculateAutoScale(new Vector2i(1280, 720), 1f,
            new Vector2i(1024, 768), new Vector2i(upperX, upperY), 0.5f), Is.EqualTo(1f));
    }
}
