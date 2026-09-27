using System;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;

namespace TheCloser
{
    /// <summary>
    /// Loads the app's WPF styles. Theme.xaml is embedded as a resource at build time (no XAML/BAML
    /// compilation, since TheCloser builds with the plain C# compiler) and merged into the application's
    /// resources at startup so the views can resolve styles with Application.Current.FindResource.
    /// </summary>
    internal static class Theme
    {
        private const string ResourceName = "TheCloser.Theme.xaml";

        public static void Init(Application app)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Theme.xaml resource is missing from the build (expected '" + ResourceName + "').");
                var dict = (ResourceDictionary)XamlReader.Load(stream);
                app.Resources.MergedDictionaries.Add(dict);
            }
        }
    }
}
