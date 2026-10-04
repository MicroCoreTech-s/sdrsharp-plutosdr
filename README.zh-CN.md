# SDR# PlutoSDR 插件

为 **AIRSPY SDR# Studio v1.0.0.1921（32 位）** 添加 ADALM-PLUTO 接收前端。

PlutoSDR 会像内置电台一样出现在 SDR# 的 **Source** 菜单里，满速流式输出 IQ，并自带一个可调
频率、采样率、射频带宽、增益／AGC 以及显示实时 RSSI 的源面板。

![SDR# 使用 PlutoSDR 前端接收](docs/images/sdrsharp-plutosdr.png)

> English: [README.md](README.md)

---

## 状态

已用真实硬件验证 —— *Analog Devices PlutoSDR Rev.B (Z7010-AD9364)，固件 v0.39-dirty* —— 在 SDR#
内持续 **2.005 MSPS**，无丢样（数据取自插件自身的块计数器）。

## 功能

- 在 **Source** 菜单中显示为 *PlutoSDR (ADALM-PLUTO)*，SDR# 将其完全按内置源对待。
- 满速 IQ 流式接收，12 位样本转换为 `Complex`。
- 覆盖 AD9364 的 70 MHz – 6 GHz 全频段，频率范围由设备实际读取。
- 采样率 0.5 – 30.72 MSPS、射频带宽 0.2 – 56 MHz，均按硬件上报范围校验。
- 手动增益（−1 … 73 dB）或 AGC（`slow_attack`）。
- 实时信息：型号、固件、序列号、libiio 版本、传输方式、RSSI 与当前增益。
- 两种传输自动选择，默认方式**不依赖任何原生库**。
- 不转发任何原生库：完全**不使用** SDR# 目录里那个 `libiio.dll`。

## 前置条件

| | |
|---|---|
| SDR# | 1921，**32 位**（`SDRSharp.dotnet9.exe` / `SDRSharp.dotnet8.exe`） |
| .NET | 与之匹配的 **x86** 桌面运行时（8 或 9）—— SDR# 能跑通常就已具备 |
| PlutoSDR | 通过 USB 连接，RNDIS 网卡已就绪，`192.168.2.1:30431` 可达 |

插件必须是 32 位，因为 SDR# 1921 是 32 位进程。

## 安装

### 使用发布包（无需构建工具）

只需要两个文件：

```
<SDR# 目录>\
  SDRSharp.dotnet9.exe
  Plugins\
    PlutoSDR\                          <- 子目录名任意
      SDRSharp.PlutoSDR.dll
      MagicLine.txt
```

`MagicLine.txt` 是 SDR# 发现插件的登记文件，必须**恰好一行**：

```xml
<add key="PlutoSDR" value="SDRSharp.PlutoSDR.PlutoSDRPlugin,SDRSharp.PlutoSDR" />
```

**不要**把 `SDRSharp.Radio.dll`、`SDRSharp.Common.dll`、`SDRSharp.PanView.dll` 放进去。SDR# 自带
这些程序集，多一份会因 `Assembly with same name is already loaded` 而加载失败。

### 从源码安装

```powershell
git clone <本仓库>
cd <仓库>

# 一步完成：按需提取宿主程序集、编译、安装
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -SdrSharpDir "C:\SDRSharp"
```

或手动执行：

```powershell
# 宿主程序集不随仓库分发，需从你自己的 SDR# 中提取
powershell -ExecutionPolicy Bypass -File tools\extract-refs.ps1 -SdrSharpDir "C:\SDRSharp"

dotnet build src\PlutoSDR\SDRSharp.PlutoSDR.csproj -c Release
# 然后把 DLL 与 MagicLine.txt 一起复制到 <SDR#>\Plugins\PlutoSDR\
```

完整步骤（含预期日志输出与排查表）见 [docs/DEPLOY.zh-CN.md](docs/DEPLOY.zh-CN.md)。

## 使用

1. 启动 SDR#。
2. **Source** 菜单 → **PlutoSDR (ADALM-PLUTO)**。
3. 按 **Play**。

插件是**被动**的：它只注册数据源，其他什么都不做。它不会自动选源、不会自动开始接收、也不会
改写 SDR# 自身的配置。一切行为都来自你在 Source 菜单里的选择或插件面板上的按钮。

插件自己的面板（SDR# **Display** 菜单 → plugins）显示注册状态、宿主当前数据源，以及两个按钮：
*Use PlutoSDR as the source now*（立即切换）和 *Re-register the frontend*（重新注册）。

### 源面板

面板采用**单列满宽自适应**布局，会跟随 SDR# 源面板的实际宽度伸缩，不会溢出产生横向滚动条：

![PlutoSDR 源面板](docs/images/source-panel.png)

| 控件 | 说明 |
|---|---|
| Device URI | `auto`（默认）、`ip:<主机>`、或 `usb` |
| Sample rate | 0.5 – 30.72 MSPS，可直接输入数字 |
| RF bandwidth | 0.2 – 56 MHz |
| AGC (slow attack) | 切换 `gain_control_mode` |
| RX gain | 手动 `hardwaregain`，−1 – 73 dB |
| 状态行 | 型号、固件、序列号、libiio 版本、传输方式；流式时显示 RSSI 与增益 |

设置保存在 `Plugins\PlutoSDR\PlutoSDR.config`：

```ini
DeviceUri=auto
SampleRate=2000000
Bandwidth=2000000
GainDb=40
Agc=false
Frequency=432948310
```

需**正常关闭** SDR# 才会写回；强制结束进程会跳过保存。

## 工作原理

### 注册一个 SDR# 不认识的前端

SDR# 1921 没有公开的硬件前端扩展点。它的内置源由 `MainForm.InitializeGUI()` 通过私有实例方法
`LoadSourceType(string name, Type type, int rank)` 按名字注册，该方法会填充前端表并向 Source
菜单追加对应条目。插件是在之后的 `MainForm_Load` 才加载的，因此本插件用反射调用同一个方法。
此后宿主会完全按内置源对待 PlutoSDR：对类型执行 `Activator.CreateInstance`，再用 `is` 检查探测
它实现了哪些电台接口。

要让这条路走通，有三个细节（均对着发布版二进制逐个验证过）：

1. **`ISharpControl` 不是窗体。** SDR# 传给插件的是一个只做转发的 `SharpControlProxy`；宿主窗体
   需从代理里取出，取不到时回退到 `Application.OpenForms`。
2. **菜单位置很关键。** 已选数据源是按 `sourceMenuItem.DropDownItems[selected + 2]` 解析的，而
   `LoadSourceType` 只会往末尾追加，SDR# 又在内置前端**之后**才添加 *Baseband from Sound Card*
   条目。因此单纯追加会让我方条目右移一格，宿主查到错误的名称、查找失败、什么也打不开。注册后
   需把条目移动到 `builtinCount + 2`。
3. **部分宿主接口需要前端配合实现。** `ISpectrumProvider`、`IControlAwareObject`、
   `ISampleRateChangeSource` 都是可选的，但正是实现它们才能让频谱宽度、宿主控制对象和采样率变更
   正确联动。

### 传输方式

* **`ip:` —— 默认。** Pluto 在自己的 USB 网口上运行着同一个 IIOD 服务，地址
  `192.168.2.1:30431`。可跑满采样率，且不需要任何原生库。
* **`usb` —— 需手动选择。** 在 USB 字符串描述符为 `"IIO"` 的接口上跑 IIOD：厂商控制请求只用于
  管道复位／打开，协议本身走 bulk 端点 —— 与 libiio 的 USB 后端完全一致。

  它**不是**默认值，因为 SDR# 自带的 32 位 `libusb-1.0.dll`（1.0.21）在枚举这个复合设备时会发生
  访问违例。libusb 自身日志报
  `device '\\.\USB#VID_0456&PID_B673&MI_02#...' is no longer connected!`，进程随即崩溃；
  而 libiio 使用的 64 位 libusb 1.0.26 没有这个问题。要启用 `usb`，请先把 SDR# 目录下的
  `libusb-1.0.dll` 换成更新的 **32 位**版本。

自带的 32 位 `libiio.dll` 同样不可用：它依赖 32 位 `libxml2.dll` 与 `libserialport-0.dll`，而这两
个文件在普通 Windows 上并不存在。直接说 IIOD 协议就完全绕开了这个依赖。

IIO 接口是通过遍历原始配置描述符、查找等于 `"IIO"` 的字符串描述符定位的。**不要写死接口号**：
在测试机上它是接口 **5**，而不是通常假设的 1。

### IIOD 协议

`src/PlutoSDR/Iio/IiodClient.cs` 实现了 libiio 的 USB 与网络后端共用的文本协议：

| 命令 | 用途 |
|---|---|
| `VERSION` | 后端版本 |
| `PRINT` | 上下文 XML，用于发现设备与通道 |
| `READ <dev> [INPUT\|OUTPUT <chn>] <attr>` | 读属性 |
| `WRITE <dev> [INPUT\|OUTPUT <chn>] <attr> <len>` + 数据 | 写属性 |
| `OPEN <dev> <samples> <mask>` | 分配内核缓冲 |
| `READBUF <dev> <bytes>` | 拉取样本 |
| `CLOSE <dev>` | 释放缓冲 |

上下文 XML 用于发现 `ad9361-phy` / `cf-ad9361-lpc`、RX 本振通道、增益通道，以及构成缓冲掩码的
有序扫描通道。

### 硬件数据通路

有两个很容易踩错的地方，都是实测踩出来的：

* **一次 `READBUF` 必须排空整个内核缓冲。** DMA 引擎在缓冲填满后立即回收，请求少了就会静默丢弃
  剩余部分 —— 只读 1/4 时实测正好是 2 MSPS 流里的 0.5 MSPS。因此块大小取
  `BufferSamples × 4` 字节。
* **`READBUF` 响应的首个数据块前总会带通道掩码**，无论调用方是否需要。漏读会让之后每次读取错位
  9 字节，第一个块之后协议即失步。

样本格式为 `le:S12/16>>0` —— 右对齐 12 位，饱和点在 ±2048（把增益拉到 73 dB 观察到削顶点后确认）。
按 1/2048 缩放写入 `SDRSharp.Radio.Complex`。

## 已知限制

* **用过一次 PlutoSDR 后，SDR# 下次启动可能停在 "Baseband from Sound Card"。** SDR# 把所选数据源
  持久化为索引，并按 `selected = (stored > count) ? count : stored` 载入。第三方源位于索引
  `count`，恰好能通过这个钳位；而该判断执行时插件尚未注册，于是
  `SourceIsSoundCard`（`selected >= count`）判定为真。功能没有损坏，重新在 Source 菜单里选一次
  **PlutoSDR** 即可。要彻底修好就得改写 SDR# 自身配置里的 `iqSource` —— 本插件刻意不做这件事。
* `usb` 传输需要比 SDR# 自带版本更新的 32 位 `libusb-1.0.dll`（见上）。
* 未实现发射；这是纯接收前端。
* USB 2.0 下双通道 16 位 IQ 实际上限约 6 MSPS，2–5 MSPS 是稳妥区间。

## 常见问题

完整排查表见 [docs/DEPLOY.zh-CN.md](docs/DEPLOY.zh-CN.md)。简表：

| 现象 | 检查 |
|---|---|
| Source 菜单里没有 *PlutoSDR* | `MagicLine.txt` 是否存在且拼写完全正确；DLL 是否为 x86；同级目录有无多余的 `SDRSharp.*.dll`；看 `PluginError.log` |
| 菜单里有，但一直未连接 | 看 `Plugins\PlutoSDR\PlutoSDR.log` 中的 `Open('...') failed` |
| `Could not open the PlutoSDR` | `Test-NetConnection 192.168.2.1 -Port 30431`；检查 RNDIS 网卡；用 `iio_info.exe -s` 交叉验证 |
| SDR# 一启动就 `0xC0000005` 崩溃 | `DeviceUri` 被设成了 `usb`，改回 `auto` |
| 频谱空白 | 按 **Play**；频率在 70 MHz – 6 GHz 内；提高增益 |
| RSSI 常年接近 100 dB | 输入饱和 —— 降低增益或启用 AGC |

另有一个不依赖 SDR# 的独立诊断工具，可完整跑通设备层：

```powershell
dotnet run --project tools\PlutoIioTest -c Release -- "C:\SDRSharp" ip:192.168.2.1 40 200
```

它会连接、配置硬件、回读真实的采样率与本振寄存器、流式读取若干块，并报告**有效采样率** ——
这样就能区分"真的在跑 2 MSPS"和"链路在悄悄丢掉四分之三样本"。

## 构建

```powershell
dotnet build src\PlutoSDR\SDRSharp.PlutoSDR.csproj -c Release
```

* 目标框架 `net9.0-windows`，平台 `x86`（SDR# 1921 是 32 位进程）。
* `src\PlutoSDR\refs\` 存放**仅供编译**的 SDR# 宿主程序集，引用标记为 `Private=false`，绝不会复制到
  输出目录。该目录已被 git 忽略，用 `tools\extract-refs.ps1` 生成 —— 它从你自己的
  `SDRSharp.dotnetN.exe` 中解包这些程序集，并逐一读取其程序集元数据做校验。

本仓库没有 CI 构建，原因相同：宿主程序集无法再分发，运行器没有它们就编译不了。

## 诊断

`Plugins\PlutoSDR\PlutoSDR.log` 记录注册过程、最终的 Source 菜单布局、传输方式、设备配置、流式
启动以及周期性块计数。SDR# 会吞掉打开数据源时抛出的异常，所以这个文件是查看前端在运行中的应用
内部究竟做了什么**唯一**的途径。

正常的日志如下：

```
=== plug-in Initialize ===
host control object = SDRSharp.SharpControlProxy, main form = SDRSharp.MainForm
frontend registered as 'PlutoSDR (ADALM-PLUTO)' (built-in sources: 11)
Source menu now: ... | [13] 'PlutoSDR (ADALM-PLUTO)' tag=11 | [14] 'Baseband from Sound Card' tag=<null>
opened Analog Devices PlutoSDR Rev.B (Z7010-AD9364) fw v0.39-dirty via TCP 192.168.2.1:30431; rate=2000000 LO=100000000 gain=40dB
streaming started: buffer 61440 samples (245760 bytes per block), rate 2000000
first block delivered to SDR#: 61440 IQ samples at 2000000 SPS
```

`frontend registered` → 注册成功。`[13] ... tag=11` → 菜单位置正确。`opened ... via TCP` → 硬件已
应答。`first block delivered` → IQ 确实流入了 SDR# 的接收链路。

## 目录结构

```
src/PlutoSDR/
  PlutoSDRPlugin.cs        ISharpPlugin 入口与宿主注册
  PlutoSDRIO.cs            SDR# 前端实现与 IQ 流式线程
  PlutoDevice.cs           设备发现、属性、内核缓冲
  PlutoControlPanel.cs     源配置面板
  PlutoSettings.cs         设置存储
  PlutoLog.cs              滚动日志
  Iio/
    IiodClient.cs          IIOD 协议（基于带缓冲的字节流）
    UsbTransport.cs        libusb 传输（需手动选择）
    TcpTransport.cs        TCP 传输（默认）
    LibUsb.cs              libusb P/Invoke 声明
    IIioTransport.cs
  MagicLine.txt            SDR# 插件注册行
tools/
  extract-refs.ps1         从本机 SDR# 解包宿主程序集
  deploy.ps1               一步完成编译与安装
  PlutoIioTest/            独立设备层诊断工具
  PanelShot/               在多个宽度下渲染源面板，用于检查布局
docs/
  DEPLOY.zh-CN.md          部署教程
```

## 致谢与许可

MIT —— 见 [LICENSE](LICENSE)。第三方、商标与再分发说明见 [NOTICE.md](NOTICE.md)。

本项目与 Analog Devices、Airspy 无隶属关系。插件在编译期链接 SDR# 宿主程序集；这些程序集是
Analog Devices 的财产，不随本项目分发，需从你自己的 SDR# 安装中提取。
