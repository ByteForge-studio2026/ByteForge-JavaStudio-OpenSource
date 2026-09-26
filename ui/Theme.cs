// Theme.cs —— 深浅主题切换
//
// 做法：整本 ResourceDictionary 换掉。所有控件用 DynamicResource 取色，
// 字典一换，全窗口立刻跟着变，不用一个个控件去刷。

using System.Windows;

namespace JavaStudio;

internal static class Theme
{
    private static bool _dark;

    public static bool IsDark => _dark;

    public static void Apply(bool dark)
    {
        _dark = dark;
        var dicts = Application.Current.Resources.MergedDictionaries;
        if (dicts.Count == 0) return;

        // 把第 0 本（就是现在这本主题）替换掉，别追加，
        // 追加会让两本字典都在，DynamicResource 取到先加载的那本。
        dicts[0] = new ResourceDictionary
        {
            Source = new System.Uri(
                dark ? "Themes/Dark.xaml" : "Themes/Light.xaml",
                System.UriKind.Relative)
        };
    }

    public static void Toggle()
    {
        Apply(!_dark);
        Core.SetSetting("theme", _dark ? "dark" : "light");
        Config.Theme = _dark ? "dark" : "light";
        Config.Save();
    }
}
