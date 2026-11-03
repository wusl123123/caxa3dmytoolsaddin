# caxa3dapigetaddin-踩坑与教训

## 六、踩坑与结论

1. **事件 sink 必须类级 `[ComVisible(true)]`。** 集合体要求插件程序集级 `ComVisible(false)`，但 `Advise` 需要 CCW；只在 sink 类/接口上开可见性，不要恢复程序集级 true，否则 Host 清单可能被污染。
2. **CRX 路径不能靠 `AppDomain.BaseDirectory`。** 插件运行在 CAXA bin 下，该目录没有 `.crx`；必须用 `IPluginHost.PluginDirectory`，否则「注册CRX模块」永远找不到文件。
3. **提权等待不能 `WaitForExit`。** 在 STA UI 线程阻塞会让 CAXA 假死；改为 `DoEvents + Sleep(100)` 轮询。
4. **`build.ps1` 需带 `Extra = @('.crx')`。** 否则只暂存 DLL，运行期缺 CRX，图纸环境事件采集静默缺失。
5. **插件 DLL 不内嵌清单。** 迁入后删除原 mt/sn 后处理，`mt.exe -inputresource:...#2` 读不到 manifest 才是正确状态。

