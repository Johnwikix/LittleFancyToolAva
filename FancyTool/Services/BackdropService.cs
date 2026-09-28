using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using FancyToolAva.Models;

namespace FancyToolAva.Services
{
    public interface IBackdropService
    {
        void Attach(Window window, Panel layerHost);
    }

    /// <summary>
    /// 把 <see cref="BackdropMode"/> 应用到主窗口：窗口背景设为透明后由 DWM 绘制
    /// Mica / 亚克力等系统背景（等价 WinUI 的 MicaBackdrop / DesktopAcrylicBackdrop）。
    /// 非 Windows 平台强制回退纯色。
    /// </summary>
    public sealed class BackdropService : IBackdropService
    {
        private static readonly Color TintDark = Color.Parse("#FF202020");
        private static readonly Color TintLight = Color.Parse("#FFF3F3F3");

        /// <summary>自定义亚克力色调预设；空串表示跟随主题。</summary>
        internal static readonly string[] TintPresets =
        [
            "", "#FF0078D4", "#FF8764B8", "#FF107C10", "#FFCA5010", "#FFC42B1C"
        ];

        internal static int TintPresetCount => TintPresets.Length;

        private readonly AppObserveModel _app;
        private Window? _window;
        private Panel? _layerHost;
        private ExperimentalAcrylicBorder? _acrylicLayer;
        private IReadOnlyList<WindowTransparencyLevel> _appliedHint = [];

        public BackdropService(AppObserveModel app)
        {
            _app = app;
            _app.PropertyChanged += OnAppPropertyChanged;
            SubscribePreferences(_app.Preferences);
        }

        public void Attach(Window window, Panel layerHost)
        {
            _window = window;
            _layerHost = layerHost;

            if (Application.Current is { } app)
            {
                // 深浅主题切换时亚克力着色需要跟随刷新
                app.ActualThemeVariantChanged += (_, _) => Apply();
            }

            Apply();
        }

        private void OnAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppObserveModel.Preferences))
            {
                SubscribePreferences(_app.Preferences);
                Apply();
            }
        }

        private void SubscribePreferences(AppPreferences preferences)
        {
            preferences.PropertyChanged += OnPreferencesPropertyChanged;
        }

        private void OnPreferencesPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AppPreferences.BackdropIndex)
                or nameof(AppPreferences.AcrylicTintIndex)
                or nameof(AppPreferences.AcrylicTintOpacity)
                or nameof(AppPreferences.AcrylicMaterialOpacity))
            {
                Apply();
            }
        }

        private BackdropMode EffectiveMode =>
            OperatingSystem.IsWindows() ? _app.Preferences.Backdrop : BackdropMode.None;

        private void Apply()
        {
            if (_window is null || _layerHost is null)
            {
                return;
            }

            var mode = EffectiveMode;

            // WinUI: SystemBackdrop / MicaBackdrop / DesktopAcrylicBackdrop
            // Avalonia: Window.TransparencyLevelHint（Win32 层自动调用 DWM）
            var hint = mode switch
            {
                BackdropMode.Transparent => new[] { WindowTransparencyLevel.Transparent },
                BackdropMode.Acrylic or BackdropMode.CustomAcrylic => new[]
                {
                    WindowTransparencyLevel.AcrylicBlur,
                    WindowTransparencyLevel.Blur // Win10 回退
                },
                BackdropMode.Mica => new[] { WindowTransparencyLevel.Mica },
                _ => new[] { WindowTransparencyLevel.None }
            };

            // 两个亚克力模式之间切换时 hint 相同，重复赋值会让 Win32 端重设
            // DWM accent，造成闪烁/残影
            if (!hint.SequenceEqual(_appliedHint))
            {
                _window.TransparencyLevelHint = hint;
                _appliedHint = hint;
            }

            bool backdropActive = mode != BackdropMode.None;
            // 清除本地值即回到主题默认的 ApplicationPageBackgroundThemeBrush
            _window.Background = backdropActive ? Brushes.Transparent : null;
            SetContentBackgroundTransparent(backdropActive);

            if (mode is BackdropMode.Acrylic or BackdropMode.CustomAcrylic)
            {
                ReplaceAcrylicLayer(CreateMaterial(mode));
            }
            else
            {
                RemoveAcrylicLayer();
            }
        }

        private ExperimentalAcrylicMaterial CreateMaterial(BackdropMode mode)
        {
            bool dark = _window!.ActualThemeVariant == ThemeVariant.Dark;
            var preferences = _app.Preferences;

            Color tintColor;
            double tintOpacity;
            double materialOpacity;

            if (mode == BackdropMode.Acrylic)
            {
                // 模拟 WinUI DesktopAcrylicBackdrop 的默认观感
                tintColor = dark ? TintDark : TintLight;
                tintOpacity = 0.50;
                materialOpacity = 0.65;
            }
            else
            {
                // 模拟 WinUI DesktopAcrylicController: TintColor/TintOpacity/LuminosityOpacity
                var hex = TintPresets[Math.Clamp(preferences.AcrylicTintIndex, 0, TintPresets.Length - 1)];
                tintColor = hex.Length > 0 && Color.TryParse(hex, out var color)
                    ? color
                    : dark ? Color.Parse("#FF3C3C3C") : Colors.White;
                tintOpacity = preferences.AcrylicTintOpacity / 100.0;
                materialOpacity = preferences.AcrylicMaterialOpacity / 100.0;
            }

            return new ExperimentalAcrylicMaterial
            {
                BackgroundSource = AcrylicBackgroundSource.Digger,
                TintColor = tintColor,
                TintOpacity = tintOpacity,
                MaterialOpacity = materialOpacity,
                FallbackColor = dark ? Color.Parse("#FF1F1F1F") : Color.Parse("#FFF3F3F3")
            };
        }

        private void ReplaceAcrylicLayer(ExperimentalAcrylicMaterial material)
        {
            // Avalonia 12.1.1 的 ServerCompositionExperimentalAcrylicVisual 注册
            // Material/CornerRadius 属性时未挂变更回调：原地更新材质不会让合成器
            // 失效重绘，只有其它控件产生的脏区（悬停、导航）或窗口尺寸变化才会
            // 带出新材料，表现为“只有部分区域生效”。因此每次更新都整个替换
            // 亚克力层元素——视觉树结构变化必然强制整窗重合成，且单批次原子
            // 提交不会闪烁。
            RemoveAcrylicLayer();
            _acrylicLayer = new ExperimentalAcrylicBorder
            {
                IsHitTestVisible = false,
                Material = material
            };
            // 插在最前，始终位于内容之下
            _layerHost!.Children.Insert(0, _acrylicLayer);
        }

        private void RemoveAcrylicLayer()
        {
            if (_acrylicLayer is { } layer)
            {
                _layerHost!.Children.Remove(layer);
                _acrylicLayer = null;
            }
        }

        private void SetContentBackgroundTransparent(bool transparent)
        {
            // FANavigationView 内容区默认用 NavigationViewContentBackground 遮住窗口背景，
            // 启用背景效果时在窗口作用域覆盖为透明，关闭时移除覆盖还原主题默认
            if (transparent)
            {
                _window!.Resources["NavigationViewContentBackground"] = new SolidColorBrush(Colors.Transparent);
            }
            else
            {
                _window!.Resources.Remove("NavigationViewContentBackground");
            }
        }
    }
}
