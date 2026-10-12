using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Robust.Client.WebView;
using Robust.Client.WebView.Cef;
using Robust.Shared.Maths;
using SixLabors.ImageSharp.PixelFormats;
using Xilium.CefGlue;

namespace Robust.Client.WebView.Tests;

[TestFixture]
public sealed class CefRenderingTests
{
    private static Type RendererType => typeof(WebViewControl).Assembly.GetType(
        "Robust.Client.WebView.Cef.WebViewManagerCef+ControlRenderHandler", true)!;

    [Test]
    public void CapabilityMarkerIsReadableThroughTheStockSandboxApi()
    {
        using var capabilities = typeof(WebViewControl).Assembly
            .GetManifestResourceStream("Robust.Client.WebView.NativeLifecycle.v1");
        Assert.That(capabilities, Is.Not.Null);
    }

    [Test]
    public void ViewRectangleUsesSnapshotAndRemainsNonEmptyDuringClose()
    {
        var renderer = RuntimeHelpers.GetUninitializedObject(RendererType);
        RendererType.GetMethod("UpdateViewMetrics", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(renderer, new object[] { new Vector2(240, 160), 1.5f });
        var args = new object[] { null!, null! };
        var getRect = RendererType.GetMethod("GetViewRect", BindingFlags.NonPublic | BindingFlags.Instance)!;
        getRect.Invoke(renderer, args);
        Assert.That(((CefRectangle)args[1]).Width, Is.EqualTo(240));
        Assert.That(((CefRectangle)args[1]).Height, Is.EqualTo(160));
        RendererType.GetField("_closing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(renderer, true);
        getRect.Invoke(renderer, args);
        Assert.That(((CefRectangle)args[1]).Width, Is.EqualTo(1));
        Assert.That(((CefRectangle)args[1]).Height, Is.EqualTo(1));
    }

    [Test]
    public void SparseCornerUpdatesDoNotUploadTheWholeView()
    {
        var regions = new DirtyRegions();
        regions.Add(1024, 1024, new CefRectangle(0, 0, 32, 32));
        regions.Add(1024, 1024, new CefRectangle(992, 992, 32, 32));
        Assert.That(regions.FullUpload, Is.False);
        Assert.That(regions.Rectangles, Has.Count.EqualTo(2));
        Assert.That(regions.Rectangles.Sum(rect => rect.Width * rect.Height), Is.EqualTo(2048));
    }

    [Test]
    public void OverlappingRegionsMergeAndClearAfterUpload()
    {
        var regions = new DirtyRegions();
        regions.Add(100, 100, new CefRectangle(0, 0, 20, 20));
        regions.Add(100, 100, new CefRectangle(10, 0, 20, 20));
        Assert.That(regions.Rectangles, Is.EqualTo(new[] { new UIBox2i(0, 0, 30, 20) }));
        regions.Clear();
        Assert.That(regions.Rectangles, Is.Empty);
        Assert.That(regions.FullUpload, Is.False);
    }

    [Test]
    public void RegionsClipToTheFrameAndIgnoreEmptyRectangles()
    {
        var regions = new DirtyRegions();
        regions.Add(100, 100, new CefRectangle(-5, -5, 10, 10));
        regions.Add(100, 100, new CefRectangle(101, 0, 10, 10));
        regions.Add(100, 100, new CefRectangle(50, 50, 0, 0));
        Assert.That(regions.Rectangles, Is.EqualTo(new[] { new UIBox2i(0, 0, 5, 5) }));
    }

    [Test]
    public void TooManyRegionsFallBackToOneFullUpload()
    {
        var regions = new DirtyRegions();
        for (var i = 0; i < 17; i++)
            regions.Add(1000, 1000, new CefRectangle(i * 10, 0, 2, 2));
        Assert.That(regions.FullUpload, Is.True);
        Assert.That(regions.Rectangles, Is.Empty);
        regions.Clear();
        Assert.That(regions.FullUpload, Is.False);
    }

    [Test]
    public void LargePaintFallsBackToOneFullUpload()
    {
        var regions = new DirtyRegions();
        regions.Add(1000, 1000, new CefRectangle(0, 0, 800, 800));
        Assert.That(regions.FullUpload, Is.True);
    }

    [Test]
    public void ResizingCopiesTheWholeFrameAndDisposesThePreviousImage()
    {
        var pixels = Enumerable.Repeat(new Rgba32(10, 20, 30, 40), 16).ToArray();
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        var buffer = new ImageBuffer();
        var previous = buffer.Buffer;
        try
        {
            buffer.UpdateBuffer(4, 4, pin.AddrOfPinnedObject(), new CefRectangle(1, 1, 1, 1));
            Assert.That(buffer.Buffer[0, 0], Is.EqualTo(pixels[0]));
            Assert.That(buffer.Buffer[3, 3], Is.EqualTo(pixels[15]));
            Assert.Throws<ObjectDisposedException>(() => { _ = previous[0, 0]; });
        }
        finally
        {
            pin.Free();
            buffer.Buffer.Dispose();
        }
    }

    [Test]
    public void LatePaintDoesNotTouchTheDisposedBuffer()
    {
        var renderer = RuntimeHelpers.GetUninitializedObject(RendererType);
        var buffer = new ImageBuffer();
        RendererType.GetField("<Buffer>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(renderer, buffer);
        RendererType.GetField("DirtyRegions", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(renderer, new DirtyRegions());
        RendererType.GetMethod("BrowserClosed", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(renderer, null);
        var paint = RendererType.GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // An invalid pointer is safe here only because a closing renderer must not read it.
        Assert.DoesNotThrow(() => paint.Invoke(renderer, new object[]
        {
            null!, CefPaintElementType.View, new[] { new CefRectangle(0, 0, 1, 1) }, new IntPtr(1), 1, 1
        }));
        Assert.That(RendererType.GetField("IsDirty", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer), Is.False);
    }

    [Test]
    public void FractionalWheelDeltasAccumulateAcrossEvents()
    {
        var type = typeof(WebViewControl).Assembly.GetType("Robust.Client.WebView.Cef.WebViewManagerCef+ControlImpl", true)!;
        var control = RuntimeHelpers.GetUninitializedObject(type);
        var consume = type.GetMethod("ConsumeWheel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Vector2i Wheel(Vector2 delta) => (Vector2i)consume.Invoke(control, new object[] { delta })!;
        Assert.That(Wheel(new Vector2(0, 0.25f)), Is.EqualTo(new Vector2i(0, 12)));
        Assert.That(Wheel(new Vector2(0, 0.25f)), Is.EqualTo(new Vector2i(0, 13)));
        Assert.That(Wheel(new Vector2(0.01f, -0.25f)), Is.EqualTo(new Vector2i(0, -12)));
        Assert.That(Wheel(new Vector2(0.01f, -0.25f)), Is.EqualTo(new Vector2i(1, -13)));
    }

    [Test]
    public void AlreadyClosedBrowserCanBeClosedAgain()
    {
        var type = typeof(WebViewControl).Assembly.GetType("Robust.Client.WebView.Cef.WebViewManagerCef+ControlImpl", true)!;
        var control = RuntimeHelpers.GetUninitializedObject(type);
        var close = type.GetMethod("CloseBrowser", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.DoesNotThrow(() => close.Invoke(control, null));
        Assert.DoesNotThrow(() => close.Invoke(control, null));
    }
}
