# 下一位 AI 接手说明（用户要求停止，当前工作快照）

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
