# Windows 安装包

安装包由 Inno Setup 7.1.1 编译，默认为当前用户安装到 `%LOCALAPPDATA%\Programs\CodexTrafficMonitor`，提供开始菜单、可选桌面快捷方式和 Windows 卸载入口。安装程序不修改代理配置，不删除 `%LOCALAPPDATA%\CodexTrafficMonitor` 中的历史记录。

在 Windows PowerShell 7 中运行：

```powershell
./build.ps1 -OutputDirectory ./output/release
$compiler = ./tools/ensure-inno-setup.ps1
./build-installer.ps1 -PayloadDirectory ./output/release -InnoCompiler $compiler
```

`ensure-inno-setup.ps1` 从 [Inno Setup 官方发布](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_1)下载固定版本，核验 SHA-256 和 `Pyrsys B.V.` 的 Authenticode 签名，然后以官方便携模式解包到项目 `.tools`，不做系统全局安装。也可直接通过 `-InnoCompiler` 指定已安装的 Inno Setup 7.1.1 或更新版。

安装器只打包发布目录中的 `CodexTrafficMonitor.exe`、`README.md`、`LICENSE` 和 `THIRD-PARTY-NOTICES.txt`，不会收集用户数据或本机验证报告。

升级前由临时目录中的新程序执行 `--maintenance prepare`，正常保存并退出同用户的监控实例。旧版不支持安全退出时，安装器提示用户从监控设置或托盘退出后重试。安装器不会强制终止进程，也不使用 Windows Restart Manager 结束其他应用。

文件更新后由安装目录中的程序执行 `--maintenance complete`，迁移此前已开启且确属本程序的登录任务。UAC 取消或迁移失败会明确提示；静默安装以退出码 `10` 报告“文件安装完成但自动监控迁移未完成”。卸载前执行 `--maintenance uninstall`，只处理本安装路径对应的监控实例和自有任务。

可单独验证包装生命周期：

```powershell
./build-installer.ps1 -PayloadDirectory ./output/release -IsolatedTest
./tests/installer-isolation.ps1 -InstallerPath ./output/release-installer/CodexTrafficMonitor-Setup-<版本>-win-x64-isolated-test.exe
```

隔离构建使用独立 AppId、测试安装目录和快捷方式名称，并在编译期禁用维护命令及应用启动。该检查覆盖安装、升级、卸载、文件保留和现有用户环境不变，不代替维护命令与正式安装的集成验证。带 `-isolated-test` 的文件仅供测试，不应上传为发行附件。
