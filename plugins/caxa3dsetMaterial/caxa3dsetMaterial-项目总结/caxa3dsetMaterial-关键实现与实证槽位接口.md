# caxa3dsetMaterial-关键实现与实证槽位接口

## 一、材料库文件与材料页面的关系

### 1. 文件位置与编码

| 文件 | 大小 | 根节点 | 分类数 | 材料数 |
|---|---|---|---|---|
| `GB Materials.xml` | 762,858 B | `<MaterialLib Type="GB Materials" Version="1">` | 16 | 378 |
| `Standard materials.xml` | 531,536 B | `<MaterialLib Type="Standard Materials" Version="1">` | 15 | 260 |
| `Sustainability extras material.xml` | 11,216 B | `<MaterialLib Type="Sustainability extras" Version="1">` | 3 | 8 |

- 安装目录：`C:\Program Files\CAXA\CAXA 3D\2027\AppData\zh-cn\materials\Materials\`（本项目已整目录拷贝到 `materials\`）。
- 编码：**UTF-16LE + BOM**（`FF FE`），声明 `encoding="UTF-16"` / `utf-16`。自造库必须保持同样编码，否则极易解析失败。
- 数值允许普通十进制与小写 e 记数（`0.200E+12`、`0.160E-4`、`1.2e+11`）。

### 2. 树结构与材料页面的对应

材料页左侧树的根节点 = **MaterialLib**（显示名"国标材料"，与 `Type` 值对应，由程序本地化），下一级 = `classification`（如"铁""合金钢"），叶子 = `material`。截图 `信息收集\材料页面.PNG` 中 `国标材料 → 铁 → 软铁(SN)` 的顺序与 `GB Materials.xml` 完全一致。

```
<MaterialLib Type="..." Version="1">
  <classification name="铁">
    <material name="软铁(SN)" GUID="..." description="" propertysource="">
      <physicalproperties Type="eLinear_elastic_isotropic">
        <EX displayname="弹性模量" value="120000000000.000000"/>
        ...
      </physicalproperties>
      <shaders>
        <shader path="\metal\iron\cast iron.xml"/>          <!-- 基路径：materials\Graphics\Materials -->
        <swtexture path="\images\textures\metal\cast\cast_rough.jpg"/> <!-- 基路径：materials -->
        <swatchcolor RGB="0.439,0.412,0.412">
          <ColorOptical Ambient="1" Transparency="0" Diffuse="1" Specularity="1" Shininess="0.31" Emission="0"/>
        </swatchcolor>
      </shaders>
    </material>
  </classification>
</MaterialLib>
```

### 3. 属性键 ↔ 页面字段 ↔ 单位（实测映射）

| XML 元素 | 页面显示名（各库写法略有差异） | 单位 | 备注 |
|---|---|---|---|
| `DENS` | 密度 / 质量密度 / Mass density | kg/m³ | 直接决定零件重量 |
| `EX` | 弹性模量 / X 弹性模量 | N/m² | |
| `NUXY` | 泊松比 / XY 泊松比 | — | |
| `GXY` | 抗剪模量 | N/m² | |
| `ALPX` | 热膨胀系数 / 热扩张系数 | /K | |
| `KX` | 导热率 / 热导率 | W/(m·K) | |
| `C` | 比热 | J/(kg·K) | |
| `SIGXT` | 抗拉强度 / 张力强度 / Tensile strength | N/m² | |
| `SIGYLD` | 屈服强度 / 屈服力 | N/m² | |
| `SIGXC` | 抗压强度 / 压缩强度 | N/m² | 仅标准库有 |
| `RK` | 硬化因子（0=同向性，1=运动性） | — | 部分材料有 |
| `BETA` / `GAMMA` / `VISC` | 容积热膨胀系数 / 比热比率 / 动态粘度 | — | 仅标准库少量材料 |
| `材料模型名称` | — | — | 纯文本元素（无 displayname），少见 |

`physicalproperties` 的 `Type` 固定为 `eLinear_elastic_isotropic`（线性弹性各向同性），页面"模型类型"下拉即显示该项。

### 4. 数据质量（影响注入时的名称定位）

- 638 条材料、636 个唯一 GUID（2 个 GUID 重复）。
- **8 组材料名跨库/库内重名**：`Q235-A`、`Q235-B`、`Q235-A (F)`、`Q195`、`Q215`、`2A90 (GB)`、`7A10 (GB)`、`K409#` 各出现 2 次 → `SetMaterialName` 按名称定位时存在歧义，工具内应优先用 GUID 或"名称+分类"定位，并提示重名。
- 所有材料都有 `shaders`、`GUID`、`propertysource`（空串）；`description` 基本为空。
- 外观（颜色/光亮度/透明度/凸痕/反射/贴图/散射 7 个页签）由 `shaders` 决定：`shader path` 指向 `materials\Graphics\Materials\<材质族>\<具体>.xml`，`swtexture` 指向 `materials\images\textures\...`，`swatchcolor` 是缩略图/未渲染时的色块。
- GB 库 shader 复用度很高：193 条用 `polished steel`、120 条用 `polished aluminum`、27 条用 `cast iron`、16 条用 `satin finish stainless steel`。

## 三、注入路径（候选，含实测状态）

| 路径 | API | 说明 | 实测状态 |
|---|---|---|---|
| 材质名称注入 | `IZPartProperty.SetMaterialName(string name, bool useByOnlyThisPart)` | 给零件写材料名；`true`=仅此零件使用（类似页面提示的"拷贝到自定义库"） | 待实测 |
| 材质名称读取 | `IZPartProperty.GetMaterialName(ref string, ref bool)`；`IZElement.GetMaterialNameProperty(ref string)` | 读取现有材料名 | 待实测 |
| 密度注入 | `IZPartProperty.MassDensity`（get/put, double） | 直接设定密度，决定 `CalculatedMass` | 待实测（单位/是否随材料联动） |
| 质量模式 | `IZPartProperty.MassType`（`Z_MT_Calculated=0` / `Z_MT_Specified=1`）、`SpecifiedMass`、`CalculatedMass` | 计算重量 or 指定重量 | 待实测 |
| 重量校验 | `IZPartProperty.GetMassProperties(density, useLocalCoord, accuracy, ...)` | 用指定密度算体积/质量/惯性矩，可作"副作用判据" | 待实测 |
| 外观注入 | `IZPart.SurfaceFinish`（get）、`IZBodyFace.GetSurfaceFinish/SetSurfaceFinish`、`IZBaseApp.ShaderMgr`→`IZShaderMgr.AddShader/FindShader/GetShader` | 表面外观（颜色/反射/贴图等） | 待实测 |
| 材质库扩展 | 向 `AppData\zh-cn\materials\Materials\` 增放自定义 `*.xml`（同 schema） | 让自备材料出现在材料页并在 `SetMaterialName` 时可按名命中 | 待实测（程序未在任何 dll 中出现该路径常量，推测为目录枚举加载） |

补充事实：
- 页面提示"默认材质库中的材质不能被编辑，必须先拷贝到自定义库"，且用户已知"自定义材料修改后无法导出" → 自定义材料随文档（.ics）走，不能跨机复制；**跨机分发只能靠自造 XML 库文件**，这也印证了"材质库扩展"路线的价值。
- 注册表中材料库条目仅有一条：`Cat0007 = Scene\Materials.icc`；磁盘上并不存在 `Materials.icc`（由程序按需生成），因此 XML 目录不是通过配置注册的。

## 四、国标材料的设置方法（可复用规范）

1. **造库**：复制 `GB Materials.xml` 为骨架，只保留需要的 `<classification>`/`<material>`，`MaterialLib Type` 改为自己的库名（决定材料页根节点显示名）。
2. **每个材料必填**：`name`（唯一，建议"牌号 (GB)"）、`GUID`（新生成，不可与官方重复）、`description`、`propertysource`（可空串）；`physicalproperties Type="eLinear_elastic_isotropic"`。
3. **物理量按 SI 单位填裸值**，至少 `DENS/EX/NUXY/GXY/ALPX/KX/C/SIGXT/SIGYLD`；`displayname` 可沿用官方写法（"密度""弹性模量"…），程序按元素名 `DENS` 等识别，不依赖 displayname。
4. **外观三件套**：`shader path` 选 `Graphics\Materials` 下已有外观（钢/铝/铁等），`swtexture` 选 `images\textures\...`，`swatchcolor RGB` + `ColorOptical`（Ambient/Diffuse/Specularity/Shininess）给缩略图色块；三项可省略 `swtexture`，但 `shader` + `swatchcolor` 建议保留。
5. **保存为 UTF-16LE + BOM**，根节点 `Version="1"`。
6. 参考实现要点：GB 库把国标号写进分类名（如"碳素结构钢 GB/T 700-2006""不锈钢 GB/T 20878-2024"），材料名带 `(GB)` 后缀；同一牌号在不同标准下会重复（Q235-A 等），造库时要靠 GUID 去重。

