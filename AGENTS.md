# baize-godot 项目规则（强制）

本文件是 baize-godot fork 的强制开发规则，AI 与开发者均须遵守。与全局规范冲突时，本文件优先（fork 特有约束）。

## 1. 架构总览（2026-08-21 更新：All-in C# 路线，gd_provider 已退役）

**当前路线**：Godot Fork All-in C#（决策唯一权威见
`D:\MisuNotes\3D游戏开发\Godot_ALL_IN_C#\Godot_Fork_All-in-CSharp_总方案.md`）。

```text
C# 开发面（Gameplay/Tools/编辑器，唯一脚本语言）
        ↓
C# Platform（ECS-first Runtime / Schema 中心化 / 三级 Reload / 开发丝滑度）
        ↓
Godot Core（C++ 引擎：渲染/物理/资源/平台管线）
```

- **Godot Core**：Godot 进程整体（引擎 + 编辑器核心 + 渲染服务），C++ 保留在机器密集计算
  （渲染内核/物理求解 Jolt/平台 SDK）；
- **C# 开发面**：Gameplay/Tools/编辑器/脚本全部 C#（Unity 模式——C++ 引擎内核 + C# 唯一开发面）；
- **ECS-first Runtime**：Friflo.Engine.ECS 底座 + 场景树/关系/序列化（详见总方案 §3-4）；
- **AI 对接层**：MCP 标准（Wick / MCP C# SDK），gd_provider（C++ 自研）已退役删除；
- **脚本系统**：Roslyn 运行时编译（编辑器内 C# 编译即脚本，非独立脚本语言）。

**关键决策**（均已在总方案固化，改决策需先改总方案）：

| # | 决策 |
|---|---|
| D1 | 基底 = Godot 4.8-dev（当前主线，固定里程碑） |
| D2 | 目标框架 = 完全 .NET 11 + C# 15（方案 B，Preview 起步） |
| D3 | 脚本语言 = 仅 C#（产品面禁用 GDScript，源码目录保留减小 merge 冲突） |
| D4 | 少自研多集成 + 生态优先（社区成熟方案优先，自研仅限差异化创新） |
| D5 | ECS-first Runtime + Scene DB 编辑器（终极目标）+ Avalonia UI 层 |
| D6 | 热重载 = 有界优化（三级 Reload，Level 3 ALC 已成熟） |

**GDExtension 定位**：不用作能力层/脚本层载体（原因见
`doc/plans/GDExtension机制澄清与选型-为什么能力层不用它.md`）。

**已放弃的路线（勿恢复）**：Web/TS 集成（Electron UI、CEF WebDock、Node sidecar、TS 脚本语言、
`web/ui` 旧壳）、gd_provider（C++ AI 对接层，已被 MCP 标准取代）、GDScript——历史见 git，
勿从历史恢复旧代码/旧文档到工作区。**仓库内禁止新增 .gd 文件**。
（注意：`platform/web/` 是**上游 Godot Web 导出平台**，保留不动，勿与已删除的 fork `web/` 混淆。）

## 2. 构建流程

- **task 是唯一构建入口**（Taskfile.yml：dev/pro/dev-install/pro-install/dev-run）。构建逻辑在 `misc/scripts/build.py`。
- `task dev` → `build.py` → scons 构建编辑器（原版流程，无外部预构建钩子）。
- 构建产物：`bin/godot.windows.editor.*.exe` + `*.console.exe`（console 版日志直出终端，CLI/AI 驱动用）。

## 3. 测试规则（2026-08-21 更新）

**分层**：

| 层 | 方式 | 覆盖对象 |
|---|---|---|
| C# 单测 | xUnit/NUnit（随项目库） | 纯逻辑（Service/ECS/协议，零引擎） |
| 端到端 | spawn headless 编辑器 + 断言 | 引擎行为验证（godot --headless + 自动化脚本） |

**强制规则**：
- 测试项目一律放仓库内（`test-projects/`），禁止项目外绝对路径（换机器失效——已踩过 refers/ 外部路径坑）；
- 新能力/协议变更：e2e 补断言（读写验证 + 错误契约）；新 C# 纯逻辑：补单测。

## 9. Godot 测试时限（30 秒规则，强制）

打开 Godot 编辑器做验证/排障的命令（如
`./bin/godot.windows.editor.dev.x86_64.console.exe --path <项目> --editor > 日志 2>&1 &`）：

- **默认 30 秒内自动关闭**（sleep 30 → 采样日志 → Stop-Process 清理全部 godot 进程）
- 只需确认打开状态/页面加载/生成日志的场景：30 秒足够，**禁止拖到 1-2 分钟**
- 需要长时间持续的（长时间稳定性、内存增长、GPU/性能采样等）可突破 30 秒，但必须说明理由
- **每次测试后必须清理残留进程**（`Stop-Process -Name 'godot.windows*'`），残留进程污染后续测试
- **临时日志统一放 `.tmp/` 目录**（已 gitignore）：验证/排障产生的日志（如 `xxx.log`、`xxx_run.txt`）禁止散落根目录，用完即清或留 `.tmp/`；根目录只保留仓库文件。
## 9.1 交互类验证流程（强制：禁止模拟点击窗口）

**测试中禁止模拟窗口点击/键盘输入**（SetCursorPos + mouse_event / keybd_event / SendInput 等窗口自动化输入一律不用）——已多次踩坑（2026-08-05 焦点双轨修复）：桌面全屏窗口拦截命中（Typora/终端等）、`GetAsyncKeyState` 在 Godot 主线程恒返回 0（线程无输入队列）、模拟坐标与真实布局偏差，自动化假阴性浪费大量轮次。

交互/焦点/UI 行为类验证（点击顺序、焦点转移、输入生效等）统一走**用户协助流程**：

1. **构建带日志输出的版本**：在关键决策点打印可观测标记（如 `focus-return: …`），日志量控制在不刷屏（事件触发才打）
2. **打开 Godot**（编辑器 + 目标项目，按 §9 的 30 秒规则取舍运行时长）
3. **指导用户点击/操作**：给出清晰操作序列与每步预期日志标记
4. **用户操作完，分析日志**：以日志证据 + 用户结论双重确认，再决定是否迭代

原则：Agent 觉得复杂、脆弱或已多轮试错的操作，**主动请求用户协助**（说明步骤与预期日志标记）——用户亲测几秒即可完成，比 Agent 闭门造车模拟输入高效得多。需要长时间观察的场景可要求用户操作后保持窗口，Agent 后台采样日志。

## 11. 新文件头规则（2026-08-03 用户裁决）

**新文件用单行 SPDX 标识**，不复制 Godot 上游的 30 行长版权块（占用顶部空间、妨碍读码）：

```cpp
// SPDX-License-Identifier: MIT
```

- 适用：本仓库所有**新建**的 C++ 头/源文件（C# 文件按各自生态惯例，可省或同用 SPDX 单行）
- **既有文件不动**：Godot 上游文件保留原版权块
- 合规依据：MIT 许可由 SPDX 标识 + 仓库 LICENSE 文件满足；Godot 上游 4.x 新文件也逐步转
  SPDX 单行风格

## 12. 编码规范与中文优先（2026-08-03 用户裁决）

**fork 立场（用户裁决）**：本仓库是 Godot 的 fork——**不被上游历史负担束缚**。上游为兼容/性能保留的旧契约（如 `String(const char*)` 的 Latin-1 语义）与中文优先冲突时直接改进（标注 FORK-CUSTOM），不必因"上游没这么做"而妥协。

**中文优先（用户裁决）**：项目第一语言是中文——代码/日志/文档/UI 的字符串默认中文；全链路 UTF-8。

**全链路 UTF-8**（外部消费方 ↔ Godot ↔ 协议 JSON ↔ 文件），防乱码关键：

- **FORK-CUSTOM（b175d92bd6）**：`String(const char*)` 已改为**智能解码**——合法 UTF-8（含纯 ASCII）按 UTF-8 解码，非法序列回退 Latin-1（兼容字节透传）。C++ 中文字面量/UTF-8 数据直接构造即正确——**旧硬规则"必须 String::utf8()"已废除**；显式 `String::utf8()` 仍可用于强制 UTF-8 语义（外部二进制等）
- 转出给外部（AI/CLI/文件）用 `.utf8()`（CharString）
- 协议 JSON：`JSON::stringify/parse_string` 默认 UTF-8
- 文件/页面：Godot 文本默认 UTF-8；HTML 必须带 `<meta charset="utf-8">`
- 日志：中文日志可直接写（String 构造已 UTF-8）；显示依赖终端代码页——Godot 输出面板/UTF-8 终端正常，GBK 控制台需 `chcp 65001`（显示层，非数据问题）

## 14. 文档索引

- **决策唯一权威**：`D:\MisuNotes\3D游戏开发\Godot_ALL_IN_C#\Godot_Fork_All-in-CSharp_总方案.md`（All-in C# 路线：
  战略宪法/技术路线/架构模式/生态集成/实施路线——决策以总方案为准，改决策先改总方案）
- **GDExtension 澄清**：`doc/plans/GDExtension机制澄清与选型-为什么能力层不用它.md`
- **排障记录**：`doc/troubleshooting/README.md`（故障排查积累——现象/排查/根因/修复/验证；新问题按模板追加）
- **P2.4 及后续交接文档**：`doc/plans/P2.4-交接文档.md`（P2 成果全景 + W1 Core（Scene DB 无 UI）要做什么 + 关键概念/坑 + 实施顺序——接手 P2.4 的同事先读这个）
