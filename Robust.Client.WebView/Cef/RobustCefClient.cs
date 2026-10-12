using Xilium.CefGlue;

namespace Robust.Client.WebView.Cef
{
    // Simple CEF client.
    internal sealed class RobustCefClient : BaseRobustCefClient
    {
        private readonly CefRenderHandler _renderHandler;
        private readonly CefRequestHandler _requestHandler;
        private readonly CefLoadHandler _loadHandler;

        private readonly CefLifeSpanHandler _lifeSpanHandler;
        private sealed class CloseHandler(System.Action closed) : CefLifeSpanHandler
        {
            protected override void OnBeforeClose(CefBrowser browser) => closed();
        }
        protected override CefLifeSpanHandler GetLifeSpanHandler() => _lifeSpanHandler;

        internal RobustCefClient(CefRenderHandler handler, CefRequestHandler requestHandler, CefLoadHandler loadHandler, System.Action closed)
        {
            _renderHandler = handler;
            _requestHandler = requestHandler;
            _loadHandler = loadHandler;
            _lifeSpanHandler = new CloseHandler(closed);
        }

        protected override CefRenderHandler GetRenderHandler() => _renderHandler;
        protected override CefRequestHandler GetRequestHandler() => _requestHandler;
        protected override CefLoadHandler GetLoadHandler() => _loadHandler;
    }
}
