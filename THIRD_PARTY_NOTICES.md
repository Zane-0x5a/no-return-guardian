# 第三方组件

发布包（安装程序与便携 zip）包含以下第三方组件。许可证全文随包放在 `licenses\` 目录；
Python 与 Frida 的许可证还保留在各自的位置（见下表）。

| 组件 | 版本 | 用途 | 许可证 | 随包位置 |
| --- | --- | --- | --- | --- |
| Python（Windows 嵌入式包） | 3.13.7 | 运行游戏内恢复脚本 | Python Software Foundation License 2.0（含 OpenSSL、libffi、SQLite 等随附组件的条款） | `native-recovery\python\LICENSE.txt` |
| Frida | 17.17.0 | 游戏内恢复挂接游戏进程 | wxWindows Library Licence 3.1 | `native-recovery\artifacts\native-probe-deps\frida-17.17.0.dist-info\licenses\COPYING` |
| Microsoft Edge WebView2 SDK | 1.0.4258.31 | 显示界面（程序集与加载器编进 exe） | BSD 3-Clause 风格（Microsoft） | `licenses\WebView2-LICENSE.txt`、`licenses\WebView2-NOTICE.txt` |
| React、React DOM | 19.3.0 | 界面 | MIT | `licenses\react-LICENSE.txt`、`licenses\react-dom-LICENSE.txt` |
| scheduler | 0.28.0 | React 依赖 | MIT | `licenses\scheduler-LICENSE.txt` |
| Paper Shaders（`@paper-design/shaders`、`@paper-design/shaders-react`） | 0.0.81 | 界面背景色场 | Apache License 2.0 | `licenses\paper-shaders-LICENSE.txt`、`licenses\paper-shaders-NOTICE.txt` |

构建工具（不进发布包）：Inno Setup 6 生成安装程序，其简体中文界面文字来自
[Inno-Setup-Chinese-Simplified-Translation](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation)
（`installer\ChineseSimplified.isl`）；Vite、TypeScript 等开发依赖见 `src\NoReturnGuardian.Web\package.json`。

Python 与 Frida 在构建时从 python.org 和 PyPI 下载，按 `scripts\build-tools.ps1` 里固定的 SHA-256 核对后才使用。
