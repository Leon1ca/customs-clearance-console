# a9cf172 截图步骤停滞：短时只读排查

范围：固定 `a9cf172fef08b939396db2961accbfd5847f55b5` 的 CaptureAllStates、桌面模式、渲染/事件与CLI异常路径。未本机运行产品/测试。run `35961148492` 的“停在快照步骤”来自主路由状态信息；尚无该步骤运行中堆栈或窗口证据，不能断言具体根因。本文不改变 FINAL-ACCEPTANCE 的源码结论。

## 确定存在的阻塞机制

`Program.cs:13–14` 在判断命令行模式前，对所有模式启用 CatchException，并把 Application.ThreadException 交给 AppLog.ShowUnexpected。`AppLog.Desktop.cs:5–8` 先写日志，再同步 MessageBox.Show，必须有人关闭才返回。因此 `--ui-state-snapshot` 内 Show、重绘、窗口事件或 Application.DoEvents 期间只要有UI事件异常，就能在无人值守机器上一直等待弹窗。当前工作流该步骤直到25分钟超时（windows-ui-v2.yml:143–147），没有CLI模式的立即失败保护。这条机制确定；本次是否已经触发仍待AppLog/截图证实。

建议先修CLI异常处理：在测试/截图命令模式，记录异常到stderr及随产物收集的日志后非零退出，禁止显示MessageBox；正常桌面启动仍沿用交互报错。不要捕获异常后当成功继续。

## 没有找到确定的代码无限循环

- CaptureAllStates 的16个基础状态、2个processing状态、2个菜单及6个对话框都是固定数量（SelfTest.cs:678–719）。
- EnumDisplaySettings 循环按mode递增，API返回false即退出（978–988）；未看到把mode重置或永不变的条件。不能仅因 `for(;;)` 外形判无限循环。
- ChangeDisplaySettings 同步调用位于开始生成所有状态之前（639–640,995）。如果**本轮**已产出后续状态PNG，则它已返回；旧轮“图片很快”不能据此排除本轮环境调用停滞。
- Application.DoEvents 是最需要定位的消息泵边界，出现于新建窗口、状态配置、RenderForm、屏幕捕获前及菜单开关（655,777,786,809,836,1069）。循环本身有限，不代表其中的事件或原生调用一定返回。
- 本轮新增 CopyFromScreen 在显示窗口后调用（1064–1074）；这是另一个待定位的原生阻塞点。源码不足以判定它就是根因。

## 最小定位动作（交 DeepSeek 云端处理）

1. 在桌面模式切换前后、每个 `state-size` 开始/结束、Show前后、DoEvents前后、DrawToBitmap前后、CopyFromScreen前后、菜单/对话框前后输出并flush时间戳。只需阶段日志，先确认停在哪个调用。
2. 为快照子进程设置短于整个步骤的超时；超时前收集已有PNG文件名/时间、AppLog文件、当前桌面截图及进程状态，再非零结束并上传证据。现有工作流只上传 artifacts/validation，AppLog默认位置未必被包含，应显式复制其路径输出的日志。
3. 如日志显示 ThreadException，使用第一条异常堆栈定位实际控件；如停在桌面调用/CopyFromScreen，则针对云端显示会话处理。没有阶段/堆栈证据前，不建议修改产品绘制逻辑来猜测修复。

优先级判断：**先消除确定存在的CLI模态弹窗阻塞并增加阶段日志**，比延长25分钟等待更能获得下一轮可用证据。当前没有证据支持认定某个绘制循环无限运行。

## 已有图证据 / 确切残留（a9cf172 部分产物）

证据目录：`/Users/leon1ca/Documents/Codex/2026-09-24/evidence-a9cf172-partial/ui-states`。本轮仅查看 unloaded、ready、complete、filter-empty 的1200×720真实屏幕PNG，并对照同尺寸设计 `20-window-min-1200x720.png` 与 ready 的 DrawToBitmap PNG。不代表其余尺寸、处理态、菜单或对话框已通过；FINAL仍待完整产物验收。

### P1：初始未载入/待识别状态的正文在真实窗口完全空白

`unloaded-1200x720-screen.png` 缺少空状态引导；`ready-1200x720-screen.png` 缺少已载入文件数和开始识别说明。作为对照，`ready-1200x720.png` 中两行说明可见，而 `filter-empty-1200x720-screen.png` 中“没有匹配的记录”和说明在真实屏幕可见。这是实际窗口初始状态显示问题，不能以 DrawToBitmap 正常判通过。

固定源码的 `MainForm.cs` UpdateSummary 先将 `_recordState.Visible` 设为状态值，然后仅在 `_recordState.Visible` 为true时执行 LayoutRecordState/BringToFront。父窗口尚未显示时，Visible有效getter会受祖先隐藏状态影响，因此初始化可跳过布局/置顶；`MainForm.Layout.cs` 中表格先加入host，存在遮住状态面板的路径。建议用本次计算的 `statePanelVisible` 决定布局/置顶，并保证首次显示及host调整尺寸时边界正确；以两个初始状态真实屏幕PNG复核，不仅重跑DrawToBitmap。

### P2：版本号前缀与分隔线被标题遮挡

四张真实屏幕图都只露出“5.0”；同批 DrawToBitmap 图显示完整“v1.5.0”，因此不是版本值本身缺失。固定 `MainForm.Layout.cs` 标题控件从x60起、宽160，覆盖至x220；分隔线在x186，版本标签从x197开始，几何范围实际重叠。缩短标题控件到实际所需宽度，或将分隔线与版本移到标题边界之后，保证无重叠；验收要求真实屏幕完整显示版本及分隔线。

### P2：表格序号表头 # 被裁至几乎不可辨认

`complete-1200x720-screen.png` 最左表头只剩极窄痕迹，行内01等序号可读；设计中的#完整可见。最小尺寸的序号列很窄，应针对该列取消通用左右表头padding、居中绘制，或保留足够列宽。只调整该列即可，不需要泛化重做表格视觉。

### 已从这批真实屏幕确认改善

四个筛选按钮文字及数量、搜索框、清理操作、底部分页文字均已显示；完成态KPI和多币种总额可读且未见原来的卡片文字重叠；待识别态文件数量显示10，与已载入数量相符；页标题/状态与副标题已经显示。完成态在最小尺寸下展示行数少于设计，但本次不据此要求重做密度。

### 阻塞原因已由云端日志确认

本批 `data/unloaded-1280x800/关单核验台数据/app.log` 记录前一个窗口释放后，searchTimer.Tick继续触发RefreshGrid，最终向已没有列的DataGridView添加行，抛出InvalidOperationException。结合上述统一ThreadException→模态MessageBox路径，本次停滞原因已有实际日志支持。DeepSeek正在修复窗口/计时器生命周期与CLI立即失败；不再将本次原因列为桌面模式或截图原生调用的未知推测。

## 633db49 完整产物有界预验收

证据：`/Users/leon1ca/Documents/Codex/2026-09-24/evidence-633db49`，关联 run `35962368728`。仅静态检查已下载的图片、JSON、Markdown与XLSX压缩包XML，未执行本机产品或测试。**本轮未发现超出上述三项UI残留的明确新功能/可读性阻断**；已知浏览器25/27及两项失败继续由DeepSeek处理，不能标记本提交整体通过。

### 实际图片

- 查看四档 `complete-*-screen.png`：实际1200×720、1280×800、1440×900、1920×1080均有可读的操作区、筛选搜索、金额及分页；1200合并关别/目的国，其余档位按宽度呈现。版本和#裁切在所有档位仍可见，属于已知问题。
- 额外查看1280未载入、1440/1920待识别及1280/1440/1920筛选空真实屏幕：初始正文空白并非仅1200出现；筛选空说明可读。仍是同一个初始状态面板问题，不另增缺陷。
- 两档 processing 真实屏幕有当前文件、进度条、取消入口、锁定说明，无文字相互遮挡。合成快照中“完成5”与列表0不是完整识别回放证据，因此不凭这组静态数据宣告实际增量识别已验证，也不据此直接判产品丢结果。
- 设置图中两个目录、解释、保存/取消可读；明细图中项号、名称、数量/单位、单价、币种、总价完整。正常明细明确写“未与独立票面总额核对”，不再同源自比称一致；冲突明细主4800与复核4750可读，底部仅合计可靠7680并说明第二项未计入。
- 列表清理一次确认、关单清理两次确认中的目录/文件数/回收站说明和动作可读。导出菜单明确Excel与Markdown；清理菜单三类及说明可读。未发现应为本轮追加修复的重叠或缺字。
- 菜单含实际popup bounds/DPI记录；dialog文件只有渲染快照，没有对应`-screen`文件。由于已知顶栏遮挡曾仅在真实屏幕出现，本结论仅确认弹窗渲染内容可读，不把DrawToBitmap冒充实际桌面遮挡验证。

### 导出文件直接核对

独立打开XLSX包XML确认“关单列表/关单明细/币种汇总”三表，分别62/61/4行（含标题，即61条记录、60条明细、3币种）；均冻结首行。`010120260000000001`、`000123`、`=SUM(A1:A2)`保存为inlineStr，三表均无公式节点。明细单元格G12保留数值0.0004，采用最多28位可选小数格式，旧三位格式抹成0的问题已关闭。

Markdown实际包含61个逐关单段落，完整来源/关别/目的国、逐项价格、可靠性及冲突主复核值可见；缺失明细明确要求重新识别，不伪造分项。`export-validation.json`报告Pass=true、61记录/3币种/60明细，和直接检查的结构一致。没有在本机打开Excel做应用渲染，因此此处属于格式/数据核对。

### 环境与验收边界

`ui-states.json`记录桌面成功设为1920×1080，四档实际窗口/PNG尺寸与请求一致；实际DeviceDpi、windowDpi、systemDpi都为96，其他缩放仍待真实机器验证。字体报告确认Noto Sans SC Regular/Bold/Medium及JetBrains Mono Regular/Medium实际加载。

`environment.json`的构建输出目录探测里rootLicense/thirdPartyNotices为false，不能据此直接断言正式ZIP缺许可；正式包另有独立打包检查，本次证据目录未含ZIP，不在此替代该检查或宣称已独立核对包内容。最终报告仍等待最终同一SHA、浏览器失败修复后产物，以及上述三项UI修复后的真实屏幕图。

## 07851ef 定点源码复核

固定提交：`07851efa983212d22fb3091cb4809b802c8ffcb3`。只读取该提交相对633db49的相关修复及必要调用链，未运行本机产品/测试。最终云端与真实屏幕仍待该提交产物。

### 三项UI：源码根因已处理，运行证据待补

MainForm以独立`_recordStateWanted`控制初始化布局/置顶，并在OnShown再布局，避免祖先尚未显示导致Visible有效getter为false的路径。标题按实际字体测量宽度，分隔线与版本紧随其后，消除原先固定几何重叠；序号表头独立使用零padding与居中。SelfTest新增状态面板有效可见/实际层次/范围及标题边界检查。现有修复与已发现根因对应，不要求进一步重做视觉；仍需最终真实屏幕确认。

### OOPIF等候monitor：未发现弱化核心成功断言

FormHtml由固定400ms改成最多约20秒等候monitor，再对实际查询按钮执行DOM click并填充合成结果。没有直接修改monitor.queryAt或跳过生产结果核验；截图仍通过可见卡片的CDP鼠标点击，saved、PNG存在、首尾颜色标记、尺寸检查均保留。它不等同于人为鼠标查询，但该动作方式本轮没有从真实鼠标降级（此前同样使用DOM click）。因此可视作修正过早查询的夹具竞态，运行成功仍待云端。

### P2明确残留：样式基线缺失时可跳过恢复断言

`BrowserCaptureE2E.cs:124–125`吞掉afterLoad前测异常，只写AppLog。跨源与OOPIF场景在约510/555行仅当`originalStyle is not null`时比较恢复前后值；如果前测瞬时CDP/JS错误、后续截图成功，场景可以Pass并声称“原样式优先级复原”，实际没有任何原始样式依据。

此外EvaluateRawAsync底层在无value/结构不符时可返回空字符串，FrameStyleProbe在找不到iframe时返回`{missing:true}`。因此不能仅把null改成失败：前后值都应解析为有效对象、明确没有missing=true，并包含height/heightPriority/maxHeight/maxHeightPriority四个字符串字段，再逐值或严格序列化比较；允许合法的空属性值，但不允许缺失整个证据对象。前测异常应直接使场景失败（可用Harness结果或传播至统一失败路径），不可仅记日志继续宣称恢复通过。这是证据假阳性漏洞，尚不等同于已证实生产恢复失败。

## 5a5f9ba 小范围源码复核

固定提交：`5a5f9baf466bf7549aac319318bf2fbb921c828e`；仅复核前述测试漏洞及OOPIF URL登记/主子frame语义，未运行本机产品/测试。

### 样式证据漏洞：源码关闭

前测异常现在返回Harness失败；两场景均调用AssertFrameStyleRestored，先验证前后为非空JSON对象、非missing=true、四个字符串字段齐全，再逐字段严格比较。空字符串、缺字段、missing对象与读取异常均会增加失败详情，不能跳过断言并宣称恢复。允许具体CSS属性值为空是正确的，不等同于允许整份证据为空。

### P1残留：子session根frame导航仍可覆盖顶层页面身份

`BrowserValidation.cs` OnFrameNavigated（约509–524行）仍以`parentId.Length == 0`作为唯一isMain条件，不读取`SessionIdOf(message)`。CdpClient统一事件分发保留子sessionId并交给同一handler（约1664行）。因此，子target自身根frame的Page.frameNavigated在没有parentId时，会被当作整个页面的主frame，覆写`_mainFrameId`、`_currentUrl`，并增加顶层`_navigationGeneration`。这是确定的条件分支错误；本轮不声称已在云端复现该具体事件序列。

触发影响：官方swapp子frame URL会走主页面IsTargetUrl校验并被判离开核验页；原主frame与子frame身份颠倒，ExpandFrameOwnersAsync还会跳过被误认作main的iframe，破坏截图前展开。新增attachUrl和child Page.getFrameTree仅补`_frameUrls`，没有消除这条导航路径。一次成功初始加载也不足以证明后续子frame导航不触发。

最小修复：仅root CDP session、且无parentId的frame导航允许修改顶层身份/URL及顶层generation；子session导航只更新自己的URL/上下文信息。对子session事件缺少parentId，应保留既有父frame关联，不把已知父关系覆盖为空。补一条子session无parentId导航的定点回归，断言顶层身份保持、子frame URL更新及后续授权/截图仍正确；无需增加无关测试范围。

## 07851ef 真实屏幕三项定点复验：关闭2项，保留1项

证据目录：`/Users/leon1ca/Documents/Codex/2026-09-24/evidence-07851ef/ui-states`；实际查看1200×720的unloaded/ready/complete，以及1440×900 ready和1920×1080 unloaded，全部使用`-screen.png`。本次UI代码与5a5f9ba相同；不替代最终提交的产物验收。

- **关闭：初始状态正文空白。** unloaded现在显示上传图标、“尚未载入关单”、说明和“选择关单目录”按钮；ready显示“已载入10个文件”及开始识别说明，实际可读。
- **关闭：版本遮挡。** 五张真实图均完整显示`v1.5.0`及分隔线，与标题不重叠。
- **仍保留P2：#表头。** 五张真实图最左表头仍只显示两点/细小省略痕迹，无法辨认为完整`#`，并非已经修好。前一节“源码根因已处理”不能作为该项关闭依据，现以真实图明确撤回其关闭预期。

定点修复建议：检查Index.HeaderCell的**InheritedStyle.Padding**及原生表头绘制可用空间。当前把局部Style.Padding设为Padding.Empty的断言只证明赋值，不证明没有继续继承公共表头8px边距。可将公共header padding设0、仅其他列设置8px，或对Index表头单独绘制居中的#；最终以真实屏幕该字完整可辨认验收。无需改其他列或做整体视觉重设计。

## 5a5f9ba OOPIF实际截断定点分析

证据目录：`/Users/leon1ca/Documents/Codex/2026-09-24/evidence-5a5f9ba/browser`。browser-e2e.json为26/27，唯一失败为跨站OOPIF底部标记未入图。读取固定5a5f9ba源码与已有PNG，未运行本机产品。

**实际截断确定：** `capture-oopif-frame.png`仅1241×800，顶部棕色块占y0–199，iframe区域y200–599仍正好原始400px，随后已是父页蓝色尾部。iframe只露出顶部蓝标、查询输入/按钮和滚动区域前段，查询结果行、内部滚动末端洋红色标、iframe底部紫标都缺失。对照`capture-cross-frame.png`约1854px高，三者均完整。这不是仅颜色断言写错，也不是页面已经完整而多余测试失败。

**确定的源码漏检路径：** `ExpandFramesAsync`约1043–1052行只读取root session的Page.getFrameTree并CollectFrames遍历；没有并入已附加子target、已授权context或URL登记。身份与PrepareContexts使用注册frame/context集合，因此同一个已授权OOPIF可以成功返回身份、甚至展开其内部滚动容器，却不进入owner iframe高度检查/增长。候选遗漏会留下空failed集合，被上游视为全部展开成功而继续保存。另一个明确问题是Page.getFrameTree异常分支直接返回空failed，等价于“检查失败但全部通过”。

**根因置信边界：** PNG证明owner仍400px；源码证明候选遗漏会无声保存，且与本次现象高度吻合。但产物没有本次root frame-tree/实际expand候选日志，不能排他证明该轮一定仅是树未列OOPIF；前述_mainFrameId被子session覆写也会让同一frame在1051行被跳过，应一并修复。

最小修复建议：从root tree与已登记授权子frame/session/context构建去重的展开候选，逐个确认owner及完整可见高度，任何已授权候选无法确认都拒绝保存；root tree读取失败不得当空成功。为本场景输出候选frameId/session/parent、原owner高度、内容高度和展开后高度，下一轮同时保留首尾像素断言即可验证修复，无需扩展其他范围。

## 07851ef P2 修复实现记录（不改上节独立结论）

上节为独立复核结论，保持原文；本节只记录针对该 P2 的实现响应，最终是否闭环以同一 SHA 云端运行和独立终验为准。

- **前测异常即失败**：`DriveAsync` 的 `afterLoad` 回调改为 `FrameStyleEvidence` 载体；捕获前读取 `FrameStyleProbe` 的异常写入 `BeforeError` 并立即返回 `Harness` 失败结果，场景因此判失败，不再仅记 `AppLog` 后继续。
- **证据必须具体**：新增 `TryParseFrameStyle`，要求读取结果非空、可 `JsonDocument.Parse` 为对象、`missing != true`，且 `height`/`heightPriority`/`maxHeight`/`maxHeightPriority` 四项均为 JSON 字符串（允许空串）。空串、`{missing:true}`、字段缺失或类型不符都判为“证据无效”。
- **严格比较**：新增 `AssertFrameStyleRestored`，先校验前后两侧证据，再逐字段 `Ordinal` 比较；任一侧无效都直接写入失败详情，绝不跳过比较或宣称恢复通过。捕获后读取异常同样转为失败详情。
- **保留的断言**：真实点击/`saved`、PNG 存在与尺寸、首尾/接缝像素、身份与授权校验、跨源与 OOPIF 帧诊断均未改动，未放宽或删除任何成功场景断言。

### 基线运行暴露的 OOPIF 授权根因（新增实现响应）

对固定提交 `07851ef` 的云端运行 [35963761182](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35963761182) 复核发现：核心/构建/UI 契约/字体/导出/快照/打包/ZIP/根启动器 smoke 的原始 outcome 均为 `success`，但浏览器 E2E 原始 outcome 为 `failure`（被 `continue-on-error` 掩盖），门禁如实报 `browser=failure`；浏览器 26/27，唯一失败为 `cross-site-oopif-frame`：`查询结果未就绪：waiting|not-started`。

其帧诊断显示 OOPIF 上下文 `session=<子会话>;authorized=False;url=`（URL 为空），而 `monitor=True`。根因是 `BrowserValidation.OnAttachedToTarget` 未登记子 target 的 URL：OOPIF 的 `Page.frameNavigated` 在子会话启用 Page 之前触发会丢失，`Page.getFrameTree` 初始种子又可能早于 iframe 建立，于是该 frame 永远没有 URL、永远不通过 `IsAuthorizedFrameId`，身份探针从不进入该 frame。实现响应：attach 时登记 `targetInfo.url`，并在子会话 `Page.enable` 后查询一次 `Page.getFrameTree` 回填真实 URL（导航仍未提交时由随后到达的 `frameNavigated` 覆盖）。这只让真实 OOPIF URL 参与既有授权判定，不放宽授权规则；`cross-origin-frame`、身份/结果核验与像素断言均保留。
