# UI v2 接手说明与修复记录

## 2026-09-24 接手修复结果（Claude，分支 `claude/customs-console-review-optimize-t4epeg`）

下文“原交接快照”中的**唯一已知阻断已本地复现、定位根因并修复**，另找出并修复了同一路径上 4 个此前未发现的缺陷（其中 1 个会保存空白错误截图），Windows 云端完整门禁在多个提交及同 SHA 重跑上通过；同时对全项目做了稳定性/性能审查。真实官网人工验证码全流程、Windows 10、125/150/200% 实际显示环境**仍未实测**（见文末）。

### 如何复现（此前从未在本地复现过）

新增 `tests/BrowserE2E.Portable`：不改产品代码，在 Linux/macOS 上用任意 Chromium 直接运行生产 `BrowserCaptureE2E`/`BrowserValidation`，可重复单个场景（用法见其 README）。云端间歇问题都与时序有关，**必须在 CPU 满载下**循环运行才能稳定复现。

| 代码 | `oopif-viewport-read-failure` 满载循环结果（Chromium 141 headless） |
| --- | --- |
| 2426e12（原交接前基线） | 15 次失败 4 次：1 次与云端相同的“快速重试第 2 次 120 秒无结论”，3 次**保存成功但 PNG 内跨进程 iframe 空白/截断** |
| 2f2f4a6（原交接未验证补丁） | 15 次失败 2 次：点击被当成“非目标页”忽略、OOPIF 下半截空白 |
| 本分支最终代码 | 见下方“验证证据” |

### 已证实的根因与修复（均在 `BrowserValidation.cs` / `BrowserScripts/*.js` / `BrowserCaptureE2E.cs`）

1. **建连时帧树快照覆盖了更新的导航**：`Page.getFrameTree` 常在首个提交前返回 `url=""`，它在 `ConnectAsync` 中无条件写回 `_currentUrl`，覆盖已先到达的 `frameNavigated`；随后卡片点击被判为“当前页面不是核验目标页”并忽略（日志 `context=2 · url=`），测试侧即“点击后无完成事件”。修复：快照只补齐未知值。
2. **OOPIF 地址长期未知**：约 60% 的 OOPIF 附加时 URL 为空，子会话提交可能早于 `Page.enable`。修复：授权前按需从根/子会话帧树补齐未知 URL；被拒绝的绑定先刷新一次再判定。
3. **OOPIF 占位地址 `":"` 被当成已知地址**：根会话对已迁出进程的 frame 报告 `":"`，既非空也非 `about:`，于是被判为“已知且未授权”——该文档不安装点击监视器、未知地址刷新也跳过它，且 `ExpandFramesAsync` 会用它覆盖真实地址。修复：只有可解析的绝对 URL 才算已知（仪表化复现：修前 60 个会话中 5 个 OOPIF 上下文既未授权也非待定，修后 0/40）。
4. **监视器绑在已丢弃的文档上**：iframe 初始 `about:blank` 被同源文档替换时 **Window 对象被复用**，`monitor.js` 只看 `window.__customsConsoleMonitor`，于是监视器仍挂在旧文档，真实“查询”点击无人监听（身份停在 `waiting|not-started`，生产中表现为“查询结果尚未确认”拒绝截图）。修复：监视器只在绑定当前 document 时才算已安装。
5. **保存了空白 OOPIF 截图（产品缺陷）**：放大视口后固定等 200ms 即截图；负载下跨进程 iframe 未重绘，下半截被保存成空白。修复：(a) 有界等待页面报告放大后的视口、每个 OOPIF 用 IntersectionObserver 报告自身完全可见、再过两帧；(b) 拼图后对每个 OOPIF 底部区域重新截取比对，拼图区域为纯色而新截图有内容即重新截取（最多 3 次），仍为空白则拒绝保存。满载下 (b) 实际触发并纠正了数十次绘制滞后，未再出现空白截图。
6. **真实点击被浏览器路由到别处**：页面内精确命中只证明布局，浏览器按合成器命中数据路由输入。E2E 点击改为先移动真实鼠标，确认按钮收到 `pointermove` 后才按下（仍只发送一次按下/抬起，不自动补点、不调用内部 requestCapture、不放宽断言）。
7. 其他：`capture-restore.js` 用错误键名比较，原内联 max-height 存在时 `stylesRestored` 恒为 false；卡片不再把保存目录当 HTML 解析；拒绝与帧诊断输出 frame/URL/monitor 绑定/queryAt；CDP 单命令 90 秒上限，挂死的渲染进程不再永久占住截图锁。

原交接三文件补丁（就绪闸门、closed-shadow 精确命中、计数器、`LastCompletionReadiness`）已审查、编译、运行并保留；其中列出的三项早期问题（伪 exact hit、ready=false 仍点击、恢复 false 被忽略）在最终代码中均已闭合。gate 完成时序断言保留。

### 全项目审查修复（稳定性/性能）

- 核验浏览器：用户关闭浏览器后释放会话（不再反复重连并提示“正在恢复”）；每个会话的一次性浏览器配置目录在释放时删除，崩溃遗留的 12 小时后清理（此前每次核验永久遗留数十 MB）。
- `history.json` 先落盘再原子替换；无法读取时先备份为 `history.json.corrupt-*` 再重置，避免下次保存静默覆盖。
- Excel 导出剔除 XML 1.0 非法字符（PDF 文字层控制码）——此前任一字段含控制码即整批导出失败。
- 根启动器按 Windows 命令行规则转义参数（以 `\` 结尾的目录参数不再吞掉后续参数）。
- 界面：图标解码缓存（表格每格每次重绘不再读盘解码 PNG）；行样式共享（每次重建不再为每格新建样式）；修复绘制路径中的字体/画笔/Region 泄漏；拖拽悬停不再每次鼠标移动都对所有文件 `File.Exists`。
- 解析：`Regex.CacheSize` 提升至 64（约 30 个静态模式在逐 token 循环中反复被逐出重建）；Tesseract TSV 坐标按固定区域性解析。
- `app.log` 5 MB 轮转；启动时清理超过 1 天的 OCR 临时目录；记录未观察的任务异常。
- 核心回归新增 3 项（控制字符导出、代理对保留、损坏历史备份）：`CORE_REGRESSION_OK: 72 checks`。

### 验证证据

**Windows 云端（`windows-ui-v2.yml`，每次均读取原始日志：`BROWSER_E2E · 30/30`，最终 `Enforce validation gate` success）**

| 产品提交 | 运行 | 结果 |
| --- | --- | --- |
| 9d3df1c | [35980787871](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35980787871) | 全绿 |
| d840915 | [35983003808](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35983003808) attempt 1、同 SHA 重跑 attempt 2 | 两次均全绿 |
| **671a2f6（最终产品代码）** | [35985826818](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35985826818) attempt 1、同 SHA 重跑 attempt 2 | 两次均全绿 |

之后的提交只改文档。

**本地满载复现（`tests/BrowserE2E.Portable`，Linux Chromium 141，CPU 约 3 倍超额占用）**

| 代码 | 结果 |
| --- | --- |
| d840915 | 完整 30 场景 3 轮均 30/30；`oopif-viewport-read-failure` 77/80，其余 3 次即根因 3，于 671a2f6 修复（仪表化复现 40/40） |
| 671a2f6 | `oopif-viewport-read-failure` 60/60；完整 30 场景 2 轮均 30/30。截图后绘制复核触发 37 次，其中 1 次为 `Unpainted`（旧代码会保存空白截图），均已重截纠正 |

另：核心回归 `CORE_REGRESSION_OK: 72 checks`；主程序在 Linux 上以 `EnableWindowsTargeting` 编译 0 错误。

### 仍未验证的边界（不能表述为已通过）

真实 singlewindow 人工验证码与真实关单全流程、Windows 10、125/150/200% 实际显示缩放、真实 OCR 样本回归（CI 无样本）均未实测。云端为 Windows Server 2025、96 DPI、Edge；本地复现为 Linux Chromium。

---

# 原交接快照（用户要求停止时，保留作历史）

2026-09-24。用户要求停止本任务、将当前版本上传 GitHub 并说明问题，由其他 AI 修复。实现进程和独立验收均已停止。**当前版本未最终验收，不可将本次上传当成可发布版本。** PR 保持 Draft，不合并 main，不发布 Release。

## 接手入口

- 分支：`codex/ui-v2-iteration`；PR：https://github.com/Leon1ca/customs-clearance-console/pull/1
- 需求：[PRD.md](PRD.md)；设计：仓库 `designs/2026-09-23-enterprise-ui-v2/`。
- 独立验收：[FINAL-ACCEPTANCE.md](FINAL-ACCEPTANCE.md)，目前暂缓；详细定位：[CI-TRIAGE.md](CI-TRIAGE.md)。这些文档中的历史通过结论只绑定其注明的旧 SHA。
- 本次停止时，最新已提交基线为 `ccbff9936d26b574a24a21bd9bd9cd227321d6be`。本交接提交还包含 DeepSeek 尚未编译/测试的三个文件补丁，见下一节。**本交接使用 `[skip ci]`，按用户停止要求不启动新测试；首次接手须主动云端验证。**
- 所有产品代码由 DeepSeek 经 DeepSeek Harness 实施，主路由负责 PRD/编排与本交接，另一子路由独立验收。

## 已实现内容及边界

已实现 UI v2、默认浏览器优先且必要时回退 Edge/Chrome、网页手动长截图、关单明细、Excel/Markdown 分开导出。先前固定提交的 UI、导出、字体/许可、核心回归、便携包/根启动器均有云端证据；原生四种尺寸及完整 OOPIF 长图已检查。**这些是旧提交验证事实，不是对当前未测补丁的批准。**

真实 singlewindow 人工验证码/真实关单完整流程、Windows 10、125/150/200% 实际显示环境均未实测；受控网页通过不能冒充生产网站验收。云端测试为 Windows Server 2025、96 DPI。

## 唯一已知未关闭阻断：失败后真实单次点击偶发不启动截图

场景 `oopif-viewport-read-failure`：向跨进程 iframe 截图注入视口读取失败，首次应拒绝且不生成 PNG；随后在同一会话真实鼠标点击应恢复保存，并连续两次立即重试，共产生三张完整 PNG。重复云端运行出现恢复点击或第二次快速重试等待 120 秒没有生产完成事件。

已证实并修复的竞态（2426e12）：旧版先启用网页按钮/发 CaptureCompleted，外层 finally 后释放 `_captureGate`，可能吞掉新的请求。修后由 CaptureAndReportAsync 唯一 owner 在页面恢复后释放 gate，再报告/发事件，移除了旧外层二次释放。保留这项修复及同步完成事件 gate 断言。

修后仍复现：2426e12 日志有错误、恢复保存、快速重试1保存的接受/释放记录，之后既没有新接受，也没有忙拒绝；仅两张 PNG。2336f69 在初次恢复点击就失败，旧诊断只显示命中 `host`。**仅命中卡片容器、CDP 输入命令成功返回均不能证明内部按钮收到点击。输入合成/绘制恢复时序只是待验证假设，不是已证实根因。**

## 云端记录（请准确区分）

| 提交 | 运行 | 结果及解释 |
| --- | --- | --- |
| a2ddbb2 | [35968581532](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35968581532) | 首轮全绿：核心69、浏览器30/30、构建/原生UI/导出/打包与根启动器；后续重复运行暴露间歇问题，不能据此宣布完成 |
| 57134d0（相对 a2 仅文档） | [35969514354](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35969514354) | 浏览器29/30，恢复点击超时 |
| 2426e12 | [35971352756](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35971352756) | gate 修后仍29/30，快速重试2超时 |
| 2336f69 | [35972919550](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35972919550) | 29/30，初次恢复点击超时；诊断遗漏该分支 |
| ccbff99 | [35974063743](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35974063743) | 运行绿色，但测试自动最多补点3次，独立验收不接受它证明单次点击可靠；当前未测补丁已移除此做法 |
| 本次交接快照 | 未运行 | DeepSeek 被用户要求中止，新增补丁未编译/执行/最终独立复核 |

## 随本次上传的未验证补丁

仅三个产品/测试文件，完整保留 DeepSeek 停止时内容，没有由主路由继续修代码：

1. `src/CustomsClearanceConsole/BrowserScripts/widget.js`：闭包维护 DOM click/binding 调用计数；通过已有 closed shadow 的 `shadow.elementFromPoint` 检查实际按钮或子元素；新增包含状态、矩形、视口、DPR 的诊断；有截止时间、连续两帧矩形稳定的 `whenInteractive`。
2. `src/CustomsClearanceConsole/BrowserValidation.cs`：`ClickCaptureButtonAsync` 就绪/精确命中检查，失败不发点击；`CaptureAndReportAsync` 记录独立的 `LastCompletionReadiness`，与文件保存事实分开；新增相关解析/诊断。关键入口在上述方法及 `WaitForWidgetInteractiveAsync`。
3. `src/CustomsClearanceConsole/BrowserCaptureE2E.cs`：移除自动补点，保持单次真实 mouse move/down/up；失败/超时分支增加诊断；恢复及快速重试断言包含就绪结果。重点入口 `ClickAndAwaitContractAsync`、`RunOopifViewportReadFailureAsync`。

独立路由在较早草稿发现的三项问题（伪exact hit、ready=false仍点击、恢复false被忽略）已反馈，DeepSeek随后编辑了对应路径，**停止前尚未对最终差异完成复审或云端验证，不能认定这三项已经验收关闭**。

## 建议接手顺序

1. 阅读本交接、PRD、最新三个文件 diff，静态检查未完成补丁的编译/逻辑问题。注意 closed shadow 不能用 host.shadowRoot；检查错误诊断是否准确、计数基线无效是否会误判；渲染探测本身不证明合成器已提交。
2. 在 GitHub Actions 手动运行 `.github/workflows/windows-ui-v2.yml`，ref 选本分支（仓库已有 workflow_dispatch）；本机不安装 .NET、不编译运行产品。Windows 云端已有完整工作流。
3. 失败时用按钮实际 DOM click、binding 计数差、backend 接受/忙拒绝日志与真实屏幕定位。不要用盲等待、自动重复点击、直接调用内部 requestCapture、削弱断言或挑选绿色运行掩盖问题。
4. 修后要求失败无PNG、样式/滚动/viewport/DPR恢复、单次真实点击恢复、两次快速重试及3张完整首尾PNG；保留gate完成时序断言。针对同一产品SHA复验稳定性。
5. 完整云端 gate 通过后，再独立审源码、截图/导出、根启动器与正式ZIP。注意工作流部分步骤 continue-on-error，必须看最终 Enforce validation gate 和原始日志。

Actions 产物包含 `ui-v2-validation-evidence`（日志/JSON/PNG/导出）和便携包；旧绿色产物不是本交接版本。不要从旧包发布本次未经验证的快照。

本机可选参考（接手者不在此机时可直接用 GitHub）：工作目录 `/Users/leon1ca/Documents/Codex/2026-09-24/customs-clearance-v2`；旧证据 `evidence-a2ddbb2`、`evidence-2426e12` 位于其上级目录；规划文件在同级 `planning`。凭据、dsh 私有会话日志及真实关单不上传仓库。
