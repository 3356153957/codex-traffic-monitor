本测试只用于诊断，不属于普通 Codex 流量记录。

`network-fixture/` 是无第三方依赖的 .NET 8 控制端。客户端的 apphost 有意叫 `codex.exe`，因此真实成品会经过原有进程识别、ETW 接收、方向判断和流量归集路径。服务端由 `dotnet.exe` 执行，默认不计入核心进程。脚本发现已有 `CodexTrafficMonitor` 进程时会拒绝测试，避免其他正常记录混入测试流量。测试结束后两个 fixture 都会退出。

每种协议 TCP4、UDP4、TCP6、UDP6 分别上行 262144 字节、下行 131072 字节，远端只用 127.0.0.1 或 ::1。TCP 无额外控制报文。UDP 逐包发送 4096 字节，等待带同一序号的 2048 字节响应后才发下一包，共 64 轮；3 秒收不到响应即失败，不用重试改变预期字节。IPv6 若被操作系统明确禁用则记录跳过，其他权限错误不会被当成通过。

先运行 `-PrepareOnly` 可只编译，不启动任何测试进程或 UAC。带 `-FixtureOnly` 只核对 socket 的收发字节，不声称验证了 ETW。实际计量测试需关闭其他监控实例，在已提升的 PowerShell 中运行以下命令，脚本本身不会请求 UAC。

```powershell
& '.\tests\run-network-check.ps1' -StartCapture -MonitorExe '.\dist\CodexTrafficMonitor.exe'
```

`-StartCapture` 只接受已提升的脚本环境，随后直接启动成品，不产生第二次 UAC。省略它时，脚本写出 `diagnostic-plan.json` 并等待外部单独启动采集。脚本从 `probe-data/sessions/` 发现会话元数据后再等 2 秒，随后发送测试流量。固定 35 秒 probe 结束后，它按客户端 PID、协议、本地端口及远端地址和端口检查 JSONL 流量行的上行、下行字节。若恰好反向，会在 `network-check.json` 明确给出 `DirectionReversed`。

全部会话、fixture、日志及报告放到唯一的 `tests/runs/<时间与随机值>/` 下，不写正常 LocalAppData 历史，也不修改代理、TUN、防火墙或进程排除逻辑。权限错误（例如 Windows 10013）、连接失败和接收超时均须保留在失败报告中；仅完成 socket 收发检查不能证明 ETW 方向和字节计量正确，真实结果以完整采集后产生的 `network-check.json` 为准。

可显式传 `-IPv4Only` 单独验证 IPv4 的真实计量。默认双栈测试不会自动忽略 IPv6 权限错误或超时；操作系统明确禁用 IPv6 时才记录跳过。IPv4 专项报告写出 `IPv4Only=true`，不能作为 IPv6 已验证的依据。`client-progress.json` 每组更新，任何后续组失败时，`client-result.json` 都会保留已经完成的组和当前失败组名。
