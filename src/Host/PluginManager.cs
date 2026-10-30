using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

namespace caxa3dmytoolsaddin
{
    /// <summary>
    /// 统一日志 + 站点封装，交给子插件使用。
    /// </summary>
    internal sealed class PluginHost : IPluginHost
    {
        private readonly ZAddinSite _site;
        private readonly string _pluginDirectory;
        private readonly string _pluginId;

        public PluginHost(ZAddinSite site, string pluginDirectory, string pluginId)
        {
            _site = site;
            _pluginDirectory = pluginDirectory;
            _pluginId = pluginId;
        }

        public object AddinSite { get { return _site; } }

        public string PluginDirectory { get { return _pluginDirectory; } }

        public void Log(string message)
        {
            Diagnostics.Log("[" + _pluginId + "] " + message);
        }
    }

    /// <summary>
    /// 插件扫描与生命周期管理。
    /// 只做"发现 + 加载 + 转发生命周期 + 暴露列表"，不注册任何按钮——
    /// 所有子插件按钮统一在主工具窗口内动态生成。
    /// </summary>
    [System.Runtime.InteropServices.ComVisible(false)]
    public sealed class PluginManager
    {
        private const int MaxParentLevels = 6;

        private readonly ZAddinSite _site;
        private readonly List<IPlugin> _plugins = new List<IPlugin>();

        /// <summary>已加载的子插件列表（供主工具窗口生成按钮）。</summary>
        public IReadOnlyList<IPlugin> Plugins { get { return _plugins; } }

        public PluginManager(ZAddinSite site)
        {
            _site = site;
        }

        /// <summary>扫描并加载全部插件，返回成功加载数量。</summary>
        public int LoadAll()
        {
            string root = ResolvePluginRoot();
            if (root == null)
            {
                Diagnostics.Log("未找到插件目录：已从程序目录逐级向上查找 plugins / src\\plugins 均失败");
                return 0;
            }
            Diagnostics.Log("插件根目录: " + root);

            foreach (string dll in EnumeratePluginDlls(root))
            {
                try
                {
                    LoadOne(dll);
                }
                catch (Exception ex)
                {
                    // 单个插件异常不影响其余插件与 CAXA 启动
                    Diagnostics.Log("加载插件失败 " + dll + " -> " + ex);
                }
            }

            Diagnostics.Log("插件加载完成，成功 " + _plugins.Count + " 个");
            return _plugins.Count;
        }

        /// <summary>逆序卸载全部插件。</summary>
        public void ShutdownAll()
        {
            for (int i = _plugins.Count - 1; i >= 0; i--)
            {
                try { _plugins[i].Shutdown(); }
                catch (Exception ex) { Diagnostics.Log("插件卸载异常 -> " + ex.Message); }
            }
            _plugins.Clear();
        }

        // ================================================================ 内部实现

        private void LoadOne(string dllPath)
        {
            string dir = Path.GetDirectoryName(dllPath);
            PluginLoadContext context = new PluginLoadContext(dir, Diagnostics.Log);
            Assembly asm = context.Load(dllPath);

            List<Type> pluginTypes = new List<Type>();
            foreach (Type t in asm.GetTypes())
            {
                if (t.IsClass && !t.IsAbstract && typeof(IPlugin).IsAssignableFrom(t))
                    pluginTypes.Add(t);
            }

            if (pluginTypes.Count == 0)
            {
                Diagnostics.Log("跳过（未实现 IPlugin）: " + Path.GetFileName(dllPath));
                return;
            }

            foreach (Type t in pluginTypes)
            {
                IPlugin plugin = (IPlugin)Activator.CreateInstance(t);
                PluginHost host = new PluginHost(_site, dir, plugin.Id);
                plugin.Initialize(host);
                _plugins.Add(plugin);
                Diagnostics.Log("插件已初始化: " + plugin.Id + " / " + plugin.DisplayName);
            }
        }

        /// <summary>
        /// 逐级向上查找插件目录：
        ///   部署布局：&lt;CAXA bin&gt;\caxa3dmytoolsaddin\plugins
        ///   开发布局：&lt;仓库根&gt;\src\plugins
        /// </summary>
        private static string ResolvePluginRoot()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            List<string> candidates = new List<string>();

            candidates.Add(Path.Combine(baseDir, "caxa3dmytoolsaddin", "plugins"));
            candidates.Add(Path.Combine(baseDir, "plugins"));

            DirectoryInfo dir = new DirectoryInfo(baseDir);
            for (int level = 0; level < MaxParentLevels && dir != null; level++)
            {
                candidates.Add(Path.Combine(dir.FullName, "src", "plugins"));
                candidates.Add(Path.Combine(dir.FullName, "plugins"));
                dir = dir.Parent;
            }

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate) && CountPluginDlls(candidate) > 0)
                    return candidate;
            }
            return null;
        }

        /// <summary>统计插件 DLL：根目录与一层子目录（每个插件一个子目录）。</summary>
        private static int CountPluginDlls(string root)
        {
            int count = Directory.GetFiles(root, "*.dll", SearchOption.TopDirectoryOnly).Length;
            foreach (string sub in Directory.GetDirectories(root))
                count += Directory.GetFiles(sub, "*.dll", SearchOption.TopDirectoryOnly).Length;
            return count;
        }

        /// <summary>枚举插件 DLL：根目录 + 每个一级子目录，排除 Host 与契约程序集。</summary>
        private static IEnumerable<string> EnumeratePluginDlls(string root)
        {
            List<string> result = new List<string>();
            AddDlls(root, result);

            foreach (string sub in Directory.GetDirectories(root))
                AddDlls(sub, result);

            return result;
        }

        private static void AddDlls(string dir, List<string> result)
        {
            foreach (string dll in Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (string.Equals(name, "caxa3dmytoolsaddin", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "Caxa3dMyTools.Contracts", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(dll);
            }
        }
    }
}
