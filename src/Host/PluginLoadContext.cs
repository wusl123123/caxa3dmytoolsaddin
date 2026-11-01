using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Caxa3dMyTools.Contracts;

namespace caxa3dmytoolsaddin
{
    /// <summary>
    /// 插件加载上下文（.NET Framework 4.8 无 AssemblyLoadContext）。
    /// 职责：
    ///   1. 以插件自身目录为探测根，登记到全局 AssemblyResolve 探测表；
    ///   2. 插件引用的依赖优先从该插件目录加载；
    ///   3. 契约程序集强制复用 Host 已加载的那一份，避免同一类型出现两份（类型不兼容）。
    /// </summary>
    internal sealed class PluginLoadContext
    {
        private static readonly object Sync = new object();
        private static readonly List<string> ProbeDirectories = new List<string>();
        private static bool _handlerInstalled;

        private readonly string _directory;
        private readonly Action<string> _log;

        public PluginLoadContext(string directory, Action<string> log)
        {
            _directory = directory;
            _log = log;
        }

        /// <summary>登记探测目录并安装全局解析器（幂等）。</summary>
        private void Register()
        {
            lock (Sync)
            {
                if (!ProbeDirectories.Contains(_directory))
                    ProbeDirectories.Add(_directory);

                if (_handlerInstalled) return;
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                _handlerInstalled = true;
            }
        }

        /// <summary>从插件目录加载程序集。</summary>
        public Assembly Load(string dllPath)
        {
            Register();
            return Assembly.LoadFrom(dllPath);
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            AssemblyName requested;
            try { requested = new AssemblyName(args.Name); }
            catch { return null; }

            // 契约必须与 Host 同源：否则插件里的 IPlugin 与 Host 的 IPlugin 不相等
            Assembly contracts = typeof(IPlugin).Assembly;
            if (string.Equals(requested.Name, contracts.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return contracts;

            List<string> dirs;
            lock (Sync) { dirs = new List<string>(ProbeDirectories); }

            foreach (string dir in dirs)
            {
                try
                {
                    string candidate = Path.Combine(dir, requested.Name + ".dll");
                    if (File.Exists(candidate))
                        return Assembly.LoadFrom(candidate);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("解析依赖失败 " + requested.Name + " @ " + dir + " -> " + ex.Message);
                }
            }
            return null;
        }
    }
}
