# TrayBattery

在 Windows 任务栏托盘用一个大号数字显示无线鼠标的剩余电量，顺便记住「这一箱电用了多久」。

单文件 C#、零第三方依赖、不联网、只读查询。整个程序就是一个 `.exe`，删掉就干净了。

![图标预览](assets/icon-preview.png)

---

## 支持的设备

| 类型 | 判定方式 | 说明 |
| --- | --- | --- |
| **ATK / VXE / Pulsar 系 2.4G 接收器** | HID 集合 `FF02:0002`，收发都是 17 字节 | VID `373B`、`391D`、`3554`。ATK Mouse 8K Dongle、Dragonfly F2 Turbo+ 等已实测 |
| **罗技 HID++** | VID `046D`，`FF00:0001` / `FF00:0002` | Bolt / Unifying / Lightspeed / 蓝牙直连都走同一套 `getFeature` + 电量功能 |
| **Windows 原生蓝牙电量** | `DEVPKEY_Bluetooth_Battery` | 只读设备属性，不配对、不连接 |

**其它品牌读不到。** 罗技以外的厂商各有一套私有协议（雷蛇、赛睿、海盗船、HyperX、mchose、英菲克……），本程序没有实现。右键菜单里的「为什么没有电量？」会列出本机所有 HID 接口和 VID/PID，拿着那串号去比对就知道该不该指望它。

---

## 快速开始

1. 下载 `TrayBattery.exe`（或自己编译，见下文）。
2. 双击运行。托盘出现图标，首次启动会弹一条气泡告诉你读到了什么。
3. 悬停看详情，左键点图标出菜单。

> **放到哪个目录都行，但有例外。** 见下一节。

### 启动了却没图标？

先看托盘左边的 `^` 展开区，Win11 默认把新图标折叠进去。

如果展开区里也没有，检查文件完整性标签 —— 这是本程序开发过程中踩到的真坑：

```powershell
icacls TrayBattery.exe | Select-String Mandatory
```

看到 `Low Mandatory Level` 就中招了。低完整性进程调用 `Shell_NotifyIcon(NIM_ADD)` 会被 UIPI 直接拒绝（`ACCESS_DENIED`，错误码 5），而且**静默失败** —— 进程活着、CPU 正常、就是不显示图标。

从某些压缩包解压、或者在沙箱/受限目录里编译出来的 exe 会带上这个标签，之后每次用它启动的进程都继承低完整性。修法：

```powershell
icacls TrayBattery.exe /setintegritylevel Medium
```

**每次重新编译后都要重打一次**，因为 csc 生成的是一份新文件。

---

## 功能

### 托盘图标

版式是「上半大号百分比数字 + 底部一条细进度条」。所有数字共用同一个字号 —— 以最宽的 `100` 为样本算出的最大可容字号，所以 `5`、`35`、`100` 高度一致，只是左右留白不同。

| 状态 | 进度条颜色 | 数字颜色 |
| --- | --- | --- |
| 充电中 | 蓝 `#00AAFF` | 跟随主题 |
| ≤ 15% | 红 `#E82030` | 红 `#F0323C` |
| ≤ 30% | 橙 `#FF9600` | 跟随主题 |
| 其它 | 绿 `#1EBE5A` | 跟随主题 |

数字颜色跟随系统主题（读 `HKCU\...\Themes\Personalize\SystemUsesLightTheme`，浅色任务栏用近黑、深色用近白），每次重绘都重读，切主题会跟着变。

读不到设备时显示灰色 `?`，进度条空着。

### 本次充电后用了多久

不用事后翻日志，直接显示：

- 悬停提示：`鼠标电量 Dragonfly F2 Turbo+  90% · 1h20m`
- 菜单第二行：`本次充电后已用 1 小时 20 分（100% → 90%）`

计时规则：

1. 读到「充电中 / 已充满」→ 清空本轮，充电期间不计时；
2. 拔线后第一次读到电量 → 以**那一刻 + 那个百分比**为起点；
3. 电量**回升** 5% 以上（换电池、线松了又插）→ 当作新一轮重新开始。

数据在 `%LOCALAPPDATA%\TrayBattery\`：

| 文件 | 内容 |
| --- | --- |
| `cycle.txt` | `起始时刻Ticks\|起始电量`，一行，重启后接着算 |
| `history.csv` | 电量每变一档追加一行：`时间,电量,充电标志,设备名` |

`history.csv` 就是完整日志，随便拿去算：

```powershell
# 今天电量怎么掉的
Get-Content "$env:LOCALAPPDATA\TrayBattery\history.csv" |
  Select-Object -Skip 1 | ForEach-Object { ($_ -split ',')[0..1] -join '  ' }
```

只记录**程序在跑的时候**。装上之前的历史补不回来。

### 刷新间隔

右键 → **刷新间隔**：30 秒 / 60 秒（默认）/ 5 分钟 / 15 分钟 / 手动。

存在 `HKCU\Software\TrayBattery\PollSeconds`（DWORD，0 = 手动），下次启动沿用。

调短不会更准：ATK 接收器在鼠标静止几秒后本来就停止回应，读到沉默会沿用上一次读数，只是更频繁地问「你还在吗」。

### 开机自启

菜单里的「开机自启」前面有 `✓` 就是已开启，再点一下 `✓` 消失。

状态每次都从 `HKCU\...\Run\TrayBattery` 重读，不是开关变量。exe 换了目录会认为没开 —— 因为开机那行还指着旧路径，确实起不来。

### 诊断

```powershell
TrayBattery.exe --dump
```

在同目录生成 `TrayBattery-dump.txt`：全部 HID 接口清单、罗技 / ATK / 蓝牙各自的探测结果和失败原因。

菜单里的「为什么没有电量？」会弹窗列出同样的内容，另有「复制诊断信息」直接把报告复制到剪贴板。

---

## 安全设计

写这份程序时的硬约束是「不改动任何设备状态、不引入网络行为」：

- **无联网**：没有 HTTP 客户端、没有更新器、没有遥测，源码里搜不到 socket 相关调用。
- **只读**：对设备只发查询帧 —— HID++ 的 `getFeature` / `BATTERY_STATUS`，Pulsar 的 `0x04` 电量查询。不下发固件、不改 DPI、不动灯效。
- **蓝牙只读属性**：`SetupDiGetDevicePropertyW` 读 `DEVPKEY_Bluetooth_Battery`，不配对、不连接。
- **注册表只有两个写点**：开机自启的 `HKCU\...\Run\TrayBattery`（默认关闭）和刷新间隔的 `HKCU\Software\TrayBattery\PollSeconds`。
- **数据文件只有两个**：上表那两个纯文本，不含硬件序列号。删掉程序照跑。
- **单文件可审计**：全部逻辑在 `TrayBattery.cs`，无第三方依赖，只用 Win32 + WinForms。

---

## 自己编译

需要 .NET Framework 4.x 的 `csc.exe`（Windows 自带，不用装 SDK、不用 NuGet、不联网）。

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

生成 `TrayBattery.exe`。然后：

```powershell
icacls TrayBattery.exe /setintegritylevel Medium   # 别忘这一步
```

遇到 `CS0016 未能写入输出文件` 说明有实例在跑，先 `Stop-Process -Name TrayBattery -Force`。

---

## 工作原理

### ATK / VXE / Pulsar 系

17 字节定长帧，`0x08` 开头、`0x04` 是电量命令，末字节是校验 `(0x55 - 前16字节和) & 0xFF`。

请求：`08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 xx`

回复解读（实测 `08 04 00 00 00 02 23 00 0E B2 00 ... 64`）：

| 偏移 | 含义 |
| --- | --- |
| 6 | 电量百分比（`0x23` = 35） |
| 7 | 充电标志，非 0 为充电中 |
| 8–9 | 电池电压，大端毫伏（`0x0EB2` = 3762 mV） |

只在 HID 集合 `FF02:0002` 且收发缓冲区都是 17 字节的接口上发。

### 罗技 HID++

`[report][device index][feature index][function<<4|swId][params]`，`0x10` 短帧 7 字节、`0x11` 长帧 20 字节，`swId = 0x0D`。

- `0x1004` UNIFIED_BATTERY → `p[0]` 是 SOC
- `0x1000` BATTERY_STATUS → `p[0]` 是百分比
- `0x1001` BATTERY_VOLTAGE → 毫伏
- 直连 / 蓝牙用设备索引 `0xFF`，接收器用 `1..6`

### 蓝牙

`SetupDiGetClassDevsW(NULL, DIGCF_PRESENT | DIGCF_ALLCLASSES)` 枚举 devnode，对 `BTHENUM*` / `BTHLE*` 读 `DEVPKEY_Bluetooth_Battery`（GUID `104EA319-6EE2-4701-BD47-8DDBF425BBE5`，pid 2）。

---

## 已知限制

- **电量是 5% 一档**。不是程序在偷懒，固件就这么报。实测连查 60 次（3 分钟）全是 `95%`，电压在 `4067` 和 `4072 mV` 之间来回跳，但百分比不动。4.07V 报 95% 也不符合标准锂电曲线 —— 固件用的是累积电量计，不是电压查表。
- 只支持上面那三类设备。
- 只支持 Windows。
- 没有代码签名，首次运行会触发 SmartScreen。

---

## 致谢

- 版式参考 [krisgfx9/Wireless-Device-Battery-Monitor](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor)（BatteryHub）。
- Pulsar 协议帧定义来自 [andrewrabert/python-pulsar-mouse-tool](https://github.com/andrewrabert/python-pulsar-mouse-tool)、OpenMouse、[sopparus/hitscan-battery](https://github.com/sopparus/hitscan-battery) 的抓包记录。
- 罗技 HID++ 报文格式参考 [Ithilias/logitray](https://github.com/Ithilias/logitray) 与 [andyvorld/LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery)。

## 许可

MIT，见 [LICENSE](LICENSE)。
