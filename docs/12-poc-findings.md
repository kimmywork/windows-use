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

## 8. 复现方法

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
