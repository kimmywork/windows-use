# WinUse PoC 第一轮实测结论

> **性质**：非侵入式只读探测。用真实代码在真实机器上采集数据，用来验证/推翻 `11-assumptions-register.md` 里登记的假设。
> **时间**：2026-09-22　｜　**代码**：`poc/WinUse.PoC/`（C# / .NET 10-windows / FlaUI 5.0.0）
> **原始数据**：`poc/poc-results.json`（51KB，8 个探测器全量输出）　｜　**运行日志**：`poc/poc-run.log`
> **可信度标注**：本文所有结论均为 **【实】**（实测，附数据）；未测到的部分单独列在第 6 节，**不伪装成已解决**。

---

## 0. TL;DR —— 五分钟版

| # | 结论 | 一句话 | 后果 |
|---|---|---|---|
| **C1** | UIPI 读取阻断**已证实** | 管理员窗口在 UIA 里**一个元素都看不到**，普通窗口 100% 可见 | A-01 结案；PRD P0-6 的改写是对的 |
| **C2** | PMv2 声明是**硬要求** | 不声明时 `GetDpiForSystem` **谎报 96**（真实 120），12/12 窗口坐标全错 | 不声明 PMv2，整个项目坐标就是错的 |
| **C3** | 坐标系只有**两套值**，且**差 8~9px** | UIA == `GetWindowRect`（物理像素）；DWM 视觉边界比它们每边小 8~9px | 推翻"UIA 是逻辑坐标"的旧推测；发现**新的截图↔点击偏移缺口** |
| **C4** | IME 状态**完全无法预判** | 8/8 真实窗口 `ImmGetContext` 返回 NULL | 推翻"先检测 IME 再决定输入策略"，改为无条件逐级尝试 |
| **C5** | `IsWindowVisible` **不可信** | 12 个"可见"窗口里 3 个是 cloaked 幽灵 | `window.list` 必须做 `DWMWA_CLOAKED` 二次过滤 |
| **C6** | UIA 顶层列表 ≠ 用户可见窗口 | 12 个 UIA 顶层元素中 4 个在 Win32 侧不存在；顶层 `FrameworkId` 一律 "Win32" | 窗口枚举不能直接用 UIA 桌面子元素 |
| **C7** | 进程 IL **查不到是常态** | 290 个进程里 135 个（**46.6%**）查询失败 | 任何"先查 IL 再决策"的逻辑必须有失败路径 |
| **C8** | 挂死防护轻负载可行 | 12 窗口顶层枚举隔离线程 **551ms**，25s 超时未触发 | 策略可行，但**样本不足**，不构成结论 |

**最刺眼的一条**：本轮推翻了 4 条假设，其中 **3 条（A-13 / A-14 / A-16）此前根本没被登记**——它们不是"已知的不确定性"，而是"没人意识到它是不确定性的隐含前提"。

---

## 1. 方法与安全声明

本轮 PoC **刻意做成只读**，因为需要在一台正在被正常使用、开着一屏工作窗口的机器上采集：

```
不调用 SendInput / keybd_event / mouse_event      → 不产生任何输入事件
不调用 SetCursorPos                                → 不移动鼠标指针
不调用 SetForegroundWindow / BringWindowToTop      → 不抢焦点、不打断操作
不启动 / 不关闭任何进程，不打开 / 不关闭任何窗口
不写注册表、不改任何系统设置
```

**唯一的"写"操作**是 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` —— 它只影响本探测进程自身的 DPI 感知模式，不影响系统或其他进程，且它本身就是 C2/C3 的实验变量。

运行全程约 2 秒，用户无感。

---

## 2. 采集环境

| 项 | 值 |
|---|---|
| OS | Windows 11（Build 26200 系列） |
| 显示器 | **单屏** 1920×1080 @ **125% 缩放**（effective dpi 120，raw 157，angular 137） |
| 虚拟屏幕（VSC） | x=0, y=0, 1920×1080，**原点非负** |
| 探测进程 | IL=**Medium**，未提权（`IsInRole(Administrator)` = false），UIAccess = false |
| 会话 | 非 Session 0，非远程会话 |
| 语言 | 已装 3 个键盘布局：en-US / zh-CN / ja-JP；当前线程 zh-CN |
| 采样窗口 | 12 个可见顶层窗口 |

**这个环境的局限直接决定了第 6 节的"未验证清单"**——单显示器、无国产 IM、无打开状态的对话框。

> **关于本文数字的口径（重要）**：所有数字对应 `poc-results.json` 的**最新一次运行**（`meta.generated_at = 2026-09-22 16:30:28`）。
> 开发过程中为修 `DwmGetWindowAttributeInt` 入口点 bug 重跑过一次，**进程级统计因此有两个版本**（如"IL 查询被拒"131 vs 135）。**以本文 / 磁盘文件为准**。
> 更要紧的是：进程级统计是**瞬时快照**，相邻两次运行差异可达 ±4。**引用时写"约 45%~47%"而不是精确的 46.6%**——结论方向（查不到是常态）不受影响，但精度不能假装。

---

## 3. 逐条结论

### C1 — UIPI 读取阻断：已证实（原假设 A-01 结案）

**现象**：本机同时开着两个 Windows Terminal 窗口，唯一差别是权限级别。

| 窗口 | 进程 IL | UIA 可见性 |
|---|---|---|
| 「Windows PowerShell」 | Medium | ✅ 可见（hwnd `0x408F0`，含 3 个直系子元素） |
| 「**管理员**: Windows PowerShell」 | **High**（pid 17860） | ❌ **完全不可见（0/1）** |

**对照组**：11 个 Medium IL 窗口中，8 个**真正可见**的 **100% 被 UIA 看到**；另外 3 个不可见的是 cloaked 幽灵窗口（见 C5），与权限无关。

**结论**：UIPI 阻断的粒度是"**高完整性级别进程的整个 UIA 树**"，不是单个窗口、不是偶发失败、不是"部分元素看不到"。同程序、同窗口类、同一时刻，唯一变量就是 IL。

**设计含义（重要）**：既然看不到，Agent 就**无法区分"元素不存在"和"被权限挡住了"**。PoC 数据直接支撑一个三段式判定：

```
1. Win32 侧（EnumWindows）能看到该窗口（有标题、有 pid）
2. UIA 侧查不到该窗口的任何元素
3. 该窗口所属进程的 IL > 自身 IL（或 IL 查询失败，见 C7）
   → 返回 UIPI_BLOCKED（IL 确认高于自身）
   → 返回 UIPI_SUSPECTED（IL 查不到，只能怀疑）
   而不是 ELEMENT_NOT_FOUND
```

`04` §2.4 的"SendInput 静默失败"+"看不到 UIA 树"组合起来意味着：**对高 IL 目标，v1 既打不动也看不见**，只能诚实上报并交给人类。这正好印证了当初把 PRD P0-6 AC③ 里"并成功"三个字删掉的必要性。

**影响**：`03-prd.md` P0-6（已改写）｜`06-protocol-spec.md` §9 `UIPI_BLOCKED`｜A-01 ✅ 结案

---

### C2 — Per-Monitor DPI Awareness 是硬要求，不是优化

**现象**（切换前后对比，同一批窗口）：

| | 切换前 | 切换后（PMv2） |
|---|---|---|
| `GetProcessDpiAwareness` | `PROCESS_DPI_UNAWARE`（0） | `PROCESS_PER_MONITOR_DPI_AWARE`（2） |
| `GetDpiForSystem` | **96**（谎报） | **120**（真实） |
| `GetWindowRect` 值发生变化的窗口数 | — | **12 / 12 全变** |

`SetProcessDpiAwarenessContext(-4)` 在本机调用**成功**（lastError=0）。

**结论**：进程默认处于 DPI Unaware 时，Windows 会对它**谎报系统 DPI**（固定 96），并把它拿到的所有窗口坐标做系统级缩放虚拟化。**12/12 个窗口的坐标全是错的。**

**设计含义**：
- 主机进程必须在 `Main` 最开头、创建**任何 HWND / WinRT 对象 / COM 对象之前**调用 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`。
- 必须处理**失败路径**：若返回 false（通常 `ERROR_ACCESS_DENIED`，说明运行时已代为设置），要用 `GetProcessDpiAwareness` 复核；若最终不是 PMv2，**应当降级告警**而不是假装没事。
- 这条不是"精度优化"，是**正确性前提**。不声明 PMv2，后面所有坐标相关的东西都是错的。

**影响**：`05-architecture.md` ADR-006｜`04-tech-foundation.md`｜A-02 子项 ✅ 证实

---

### C3 — 三套坐标系实测只有两套值，且差 8~9px

这是本轮**最有工程价值**也**最反直觉**的一条。

**实测数据**（PMv2 + 125% 缩放，12 个窗口，全部 dpi=120）：

| 窗口 | `GetWindowRect` | `DWMWA_EXTENDED_FRAME_BOUNDS` | UIA `BoundingRectangle` |
|---|---|---|---|
| Chrome（最大化） | `[-9,-9,1929,1029]` | `[0,0,1920,1020]` | `[-9,-9,1929,1029]` |
| 文件资源管理器 | 非最大化 | 每边小 8px（top 小 0px） | 与 `GetWindowRect` **一致** |

**两条硬事实**：

1. **UIA `BoundingRectangle` 与 `GetWindowRect` 完全一致**（delta `[0,0,0,0]`）——它俩**同源，都是 VSC 物理像素**。
   → **推翻了** `04`/`05`/`06` 里"UIA 返回逻辑坐标、需按 DPI 换算"的推测（登记为 A-16）。
2. **`DWMWA_EXTENDED_FRAME_BOUNDS` 比它们小 8~9px**：
   - 最大化窗口：每边小 **9px**（Win11 的 resize 热区延伸到屏幕外）
   - 非最大化窗口：left/right/bottom 各小 **8px**，top 小 **0px**

**为什么这条要命**：截图通道（WGC / `PrintWindow`）捕获的是**视觉边界 = DWM 矩形**，而定位/点击用的是 **UIA / `GetWindowRect` 矩形**。两个坐标系的原点差 8~9px。

**精确换算规则**（这是此前设计里缺失的部分）：

```
记 deltaL = dwm.Left  - win32.Left    # 实测 +8（非最大化）/ +9（最大化 @125%）
记 deltaT = dwm.Top   - win32.Top     # 实测  0（非最大化）/ +9（最大化 @125%）

【按窗口截图】截图原点 = DWM 视觉边界左上角
    元素在截图内的像素坐标  sx = uia_x - dwm.Left
    若误写成 sx = uia_x - win32.Left，则偏 deltaL（8~9px）

【全屏 / 虚拟屏幕截图】截图原点 = VSC 原点
    sx = uia_x - vsc_origin_x，窗口边框不参与，无此偏移
```

**因此给出一条规避策略**：**优先使用全屏 / 虚拟屏幕截图**并让截图像素坐标与 VSC 物理像素 1:1 对齐；只有在"按窗口截图"模式下才需要显式记录并减去每个窗口的 `dwm - win32` 偏移。

**对验收标准的影响**：PRD P0-14 / Bench T23/T24 的"跨屏点击误差 ≤2px"（PM 自评"拍脑袋"）现在有了具体含义——**若不做上述换算，误差就是 8~9px，≤2px 必然失守**。所以 ≤2px 的目标必须**建立在换算已完成的前提上**才成立。

**影响**：`06-protocol-spec.md` §5（坐标系权威定义）｜`04` §5｜`05` ADR-006｜A-16 ❌ 推翻并结案｜B-01 ⚠️ 部分结案

---

### C4 — IME 状态完全无法预判（原"先检测后决策"被推翻）

**现象**：对 8 个真实窗口调用 `ImmGetContext`，**全部返回 NULL**：

| 窗口类 | 应用 | `ImmGetContext` |
|---|---|---|
| `Chrome_WidgetWin_1` ×4 | Chrome / Edge / 豆包 / WorkBuddy | NULL |
| `Tauri Window` ×1 | Clash Verge | NULL |
| `Windows.UI.Core.CoreWindow` ×2 | 设置 / 输入法宿主 | NULL |
| `ApplicationFrameWindow` ×1 | 设置 | NULL |
| `CabinetWClass` ×1 | 文件资源管理器 | NULL |

**结论**：Chromium 系、Tauri 系、UWP/WinUI、Windows 资源管理器**都拿不到 IMM 输入上下文**，因此 `ImmGetOpenStatus` / `ImmGetConversionStatus` 这条路**根本走不通**，无法预判"中文输入法是开还是关"。

**设计含义**：`06` §6 原先设想的"先检测 IME 状态 → 再选输入策略"的**前置判断不成立**。改为：

```
不做任何前置预判。text.input 无条件按策略链逐级尝试：
  ① ValuePattern.SetValue（最干净，绕过 IME 组字）
  ② KEYEVENTF_UNICODE 逐字符发送（绕过 IME 拼音序列）
  ③ 剪贴板粘贴（最后兜底）
每一级都以「写后读回校验」判定成败，失败即降级到下一级。
```

好消息是：`06` §6 原本就写了"每层读回校验"，所以**策略链主体是对的，只需要把前置的 IME 检测删掉**。

**影响**：`06-protocol-spec.md` §6｜A-14 ❌ 推翻并结案

---

### C5 — `IsWindowVisible` 不可信：cloaked 幽灵窗口

**现象**：`IsWindowVisible` 返回 TRUE 的 12 个顶层窗口中，**3 个是 `DWMWA_CLOAKED = 2`（CLOAKED_SHELL）**：

| 进程 | 窗口类 | 标题 | cloaked |
|---|---|---|---|
| SystemSettings | `Windows.UI.Core.CoreWindow` | 设置 | CLOAKED_SHELL |
| ApplicationFrameHost | `ApplicationFrameWindow` | 设置 | CLOAKED_SHELL |
| TextInputHost | `Windows.UI.Core.CoreWindow` | Windows 输入体验 | CLOAKED_SHELL |

它们**有标题、有窗口类、有进程、IsWindowVisible=TRUE**，但用户根本看不到——UWP 应用被挂起后窗口进入 cloaked 状态，而 `IsWindowVisible` **不会**返回 FALSE。

**设计含义**：`window.list` 及任何窗口枚举能力必须做**二次过滤**：

```
1. IsWindowVisible(hwnd) == TRUE
2. DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED) == 0      ← 必须
3. rect.Width > 0 && rect.Height > 0                     ← 必须
4. 可选：(GetWindowLong(GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0
```

否则 Agent 会枚举到一堆"看得见的幽灵"并尝试点击它们——而这种失败最难调试，因为窗口标题、进程名全都对得上。

**影响**：`07-capability-registry.md` `window.list`｜`03-prd.md` 新增硬约束｜A-13 ❌ 推翻并结案

---

### C6 — UIA 顶层列表 ≠ 用户可见窗口列表

**现象**：

- UIA 桌面直系子元素里包含**任务栏**（`Shell_TrayWnd`）、**Program Manager**（`Progman`）、以及 **Tauri 的隐藏事件窗口**（`"Tao Thread Event Target"`）和 **Chromium 的隐藏窗口**。
- 12 个 UIA 顶层元素中，**4 个在 Win32 可见窗口列表里根本不存在**。
- **所有** UIA 顶层元素的 `FrameworkId` 一律为 `"Win32"` —— 连 Chromium 和 Tauri 也是。

**结论**：
1. 不能把 UIA 桌面直系子元素列表直接当作"用户可见窗口列表"（需与 C5 的过滤规则叠加使用）。
2. **`FrameworkId` 在顶层窗口层面没有区分度**，不能用来做"这是自绘 UI 吗 / 该降级到 PIXEL 吗"的判断——那个判断必须在**子元素层**做。

**影响**：`07-capability-registry.md` `window.list` / `element.tree`｜A-13 ❌ 推翻并结案

---

### C7 — 进程完整性级别：查不到是常态

**现象**：290 个进程中，**135 个（46.6%）无法查询 IL**（`OpenProcess` 或 `GetTokenInformation` 失败）。可查部分分布：

| IL | 数量 |
|---|---|
| Medium | 103 |
| Untrusted | 25 |
| Low | 17 |
| **High** | **10** |

> 该比例是瞬时快照，相邻运行波动 ±4，引用时请写"约 45%~47%"。

**设计含义**：任何"先查对方进程 IL 再决策"的逻辑（例如 C1 里提议的 `UIPI_BLOCKED` 判定第 3 步）**必须有查询失败的路径**，并且失败时要返回"怀疑"而不是"确认无问题"。

**影响**：`06-protocol-spec.md` §9｜A-15 ⚠️ 部分成立并结案

---

### C8 — UIA 挂死防护：轻负载可行，但样本不足

**现象**：
- 12 个顶层窗口的枚举（含每个窗口的直系子元素 + 二级子元素采样）：隔离线程总耗时 **551ms**，逐窗口合计 439ms（p50 33ms，p95 93ms）。
- 隔离线程（MTA + 25s 硬超时）**未触发超时**。

**结论**：`05` §10.3 的 `UiaIsolationRunner`（独立线程 + MTA COM + 硬超时 + 放弃）策略在轻负载下**可行**。

**但这条不构成结论**，有三个缺口，缺一不可：
1. 本机只有 12 个窗口，负载太轻；
2. **没有微信 / 企微 / 钉钉 / 飞书在运行**——真正的挂死风险恰恰来自国产 IM 这类自绘 UI 的大树；
3. **最关键**：采样时**没有任何目标进程处于无响应状态**，因此 abandon 策略**一次都没有被真正触发过**。我们证明的是"顺利时不挂"，不是"卡住时能救"。

**影响**：`05-architecture.md` §10.3｜A-03 ⚠️ 仍未证实｜A-04 🔴 仍阻塞

---

## 4. 本轮新登记的隐含假设（此前未登记，已被推翻）

| ID | 隐含前提 | 实测结果 |
|---|---|---|
| **A-13** | "UIA 桌面子元素 ≈ 用户可见窗口；`IsWindowVisible` 够用" | ❌ 推翻：3/12 是 cloaked 幽灵，4/12 UIA 元素在 Win32 侧不存在 |
| **A-14** | "可用 IMM 预判 IME 开关状态" | ❌ 推翻：8/8 窗口 `ImmGetContext` 返回 NULL |
| **A-15** | "可以查询目标进程的完整性级别" | ⚠️ 部分成立：约 46% 查不到 |
| **A-16** | "UIA `BoundingRectangle` 是逻辑坐标，需按 DPI 换算" | ❌ 推翻（方向相反）：它就是物理像素，与 `GetWindowRect` 一致 |

> **值得记住的一点**：登记在册的 28 条假设里，本轮只结案了 A-01 一条；**真正被推翻的 3 条反而是从没登记过的**。说明假设登记表的最大价值不在于整理已知，而在于**逼出那些没人意识到是假设的前提**。

---

## 5. 对设计与协议的直接影响（可执行清单）

| # | 动作 | 落在哪 | 优先级 |
|---|---|---|---|
| 1 | 主机启动最早期强制声明 PMv2，并有失败降级告警 | `05` ADR-006 / 内核启动流程 | **P0** |
| 2 | 坐标系章节区分"点击坐标系（UIA/GetWindowRect 物理像素）"与"截图像素坐标系（DWM 视觉边界）"，给出换算公式 | `06` §5 | **P0** |
| 3 | 优先全屏/虚拟屏幕截图，规避窗口边框偏移 | `06` §5 / `screen.capture` 设计 | **P0** |
| 4 | `text.input` 删除 IME 前置检测，改为无条件逐级尝试 + 读回校验 | `06` §6 | **P0** |
| 5 | `window.list` 加 `DWMWA_CLOAKED` + 尺寸二次过滤 | `07` `window.list` | **P0** |
| 6 | 新增 `UIPI_BLOCKED` / `UIPI_SUSPECTED` 三段式判定（Win32 可见 + UIA 不可见 + IL 对比） | `06` §9 | **P0** |
| 7 | `element.tree` 的降级判断放到子元素层，不依赖顶层 `FrameworkId` | `07` `element.tree` | P1 |
| 8 | IL 查询失败时返回"怀疑"而非"确认正常" | `06` §9 | P1 |
| 9 | Bench 坐标类任务判定加"未做 DWM↔UIA 换算即判失败" | `10-winuse-bench.md` | P1 |

---

## 6. 本轮**没有**验证的（诚实清单）

不要把这些误读为"已解决"：

| 项 | 为什么没验 | 需要什么条件 |
|---|---|---|
| 多显示器异构 DPI | 本机只有一块屏 | 双屏且设置不同缩放 |
| VSC **负坐标**原点 | 本机 VSC 原点是 (0,0) | 副屏放在主屏左/上方 |
| 国产 IM（微信/企微/钉钉/飞书）UIA 覆盖率 | **本机没运行任何一个** | 有这些应用的机器 |
| 高负载 UIA 挂死 | 只有 12 个窗口，负载太轻 | 20+ 窗口 + 含国产 IM |
| 对话框树形态（`#32770`） | **采样时没有任何对话框打开** | 手动打开保存/打开文件对话框后重跑 |
| `SetCursorPos` / `SendInput` 实际行为 | **属侵入式操作，本轮刻意未做**（不干扰用户） | 第二阶段 PoC，需用户知情同意、在非工作时段跑 |
| Win10 / Win11 对话框树差异 | 本机只有 Win11 | Win10 机器 |
| UIAccess / 提权形态下的行为 | 本机为 Medium IL 未提权 | 提权或签名 + UIAccess 后重跑 |

---

## 7. 第二轮 PoC 计划

**第一阶段（仍是只读，可随时跑）**——补环境：
1. 打开一个"另存为"对话框后重跑，采 `#32770` 树形态（A-06 / A-07）
2. 在有微信/企微/钉钉/飞书的机器上跑，采国产 IM UIA 覆盖率（A-04）
3. 双屏异构 DPI 环境跑，采 VSC 负坐标与跨屏坐标（A-02）
4. 造 20+ 窗口的高负载，测 UIA 挂死（A-03）

**第二阶段（侵入式，需专门安排）**——在受控环境里测：
5. `SendInput` 注入成功率、`SetCursorPos` 精度、UIPI 拒绝行为
6. 中文输入三种策略（ValuePattern / `KEYEVENTF_UNICODE` / 剪贴板）的实际成功率与读回校验开销
7. 实测点击误差，校准 B-01 的 ≤2px / ≤5px 阈值

---

## 8. 第二轮 A：双屏异构 DPI 实测（**A-02 只读侧结案**）

> **触发**：用户接入第二显示器后，同一台机器复跑只读 PoC（同一份 `poc/WinUse.PoC`，未改代码，零侵入）。
> 原始数据 `poc/WinUse.PoC/bin/Release/net10.0-windows/poc-results.json`；第一轮单屏基线备份在 `poc/poc-results.round1.json`，可直接对照。
> **这一轮推翻了我在 §7 里写的一句判断**：原写"多屏异构 DPI 需换环境"，实际情况是环境**当场就配齐了**——用户插一块屏，三个条件一次性全满足。

### 8.1 采集到的显示拓扑

| | DISPLAY1（主屏） | DISPLAY2（副屏） |
|---|---|---|
| rect | `[0, 0, 1920, 1080]` | `[0, -1200, 1920, 0]` |
| 分辨率 | 1920×1080 | 1920×1200 |
| effective DPI | **120**（125%） | **96**（100%） |
| work area | `[0, 0, 1920, 1020]` | `[0, -1200, 1920, -48]` |
| primary | ✅ | ❌ |

**虚拟屏幕（VSC）**：`x=0, y=-1200, w=1920, h=2280`，**`origin_is_negative = true`**
→ **异构 DPI**（120 vs 96）与**负坐标原点**（y = −1200）**两个场景同时成立**，A-02 所需的全部前置条件已具备。

### 8.2 结论

| 编号 | 结论 | 影响 |
|---|---|---|
| **C9** | 异构 DPI + VSC 负坐标环境**已具备**，A-02 的**只读侧全部结案** | A-02 |
| **C10** | **`GetDpiForSystem` 在异构 DPI 下只等于主屏 DPI，不可用于副屏换算** | A-02 / `06` §5.4 |
| **C11** | **DPI-unaware 进程在 100% 屏上"看起来是对的"** —— 这个 bug 只在异构 DPI 下暴露 | A-02 / 测试策略 |
| **C12** | 坐标系三方关系在副屏同样成立，**但偏移量不是常量**，C3 的"8~9px"表述需修正 | A-16 / B-01 |
| **C13** | **DWM 扩展边框不受本进程 DPI 感知等级影响，是唯一稳定基准** | 截图/视觉对齐设计 |

**C10 —— `GetDpiForSystem` 只能代表主屏**

| 进程状态 | `GetDpiForSystem` | 真实：主屏 / 副屏 |
|---|---|---|
| DPI-unaware | **96** | 120 / 96 |
| 声明 PMv2 后 | **120** | 120 / 96 |

声明 PMv2 之后 `GetDpiForSystem` = 120，**恰好等于主屏 DISPLAY1 的 DPI，而副屏是 96**。
→ **副屏坐标换算必须走 `MonitorFromPoint` / `MonitorFromWindow` + `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`，禁止用 `GetDpiForSystem` 做全局换算。** 单屏时代这个错误看不出来。

**C11 —— 最危险的失效模式：单屏 100% 下测不出问题**

PMv2 切换前后逐窗口对比（`diff_A_vs_B`）：

| 窗口 | 所在屏 / DPI | PMv2 前 | PMv2 后 | 是否变化 |
|---|---|---|---|---|
| WorkBuddy | 主屏 125% | `[-7,-7,1543,823]` | `[-9,-9,1929,1029]` | **变** |
| Clash Verge | 主屏 125% | `[488,76,1442,811]` | `[610,95,1803,1014]` | **变** |
| GitHub（Chrome） | **副屏 100%** | `[-8,-1208,1928,-40]` | `[-8,-1208,1928,-40]` | **没变** |
| 豆包（Chrome） | **副屏 100%** | `[-8,-1208,1928,-40]` | `[-8,-1208,1928,-40]` | **没变** |

`changed_win32_count = 6`（全是主屏窗口）。
**机理**：进程 DPI-unaware 时，Windows 用 `GetDpiForSystem`（= **96**）做坐标虚拟化。对 **100% 的副屏这个值恰好正确**，对 **125% 的主屏是错的**。
→ **所以副屏窗口坐标"看起来是对的"，纯属巧合。** 单屏 100% 的环境下永远测不出 DPI 感知 bug —— 第一轮 C2 只在单屏 125% 下测，没暴露这一层。**这意味着 MSAA/Win32 路径的 DPI 回归测试必须跑异构 DPI，跑单屏等于没跑。**

**C12 —— 修正 C3：偏移量不是固定的 8~9px**

| 窗口 | 所在屏 / DPI | 状态 | Win32 / UIA | DWM | 差值（DWM − Win32） |
|---|---|---|---|---|---|
| WorkBuddy | 主屏 125% | 最大化 | `[-9,-9,1929,1029]` | `[0,0,1920,1020]` | **9, 9, −9, −9** |
| GitHub | **副屏 100%** | 最大化 | `[-8,-1208,1928,-40]` | `[0,-1200,1920,-48]` | **8, 8, −8, −8** |
| Clash Verge | 主屏 125% | 非最大化 | `[610,95,1803,1014]` | `[618,95,1795,1006]` | **8, 0, −8, −8** |

在全部 6 个样本上，**UIA `BoundingRectangle` 与 `GetWindowRect` 的 delta 恒为 `[0,0,0,0]`**（含副屏、含负坐标）—— C3 的"UIA == Win32 == 物理像素"在双屏下**再次成立**。
但 **DWM 偏移量不是常量**：**最大化**时随 DPI 变化（100% → 8px，125% → 9px）；**非最大化**时 left/right/bottom 各 8px、**top 为 0**。
→ **修正 C3 原表述**（原写"125% 下固定差 8~9px"）。**偏移量禁止硬编码，必须按「是否最大化 × 该屏 DPI」取。** 这直接决定 B-01 的 ≤2px 目标能否达成。

**C13 —— DWM 是唯一不受感知等级影响的基准**

PMv2 切换前后：`changed_win32_count = 6`，**`changed_dwm_count = 0`**。
→ `DWMWA_EXTENDED_FRAME_BOUNDS` **不随本进程 DPI 感知等级漂移**，Win32/UIA 则会。
→ **截图像素对齐、视觉校验一律以 DWM 边界为准**；Win32/UIA 只用于点击命中。两条链路的基准要分开取，不能互推。

### 8.3 本轮**仍未**测的（诚实清单）

- **`SetCursorPos` 到负坐标是否可达**（副屏 y ∈ [−1200, 0]）—— 属侵入式，仍未做
- 跨屏拖拽、`MonitorFromPoint` 在屏间隙的返回值
- 副屏窗口的实际点击命中误差（B-01 的 ≤2px 在异构 DPI 下是否成立）
- 副屏存在时的 `window.list` / `scope` 行为（副屏窗口是否被正确纳入）

---

## 9. 第二轮 B：侵入式中文输入实测（**A-05 结案**）

> 本节为**追加**，不改动 §1–§8 与 §10 的任何既有内容。
> 编号说明：原稿 §8 后直接跳到 §10（§9 空缺），本节按内容归属补为 §9。

### 9.0 这一轮要回答什么

A-05 问的是「三种中文输入方案，哪个能可靠地把中文送进目标窗口」。这是**只读探针永远答不了**的问题——必须真的发按键。

- **方案 A**：`KEYEVENTF_UNICODE`，逐 UTF-16 码元 `SendInput`（文档宣称绕过键盘布局）
- **方案 B**：剪贴板写 `CF_UNICODETEXT` + `Ctrl+A` / `Ctrl+V`
- **方案 C**：UIA `ValuePattern.SetValue`

样本五个：S1 纯中文（4 码元）、S2 中英数混排（12）、S3 含全角标点（8，含 U+FF1A / U+FF1F）、S4 UTF-16 代理对（2，`U+20BB7`）、S5 长串（200）。

**目标窗口为什么不用 notepad**：Windows 11 25H2 的 `notepad.exe` 是单实例重定向应用，`Process.Start` 拿到的 pid 存活但 `MainWindowHandle` 恒为 0，真实窗口属于另一个 pid，护栏会正确地拒绝发送但也拿不到任何数据。因此改为**进程内自建目标窗口**，护栏的 pid 校验天然成立。

安全边界（全部落实在代码里）：只向自建窗口发送；发送瞬间校验「前台窗口 pid == 本进程 pid 且线程焦点在目标控件上」，否则拒绝并记 `FOCUS_GUARD_FAILED`；禁用 Win / Alt+F4 / Ctrl+Alt+Del / Alt+Tab；鼠标只 `SetCursorPos` 不点击；启动前有 5 秒倒计时与 Ctrl+C；`finally` 必定关闭自建窗口、还原剪贴板、还原前台焦点与鼠标位置。

### 9.1 实测矩阵

| 组 | 目标窗口 | `IsWindowUnicode` | 线程键盘布局 | 方案 A | 方案 B | 方案 C |
|---|---|---|---|---|---|---|
| G1 | ANSI EDIT（`CreateWindowExA`） | False | en-US `0x0409` | **0/5**，全变 `?` | **0/5**，全变 `?` | 0/5 |
| G1′ | EDIT（`CreateWindowExW`） | False | en-US `0x0409` | **0/5**，全变 `?` | **4/5**（S4 失败）¹ | 0/5 |
| G2 | ANSI EDIT（`CreateWindowExA`） | False | zh-CN `0x0804` | **4/5**（S4 失败） | **0/5**，全变 `?` | 0/5 |
| G3 | 自建 Unicode 窗口 | **True** | en-US `0x0409` | **0/5**，全变 `?` | 不适用² | 0/5 |
| G4 | 自建 Unicode 窗口 | **True** | zh-CN `0x0804` | **4/5**（S4 失败） | 不适用² | 0/5 |

¹ 同一配置另一次运行为 **5/5**（S4 亦通过）。代理对在方案 B 下**结果不稳定**，见 C17。
² 自建窗口不实现 `WM_PASTE`，方案 B 在该组无意义。**不填假数据**。

线程键盘布局用 `--thread-kl` 在**本进程 UI 线程内**临时切换（`LoadKeyboardLayout` + `ActivateKeyboardLayout`），不碰全局输入法，退出前已还原（日志可查：`现=0x4090409`，与进入时一致）。

### 9.2 关键证据：`WM_CHAR` 的 wParam

对目标控件做子类化，抓它**实际收到**的字符消息。同一段文本 `你好世界`：

| 组 | 线程布局 | EDIT 实际收到的 WM_CHAR |
|---|---|---|
| G1 / G3 | en-US | `0x003F, 0x003F, 0x003F, 0x003F`（四个 `?`） |
| G2 / G4 | zh-CN | `0x4F60, 0x597D, 0x4E16, 0x754C`（你、好、世、界） |

样本 S3 `测试：成功了吗？` 在 en-US 下的读回是 `??:?????` —— **全角冒号 U+FF1A 被映射成 ASCII `:`，全角问号 U+FF1F 被映射成 ASCII `?`**。这不是简单替换，是 Unicode→单字节代码页的 **best-fit 映射**特征。

样本 S4（`U+20BB7`，两个 surrogate 码元 `0xD842 0xDFB7`）在 **zh-CN 布局下依然是** `0x003F, 0x003F`。

进程 `GetACP()` = **936**，但 en-US 布局下走的是 **1252** 的 best-fit 表。

### 9.3 本轮结论（C14–C19）

- **C14：`KEYEVENTF_UNICODE` 的成败由「目标线程键盘布局所对应的代码页」决定，不由进程 ACP 决定。**
  这是本轮最重要的结论，也直接推翻了「`KEYEVENTF_UNICODE` 绕过键盘布局」这一常见认知。
  en-US 布局（→1252）下非 ASCII 全部损坏；zh-CN 布局（→936）下 BMP 内汉字逐字符正确。
  同一份代码、同一个目标窗口，只改线程布局，方案 A 从 0/5 变 4/5。

- **C15：失败形态是静默的——长度完全正确、零异常、内容错误。**
  15/15 失败样本的 `ActualLength == ExpectedLength`（4/4、12/12、8/8、2/2、200/200 全部相等），
  `SendInput` 返回全部事件已插入（`Ok=true`），Win32Error=0。**没有任何一个信号能告诉调用方内容错了**，
  只有逐字符读回比对才能发现。这把「注入后必须读回校验」从设计偏好升级为**硬性要求**。

- **C16：方案 A 对 BMP 外字符（代理对）必然失败，与键盘布局无关。**
  surrogate 被逐个码元独立转码，而单个 surrogate 在任何单字节代码页里都无映射 → 两个 `?`。
  zh-CN 布局下 S4 仍是唯一失败样本。emoji、扩展汉字（𠮷 类）、部分生僻字全部落入此坑。

- **C17：剪贴板方案保真度最高，但依赖目标控件的形态，且代理对结果不稳定。**
  在 W 版创建的 EDIT 上，en-US 布局中文/混排/全角标点/200 字长串**逐字符全对**；
  在 A 版创建的 EDIT 上退化为全 `?`。同一配置下 S4 一次通过、一次失败 → **flaky**，不能只跑一次下结论。

- **C18：方案 C（UIA `ValuePattern`）在自建目标上无法验证。**
  UIA 树遍历 7–11 个节点未找到支持 `ValuePattern` 的元素（自建窗口不是标准控件）。
  记录为**未验证**，不填假数据。要结案需换成真实应用的目标控件。

- **C19（方法论教训）：焦点必须落在真正的输入控件上，否则整轮数据看着像「方案全废」。**
  第一轮 15/15 全 0 的根因是 `SetForegroundWindow` 只把前台窗口切到主窗口、焦点没进 EDIT，
  而焦点校验把「焦点不是 EDIT」判为「仍可接受」后放行 → 字符被 `DefWindowProc` 吞掉。
  修法：置焦时显式 `SetFocus(EditWindow)`，且校验收紧为「焦点必须是目标控件，否则算护栏失败」。
  **宁可被护栏拦下报 `FOCUS_GUARD_FAILED`，也不要静默拿到一堆 0。**

### 9.4 对设计与协议的直接影响（可执行）

1. **`input.text` 绝不能只有 `KEYEVENTF_UNICODE` 一条路。** 必须保留剪贴板 / `ValuePattern` 降级，
   且降级判据不能只看「API 是否报错」——C15 证明报错为零时内容可能已经错了。
2. **注入前必须探测/确保目标线程键盘布局的代码页能承载待注入字符**，否则直接判 `UNSUPPORTED` 并走降级，
   不能硬发。这条要写进 06 的策略链。
3. **代理对是硬边界**：含 BMP 外字符的文本，方案 A 应被**静态排除**（可在发送前扫一遍码元），而不是发完再发现。
4. **读回校验是强制步骤**：`VERIFY_FAILED` 必须覆盖「长度相等但内容不符」这一类，
   且异常码要能区分 `LOST_CHARS`（丢字）与 `QUESTION_MARK_SUBSTITUTION`（转码损坏）——后者是本节新发现的类型。
5. **风险分级上调**：向中文用户机器注入文本，在 en-US 布局下属于「静默写错内容」，
   风险等级应按「不可逆写入」对待，而非「一次点击」。

### 9.5 本轮**仍未**验证的（诚实清单）

- **方案 C（UIA `ValuePattern`）**：自建目标不暴露该模式，未验证。
- **真实应用上的表现**：全部结论来自自建 EDIT / 自建窗口，未在任何第三方应用（Office、浏览器、IM）上验证。
- **方案 B 代理对的 flakiness 根因**：同一配置 4/5 与 5/5 都出现过，未定位是时序竞态还是 EDIT 内部行为。
- **中文输入法真正打开（有候选窗）时的表现**：`--thread-kl zh` 只切换了线程键盘布局，
  与「用户正在用拼音打字、候选窗弹出」不等价。真正的 `--ime cn` 轮（用户手动切输入法）仍未跑。
- **A-02 第二阶段（双屏负坐标）**：本轮枚举到的显示器数量仍为 **1**，未达前置条件，见下节。

### 9.6 顺带发现：副屏未检测到

第二阶段前置要求 ≥2 个显示器，本次 `EnumDisplayMonitors` 只返回 **1** 个，
虚拟屏幕 `1536×864`、原点 `(0,0)`、`原点为负=False`，因此第二阶段被正确跳过（未伪造结果）。

`1536×864` 是**默认 DPI 感知下**的虚拟屏幕值（物理 1920×1080 @125%），属预期，不是缺陷；
真正的问题是**副屏在本次运行时不在拓扑里**。这与 §8 记录的双屏环境不一致，需确认副屏当前状态后重跑。

---

## 10. 复现方法

```bash
cd D:/Workspace/research/windows-use/poc/WinUse.PoC
dotnet build -c Release
dotnet bin/Release/net10.0-windows/WinUse.PoC.dll "../poc-results.json"
```

探测器清单：

| 编号 | 名称 | 采什么 |
|---|---|---|
| P1 | 环境与权限基线 | OS / 会话 / 区域 / 自身 IL / 提权 / UIAccess / DPI 感知基线 |
| P2 | 显示器与 DPI 拓扑 | `EnumDisplayMonitors` / effective·raw·angular DPI / VSC 原点 |
| P3 | UIA 覆盖率与挂死防护 | 顶层元素、FrameworkId、遍历耗时、隔离线程超时行为 |
| P4 | 坐标一致性 | Win32 / DWM / UIA 三方比对 + PMv2 切换前后对比 |
| P5 | 中文 IME 状态 | 键盘布局、注册表、前台窗口与采样窗口的 IMM 状态 |
| P6 | 窗口与对话框形态 | 窗口类直方图、`#32770` 采样、cloaked 幽灵、UIA-only 元素 |
| P7 | 完整性级别版图 | 全进程 IL 分布、High 及以上清单 |
| P8 | UIPI 交叉验证 | **按 pid 交叉比对 IL 与 UIA 可见性**（C1 的直接证据） |

### 10.1 第二轮 B（侵入式输入实测）复现方法

```bash
cd D:/Workspace/research/windows-use/poc/WinUse.PoC.Input
dotnet build -c Release

# G1：ANSI EDIT + en-US 布局
./bin/Release/net10.0-windows/WinUse.PoC.Input.exe --phase 1 --ime en --edit ansi --out <路径>.json

# G2：ANSI EDIT + 线程切 zh-CN 布局（进程内临时切换，退出自动还原）
./bin/Release/net10.0-windows/WinUse.PoC.Input.exe --phase 1 --ime en --edit ansi --thread-kl zh --out <路径>.json

# G3 / G4：自建 Unicode 窗口
./bin/Release/net10.0-windows/WinUse.PoC.Input.exe --phase 1 --ime en --edit unicode [--thread-kl zh] --out <路径>.json
```

参数说明：`--edit unicode|ansi` 目标控件形态（默认 unicode）；`--thread-kl zh|en` 仅在本进程 UI 线程内
临时切换键盘布局、退出前还原，不碰全局输入法；`--phase 1|2`；`--ime cn|en` 仅作记录，程序不修改输入法状态；
`--out <路径>` 结果 JSON；`--no-wait` 跳过启动后的 5 秒倒计时。
