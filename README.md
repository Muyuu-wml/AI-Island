# AI Island

AI Island 是一个 Windows 桌面悬浮岛，用于集中查看 Codex、Claude 等 AI CLI 会话，并提供系统资源、音乐和系统代理控制。

## 功能

- **AI Agent 监控**：按 Codex / Claude 分组显示项目、任务状态、计时、PID 和连接状态。
- **系统资源**：显示 CPU、内存和磁盘使用情况。
- **音乐控制**：支持 QQ 音乐和网易云音乐的当前曲目、播放/暂停、上一首、下一首及打开应用。
- **Clash Verge**：读取 Windows 实际系统代理状态，支持开关代理和打开应用。
- **账户余额**：可选读取 `xc.lifesecretary.com` 账户余额，凭据和 token 使用 Windows DPAPI 加密保存。
- **通知与终端**：支持任务完成、等待确认通知，并可尝试返回关联的 Windows Terminal 窗口。
- **外观设置**：支持悬浮位置、监控顺序、增强轮廓、RGB 灯条颜色和动画模式。

## 直接运行

Windows x64 上可直接双击根目录的 `AIIsland.exe`。退出程序请使用托盘菜单。

## 环境要求

- Windows 10 2004 或更高版本（x64）
- 构建：.NET 8 SDK；运行：.NET 8 Desktop Runtime

仓库内存在 `.dotnet/` 时，构建脚本会优先使用它。

## 构建与打包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
./artifacts/app/AIIsland.exe
```

生成可发布的单文件程序：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

打包前请先退出正在运行的 AI Island。首次构建可能需要下载 .NET 运行库。

## 接入 CLI Hook

构建后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Install-Hooks.ps1
```

脚本会为 Codex 和 Claude 合并安装 Hook，并在修改前自动备份现有配置。支持 `-Provider Codex`、`-Provider Claude` 和 `-Uninstall`。安装后请重启 CLI 会话，并按 Codex 的 `/hooks` 提示审核 Hook。

## 配置与数据

配置文件位于 `%LOCALAPPDATA%/AIIsland/settings.json`。余额密码和 token 保存在加密文件中，绑定当前 Windows 用户。会话目录 `%LOCALAPPDATA%/AIIsland/inbox/sessions/` 只保存会话标识、项目目录和进程信息，不保存提示词、回答或历史任务正文。

右键悬浮岛可打开设置、暂停监控或退出程序；按住顶部标题栏可拖动位置。`--inspect` 仅用于开发检查窗口。

## 验证

```powershell
./.dotnet/dotnet.exe run --project AIIsland.SmokeTests -c Release --no-build -- .
./.dotnet/dotnet.exe run --project AIIsland.SmokeTests -c Release --no-build -- . --ui-checks
./.dotnet/dotnet.exe run --project AIIsland.SmokeTests -c Release --no-build -- . --completion-checks
./.dotnet/dotnet.exe run --project AIIsland.SmokeTests -c Release --no-build -- . --balance-checks
```

测试说明见 [`docs/VALIDATION.md`](docs/VALIDATION.md)，产品基线见 [`docs/PRD.md`](docs/PRD.md)。

## 目录结构

| 路径 | 说明 |
| --- | --- |
| `AIIsland/` | WPF 主程序源码 |
| `AIIsland.Hook/` | CLI Hook 桥接程序 |
| `AIIsland.Tests/` | 回归测试 |
| `AIIsland.SmokeTests/` | 集成和 UI 检查 |
| `scripts/` | 构建、打包和 Hook 安装脚本 |
| `docs/` | 产品需求与验收记录 |
| `artifacts/` | 构建产物与截图 |

## 技术边界

应用在本地通过 Hook JSON、事件总线、状态机和 WPF 展示状态，不依赖云服务。提示词、回答和工具参数只在内存中解析，不写入事件或日志。
