# 关单核验台：功能代码审查与精简建议

审查日期：2026-09-09。基线：`Leon1ca/customs-clearance-console`，`main @ cc1ae62`。当前程序集与 README 均为 1.4.0；历史 v1.7 目录不代表当前发布版本。

**结论：可以精简和整理，但应先修复结果可靠性与批次状态问题，再调整模块边界。** 现有 OCR、字段解析、逐项复核、导出、清理已具有基本职责分离，不需要重写整套系统。

本次交付为界面设计提案、原型和代码审查；没有修改生产 C#、发布脚本或真实业务数据，没有生成新的 Windows 可执行文件。

## 审查范围与证据等级

- 阅读了主窗口、主题控件、目录/核验/清理弹窗、批次扫描、文本提取、字段解析与复核、浏览器核验、状态存储、Markdown 导出、现有自测及相关架构文档。
- 主程序目录有 **21 个直接所属 C# 文件，共 5,040 行**（含 334 行 SelfTest）；另有 9 个 `RapidOcrSource` 第三方源码文件。未将第三方源码纳入行数精简目标。
- **已执行验证**：从当前 C# 中提取的网页监测 JavaScript，在本地合成页面中执行了 3 个场景；原型的关键流程和桌面/窄屏布局进行了浏览器检查。
- **静态确认**：其余 C# 问题通过调用链和分支条件确认，给出可复现条件；未声称在 Windows 上运行复现。
- 当前电脑是 macOS，未发现 .NET SDK，缺少 Windows 原生 OCR 依赖。未运行 WinForms、自测入口、真实关单 OCR 或真实网站端到端测试。网页合成测试不能代替官方网站适配测试。

以下源码链接固定到审查提交。P1 表示建议在下一版发布前优先处理；P2 表示可靠性或维护问题，应安排修复。

## 发现的问题

### R1 · P1：完整表格捷径会绕过已存在的金额冲突

位置：[DeclarationReconciler.cs:98](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/DeclarationReconciler.cs#L98)、[160](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/DeclarationReconciler.cs#L160)、[175](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/DeclarationReconciler.cs#L175)。证据：静态确认。

`HasCompleteTableAdvantage` 只检查条目数量、金额为正、币种和页码等结构。达到数量差后，`ReconcileLineTotals` 提前返回，把强引擎的所有金额设为可靠，并把它自己的金额写成 `VerificationAmount`，不会进入后面的逐项冲突比较。

最小反例：主引擎读取同页同币种的 4 项，金额分别为 100、200、300、400；第二引擎只读取第 1 项且金额为 999。4 对 1 满足捷径，100 与 999 的明确冲突被跳过，4 项均可进入合计。即使保留“单引擎完整表格补全”的产品策略，也不能吞掉已经存在的对应项冲突。

建议：先对齐共有项并保留冲突，再对未匹配项应用明确的补全策略；`VerificationAmount` 只记录真实第二引擎的结果。增加“4 对 1 且第 1 项冲突”的回归测试。现有 `RunLineTotalSafetyRegression` 仅验证预先标记为不可靠的金额不求和，没有覆盖这个上游判断。

### R2 · P1：新批次预检失败时，旧列表会被清空并持久化

位置：[MainForm.cs:299](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/MainForm.cs#L299)、[BatchScanner.cs:36](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/BatchScanner.cs#L36)。证据：静态确认。

`RunScanAsync` 先执行 `_state.Records = []`，随后扫描器才检查目录中的支持文件是否为 0 或超过 200；异常分支又保存已经清空的状态。

复现条件：已有识别记录，选择空目录或包含 201 个支持文件的目录，点击开始识别。预期整批拒绝并保留旧结果，当前调用链会丢失旧列表（不删除源文件）。拖入超过 200 个文件的入口已有提前检查，但目录扫描仍存在此问题。

建议：统一预检得到不可变的 `ScanPlan`；预检成功后再开始新批次。扫描中单独存放已完成记录，取消时提交完成部分，预检失败不改历史。

### R3 · P1：重复标记会吞掉原始异常，截图状态又遮挡识别状态

位置：[BatchScanner.cs:70](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/BatchScanner.cs#L70)、[MainForm.cs:328](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/MainForm.cs#L328)、[346](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/MainForm.cs#L346)。证据：静态确认。

首次 `MarkDuplicates` 会将“需关注”覆盖为“重复单号”，异常筛选因此不再包含它。下一次调用时，因为 Status 已经不再是“需关注”，原始 Warning 不会再次保留。扫描器逐条上报和结束阶段都会调用此方法，所以在正常扫描流程中即可触发。

另一个表现：只要 `ScreenshotPath` 非空，表格状态直接显示“已核验”，会遮挡重复与识别异常，也没有确认截图文件仍然存在。

建议拆成独立维度：`RecognitionStatus/RecognitionWarnings`、`DuplicateInfo`、`VerificationCapture`。重复分析应返回派生结果、可重复调用，不改原始识别状态。UI 保留异常与重复徽标，截图只标记“已留存”。清理部分源文件后也应重新计算规范记录与去重合计，避免跨目录拖入时剩余副本仍带过期的 `IsCanonical=false`。

### R4 · P1：源文件/截图清理缺少承诺的二次确认和具体范围

位置：[MainForm.cs:479](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/MainForm.cs#L479)、[491](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/MainForm.cs#L491)、[ModalDialogs.cs:113](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/ModalDialogs.cs#L113)。证据：静态确认。

两个清理入口只调用一次 `ConfirmationDialog.Confirm`，随后直接执行回收站操作。弹窗传入的是通用文案，没有目标路径与文件数量；与 README 和 `docs/ui-and-boundaries.md` 的双确认规则不一致。

建议先形成 `CleanupPlan`，展示规范化目录、文件类型、数量、当前层范围；第二步确认使用同一份候选清单。保留当前回收站、根目录保护和失败文件报告机制。列表清理继续单独处理，明确不删除文件。

### R5 · P1：自动截图未核对结果单号，可能以 A 单号留存 B 单结果

位置：[BrowserValidation.cs:476](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/BrowserValidation.cs#L476)、[VerificationForm.cs:139](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/VerificationForm.cs#L139)。证据：**合成页面已复现脚本缺陷**，完整 Windows 流程未实测。

探测器虽保存 `state.declarationNo`，但判断 ready 时仅检查页面变化、关键词与结果行数，没有使用目标单号。用户在浏览器中更改查询单号时，B 单结果可以被判断为 ready，而截图调用仍使用记录 A 的单号命名。

本地合成测试请求 `310120260908250703`、显示 `310120260908999999`，实际返回 `ready|…`。这证明脚本缺少身份绑定，不代表真实官网当前结构一定会触发同样结果。

建议读取查询输入与结果区的单号，并与目标单号一致后才自动留存；无法确认时显示原因，交给人工核对，不能默认通过。将“截图留存”与“海关审核通过”明确区分。

### R6 · P2：扫描期间仍可清理列表和源文件，批次状态不一致

位置：[MainForm.cs:301](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/MainForm.cs#L301)、[370](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/MainForm.cs#L370)、[466](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/MainForm.cs#L466)。证据：静态确认。

当前只按扫描状态禁止导出，清理入口没有同等保护。识别中清空列表后，扫描器内部的 `result` 仍含旧结果，结束时再次赋给 `_state.Records`，列表会重新出现；源文件清理也可移走本批后续尚未处理的文件。

建议集中维护 `ScanSession`/统一命令可用状态。扫描期间禁用改变批次内容的清理操作，允许搜索与取消；所有记录更新绑定同一个批次标识。关闭窗口时也要明确取消并释放扫描会话。

### R7 · P2：键盘触发查询可能无法启动自动结果检测

位置：[BrowserValidation.cs:470](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/BrowserValidation.cs#L470)。证据：**合成页面已复现**。

监测器监听 `pointerdown` 和 `submit`，未监听 `click`。如果页面使用 `type=button` 的查询按钮，键盘 Enter/Space 产生 click，不产生 pointerdown 或表单 submit，`queryAt` 保持 0。

相同有效结果中，鼠标事件链返回 `ready|…`，仅 click 的键盘等效事件链返回 `waiting|not-started`。

建议以查询按钮 click 和 form submit 统一记录查询起点，做好去重，并覆盖键盘操作、鼠标操作、页面导航、验证码失败后重试的测试。

### R8 · P2：长截图分片的 Bitmap 使用已关闭的底层流

位置：[BrowserValidation.cs:285](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/BrowserValidation.cs#L285)。证据：静态确认 + 官方 API 契约。

每个分片在 `using var stream` 中创建 `new Bitmap(stream)` 后加入列表；循环迭代结束流被关闭，但之后才绘制这些 Bitmap。GDI+ 可能延迟读取底层数据，存在绘制/保存失败风险。不能把“某些 PNG 能成功”当作生命周期正确的证据。[Microsoft Bitmap(Stream) 文档](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.bitmap.-ctor?view=net-8.0)。

建议在流存活时绘制进独立的目标位图，或让流与 Bitmap 同生命周期；使用逐片解码、绘制、释放，避免同时持有全部分片再创建完整位图。补充多分片长图测试和内存峰值观察。

### R9 · P2：取消 OCR 后临时文件和外部进程没有完整收尾

位置：[DocumentExtractor.cs:32](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/DocumentExtractor.cs#L32)、[137](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/DocumentExtractor.cs#L137)、[227](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/DocumentExtractor.cs#L227)、[308](https://github.com/Leon1ca/customs-clearance-console/blob/cc1ae62/src/CustomsClearanceConsole/DocumentExtractor.cs#L308)。证据：静态确认 + 官方 API 语义。

PDF 渲染目录与 OCR 目录的删除都在成功路径末尾，取消或异常会跳过删除。`WaitForExitAsync(token)` 取消的是等待，不等于终止 Tesseract；仅 Dispose Process 对象也不是终止命令。[Microsoft WaitForExitAsync 文档](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexitasync?view=net-8.0)。

建议抽取临时目录作用域与 OCR 进程执行器，统一 `try/finally`、取消后终止本次启动的进程并等待退出、释放图片句柄、最后删除临时目录，失败日志保留。分别覆盖定向检测取消、主 OCR 取消和第二引擎失败。

## 可以精简的部分

| 位置 | 具体整理方式 | 保持的边界 |
|---|---|---|
| MainForm，530 行 | 将布局构造移入 `MainForm.Layout.cs`；抽出批次会话和记录视图构建；避免事件处理器内同时更新历史、重复、统计与 UI | 移文件本身不会减少总行数；先消除状态重复写入 |
| Theme，739 行 | 将颜色/字号/间距令牌与自绘按钮、下拉、统计卡等控件分文件；统一资源释放与图标缓存 | 保留必要的高 DPI 和键盘支持，不用大量自绘替代已有稳定控件 |
| BrowserValidation，558 行 | 分为浏览器定位、DevTools 连接、网站适配脚本、截图拼接；监测脚本作为明确资源维护 | 先修复单号绑定及查询事件，不改变人工验证码边界 |
| 遗留浏览器接口 | `NavigateFallbackAsync` 及内部专用路径无生产调用；`BrowserPreference` 属性无读取；三参数 `StartAsync` 忽略 preference，仅 Program 自测仍使用 | 删除或迁移前更新自测；历史 JSON 字段做兼容，不要求用户清历史 |
| 金额格式化 | `MoneyTotals`、`MoneyTotalsCompact` 没有调用；`DisplayTotal` 与 `MoneyTotalsLines` 可共享统一格式化内核 | UI 使用本地数字格式，Markdown 导出保留明确稳定格式；按币种独立合计 |
| 文件扩展名与路径 | 多个入口反复构造扩展名集合、检查路径，可抽成小型输入策略与只读清理计划 | 保留当前层、200 文件上限、回收站与根目录保护 |
| StateStore.Clear | 当前无调用，可移除不必要的删除入口 | 保留已使用的临时文件替换写入方式 |
| 自测组织 | 将纯解析/去重/导出测试与 Windows UI/浏览器/样本集成测试分开 | 让核心回归可以独立运行；补充实际失败分支，而不是只覆盖最终求和 |

不建议：为了减少行数合并两套 OCR 文本、删除规则回退、重写第三方 RapidOCR、引入数据库/复杂插件系统，或把 200 条记录的表格先改成复杂虚拟化框架。

## 建议实施顺序与验收

1. **结果正确性**：修复 R1/R2/R3/R5。加入金额冲突、重复分析幂等、预检失败保留旧结果、结果单号不一致禁止自动留存的测试。
2. **会话与资源**：修复 R4/R6/R7/R8/R9。覆盖识别期间的清理保护、键盘查询、取消收尾、多分片截图与失败路径。
3. **原生 UI 落地**：按本次设计规范调整 MainForm/Theme/弹窗；状态字段迁移与布局重构一起验收。
4. **删除遗留代码与整理**：逐个确认无调用，再删除旧入口。提取公共逻辑后运行原有回归和新增用例。
5. **Windows 验收**：Win10/Win11；1200×720、1600×1000；100%/125%/150%/200% DPI 和跨屏拖动；中英文长字段、3 个以上币种、200 文件、异常与重复共存、取消、清理失败、长截图、完整 Markdown 导出。

不以“删掉多少行”为验收条件。目标是状态只有一个来源、批次转换可预测、异常不会丢失、核心逻辑能独立测试。

## 已执行的网页脚本复现

[复现页面](tests/browser-monitor-repro.html) · [实际输出](tests/browser-monitor-results.json)。脚本从审查基线原样提取，仅将 C# 字符串转义还原并提供测试单号；测试使用合成 DOM 和受控时间，不联网查询真实关单。

| 场景 | 实际输出 | 结论 |
|---|---|---|
| 鼠标事件，结果单号一致 | `ready` | 正常对照成立 |
| 鼠标事件，结果单号不同 | `ready` | 结果身份未校验 |
| 键盘等效 click，非表单按钮，结果一致 | `waiting / not-started` | 查询事件被漏记 |
