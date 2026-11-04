using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace LinuxOps.Services;

/// <summary>
/// Дневная и ночная тема. Палитра — первый словарь в App.xaml (Light.xaml / Dark.xaml),
/// стили берут цвета через DynamicResource, поэтому замена словаря перекрашивает всё сразу.
/// </summary>
public static class ThemeManager
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";

    private static bool _hooked;

    /// <summary>Выбор пользователя: light, dark или system.</summary>
    public static string Mode { get; private set; } = System;

    /// <summary>Сейчас ночная тема (с учётом «как в Windows»).</summary>
    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Init()
    {
        if (!_hooked)
        {
            _hooked = true;
            // Окна, созданные в коде (диалоги), берут фон и текст из темы, а заголовок — тёмный в ночной теме.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Mode == System)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(System, save: false));
            };
        }

        Apply(DefaultsStore.Theme, save: false);
    }

    public static void Apply(string mode, bool save = true)
    {
        mode = mode is Light or Dark ? mode : System;
        Mode = mode;
        if (save) DefaultsStore.Theme = mode;
        var dark = mode == Dark || (mode == System && WindowsUsesDark());
        var app = Application.Current;
        if (app is null) return;
        var dictionaries = app.Resources.MergedDictionaries;
        var uri = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        if (dictionaries.Count > 0 && dictionaries[0].Source is { } current && IsPalette(current))
        {
            if (IsDark != dark || current.OriginalString != uri.OriginalString)
                dictionaries[0] = new ResourceDictionary { Source = uri };
        }
        else
        {
            dictionaries.Insert(0, new ResourceDictionary { Source = uri });
        }

        IsDark = dark;
        foreach (Window w in app.Windows) SetTitleBar(w);
        Changed?.Invoke();
    }

    private static bool IsPalette(Uri source)
        => source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)
           || source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase);

    public static bool WindowsUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w) return;
        // Неявный стиль Window не применяется к наследникам (MainWindow, AppDialog …) — ставим ссылки на ресурсы сами.
        if (w.ReadLocalValue(Window.BackgroundProperty) == DependencyProperty.UnsetValue)
            w.SetResourceReference(Window.BackgroundProperty, "PaperBrush");
        if (w.ReadLocalValue(Window.ForegroundProperty) == DependencyProperty.UnsetValue)
            w.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        SetTitleBar(w);
    }

    /// <summary>Главное окно — всегда тёмный заголовок (под шапку); прочие — по теме.</summary>
    public static void SetTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = w is MainWindow || IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
