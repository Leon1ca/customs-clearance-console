# UI v2 迭代实施记录（DeepSeek Harness）

分支：`codex/ui-v2-iteration`　基线：`32aebdb`　目标版本：1.5.0

本文件按 PRD 阶段持续更新：进度、决策、工作流链接 / 提交 SHA、未完成项。每个里程碑更新一次。

## 里程碑 M1 · 分支、设计输入、Actions（PRD 阶段 1）

- [x] 确认基线分支与设计输入：`designs/2026-09-23-enterprise-ui-v2`（DESIGN_SPEC、tokens、23 组 PNG、icons、capture-widget.html、参考 C#）；以 PRD 为需求基线，附件仅作设计输入。
- [x] 建立 Windows Actions 工作流 `.github/workflows/windows-ui-v2.yml`：
  - push 到 `codex/ui-v2-iteration` / `workflow_dispatch` 自动触发；`permissions: contents: read`。
  - 从仓库 v1.4.0 release 下载 ZIP 与 `SHA256SUMS.txt`，校验 SHA256 后执行 `scripts/bootstrap-from-release.ps1` 准备 OCR / 原生依赖（不提交模型二进制）。
  - `core-regression` 作业先跑核心回归；`windows-validation` 作业跑主程序构建、原生 UI 契约、环境/字体报告、Excel/Markdown 导出校验、受控浏览器截图 E2E、便携打包，并上传证据与 ZIP。
- [x] 版本统一为 1.5.0（主程序 / 启动器 / AssemblyInfo），新增 `docs/release-notes-v1.5.0.md`（便携包要求 `更新说明-v1.5.0.md`）。

## 里程碑 M2 · 数据扩展、明细、导出（PRD 阶段 2）

- [x] `DeclarationLineTotal` 新增商品名称 / 数量 / 单位 / 单价及对应复核字段；`AppState` schema → 6；旧历史缺字段按空值加载。
- [x] 解析器按行定位商品/数量/单位/单价；无法定位保持空，**不通过金额倒算**；双引擎合并时主值优先、缺失用复核补全、差异保留复核值。
- [x] 只读明细弹窗 `DetailForm`（600×480）：页码/项序、长表滚动、头尾固定、冲突高亮与另一引擎值、逐币种汇总；无分项显示“未保存分项，请重新识别”；合计为可靠分项求和，不宣称独立票面一致（PRD §6 补充说明）。
- [x] Excel 导出 `ExcelListExporter`：纯框架实现真实 OOXML `.xlsx`，三张表（关单列表 / 关单明细 / 币种汇总），18 位编号与公式型文本按文本保存、金额数值、未识别留空、冻结标题、列宽与数字格式。
- [x] Markdown 导出补齐明细字段与列表信息，保留长内容与转义口径。
- [x] 导出下拉两入口（Excel / Markdown），系统保存框限制扩展名，建议名 `关单列表_yyyyMMdd_HHmm`，整批快照（含隐藏分页与重复文件）。
- [x] `ExportValidation` 合成 60 条边界样例（前导零、公式型文本、多币种、重复、冲突、缺字段、旧历史）并重新解析校验，产出 `export-validation.json`。

## 里程碑 M3 · 浏览器会话与手动长截图（PRD 阶段 3）

- [x] 重写 `BrowserValidation`：CDP 改为单一接收循环 + 命令序号关联 + 事件订阅（`Runtime.bindingCalled`），命令与事件不再互相消费；发送串行化。
- [x] 点击“校验”直接打开默认（必要时回退 Edge/Chrome）浏览器并注入 Shadow DOM 卡片（`ccc-` 前缀、可拖动/收起），不再弹出旧核验对话框，不再自动截图。
- [x] 网页卡片“长截图”通过 `Runtime.addBinding` 请求；`capture-prepare` 展开嵌套滚动并隐藏控件，`capture-restore` 在 finally 恢复；>6000 万像素明确拒绝（不裁切），尺寸超限同样拒绝。
- [x] 结果单号校验排除卡片（Shadow DOM / `#ccc-widget-host` 排除）；输入一致但结果不一致也拒绝。
- [x] 文件名默认 18 位单号，重复唯一化；先写 `.tmp` 再原子替换；失败/取消不留有效 PNG、不回填。
- [x] 成功后回填本批同单号记录并持久化；不同会话/批次隔离，批次切换、列表清理、退出时使旧会话失效；不操作普通浏览器会话。
- [x] 受控浏览器 E2E `BrowserCaptureE2E`：本机回环合成页，验证整页首尾标记、控件隐藏/恢复、错误单号拒绝、超长拒绝、双击单次保存、刷新单实例重注入。

## 里程碑 M4 · 主界面与资源（PRD 阶段 4）

- [x] `DesignTokens` / `Responsive` 落地 96 DPI 逻辑尺寸与四档窗口；`Theme` / `AppFonts` / `UiV2Icons` 接入设计包图标与随包字体。
- [x] 主工作台按设计重排：48 顶栏、标题行状态标签、2×2 统计面板、金额汇总（份数 / 去重前 / 去重后 / 重复扣减 / 未确认提示）、记录工具栏（筛选分段 / 搜索 / 清理下拉）、真实列与分页 50/100/200（默认 50）、底栏。
- [x] 状态：未载入（虚线投放区）、待识别、识别中（进度条 + 锁定条 + 取消）、识别完成、筛选无结果、重复、需关注、已留存；识别中禁用设置/拖入/导出/清理/核验。
- [x] 清理三类：列表一次确认；文件两步确认；最终按钮不作为 AcceptButton（Enter 不触发）。
- [x] 字体：`fonts/NotoSansSC-Variable.ttf`（Google Fonts 官方，SIL OFL 1.1）与 JetBrains Mono Regular/Medium/Bold + 许可原文，`fonts/FONTS.md` 记录来源。
- [x] 原生快照入口 `--ui-state-snapshot`（四尺寸 × 状态 + 菜单 + 弹窗 + `ui-states.json` 布局断言）。

## 里程碑 M5 · 独立预审修复（R1 / R2）

静态预审（`REVIEW-round1.md`、`REVIEW-round2-browser.md`）问题已在本分支修复：

- R1-1 数量仅取价格列左侧相邻数字碎片，按列边界与横向间距校验，间隔数字不再拼接。
- R1-2 行边界改为“本行项号顶部”，上一行的商品/单位尾行不再污染下一行。
- R1-3 数量与单位成对补全，不再构造两引擎都没有的组合；仅当数量一致（或单位一致）才用复核值补全。
- R1-4 新增 `NumberFormats.Exact`，Markdown 数量/单价按实际有效小数位导出，不再依赖 UI 缩略格式。
- R1-5 Excel 明细新增“复核单位”“整项一致”“金额确认”，区分金额一致与整项一致。
- R1-6 `ExcelListExporter.Validate` 改为独立回读：三表表头逐列、单元格类型与值、前导零/公式型文本、无公式节点、缺值为空、复核字段、币种汇总、行数与重复记录；`export-validation.json` 的描述与实际断言一致。
- R1-7 明细结论改为“已识别可靠分项合计”，不以同源数据自比宣称独立一致。
- R1-8 `DesignMenu` 按 anchor `DeviceDpi` 缩放宽度/项高；`AppFonts` 优先请求真实字重再回退，避免变量字体丢字重。
- R2-1 `identity` 与截图核心均传入会话单号。
- R2-2 `identity.js` 只接受真实结果区域单号，保留错误/加载/新结果变化（stale）约束；截图前二次探测要求指纹稳定。
- R2-3 网页卡片操作按钮在 idle/capturing 之外的状态保持可见（重试/重新截图）。
- R2-4 使用默认（页面）执行上下文逐 frame 读取 monitor 与准备/恢复，并扩展同源 iframe 高度；E2E 增加同源 frame 场景。
- R2-5 回填事件携带会话 id，执行时校验会话仍注册，失效会话（切批次/清列表/退出）不再污染新批次。
- R2-6 仅连接目标 URL 页面；`Runtime.bindingCalled` 校验来源上下文与当前页面 URL，离开目标页即忽略请求。
- R2-7 卡片等待 `documentElement` 挂载成功后才标记已安装。
- R2-8 记录并恢复各滚动容器位置；恢复使用独立超时 token；连接中断后启动真实重连任务。
- R2-9 E2E 扩展：>12000px 分片拼接与接缝像素、未查询/验证码错误/拒写拒绝、可见按钮重复截图唯一命名、同源 frame、刷新单实例。

## 里程碑 M6 · 独立预审 R3 定点修复

对 `REVIEW-round3-ui-package.md` 逐条处理，并按要求补齐 R1/R2 的云端回归。代码改动均已提交，结论以云端运行为准（见"云端运行记录"）。

- **R3-1 编译**：`DetailForm.Columns()` 实体化为 6 列元组列表并统一使用 `.Rect`；`RecordStatePanel` 的 `S(int)` 调用一致。
- **R3-2 复制为空**：删除 `CellFormatting` 中把 No/Amount/Status 置空的逻辑，`RefreshGrid` 写入真实完整文本；`CopySelectedCells` 读取 `Value` 并经 `ExtractCopySelection()`；新增 `RunCopySelectionRegression` 真实选择 3 行 × 6 列并断言单号/源文件/币种/状态/操作列均非空，金额文本不跨币种相加。
- **R3-3 多币种明细**：明细新增独立“币种”列并逐行标币；页脚按币种逐行合计，行数多时页脚增高，绝不跨币种求和；`ReliableCurrencyTotals`/`CurrencySummaryText` 供回归断言。
- **R3-4 汇总**：`MoneySummaryPanel` 开启真实 `AutoScroll`，超出部分出现滚动条而非静默丢弃币种；未确认金额按币种分列（`UnconfirmedRow`），并纳入"含不可靠分项"的部分确认记录（不再只取 Totals 全空的记录）；记录表金额单元格逐币种绘制、溢出显示" +N 币种"，完整文本进入 Tooltip。
- **R3-5 包结构**：publish 后把 `app/tools` 移到包根 `tools`，根目录保留 `runtime/`、`tools/`、根启动器；`package-release.ps1` 校验根 tools/runtime/许可；新增"干净解压 ZIP → 根启动器 `--ui-contract-self-test` 与 `--env-report`"smoke 步骤，记录退出码并校验解压后字体真实加载。
- **R3-6 字体/许可**：删除随仓库的可变字体；`scripts/prepare-fonts.ps1` 从固定提交 `google/fonts@2894aab3` 下载 Noto Sans SC 可变字体并核对 SHA256，用 `fontTools 4.60.2` 实例化 400/500/700 静态 TTF 并校验无 `fvar`、`usWeightClass` 正确；下载 `JetBrainsMono-2.304.zip` 核对 SHA256；OFL 原文随包并记录保留字名 `Source` 未被使用；根 `LICENSE`、`third-party-notices/`、`app/fonts` OFL 随包；`global.json` 固定 SDK 8.0.x；`AppFonts.VerifyShipset()` 在环境报告中硬校验静态字重解析。
- **R3-7 DPI/菜单**：`ProbeDpi()` 在 PER_MONITOR_AWARE_V2 线程上下文读取真实 `DeviceDpi`/`GetDpiForWindow`/`GetDpiForSystem` 并写入报告；`CaptureRealMenu` 通过真实按钮点击弹出真实 `DesignMenu` 窗口、断言其位于工作区并截图（另存弹出窗口图与合成图）；`RunDesignMenuInteractionRegression` 走真实菜单命中/选择事件路径。
- **R3-8 长字段**：明细行悬停 Tooltip 展示完整商品名、数量/单位、单价、总价、另一引擎值与说明；页脚 Tooltip 展示完整结论；金额与单号单元格 Tooltip 为完整多币种/含源文件文本。
- **浏览器 E2E**：在既有场景上新增"不点不保存"、"结果缺号拒绝"、"旧结果未变化拒绝"、"失败后重试成功"、"跨源 frame"、"并发会话隔离"；成功场景增加控件排除像素断言与内部滚动末端断言；>12000px 场景增加首/中/尾标记断言。

## 里程碑 M7 · 独立预验收（`ACCEPTANCE-preflight.md`）数据/UI 与 B1–B5 定点修复

针对独立预验收列出的四个数据/UI 问题与 B1–B5 浏览器阻断项逐条修复，并把该报告"本轮证据不足"清单转成生产路径 E2E。全部结论以本轮云端运行为准。

### 数据 / UI

- **4-1 数量误认型号**：`DeclarationParser.AttachRowDetails` 只有在存在明确列证据时才写 `Quantity`——数字落在“数量”列表头带内（`FindQuantityColumn`，表头同时含“单位”时只取其前 60% 作数量列）或同一视觉行右侧紧邻已知单位（`IsKnownUnitToken`，单字符单位要求精确匹配）。否则保持 `null`，不再把价格列左侧 18% 内最靠右的数字当数量；商品名称因此保留型号。新增生产 token 样本：型号 `2026` 且数量空缺 → `Quantity == null` 且商品名含 `2026`；有单位证据（`12.5 KG`）与有“数量”表头两种情况仍正确解析。
- **4-2 Excel 整项一致**：`DeclarationLineTotal` 新增 `HasAmountDifference` / `HasValueDifference` / `IsFullyVerified` / `ItemConsistency`（存在差异 / 一致 / 未完整复核）/ `AmountVerification`（金额不一致 / 金额未复核 / 金额一致）。明细表“整项一致”“金额确认”改用三态结论；金额差异计入整体差异，单引擎旧历史不再显示“一致”。`Validate` 逐行核对三态与金额、数量/单价精度；合成批次新增金额冲突、完整一致、部分复核三个显式样本。
- **4-3 明细精度**：`DisplayQuantity` / `DisplayUnitPrice` 改用 `NumberFormats.Exact`，全文 Tooltip 使用 `ExactQuantityUnit` / `ExactUnitPrice` 并补另一引擎数量/单位/单价；`0.0004` 与 `0.1234567` 不再显示为 `0`。Excel 数量/单价数字格式改为 `#,##0.############`。
- **4-4 金额汇总 GDI 滚动**：不再依赖会被 GDI `TextRenderer` 忽略的 `TranslateTransform`，改为把 `AutoScrollPosition.Y` 折进每个矩形，文字/底色/图标一起滚动。新增原生回归 `RunMoneySummaryScrollRegression`：真实滚动到底后断言末币种行矩形可见、首行移出固定表头，并在 `DrawToBitmap` 的滚动后矩形内检出文字像素（旧实现该处为空白）。

### 浏览器 B1–B5

- **B1 跨源 iframe 完整捕获**：`ExpandFramesAsync` 通过 CDP `Page.getFrameTree` + `DOM.getFrameOwner` + `DOM.resolveNode` + `Runtime.callFunctionOn` 把每个子框架的 owner 元素增高到框架文档高度（跨源同样适用；无默认上下文时用 `Page.createIsolatedWorld` 读取）。无法扩展且内容高于元素的框架会使整次捕获明确失败而不截断保存；`finally` 中 `RestoreFramesAsync` 恢复原高度。跨源 E2E 断言紫色底部标记完整入图，不再只看顶部首屏。
- **B2 上下文销毁与逐上下文恢复**：订阅 `Runtime.executionContextDestroyed` / `executionContextsCleared` 维护活跃上下文集；`PrepareContextsAsync` 记录真正准备成功的上下文（含主上下文），`finally` 只恢复这些上下文，准备中途失败也会恢复已改框架。刷新 E2E 改为刷新后重新查询并真实点击截图。
- **B3 目标 Uri 精确授权**：新增 `TargetUrlPolicy`（协议 https、精确主机 `www.singlewindow.cn`/`singlewindow.cn`、`/publicInquiryDetail` 路由），`https://example.com/?singlewindow` 被拒绝；测试地址必须显式传 `allowTestTarget`，否则构造即抛错；`Runtime.bindingCalled` 改为校验触发 context 所属 frame 的 URL（`IsAuthorizedContext`），不再只判断“已知 context + 顶层字符串”。
- **B4 捕获期间重验**：主框架导航递增 `_navigationGeneration`，分片循环内检查代次与目标 URL；新增布局无关的 `verify.js` 结果指纹（只含结果区域与 `queryAt`，不含被准备脚本改变的文档高度），在准备前、保存前对比，并在原子落盘前再次校验代次/URL，任何变化返回 error、不落盘、不触发成功回填。新增 `BrowserCapturePolicy` 与核心回归断言：失败/串号结果不得回填。
- **B5 已有框架与刷新注入**：`InjectShellAsync` 先向所有已有允许框架安装 monitor（`EvaluateAllContextsAsync`），再只在主框架安装卡片；`monitor.js` 不再无保护读取 `document.documentElement.scrollHeight`（根元素未建立时返回 0），因此 document-start 注入不会中断整段 shell；重连时先移除旧 `addScriptToEvaluateOnNewDocument` 标识避免重复。

### 证据加强

- 成功场景改为**真实 CDP 鼠标点击**可见卡片按钮（`captureButtonRect` + `Input.dispatchMouseEvent` + `elementFromPoint` 命中记录），不再调用 `requestCapture()`；断言截图全图不含卡片主色、`__cccLastCapture` 记录控件准备时隐藏/恢复后可见、原样式与原滚动位置均恢复。
- 缺号场景改为真实点击后由生产路径拒绝（断言命中 host 且状态为生产 error，不用测试侧超时冒充）；新增导航离开/返回、捕获中结果变化、断连恢复、刷新后点击截图场景；跨源场景在真实扩展后断言底部标记。

### 云端运行记录

| 运行 | 提交 | 结论 |
|---|---|---|
| [35954437659](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35954437659) | `3a7dfa5` | 核心回归通过；主程序 14 处 `DetailForm` 编译错误 |
| [35954697691](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35954697691) | `0d7b8cf` | 编译通过；自检 `缺少标题行/顶栏按钮` |
| [35955293382](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35955293382) | `4d54daf` | 字体 SHA256/实例化/构建通过；自检"记录区与统计区发生重叠"坐标空间误报 |
| [35955897503](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35955897503) | `72212a3` | 核心/构建/UI 契约/字体/导出/快照通过；浏览器 E2E 失败：`monitor.js` 在 document-start 无保护读取根元素致整段 shell 中断（"控件未注入"），本轮 B5 已修 |
| 本轮 | 见下 | M7 修复后待云端验证 |


## 里程碑 M8 · 独立复核 R4 产物 / R5 / R6 定点修复

针对 `REVIEW-round4-artifacts.md`（基于 `72212a3` 的云端 PNG/XML）、`REVIEW-round5-fixed-commit.md`（基于 `6be4e7f` 的调用路径复核）与 `REVIEW-round6-official-dom.md`（官方查询 frame 静态 DOM 核对）逐条修复。R4 的界面修复为上一轮已完成但未提交的工作区改动，本轮保留并补齐断言与真实尺寸证据；R5、R6 为本轮新修。

### R4 界面与真实尺寸证据

- **R4-1 按钮文字**：`Theme` 的六个普通按钮工厂把 `text` 传给 `Style(...)`，`Style` 现在写入 `Text` 与 `AccessibleName`（原先固定 `Text = string.Empty`）。分页“上一页/下一页”、清理、设置浏览/取消/保存文字恢复可见可访问。
- **R4-2 工具栏/页脚溢出**：嵌套 toolbar/footer `TableLayoutPanel` 增加显式 `RowStyle(Percent,100)`，子控件 `Dock=Fill` 且带上下 margin；`SearchField` 内边距改为 `(44,5,12,5)`。`CaptureAllStates` 新增几何断言：toolbar/footer 每个可见子控件都在父 `ClientRectangle` 内，清理按钮高度不超过设计槽位。
- **R4-3 KPI 与文件数**：新增 `KpiPanel.RequiredHeight(dpi, compact)` 与统一 `CompactMetrics()`（按 `RequiredHeight(false)` 判定），标签/数值/注释三段按像素分配；`MainForm.BatchTotalForDisplay()` 按真实批次状态取 `_batchFileCount`/进度总数，待识别显示载入文件数；`StatsRowHeight()` 取 KPI 与币种汇总的较大值。新增断言：KPI 高度 ≥ 所需、四格 label/value/note 不相交且不越界。
- **R4-4 标题接线**：`UpdateTitleBlock()` 把真实状态、目录、开始/完成时间与记录数写入 `TitleBlock.Set`，随 `UpdateSummary` 更新；断言状态与副标题非空。
- **R4-5 四档真实尺寸**：`CaptureAllStates` 先用 `EnumDisplaySettings`/`ChangeDisplaySettings` 把桌面提升到 ≥1920×1080；`NewSnapshotForm` Show 后按 `DeviceDpi` 设置最终 `ClientSize` 并断言未被约束（`MainForm.AllowOversizeForSnapshot` 配合 `WM_GETMINMAXINFO` 解除窗口跟踪尺寸上限）；`SaveSnapshot` 断言 PNG 像素等于实际 `ClientSize`，并额外用 `Graphics.CopyFromScreen` 对可见窗口做**真实屏幕抓图**（`*-screen.png`）。每档记录请求逻辑尺寸、实际 ClientSize、PNG 尺寸、工作区/屏幕、DPI 与按实际尺寸计算的 Responsive 分支；任一关键状态缺少真实屏幕抓图即失败，不再把 1044 宽图当 1200/1920 通过。
- **R4-6 Excel 显示精度**：数量/单价 numFmt 提升到 28 位小数（`#,##0.############################`）；`ExcelListExporter.Validate` 解析每个数量/单价单元格的样式→numFmtId→formatCode，按格式可显示的小数位回读并标记会被舍入/显示为零的值；合成样例的冲突 Note 与复核数量/单价改由同一组真实数值推导，样例自洽。

### R5 定点缺陷

- **R5-1 顶层 waiting 不再抢先**：`VerifyStableAsync` 的可终止标记收紧为 `stable|/mismatch|/error|/stale|`；`EvaluateAllContextsAsync` 先在顶层、再遍历所有**已授权** frame 上下文，任一 frame 给出终态即采纳，全为 waiting 时按强度返回最具体结果。顶层外壳不再因 `waiting|not-started` 提前结束，同源/跨源 iframe 内查询可正常收敛。
- **R5-2 准备账本与异常**：`capture-prepare.js` 在首次修改前先发布 `window.__cccCaptureState`，之后每步只增不改；`PrepareContextsAsync` 在发送执行前把目标登记进恢复账本，CDP `exceptionDetails` 由 `EvaluateContextAsync` 抛为异常，缺少 `prepared` 标记或脚本异常都会拒绝保存；`finally` 恢复全部已触达目标。
- **R5-3 真实可见高度**：`ExpandFramesAsync` 写入高度后等待布局，回读 iframe 的 `clientHeight`、计算样式 `max-height` 与 frame 自身 `scrollHeight/innerHeight`；只有内容完全暴露才算成功，否则拒绝保存。增长前保存 `height`/`max-height` 的**值与 priority**，恢复时按原 priority 还原（同源 JS 分支同样处理）。
- **R5-4 授权贯穿链路**：新增 `TargetUrlPolicy.IsAuthorizedFrame`（可信单窗口同源 frame + 已核实 `swapp.singlewindow.cn` 的 `/qspserver/sw/qsp/query/view/queryDecStatus` 路由，https）；`IsAuthorizedContext(contextId, sessionId)` 对未知 frame/来源直接拒绝，不再回退顶层 URL；读取/注入/自动填写/身份/准备/扩展全部只遍历授权 frame；`ConnectAsync` 登记完整 frame 树与父子关系，并支持 `Target.setAutoAttach`（flatten）经子 target/session 处理 OOPIF。
- **R5-5 单位子串**：`IsKnownUnitToken` 改为完整单位匹配（精确等于 KnownUnits，或 `<数字><单位>` 且余下部分精确等于单位），删除 `Contains` 子串匹配；数量补全改为**相邻**单位证据（`ordered[k+1]` 且间距 ≤ `maxFragmentGap`），任意单词不再写入单位。新增 HEADSET/RESET 生产路径反例。
- **R5-6 复核来源**：`DeclarationLineTotal` 新增 `BackfilledFields`（从复核引擎补入的字段）与 `AmountOnlyFromSecondary`；`IsFullyVerified` 要求字段既存在且**非补入**，`AmountVerification` 对仅复核金额标为“金额未复核”。补值不再被当作双引擎独立一致。

### R6 官方查询 DOM 兼容（静态源码核对）

- **共用结果区域定义**：`identity.js`/`verify.js`/`probe.js` 的结果选择器改由单一生产常量 `BrowserValidation.ResultSelectors` 注入（`__RESULT_SELECTORS__`），新增 `#queryDetail .display-content .content-field` 与 `[class*=field-order]`，覆盖官网 `processData` 渲染的 `.field-order/.field-title/.field-time/.field-text`；单号只从可见结果文本中的 18 位号读取，输入框/标题/卡片仍不能充当身份。
- **错误文案**：错误集合补充“验证码无效”“验证码输入错误”“没有符合条件的数据”。
- **仿官网 DOM fixture + 场景**：新增 `/official` frame（`#entryId` 无 placeholder + 相邻“报关单号”label、`#queryBtn`、`#queryDetail` + `.display-content/.content-field/.field-order`，结果仅在 iframe，顶层无查询结果），E2E 用真实 CDP 鼠标在 frame 内点击 `#queryBtn` 并真实点击卡片，覆盖成功、错号、空结果、验证码无效、捕获中 `field-order` 变化五种场景，成功场景断言首尾标记完整入图。
- **诚实边界**：静态源码不能保证真实响应的每个 key 都是 18 位号。若已授权官方端点返回的结果区域不含 18 位号，当前实现按“未确认”拒绝保存，不会用输入框的值冒充成功；此类结果的可靠关联仍需实机人工验收，未宣称现场验证。

### 浏览器 E2E 与云端失败

- 修复合成页 `#scrollband` 的 margin 塌陷（改绝对定位），`success-fullpage` 页脚标记回到文档末端。
- `same-origin-frame` 由 R5-1 修复；`cross-origin-frame` 改为**顶层无查询/结果、查询仅在跨源 iframe** 并加 `max-height` 约束与样式 priority 复原断言。
- 新增 `cross-site-oopif-frame`：`.test` 主机映射到回环，构造真正跨站 OOPIF，验证会话路由下的查询、准备、扩展与完整捕获。
- 现有刷新/导航返回/结果变化撤销/重连/长图分片/去重等成功场景保持不重写。

### 云端运行记录

| 运行 | 提交 | 结论 |
|---|---|---|
| [35958026991](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35958026991) | `6be4e7f` | 核心/构建/UI 契约/字体/导出/快照通过；浏览器 20 场景 17 通过，`success-fullpage` 页脚标记、`same-origin-frame`（R5-1）、`cross-origin-frame` 失败 |
| [35960642742](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35960642742) | `91cfaa2` | CoreRegression 通过；windows-validation 的 Build main application 因 `BrowserCaptureE2E.cs` 在 `/official` fixture 使用尚未声明的 `mode` 报 `CS0841`，下游 8 个步骤全部失败。已在 M9 修复 |

## 里程碑 M9 · 最终验收 P1（prepare 失败传播）与构建阻断定点修复

针对独立最终验收 `FINAL-ACCEPTANCE.md`（基线 `6ef38ba`）保留的唯一阻断 P1 定点修复；不改动该报告的结论，只更新实现状态。

- **P1 · prepare 局部失败假成功**：`capture-prepare.js` 不再吞掉非预期异常。滚动容器循环与外层扫描、iframe 列表/访问/增长各步把非预期异常记入 `failures`；若有失败则在返回 `prepared` 标记前 `throw new Error('prepare-failed:…')`，由 CDP `exceptionDetails` 传到 C#，走 `CaptureCoreAsync` 的错误分支，绝不保存未展开页面。只有跨源文档不可读（`SecurityError` 或 cross-origin/denied 文案）属于预期分支，仍交给 CDP frame 路径。恢复账本仍在首次修改前发布、只增不改；抛错后 C# `finally` 依账本恢复已改样式/`!important` 优先级/滚动偏移/窗口滚动与卡片可见性。
- **生产路径失败注入 E2E**：`TestServer` 新增 `prepfail=1` 受控页（`#bad-scroller` 在 `#content .scroller` 之前，令反向遍历先成功展开 `.scroller`，再由 `#bad-scroller` 的一次性 `style.setProperty` 覆盖抛错）。新增 `prepare-failure-restore` 场景：真实 CDP 点击卡片后断言返回 `error` 且文案为“准备失败”、目录无 PNG；`.scroller` 的 `height/max-height/overflow-y` 内联样式复位、`scrollTop` 回到 300、`__cccCaptureState` 已清理、卡片未残留隐藏；随后一次性注入已解除，按钮仍可点击且重试真实保存成功。不重写、不放宽任何既有成功场景断言。
- **构建阻断 CS0841**：`BrowserCaptureE2E.Page` 的 `mode` 解析移到 `/official` fixture 分支之前、删除原后置声明；此错误此前使 `windows-validation` 在 Build 即失败，掩盖了所有真实运行结论。

### 云端运行记录（M9）

| 运行 | 提交 | 结论 |
|---|---|---|
| 本轮 | 见下 | P1 + CS0841 修复后待云端验证 |

## 未完成 / 待云端验证

- [ ] M7 本轮云端运行的最终步骤结论（核心回归 / 自检 / 环境与字体 / 导出 / 快照 / 浏览器 E2E / 打包 / 干净解压 smoke）；在结论出来前不把未运行步骤写成通过。
- [ ] 真实单一窗口人工验证码流程与真机 DPI 缩放（125/150/200%）仍需人工验收；云端 runner 实际 DeviceDpi 如实记录，未伪造。真实网站路由校验以 `https://www.singlewindow.cn/#/publicInquiryDetail?id=pi4` 为唯一生产目标，仍需实机确认。
- [ ] 独立验收子路由结论与缺陷闭环。
- [x] Noto Sans SC 静态字体改由云端 `fontTools` 从官方固定提交实例化（见 `fonts/FONTS.md`），不再依赖可变字体，也不再作为偏差留待用户确认。
- [x] 跨源 frame 改为通过 CDP `DOM.getFrameOwner`/`Runtime.callFunctionOn` 扩展外层并把框架文档高度纳入截图；无法完整扩展时明确拒绝保存，云端 E2E 以紫色底部标记断言完整性。

## 决策记录

1. Excel 不引入 NuGet 依赖，使用 `ZipArchive` + `System.Xml.Linq` 手写 OOXML，避免离线构建风险。
2. `record.Totals` 对有关单分项的记录由可靠分项求和，属同源数据；明细结论因此显示“已识别可靠分项合计”，不宣称与独立票面总额一致（遵循 PRD §6 补充说明）。
3. 无边框窗口在顶栏右侧保留最小化/最大化/关闭按钮（设计稿未画），其余顶栏元素严格按设计；已在本文记录该偏差。
4. 浏览器 E2E 使用 `--headless=new` 受控实例与本机回环页面，不代表真实网站校验通过。
