# 开发说明

## 工作原理

- **战备快照**：从存档目录读取 `gamedata` 里的五个工作文件（`R0A.save`、`R0A.save-backup`、`0P.save`、`0P.save-backup`、`nr.bin`），
  在守护器自己的目录里按游戏的退出导出格式重建 `SAVEFILER0A` 兼容槽位（CRC32C 与尾部），并反向逐字节核对；整份快照带 SHA-256 清单。
  手动保护要求状态字已识别、双镜像一致、写批同代并两次稳定探测。游戏把工作文件全部收起、只留原生导出时，走独立的还原路径（schema 6）。
- **出发自动保存**：玩家在兵营时，守护器把游戏写好的每份兵营存档暂存一份；在游戏里观察到路线板出发（`player-next-task`）时，
  把出发前的最后一份发布为“出发前战备”，并记下这次出发的路线，供“重开战斗”重放。
- **游戏内恢复**：`native-recovery\` 里的 Python 脚本通过 Frida 临时挂接游戏进程。结算页的“继续”在游戏命令层代答，
  菜单和战备核验后经游戏自己的载入链载回战备，再把十项征途统计改回战备时的值。挂接前核对每个用到的代码地址与验证过的
  `tlou-ii.exe` 逐字节一致（`NativeCodeSignature.py`），完成后撤销挂接并复核原代码。
- **文件恢复**：只在游戏完整退出后进行，按事务写回：兼容槽位先写，工作文件其次，`nr.bin` 最后；写前留下现场，失败回滚。
- **更新**：游戏没运行时每天一次 HEAD 请求 `releases/latest`，只读跳转地址里的 `vX.Y.Z`。玩家点“更新”后，
  从这一版的发布下载安装程序和 `SHA256SUMS.txt`，核对校验和与文件版本号，在游戏没运行时以 `/SILENT /RELAUNCH` 运行；
  安装程序用 `--exit` 请守护器退出，装好后用 `--minimized` 重新打开它。便携版只打开下载页。
- **界面**：WebView2 承载 `src/NoReturnGuardian.Web` 构建出的单文件页面，背景是 Paper Shaders 绘制的色场。
  图标由 `scripts/make-icons.py` 从同一个色场生成。

## 构建

需要：

- Visual Studio 2022 或 Build Tools，选“.NET 桌面开发”（MSBuild、Roslyn 和 .NET Framework 4.8）。
- Node.js 20.19 以上（构建界面）。
- Python 3.13（只用于跑测试；发布包自带自己的 Python），再装测试依赖：`python -m pip install -r scripts\requirements-test.txt`。
- Inno Setup 6（只用于生成安装程序）：`winget install JRSoftware.InnoSetup --scope user`。
- 首次构建原生组件时联网：从 python.org 下载 Python 嵌入式包、从 PyPI 下载 Frida，按 `scripts\build-tools.ps1` 里固定的 SHA-256 核对，缓存在 `build-cache\`。

```powershell
.\scripts\build.ps1 -IncludeNativeRecovery   # 构建到 dist\NoReturnGuardian（不加开关则不含游戏内恢复）
.\scripts\test-all.ps1                       # 全部测试：C#、Python、Node 和原生恢复夹具
.\scripts\package.ps1                        # 在全新目录构建、跑全部测试，生成 out\ 下的安装程序、zip 和校验和
.\scripts\test-installer.ps1                 # 安装程序冒烟测试（CI 里跑；会真的启动、退出守护器）
```

界面与 WebView2 组件都编进了 `NoReturnGuardian.exe`；游戏内恢复组件在它旁边的 `native-recovery\`。

## 测试与界面检查

部分测试重放维护者本机的实机记录（`artifacts\`，含个人存档，不在仓库里），没有这些文件时自动跳过；其余测试都用合成数据。
`test-all.ps1` 最后列出跳过了哪些、为什么；加 `-RequireEvidence` 时有任何跳过都算失败。开发时的辅助程序和 Frida 构建到 `tools\`。

界面开发：在 `src\NoReturnGuardian.Web` 里 `npm run dev`，浏览器打开 `http://127.0.0.1:5178/?mock&scene=guarding`
（`scene` 可以是 `guarding`、`observing`、`idle`、`alert`、`empty`、`noprofile`、`blocked`；`&update=1` 模拟有新版本）。
`.\scripts\render-ui.ps1 -Name 名称` 用真实宿主和本机状态截图，加 `-Scene` 截模拟场景。截图模式只读，不执行页面发出的命令。

## 发布新版本

1. 改 `src\NoReturnGuardian.App\Properties\AssemblyInfo.cs` 里的版本号（安装包、zip 和标签都从这里取）。
2. 推送 `v<版本>` 标签。GitHub Actions 会构建、测试、装一遍安装程序（安装、升级、更新后重新打开、卸载），
   并建一个附带安装程序、zip 和校验和的草稿发布，确认后手动发布。发布之后，已安装的守护器才会提示更新。
