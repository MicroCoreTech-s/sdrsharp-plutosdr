# SDR# 1921 PlutoSDR 插件 — 部署教程

把 ADALM-PLUTO 变成 SDR# 里的一等接收源：出现在 **Source** 菜单、满速流式接收，并带一个可调
频率／采样率／带宽／增益并显示实时 RSSI 的源面板。

插件是**被动**的：注册完数据源就什么都不做。不会自动选源、不会自动开始接收、也不会改写 SDR#
自身的配置。

| 部署方式 | 适用场景 | 需要源码 | 需要 dotnet SDK |
|---|---|---|---|
| [A. 发布包手动拷贝](#方式-a发布包手动拷贝最简单) | 已拿到编译好的 DLL | 否 | 否 |
| [B. 一键脚本](#方式-b一键脚本推荐) | 有源码仓库 | 是 | 是 |
| [C. 从零构建](#方式-c从零构建) | 想自己改代码 | 是 | 是 |

---

## 一、前置条件

| 项目 | 要求 | 检查方式 |
|---|---|---|
| SDR# | **1921 版本、32 位**（`SDRSharp.dotnet8.exe` / `SDRSharp.dotnet9.exe`） | 见下方命令 |
| .NET 运行时 | 与 SDR# 匹配的 **32 位**桌面运行时（8 或 9） | `dir "C:\Program Files (x86)\dotnet\shared\Microsoft.WindowsDesktop.App"` |
| PlutoSDR | 已通过 USB 连接，**RNDIS 网卡已就绪**，`192.168.2.1:30431` 可达 | `Test-NetConnection 192.168.2.1 -Port 30431` |
| 权限 | 对 SDR# 安装目录有写权限 | 部署脚本会实际写入，失败会报错 |

> **为什么必须是 32 位？** SDR# 1921 本体是 x86 进程，插件必须同为 x86，否则加载即失败。

确认 `192.168.2.1:30431` 可达：

```powershell
Test-NetConnection 192.168.2.1 -Port 30431 -InformationLevel Quiet
```

返回 `True` 即可。若为 `False`，见[第六节](#六常见问题排查)第 3 条。

---

## 方式 A：发布包手动拷贝（最简单）

只需要 **两个文件**，不需要源码也不需要 SDK：

```
<SDR# 目录>\
  SDRSharp.dotnet9.exe
  ...
  Plugins\                         <- 若不存在就新建
    PlutoSDR\                      <- 子目录名任意
      SDRSharp.PlutoSDR.dll
      MagicLine.txt
```

**第 1 步** 新建目录：

```powershell
New-Item -ItemType Directory -Force "C:\SDRSharp\Plugins\PlutoSDR"
```

**第 2 步** 拷贝两个文件（把 `<发布包>` 换成解压后的目录）：

```powershell
Copy-Item "<发布包>\SDRSharp.PlutoSDR.dll" "C:\SDRSharp\Plugins\PlutoSDR\" -Force
Copy-Item "<发布包>\MagicLine.txt"          "C:\SDRSharp\Plugins\PlutoSDR\" -Force
```

`MagicLine.txt` 是 SDR# 发现插件的登记文件，内容必须**恰好一行**：

```xml
<add key="PlutoSDR" value="SDRSharp.PlutoSDR.PlutoSDRPlugin,SDRSharp.PlutoSDR" />
```

格式是 `完全限定类型名,程序集名`。写错会导致插件被静默忽略。

**第 3 步** 确认没有多拷宿主程序集。`Plugins\PlutoSDR\` 里**只能有** `SDRSharp.PlutoSDR.dll`
这一个 `SDRSharp.*.dll`：

```powershell
Get-ChildItem "C:\SDRSharp\Plugins\PlutoSDR" -Filter "SDRSharp.*.dll"
```

如果出现 `SDRSharp.Radio.dll`、`SDRSharp.Common.dll`、`SDRSharp.PanView.dll`，**必须删掉**。
SDR# 自带这些程序集，多一份会触发 `Assembly with same name is already loaded` 而导致加载失败。

---

## 方式 B：一键脚本（推荐）

脚本依次完成：校验 SDR# 目录与位数 → 按需提取宿主程序集 → 编译插件 → 拷贝到
`Plugins\PlutoSDR\` → 清理误放的宿主程序集 → 检查 Pluto 连通性。

```powershell
cd <仓库根目录>
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -SdrSharpDir "C:\SDRSharp"
```

> 路径含 `#`（例如 `D:\SDR#`）是合法的，但**必须用引号包起来**，否则 PowerShell 会把 `#` 当注释
> 起始符。

只安装、不重新编译：

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -SdrSharpDir "C:\SDRSharp" -SkipBuild
```

实测输出：

```
1. Checking the SDR# installation
  [ok]   SDRSharp.dotnet9.exe  (32-bit)
  [ok]   .NET 9 x86 desktop runtime  (8.0.28, 9.0.12)
2. Preparing the plug-in assembly
  [..]   src\PlutoSDR\refs is empty; extracting host assemblies from the local SDR# install
  [ok]   SDRSharp.Common.dll  (28 KB)
  [ok]   SDRSharp.PanView.dll  (324 KB)
  [ok]   SDRSharp.Radio.dll  (328 KB)
  [ok]   assembly ready: SDRSharp.PlutoSDR.dll  (58880 bytes)
3. Installing into SDR#
  [ok]   copied SDRSharp.PlutoSDR.dll -> C:\SDRSharp\Plugins\PlutoSDR
  [ok]   copied MagicLine.txt (how SDR# discovers the plug-in)
4. Checking that the Pluto is reachable (default TCP transport)
  [ok]   192.168.2.1:30431 reachable
Installed.
```

---

## 方式 C：从零构建

**第 1 步** 安装 .NET SDK（9.0 或更高）。

**第 2 步** 提取宿主引用程序集。插件编译需要 SDR# 的三个程序集，它们内嵌在
`SDRSharp.dotnetN.exe` 里：

```powershell
powershell -ExecutionPolicy Bypass -File tools\extract-refs.ps1 -SdrSharpDir "C:\SDRSharp"
```

脚本会写入 `src\PlutoSDR\refs\`，并**逐一读取每个程序集自身的元数据来校验身份**，不依赖 exe
内嵌清单里的名称与偏移配对（那部分容易出错）。三个文件都应输出 `[ok]`。

这些程序集是 Analog Devices 的财产，**不要提交到 git** —— `.gitignore` 已排除该目录。

**第 3 步** 编译：

```powershell
dotnet build src\PlutoSDR\SDRSharp.PlutoSDR.csproj -c Release
```

产物：`src\PlutoSDR\bin\Release\SDRSharp.PlutoSDR.dll`

**第 4 步** 按[方式 A](#方式-a发布包手动拷贝最简单) 拷贝到 SDR#。

> 项目固定 `net9.0-windows` + `x86`；`refs\` 的引用标记为 `Private=false`，确保宿主程序集**不会**
> 被复制到输出目录。

---

## 四、首次启动与验证

1. 启动 `SDRSharp.dotnet9.exe`。
2. **Source** 菜单 → **PlutoSDR (ADALM-PLUTO)**。
3. 按 **Play**（▶）。

按顺序应当看到：

- 标题栏 `AIRSPY SDR# Studio v1.0.0.1921 32-Bit - PlutoSDR (ADALM-PLUTO)`
- 左侧 **Source** 面板显示 `Analog Devices PlutoSDR Rev.B (Z7010-AD9364)`、固件版本、序列号、
  libiio 版本、传输方式，状态 `Connected.`
- 底部状态行持续刷新 `RSSI xx.x dB  gain xx.x dB  streaming`
- 频谱与瀑布图实时滚动

### 看日志确认（推荐）

SDR# 会吞掉打开数据源时抛出的异常，所以**日志是唯一能看清内部行为的地方**：

```powershell
Get-Content "C:\SDRSharp\Plugins\PlutoSDR\PlutoSDR.log" -Tail 20
```

`Plugins\PlutoSDR\` 下的文件：

| 文件 | 作用 |
|---|---|
| `PlutoSDR.log` | 插件运行日志：注册、Source 菜单布局、连接、配置、流式块计数 |
| `PlutoSDR.config` | 插件设置（设备 URI、采样率、带宽、增益等） |

**部署成功的日志长这样**（实测）：

```
=== plug-in Initialize ===
host control object = SDRSharp.SharpControlProxy, main form = SDRSharp.MainForm
frontend registered as 'PlutoSDR (ADALM-PLUTO)' (built-in sources: 11)
Source menu now: [0] 'Configure' tag=<null> | ... | [13] 'PlutoSDR (ADALM-PLUTO)' tag=11 | [14] 'Baseband from Sound Card' tag=<null>
```

上面四行说明**插件已就绪**（注意：到这一步插件就停下来了，不会自己去选源）。当你手动选择
PlutoSDR 并按 Play 后，追加出现：

```
opened Analog Devices PlutoSDR Rev.B (Z7010-AD9364) fw v0.39-dirty via TCP 192.168.2.1:30431; rate=2000000 LO=100000000 gain=40dB
streaming started: buffer 61440 samples (245760 bytes per block), rate 2000000
first block delivered to SDR#: 61440 IQ samples at 2000000 SPS
streaming: 400 blocks delivered (61440 samples each)
```

验收要点：

| 日志行 | 含义 |
|---|---|
| `frontend registered` | 注册成功 |
| `[13] 'PlutoSDR (ADALM-PLUTO)' tag=11` | Source 菜单位置正确 |
| `opened ... via TCP` | 真实硬件已连上 |
| `first block delivered` | IQ 数据已真正进入 SDR# 接收链路 |

同时 `PluginError.log` 应为空或与本插件无关。

---

## 五、配置

### 源面板（Source 面板内）

| 控件 | 说明 |
|---|---|
| Device URI | `auto`（默认）、`ip:<主机>`、或 `usb` |
| Sample rate | 0.5 – 30.72 MSPS，可直接输入数字 |
| RF bandwidth | 0.2 – 56 MHz |
| AGC (slow attack) | 勾选后切换 `gain_control_mode` |
| RX gain | 手动增益，−1 – 73 dB |
| 状态行 | 型号、固件、序列号、libiio 版本、传输方式；流式时显示 RSSI 与增益 |

### 插件面板（SDR# **Display** 菜单 → plugins）

- **Use PlutoSDR as the source now** — 立即切到 PlutoSDR（等同于在 Source 菜单里点一下）
- **Re-register the frontend** — 注册失败时重试
- 顶部显示注册状态与宿主当前数据源

### 配置文件

`Plugins\PlutoSDR\PlutoSDR.config`，纯文本 `键=值`：

```ini
# SDR# PlutoSDR plugin settings
DeviceUri=auto
SampleRate=2000000
Bandwidth=2000000
GainDb=40
Agc=false
Frequency=432948310
```

改文件后需重启 SDR#。**正常关闭** SDR# 时插件会写回；强制结束进程不会保存。

### Device URI 怎么选

| 值 | 走哪条路 | 说明 |
|---|---|---|
| `auto` | TCP `192.168.2.1` → `pluto.local` | **推荐**，满速稳定，不依赖任何原生库 |
| `ip:192.168.2.1` | 指定 TCP 地址 | 网段被改过时用它 |
| `ip:pluto.local` | mDNS 解析 | 设备 IP 变了也能找到 |
| `usb` | libusb bulk 端点 | 见下方警告，**默认不要用** |

> ⚠️ **`usb` 目前会导致 SDR# 崩溃**。SDR# 自带的 32 位 `libusb-1.0.dll`（1.0.21）在枚举 Pluto 这个
> 复合设备时会发生访问违例（`0xC0000005`），libusb 自身日志报
> `device '\\.\USB#VID_0456&PID_B673&MI_02#...' is no longer connected!`。
> 64 位的 libusb 1.0.26 没有这个问题。
> 要启用 USB 通路，需先把 SDR# 目录下的 `libusb-1.0.dll` 换成更新的 **32 位**版本。

---

## 六、常见问题排查

### 1. Source 菜单里没有 “PlutoSDR (ADALM-PLUTO)”

```powershell
# a) 两个文件是否都在
Get-ChildItem "C:\SDRSharp\Plugins\PlutoSDR"

# b) MagicLine.txt 内容是否恰好一行、类型名是否正确
Get-Content "C:\SDRSharp\Plugins\PlutoSDR\MagicLine.txt"

# c) DLL 是否为 32 位 (期望 0x014C)
$b=[IO.File]::ReadAllBytes("C:\SDRSharp\Plugins\PlutoSDR\SDRSharp.PlutoSDR.dll")
$pe=[BitConverter]::ToInt32($b,0x3C); '{0:X4}' -f [BitConverter]::ToUInt16($b,$pe+4)

# d) 看宿主报的加载错误
Get-Content "C:\SDRSharp\PluginError.log" -Tail 30
```

常见原因：`MagicLine.txt` 拼写错误、DLL 不是 x86、`Plugins\PlutoSDR\` 里混进了宿主程序集。

### 2. 菜单里有，但选中后没有信号 / Source 面板显示未连接

看 `PlutoSDR.log`：

- 有 `frontend registered` 但没有 `opened ...` → 连接失败，日志里会跟一行
  `Open('...') failed: ...`，按第 3 条处理。
- 完全没有 `=== plug-in Initialize ===` → 插件根本没被加载，回到第 1 条。
- 有 `opened` 但没有 `streaming started` → 数据源打开了但没开始接收，按 **Play**。

### 3. `Could not open the PlutoSDR` / 连接超时

默认走 TCP，所以先确认网络侧：

```powershell
Test-NetConnection 192.168.2.1 -Port 30431
Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'RNDIS|Remote NDIS' }
```

- 没有 RNDIS 网卡 → Pluto 的 USB 驱动没装好。检查设备管理器是否有未识别设备；必要时重装 Pluto
  的 Windows 驱动包。
- 有网卡但连不上 → 网段冲突。Pluto 默认 `192.168.2.1`，若本机其它网卡也在该网段会冲突，需改
  Pluto 的 IP 或用 `ip:<新地址>`。
- 用官方工具交叉验证：

```powershell
& "C:\Program Files\IIO Oscilloscope\bin\iio_info.exe" -s
```

能同时列出 `usb:...` 与 `ip:192.168.2.1` 两条上下文，说明硬件与驱动都正常。

### 4. SDR# 一启动就崩溃（无窗口，或 `0xC0000005`）

几乎一定是 `DeviceUri` 被设成了 `usb`。把 `Plugins\PlutoSDR\PlutoSDR.config` 里的 `DeviceUri`
改回 `auto`：

```ini
DeviceUri=auto
```

### 5. 下次启动时停在 "Baseband from Sound Card"

这是 **SDR# 自身**的行为，不是插件故障。SDR# 把所选数据源持久化为索引，并按
`selected = (stored > count) ? count : stored` 载入；第三方源正好位于索引 `count`，能通过该钳位，
而此时插件尚未注册，于是 `SourceIsSoundCard`（`selected >= count`）判定为真。

在 Source 菜单里重新选一次 **PlutoSDR (ADALM-PLUTO)** 即可。插件**不会**去改写 SDR# 配置里的
`iqSource` 来回避这个问题。

### 6. 频谱一片空白 / 没有音频

- 没按 **Play**（▶）。
- 频率超出 Pluto 范围。AD9364 是 **70 MHz – 6 GHz**，超出会被钳位或设置失败。
- 增益太低：RX gain 拉到 40 dB 左右，或勾选 AGC。
- 换到已知有信号的频点（如本地 FM 广播 88–108 MHz）验证通路。

### 7. RSSI 常年 90 dB 以上、频谱发白

输入饱和，通常是**没接天线**或前级增益过大。降低 RX gain，或勾选 AGC。

### 8. 采样率上不去 / 卡顿

- USB 2.0 链路实际可用约 **6 MSPS** 以内，再高会丢样。2–5 MSPS 是稳妥区间。
- 采样率越高 CPU 占用越高；同时开多个 DSP 插件会加剧。

### 9. 想确认插件是否真的在满速收数据

日志里的块计数可以直接算吞吐：

```
streaming: 400 blocks delivered (61440 samples each)    12:12:53.852
streaming: 800 blocks delivered (61440 samples each)    12:13:06.141
```

400 块 × 61440 样本 ÷ 12.257 s ≈ **2.005 MSPS**，与设定采样率一致即为零丢样。

或者用不依赖 SDR# 的独立诊断：

```powershell
dotnet run --project tools\PlutoIioTest -c Release -- "C:\SDRSharp" ip:192.168.2.1 40 200
```

它会回读硬件真实的采样率寄存器（`HARDWARE sampling_frequency` / `rx_path_rates`）并报告有效采样率，
可以区分"真在跑 2 MSPS"和"链路在悄悄丢掉四分之三样本"。

---

## 七、更新与卸载

### 更新

重新跑部署脚本即可覆盖已安装的插件；`PlutoSDR.config` 不会被改动，你的设置会保留。**必须先关闭
SDR#**，否则 DLL 被占用 —— 脚本会检测到正在运行的实例并给出明确提示，而不是执行到一半失败：

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -SdrSharpDir "C:\SDRSharp" -SkipBuild
```

脚本会告诉你属于哪种情况：

```
  [ok]   installed SDRSharp.PlutoSDR.dll (60928 bytes)              # 全新安装
  [ok]   re-installed SDRSharp.PlutoSDR.dll (60928 bytes, unchanged) # 同版本重装
  [ok]   upgraded SDRSharp.PlutoSDR.dll: 58880 -> 60928 bytes        # 替换了旧版本
```

若检测到 SDR# 正在运行：

```
  [stop] SDR# is running from this folder (PID 1648).
  error: close SDR# and run this script again - the plug-in DLL cannot be replaced while it is loaded.
```

### 卸载

删除整个插件目录即可，SDR# 不会留下其他改动：

```powershell
Remove-Item -Recurse -Force "C:\SDRSharp\Plugins\PlutoSDR"
```

---

## 八、验收清单

- [ ] `Plugins\PlutoSDR\` 下同时存在 `SDRSharp.PlutoSDR.dll` 与 `MagicLine.txt`
- [ ] 该目录下**没有**其它 `SDRSharp.*.dll`
- [ ] 启动 SDR# 后，**Source** 菜单出现 `PlutoSDR (ADALM-PLUTO)`
- [ ] 选择该源后标题栏显示 `- PlutoSDR (ADALM-PLUTO)`
- [ ] Source 面板显示正确的型号／固件／序列号，状态 `Connected.`
- [ ] 按 Play 后底部显示 `streaming`，频谱与瀑布图滚动
- [ ] `PlutoSDR.log` 含 `frontend registered`、`opened ... via TCP`、`first block delivered to SDR#`
- [ ] `PluginError.log` 中没有与本插件相关的条目
