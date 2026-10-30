using System;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Maths;
using Robust.Shared.ViewVariables;

namespace Robust.Client.UserInterface;

internal partial class UserInterfaceManager
{
    [ViewVariables] public float DefaultUIScale => _clyde.DefaultWindowScale.X;
    [ViewVariables] private Vector2i _resolutionAutoScaleUpper;
    [ViewVariables] private Vector2i _resolutionAutoScaleLower;
    [ViewVariables] private bool _autoScaleEnabled;
    [ViewVariables] private float _resolutionAutoScaleMinValue;

    private void _initScaling()
    {
        _clyde.OnWindowResized += WindowSizeChanged;
        _clyde.OnWindowScaleChanged += WindowContentScaleChanged;
        RegisterAutoscaleCVarListeners();
        _uiScaleChanged(_configurationManager.GetCVar(CVars.DisplayUIScale));
    }


    private void _uiScaleChanged(float newValue)
        {
            foreach (var root in _roots)
            {
                UpdateUIScale(root);
            }
        }

        private void WindowContentScaleChanged(WindowContentScaleEventArgs args)
        {
            if (_windowsToRoot.TryGetValue(args.Window.Id, out var root))
            {
                UpdateUIScale(root);
                _fontManager.ClearFontCache();
            }

        }

        private void RegisterAutoscaleCVarListeners()
        {
            _configurationManager.OnValueChanged(CVars.ResAutoScaleEnabled, i =>
            {
                _autoScaleEnabled = i;
                foreach (var root in _roots)
                {
                    root.UIScaleSet = 1;
                    _propagateUIScaleChanged(root);
                    root.InvalidateMeasure();
                }

            }, true);
            _configurationManager.OnValueChanged(CVars.ResAutoScaleLowX, i =>
            {
                _resolutionAutoScaleLower.X = i;
                foreach (var root in _roots)
                {
                    UpdateUIScale(root);
                }
            }, true);
            _configurationManager.OnValueChanged(CVars.ResAutoScaleLowY, i =>
            {
                _resolutionAutoScaleLower.Y = i;
                foreach (var root in _roots)
                {
                    UpdateUIScale(root);
                }
            }, true);
            _configurationManager.OnValueChanged(CVars.ResAutoScaleUpperX, i =>
            {
                _resolutionAutoScaleUpper.X = i;
                foreach (var root in _roots)
                {
                    UpdateUIScale(root);
                }
            }, true);
            _configurationManager.OnValueChanged(CVars.ResAutoScaleUpperY, i =>
            {
                _resolutionAutoScaleUpper.Y = i;
                foreach (var root in _roots)
                {
                    UpdateUIScale(root);
                }
            }, true);
            _configurationManager.OnValueChanged(CVars.ResAutoScaleMin, i =>
            {
                _resolutionAutoScaleMinValue = i;
                foreach (var root in _roots)
                {
                    UpdateUIScale(root);
                }
            }, true);
        }

        internal static float CalculateUIScale(float osScale, IConfigurationManager cfg)
        {
            var cfgScale = cfg.GetCVar(CVars.DisplayUIScale);
            var scale = cfgScale == 0 ? osScale : cfgScale;
            // Some window backends temporarily report zero while changing DPI or minimizing.
            return float.IsFinite(scale) && scale > 0f ? scale : 1f;
        }

        private float CalculateAutoScale(WindowRoot root)
        {
            //Grab the OS UIScale or the value set through CVAR debug
            var osScale = CalculateUIScale(root.Window.ContentScale.X, _configurationManager);

            var windowSize = root.Window.RenderTarget.Size;
            //Only run autoscale if it is enabled, otherwise default to just use OS UIScale
            if (!_autoScaleEnabled || root.DisableAutoScaling || windowSize.X <= 0 || windowSize.Y <= 0)
                return osScale;

            return CalculateAutoScale(windowSize, osScale, _resolutionAutoScaleLower,
                _resolutionAutoScaleUpper, _resolutionAutoScaleMinValue);
        }

        internal static float CalculateAutoScale(Vector2i windowSize, float osScale,
            Vector2i minScaleRes, Vector2i maxScaleRes, float autoScaleMin)
        {
            if (!float.IsFinite(osScale) || osScale <= 0f)
                osScale = 1f;

            if (windowSize.X <= 0 || windowSize.Y <= 0)
                return osScale;

            // A valid manual scale may be smaller than the configured autoscale minimum.
            // Invalid resolution ranges must not produce NaN or reverse the scaling slope.
            autoScaleMin = float.IsFinite(autoScaleMin) && autoScaleMin > 0f
                ? Math.Min(autoScaleMin, osScale)
                : Math.Min(0.5f, osScale);
            var scaleRatioX = maxScaleRes.X <= minScaleRes.X ? osScale :
                Math.Clamp(((float) windowSize.X - minScaleRes.X) / ((float) maxScaleRes.X - minScaleRes.X) * osScale, autoScaleMin, osScale);
            var scaleRatioY = maxScaleRes.Y <= minScaleRes.Y ? osScale :
                Math.Clamp(((float) windowSize.Y - minScaleRes.Y) / ((float) maxScaleRes.Y - minScaleRes.Y) * osScale, autoScaleMin, osScale);
            //Take the smallest UIScale value and use it for UI scaling
            return Math.Min(scaleRatioX, scaleRatioY);
        }

        private void UpdateUIScale(WindowRoot root)
        {
            var newScale = CalculateAutoScale(root);
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (newScale == root.UIScaleSet)
                return;

            root.UIScaleSet = newScale;
            _propagateUIScaleChanged(root);
            root.InvalidateMeasure();
        }

        private static void _propagateUIScaleChanged(Control control)
        {
            control.UIScaleChanged();

            foreach (var child in control.Children)
            {
                _propagateUIScaleChanged(child);
            }
        }

        private void WindowSizeChanged(WindowResizedEventArgs windowResizedEventArgs)
        {
            if (!_windowsToRoot.TryGetValue(windowResizedEventArgs.Window.Id, out var root))
                return;

            root.UIScaleUpdateNeeded = true;
            root.InvalidateMeasure();
        }

        private void CheckRootUIScaleUpdate(WindowRoot root)
        {
            if (!root.UIScaleUpdateNeeded)
                return;

            using (_prof.Group("UIScaleUpdate"))
            {
                UpdateUIScale(root);
            }

            root.UIScaleUpdateNeeded = false;
        }
}
