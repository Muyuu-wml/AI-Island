# AI Island

Windows 顶部中央的灵动岛：把 **AI 额度与用量**、**AI 会话状态**、**系统监控**和**正在播放的音乐**收进一个面板。

界面与交互参考 [BrimDeck](https://github.com/MoFeng2223/BrimDeck)，核心目标是：只看本机已有的登录与记录，不要求额外注册，不编造数字。

## 当前能力

| 模块 | 状态 | 说明 |
| --- | --- | --- |
| AI 会话 | 可用 | 通过 Hook 接收 Codex / Claude Code / OpenCode 事件，显示进行中、等待确认、完成、失败；点击可定位终端或会话目录 |
| 配额 | 可用 | Claude、Codex、OpenCode Go、DeepSeek、New API 中转站、Sub2API 中转站 |
| 用量 | 可用 | Claude Code、Codex、OpenCode 本机会话记录，汇总今日 / 7 日的 Token 与估算费用 |
| 系统监控 | 可用 | CPU、内存折线图，网络上下行、磁盘可用空间，占用最高的 8 个进程（带应用图标与负载条） |
| 音乐 | 可用 | Windows 系统媒体会话（SMTC）：虚化封面背景、标题、播放进度、播放/上一首/下一首 |
| 分页与动效 | 可用 | 展开后按「会话 / 系统 / 音乐」三页切换，页签可拖动排序并记住顺序，窗口尺寸、淡入与滑动过渡 |
| 外观 | 可用 | Notch / Capsule / Indicator 三种收起形态，六套配色模板（雾蓝 / 樱花 / 薄荷 / 落日 / 葡萄 / 石墨），全屏自动隐藏 |

尚未实现（后续里程碑）：GPU 与温度、歌词、主题文件、中英文切换、自定义脚本来源、安装包与自更新。

## 数据来源

每一列都可以分别选择 **额度来源** 和 **统计来源**。本地用量在后台读取，同一来源的多列共享短期缓存；额度按各列账户分别请求。账户、站点或密钥改变后不复用旧账户缓存，离线数据不触发新预警。

| 来源 | 额度 | 用量 | 需要什么 |
| --- | --- | --- | --- |
| Claude | ✓ | ✓ | 本机登录 Claude Code（`~/.claude`） |
| Codex | ✓ | ✓ | 本机登录 Codex（`~/.codex`） |
| OpenCode | ✓ | ✓ | 本机登录 OpenCode（`~/.local/share/opencode`），或填写 OpenCode Go 密钥 |
| DeepSeek | ✓ | — | DeepSeek 官方 API 密钥（读账户余额，无需站点） |
| New API 中转站 | ✓ | — | 站点地址 + 令牌 |
| Sub2API 中转站 | ✓ | — | 站点地址 + 令牌 |

DeepSeek 官方 API 只有余额接口，没有用量接口，所以这一列只显示余额，不显示「今日 / 7 日」用量（设置页里有说明）。

所以监控的账号 **不限于官方密钥**：

- 官方订阅：读本机登录态，不需要填写任何东西；
- 官方 API 密钥：在设置里填写（DPAPI 加密）；
- 第三方中转：New API / Sub2API 两种协议；
- 其他：后续会加自定义脚本。

### 桌面端能不能监控

- **OpenCode**：可以。CLI 与桌面版共用 `~/.local/share/opencode`，全局插件目录 `~/.config/opencode/plugins` 会在启动时自动加载。设置里的「安装 Hook」会写入插件。
- **Codex**：可以安装，但桌面版有已知缺口。Codex 读取同一份 `~/.codex/hooks.json`，`SessionStart` / `UserPromptSubmit` / `Stop` / `PermissionRequest` 通常可用；Windows 上 `command_execution` 的 `PreToolUse` 可能不触发（openai/codex#24453），因此「等待确认」以 `PermissionRequest` 为准。
- **Claude Code**：Hook 写入 `~/.claude/settings.json`，额度与用量读取 Claude Code 的登录与本机会话记录。Claude 桌面应用是否共用这份登录取决于版本，当前不单独适配。
- **定位来源**：CLI 会话根据 Hook 捕获的终端 PID 聚焦窗口；桌面版没有终端时退化为打开会话目录。

## 隐私

- 只读取本机已有登录状态和会话记录；不续期、不修改任何登录。
- 登录令牌只在请求期间保存在内存；手动填写的密钥使用 Windows DPAPI 加密，保存在 `%LOCALAPPDATA%\AIIsland\secrets.dat`。
- 访问网络只用于读取额度；不读取、不上传对话内容。
- 日志只记录异常类型与消息，不记录提示词、工具参数或返回内容。

数据目录：`%LOCALAPPDATA%\AIIsland`

## 构建与运行

需要 Windows 10 2004+ / Windows 11 与 .NET 8 SDK。项目自带 `.dotnet` 时脚本会优先使用它。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
.\artifacts\app\AIIsland.exe
```

首次运行后打开托盘 → 设置 → **安装 / 修复 Hook**，然后重启对应的 AI 工具。

打包（单文件、自带运行时）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

## 结构

```
src/AIIsland.Core/     纯逻辑：来源、定价、会话状态机、系统监控
src/AIIsland/          WPF 界面、托盘、设置、音乐
src/AIIsland.Hook/     CLI Hook 桥接，写入共享收件箱
tests/AIIsland.Tests/  无依赖的控制台测试
integrations/opencode/ OpenCode 全局插件
scripts/               构建与打包
```

Hook 事件统一写入 `%LOCALAPPDATA%\AIIsland\inbox`，主程序轮询并折叠成会话状态。普通事件只监控状态；Claude 的权限请求可在岛内逐项批准或拒绝，最多等待 60 秒，超时显示“请在终端确认”。并行请求独立处理，没有工具 ID 的请求不会因其他工具完成而取消。关闭会话监控或退出岛后，等待中的 Hook 自动交还终端。Codex / OpenCode 目前仅监控状态。

测试使用独立临时目录，结束后自动删除，不改写真实设置和密钥文件。
