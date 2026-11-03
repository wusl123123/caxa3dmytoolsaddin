# caxa3dTreeBom-里程碑记录表

## 五、里程碑记录表

| 序号 | 时间 | 事件 | 结果 |
| --- | --- | --- | --- |
| 1 | 2026-10-28 | 需求解析 + 复用资产调研（setMaterial / AttributeManager / apigetaddin） | 确认遍历、属性、选中三条主链路均有实证代码可抄 |
| 2 | 2026-10-28 | 反射 interop 程序集 | `FilePreviewImage` 未声明 → 必须 IDispatch 延迟绑定；`WritePreviewToFile(string,string)` 存在 |
| 3 | 2026-10-28 | 读取模板表头 | 实际 10 列：层级/是否有工程图/缩略图/SC图号/SC名称/SC规格/SC材料/SC备注/数量/总数量 |
| 4 | 2026-10-28 | 首次编译 | 通过；mt 警告「嵌入清单使强名称失效」由 `sn -R` 解决，`sn -v` 校验有效 |
| 5 | 2026-10-28 | xlsx 零依赖冒烟测试 | 生成 5032 字节；10 部件齐全；缩略图 PNG 嵌入；XML 转义正确；表头回读一致 |
| 6 | 2026-10-28 | Excel COM 复核 | `usedRange=4x10`、`shapes=1`、`总装 & <A>` 正确还原、`总数量` 数字为数值型 |
| 7 | 2026-10-28 | 界面照搬 SwTreeBom | Form1 同构重建（连接区/表格区/日志区/右键菜单/插入删除列/缩略图自绘/日志拖拽） |
| 8 | 2026-10-28 | 部署到 CAXA 3D 2027 | DLL/PDB 拷贝成功，两个配置文件条目登记成功，DLL 强名称有效 |
| 9 | 2026-10-28 | 后台启动 CAXA 验证加载 | 进程内无任何托管 Add-in 加载（其它三个已验证插件同样未加载）→ 后台启动不具备代表性，需用户前台启动验证 |
| 10 | 2026-10-28 | 前台用户实测日志 | Add-in 成功加载并注册「树形BOM」按钮；`FilePreviewImage` 的 GetIDsOfNames 全部失败（该属性不存在），`WritePreviewToFile` 输出标准 BMP（`42 4D`）128×128，全部解码成功 |
| 11 | 2026-10-28 | 修复 SC备注 混淆填充 | 根因：`ReadRowProperties` 对表头先「去 SC 前缀」再走"备注→BOM 描述"兜底，`SC备注` 无该属性时被 `GetBOMSetting` 的 desc 填充。改为 SC* 表头只认精确同名属性（对齐 caxa3dAttributeManageraddin 的 `TargetFields` 语义），不兜底 |
| 12 | 2026-10-28 | 界面对齐参考项目 | 「定位文件」按钮 90×28、Margin(6,0,6,0)，右侧面板基准 500，并按文件名 `PreferredWidth` 动态放宽以保证完整显示；新增「窗口前置」按钮（默认 `TopMost=true`，点击切换，蓝/灰两态） |
| 13 | 2026-10-28 | 连接区显示选中零部件 | 标签改为「文件名.ics----零件名」（`FileLabelText`），2s 轮询同时比较文档路径与选中元素名，换选即刷新 |
| 14 | 2026-10-28 | 附加合并规则（caxa3dtreebomaddfix.txt） | 顶部新增「附件合并规则」按钮：开启后以规则文件字段全部一致为签名合并总数量（区分大小写、缺项即不合并、禁止兜底），签名首行显示合并值、其余行 `---`；未形成完整签名的行仍按文件名规则 |
| 15 | 2026-10-28 | 规则按钮改为「打开规则文件编辑」 | 点击打开 `%APPDATA%\caxa3dTreeBom\caxa3dtreebomaddfix.txt`（notepad），保存关闭后自动重读规则并补读属性、重算总数量；新增 `MergeRule`：只抓引号内字段、`#` 开头说明行整行隔离（说明里的 `""` 不会被当成规则）；安装脚本只播种一次、不覆盖用户修改 |
| 16 | 2026-10-28 | 第一行装配体工程图判定 | 层级 1（第一行装配体）在「同级同名 .exb」之外，追加查同名 ics 文件夹内的同名 .exb（`<基名>\<基名>.exb`、`<基名>.ics\<基名>.exb`），命中即判定有工程图；其它层级维持原规则 |
| 17 | 2026-10-29 | 现场机器「不是3D场景文档 + 场景顶层0个零件」排障与修复 | 根因：长任务用 `Task.Run` 在后台 MTA 线程调用 CAXA COM（`ActiveDoc` 返回 null），连接区（UI 线程轮询）却正常显示零部件，形成"能选中却遍历不到"的假象。改为 `RunOnUiThread` + 40ms 限流 `Application.DoEvents` 消息泵（进度可见、停止可点），所有 `IZBaseApp/IZSceneDoc/IZElement` 调用回归 STA 主线程；日志增加 TEMP→APPDATA→DLL 目录三级降级并回显路径；新增 `DescribeActiveDoc()` 文档探针（`IZDoc.Type` 的 `Z_SCENE/Z_DRAWING`、文档名、可转接口、打开文档数） |

