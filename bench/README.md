# bench/ 台架脚本

这些脚本是开发期用来证明"改动真的生效、而且没写坏东西"的，不参与构建，也不随程序发布。

**只在作者这台机器上可直接跑**：脚本里写死了仓库路径 `E:\ClaudeCode\EWeLinkLinker`、Windows 服务名 `EWeLinkLinker`（注意不是 `EWeLinkLinker.Service`，写错会让 `Stop-Service` 当场抛异常、脚本走回滚分支，而服务其实一秒没停）、以及工作目录 `%TEMP%\ewl_gui`。换机器要改的是每个脚本头部那三四个变量。

## GUI 回归臂：`gui_arms.ps1`

```powershell
pwsh -NoProfile -File bench/gui_arms.ps1 -Only ''    # 全部臂
pwsh -NoProfile -File bench/gui_arms.ps1 -Only J     # 单条臂
```

它做什么：把装在 `publish\ConfigApp` 里的那份程序复制进 `%TEMP%\ewl_gui\app`，用**假设备 + 空凭据**的沙盒配置启动 ConfigApp，用 UI Automation 点控件，然后从程序自己写的 `config\debug.log` 和落盘的 `linker.json` 里取判据——结论只允许来自程序自己的输出，不看截图猜。

- 报告落在 `%TEMP%\ewl_gui\gui_arms_ALL.txt`：每行一条 `CHECK[PASS] / CHECK[FAIL]`，最后一行 `FAILED_CHECKS=n`。**认这一行，不要只看退出码。**
- 臂清单：A 通道候选按设备通道数生成 / B 换到通道更少的设备（通道要显式收回并留日志）/ C 换到通道更多的设备（原通道要保住）/ D 越界通道拦住保存 / E 条件类型切换清掉残留参数 / F 比较符候选按类型收窄 / G1-G2 开关机睡眠唤醒这类电源事件条件 / H 点「刷新IP」（设备表整个换掉的那一瞬间）/ I1-I3 设备格空着时启用、停用、直接关窗三种情形 / J 收回之后屏幕上的显示 + 收回后再手动挑通道 / K 通道数相同的两台设备互换。
- 四条硬规矩：① 一条臂一个独立进程（否则上一臂的窗口和弹层会骗到下一臂）；② 沙盒配置的凭据字段全是空串，**绝不拿真配置跑台架**；③ 每批判据里必须至少有一条"如果修坏了它就会红"的臂，单向绿不算验过；④ 每条臂启动后先把窗口挪到 `(30,30)`——这台机器上偶发有应用盖住右半屏（实测 `x>=1281` 被一个 Pane 名为 `FLUTTERVIEW` 的窗口挡住过），合成鼠标点击会被它吃掉，症状是"点了没弹出任何东西"加上"像素判据读到别人家的字"，看着像产品坏了，其实是手没伸进去。
- J/K 为什么用像素判据：收起状态的 WPF ComboBox 在 UIA 里是"0 个后代 + 空 Name"，显示的文字根本读不出来。数截图片段的深色像素才能判"那一格是不是空白"——这条判据抓到过一个真缺陷：换到通道更少的设备后，模型和盘上都是 CH0，屏幕上那一格却是空的（深色像素 37 → 0，修好 → 47）。
- 弹层项必须**只认被测进程**：从桌面根 `FindAll(ListItem)` 会抓到别的窗口的列表项，于是"点完弹窗还有 N 项残留"是假的，补发的那次 ESC 反而把选中项改成了列表第一项。

## 配套脚本

| 脚本 | 用途 |
|---|---|
| `copy_app.ps1 -Src <目录> [-Label x]` | 把指定产物复制进沙盒，逐文件断言源与目标 sha 相同，并打印版本戳。防的是"台架跑的其实是旧二进制" |
| `cfg_diff.ps1` | 两份 `linker.json` 的语义比对：DPAPI 字段先还原明文再比（每次加密的密文都不同，逐字节比永远不命中），只打印形状不打印值；解不开时报 UNVERIFIED 而不是假报相同 |
| `runtime_baseline.ps1 -Tag x [-WaitUntil HH:MM]` | 服务运行时基线：私有内存／句柄（PID 走 `Get-CimInstance Win32_Service`，`sc.exe query` 的中文输出取不到）、`[轮询] → [RuleTrigger]` 判定延迟的中位与 p95、`轮询周期耗时过长` 条数、ERROR/WARN 条数 |
| `final_run.ps1` | 一条命令串起"复制产物 → 跑全臂 → 打印报告头尾"，避免把产物的指纹和判据的归属搞混 |
| `verify_installed.ps1` | 独立复核：盘上版本戳、服务状态、`linker.json` 哈希、有没有残留的旧包 dll |
| `closeout_scan.ps1` | 收尾扫描：还有什么进程在跑、`%TEMP%` 里有没有配置副本、仓库与远端是否同步 |

## 部署：`deploy.ps1` 与 `deploy_cfgapp.ps1`

两者都是 `-RunId <标识> -ExpectSha <7位commit>`，流程统一为：暂存目录构建（服务照常跑）→ 逐文件验过的回滚副本 → 复制 → 事后 CHECK → 任一 CHECK 不过就自动回滚。

- `deploy.ps1`：全量部署，会**停服务**（实测停机约 0.8–0.9 秒），换 `publish\Service` + `publish\ConfigApp`，起服务后等 45 秒收集新进程的轮询轮数，再判 10 项 CHECK。
- `deploy_cfgapp.ps1`：只换 ConfigApp、**不停服务**。它允不允许不停服务是按 **git 源码差**判的：`src/EWeLinkLinker.Core` 与 `src/EWeLinkLinker.Service` 在「已装的 commit..HEAD」之间 0 改动、且没有未提交改动，才继续；否则 `exit 6`，一个文件都不复制。
  不能用二进制字节判——任何一次重编译都会改掉 PE 时间戳、MVID 和版本资源里的 commit 串，实测源码一字不变也会差 145 个字节。
- 两个脚本都要管理员权限（`publish` 目录的 ACL 收紧过，Users 只剩读+执行）。提权方式：`Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','<脚本>','-RunId','<id>','-ExpectSha','<sha>'`。这台机器的 UAC 是静默放行，不会弹窗，所以"等等你看到弹窗点【是】"这种话在这里不成立。
- 判定"这次部署成功了"只认 transcript 里的 `CHECK xxx = True` 和最后一行 `DEPLOY DONE`。transcript 落在 `%TEMP%\ewl_hold\run\`，**起脚本前先删同名旧文件**，读的时候按本次 RunId 过滤——否则上一轮遗留的 `DEPLOY DONE`／`ROLLBACK DONE` 会被当成本次的结论。
- 回滚副本验过即删，不留。
