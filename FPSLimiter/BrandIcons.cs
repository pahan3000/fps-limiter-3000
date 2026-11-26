using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FPSLimiter
{
    /// <summary>
    /// Brand icons for menu surfaces and the top bar. RTSS uses its supplied ICO
    /// for the top-bar button and the white RTSS PNG in menus.
    /// </summary>
    public static class BrandIcons
    {
        private static string _ensureDir;
        private static readonly object Gate = new object();

        public static void EnsureExtracted(string preferredDirectory = null)
        {
            lock (Gate)
            {
                var dir = preferredDirectory;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    dir = ExtensionDirectory;
                if (string.IsNullOrWhiteSpace(dir))
                    dir = Path.Combine(Path.GetTempPath(), "FPSLimiterIcons");
                Directory.CreateDirectory(dir);
                _ensureDir = dir;

                // Copy any icons that already ship next to the DLL into the ensure dir.
                var ship = ExtensionDirectory;
                if (!string.IsNullOrEmpty(ship) && Directory.Exists(ship))
                {
                    foreach (var file in Directory.EnumerateFiles(ship, "rtss_*.png"))
                        TryCopy(file, Path.Combine(dir, Path.GetFileName(file)));
                    foreach (var file in Directory.EnumerateFiles(ship, "specialk_*.png"))
                        TryCopy(file, Path.Combine(dir, Path.GetFileName(file)));
                    foreach (var file in Directory.EnumerateFiles(ship, "topbar_*.png"))
                        TryCopy(file, Path.Combine(dir, Path.GetFileName(file)));
                }
            }
        }

        private static void TryCopy(string src, string dst)
        {
            try
            {
                if (!File.Exists(src)) return;
                if (File.Exists(dst) && new FileInfo(dst).Length == new FileInfo(src).Length) return;
                File.Copy(src, dst, true);
            }
            catch { }
        }

        private static string ExtensionDirectory
        {
            get
            {
                try
                {
                    var loc = Assembly.GetExecutingAssembly().Location;
                    return Path.GetDirectoryName(loc) ?? string.Empty;
                }
                catch { return string.Empty; }
            }
        }

        private static string SearchDir =>
            !string.IsNullOrEmpty(_ensureDir) ? _ensureDir : ExtensionDirectory;

        public static string TopBarRtssPath()
        {
            foreach (var root in new[] { ExtensionDirectory, SearchDir })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var iconPath = Path.Combine(root, "RTSS.ico");
                if (File.Exists(iconPath)) return iconPath;
            }
            return FindFile("topbar_rtss", 128) ?? FindFile("topbar_rtss", 48) ?? FindFile("rtss", 48);
        }

        public static string TopBarSpecialKPath()
        {
            foreach (var root in new[] { ExtensionDirectory, SearchDir })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var iconPath = Path.Combine(root, "SKIF.ico");
                if (File.Exists(iconPath)) return iconPath;
            }
            return FindFile("topbar_specialk", 48) ?? FindFile("specialk", 48);
        }

        public static ImageSource Rtss(int size = 18) => Load("rtss", size);
        public static ImageSource SpecialK(int size = 18) => Load("specialk", size);
        public static string RtssFilePath(int size = 48) => FindFile("rtss", size);
        public static string SpecialKFilePath(int size = 48) => FindFile("specialk", size);

        private static string FindFile(string prefix, int preferredSize)
        {
            foreach (var root in new[] { SearchDir, ExtensionDirectory })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (var size in new[] { preferredSize, 48, 32, 24, 20, 18, 16 })
                {
                    var path = Path.Combine(root, prefix + "_" + size + ".png");
                    if (File.Exists(path)) return path;
                }
            }
            return null;
        }

        private static ImageSource Load(string prefix, int preferredSize)
        {
            try
            {
                var path = FindFile(prefix, preferredSize);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }
    }
}
