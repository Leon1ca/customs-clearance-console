> 2026-09-24 更新：`oopif-viewport-read-failure` 间歇失败已本地复现并按根因修复，Windows 云端完整门禁通过，详见 [HANDOFF-NEXT-AI.md](HANDOFF-NEXT-AI.md) 顶部“接手修复结果”。本文以下内容为历史记录，其结论只绑定文中注明的旧 SHA。

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

## add0179 已知问题定点源码复核

固定提交：`add0179e57274b66b014878e67ec65bc5ef5575c`；针对上述问题读取固定差异及上游失败处理，未本机运行产品/测试。**此次已知源码阻断路径均有对应修复，运行及#实际图仍待run35966329282产物。**

- 主/子session身份：OnFrameNavigated现在只有`sessionId is null && parentId.Length == 0`可更新顶层身份/URL/代次；非空父关联才写入，子session省略parentId不再抹掉既有关系。新增E2E实际通过子session Page.navigate，比较顶层三项、子frame URL及已知父关联，随后点击截图，覆盖本次修复路径。
- OOPIF候选遗漏：ExpandFramesAsync在root frame tree集合上合并去重所有当前附加child target，再应用原有frame授权、测量、owner查询及展开后高度/viewport检查。与上一轮只枚举root tree相比，遗漏的已授权OOPIF现在进入同一展开路径。
- 读取树失败：异常现在返回非空失败列表；上游expandedFailures非空会返回error而非保存。已知“检查失败却空列表成功”路径关闭。
- #表头继承：共享header和grid默认padding都归零，仅各非Index表头恢复8px；新增断言检查InheritedStyle而非局部赋值，并测量字形可用宽度。修复方向对应上一轮实际残留，但曾有源码检查通过而真实图失败，仍必须等#可辨认的实际screen图再关闭。

证据表述边界：新增子session导航场景目前只断言saved与文件存在，未检查导航后PNG首尾，因此其成功文本“仍可完整截图”超出该场景自身断言。已有独立OOPIF场景仍保留首尾标记检查，未被弱化；最终应实际查看两种OOPIF产物。此处不据此再扩大产品修复范围，也不以存在PNG宣称完整性已验。

## add0179 实际图复核：#关闭，两个OOPIF图底部仍空白

证据目录：`/Users/leon1ca/Documents/Codex/2026-09-24/evidence-add0179`。查看真实`ui-states/complete-1200x720-screen.png`以及普通跨源、OOPIF、子session导航后的三张实际捕获PNG；未运行产品、未扩展代码审查。

**#表头P2关闭：** 最小1200×720真实屏幕的序号表头已是完整可辨认的`#`，与上一轮两点痕迹不同，版本号也保持完整。

**OOPIF完整性P1仍未关闭：** `browser/capture-oopif-frame.png`和`browser/child-session-navigation/310120260000000025.png`均已是1241×1854，父页蓝色尾部移至y1654，证明iframe外部高度确已展开。但两图iframe约y1191–1653区域都变成白色，原应出现的内部滚动洋红尾标、结果文本行和紫色frame底标全部缺失。普通`capture-cross-frame.png`同尺寸三者均完整。

因此本轮已不是“仍400px未展开”的旧问题，而是远程frame下方内容未进入合成图。图证与屏幕外区域未绘制/提交的推断一致，但不能仅凭PNG断言具体浏览器内部机制；实现应验证滚动到可见区域后再抓取/拼接或等效可靠渲染方案，继续保留首尾/接缝与恢复断言。

新增`oopif-child-session-navigation`虽然只检查saved/文件存在而Pass，实际PNG同样有上述白块；上一节的证据缺口现在已由坏图证实。给该场景加入与OOPIF场景同等的内部尾标、结果/底部内容检查，避免错误宣称“导航后完整截图”。顶层身份保持的导航断言可以独立成立，但不能替代图像完整性。

## 82d0d81 新视口路径定点复核

固定提交：`82d0d81f0c0e77c84bab2bd67d717ae3309e4ffe`，只审新增截图路径，未读移动工作区，未运行本机产品/测试。临时Emulation扩大viewport、重读总尺寸及finally清除override的正常处理已接入；实际渲染效果等待云端。以下是现有修复遗漏，非追加新功能要求。

### P1：OOPIF原本就足够高时不会触发绘制修复

ExpandFramesAsync只在owner需要GrowFrameFunction、`expanded.Add(frameId)`之后才加入expandedOopif。若iframe原始height已能包住其内容（例如1454px），但比真实浏览器viewport高，前面的`beforeVisible >= frameContent - 2 && beforeMax >= frameContent - 2`会直接continue。其本身无需增长，却仍需要把屏幕外内容绘入截图；主捕获路径因expandedOopif空而跳过视口调整，仍采用已被上一轮PNG证明可能漏画的旧路径。

建议记录所有参与捕获的授权OOPIF及实际绘制范围，视口调整条件取决于它们是否超出当前真实viewport，不取决于此次有没有修改过iframe高度。以原始iframe足够高的同一OOPIF夹具覆盖即可，勿用“已扩大owner”的等价断言代替尾部像素。

### P1：读取真实viewport失败后仍可能保存空白成功

捕获路径读取window.innerWidth/innerHeight的异常仅记录日志，尺寸保持0；`enlargedViewport`因要求两值大于0而变false，随后照常截图与saved。缺失的是判断远程frame必须绘制范围的必要前置证据，因此有OOPIF时不能把读取失败当作“不需要修复”。应重读可靠的实际viewport或返回失败，避免恢复为上一轮白块成功路径。正常读取到非正数也须同样处理。

### 已有测试缺口保持待修

本提交未修改BrowserCaptureE2E，子session导航后仍只验证saved/文件存在；add0179该场景已产出坏图却Pass，应补同等内部尾标/结果及frame底标断言。临时viewport改变还应由本场景或既有恢复断言核对捕获后视口回到原值；finally调用clear的源码存在本身不是运行期恢复证据。

随后固定`13c1641`仅增加读取devicePixelRatio并用作override的deviceScaleFactor，未改变以上候选登记、异常继续和E2E路径；两项遗漏及测试缺口结论不变。未重复整轮审查。

## 13c1641 两张OOPIF实际图：本轮白块问题已修复

证据：`/Users/leon1ca/Documents/Codex/2026-09-24/evidence-13c1641/browser/capture-oopif-frame.png`及`child-session-navigation/310120260000000025.png`，关联run35967517077。仅查看已下载实际PNG，未读取移动源码或运行产品。

两图均清楚呈现顶部蓝标、完整内部滚动区及洋红末端、匹配本次单号的结果文本行、紫色frame底标和父页蓝色尾部。add0179从约1191px开始的白块已经消失，临时扩大实际viewport对这两个“需增长iframe”的场景有效。此前OOPIF下半部未绘制的实际症状可在这两张图上关闭。

该图证不覆盖原本足够高无需增长的OOPIF、viewport读取失败路径及导航场景自动尾部断言；这些已交DeepSeek补修，最终仍须对修复后的同一SHA产物终验。

## 重复运行35969514354：完成通知与gate释放存在明确竞态

独立读取GitHub日志：浏览器29/30，唯一失败oopif-viewport-read-failure，注入结束后第二次重试返回harness等待超时（120秒未收到生产结论）。独立git diff确认a2ddbb2到57134d0仅IMPLEMENTATION文档变化，不能因前轮30/30忽略本次失败。FINAL已暂缓，首轮成功事实与图证保留。

定点路径（固定a2ddbb2，产品代码与57134d0相同）：

1. BrowserValidation.cs:646用CompareExchange获取_captureGate；忙时直接return，无响应。
2. CaptureAndReportAsync:691先ReportAsync(result)，widget.js:119–123会把终态按钮变为可点击；692发布CaptureCompleted。
3. _captureGate要到外层Task.Run:650的finally才归零。CaptureCompleted的订阅者可在此之前运行；测试TCS虽使用RunContinuationsAsynchronously，但不保证其另一个线程晚于finally执行。
4. 快速重试时，widget.js:143–147先设capturing/disabled，再调用binding。若binding在旧gate仍为1时送达，会被646丢弃，没有新的生产任务、完成事件或按钮恢复，符合本次“120秒无生产结论”的现象。

这是源码中确定存在的时序窗口，不等同于已经从当前日志排他证明本次失败只由它导致；应在处理时记录请求到达/接受/忙拒绝及gate释放，确认具体失败序列。

最小修复：完成捕获及恢复后，在对网页发布可重试终态和对外发布完成事件之前，保证该请求持有的gate已经由唯一owner释放。不要只在原函数提前清零而仍保留外层finally无条件二次清零，否则可能把下一已接受请求的gate清掉。保留双击防重复语义，并以完成后立即重试的定点回归验证；测试侧增加固定sleep不能代替修正生产状态顺序。ClickAndAwaitAsync的事件订阅也应在结束时解除，避免连续重试积累旧处理器，但它本身不是此处无事件的已证实根因。

## 2426e1 竞态修复定点源码复核

固定`2426e1239854031b762bea38477311fca871fab8`，只审截图gate及完成/重试相关差异，未运行本机产品/测试。**已报告竞态源码路径闭环，无新定点阻断，运行待run35971352756。**

CaptureAndReportAsync在CaptureWithRetry返回/异常、以及CaptureCore的页面恢复finally全部完成后，由自身唯一finally释放gate，再调用ReportAsync及CaptureCompleted。外层Task.Run不再释放gate；全文引用检查只有一个Interlocked.Exchange清零位置，避免旧任务晚到finally清掉新请求的锁。忙请求只记录日志，不夺取或释放其他任务的锁。

测试通过真实CDP点击，完成事件同步处理器读取gate状态，可确定性识别旧版本“通知时仍持锁”条件，不靠sleep规避。视口失败用例检查拒绝时gate释放、无PNG、同会话恢复成功及接续两次快速重试，最终恰好3张成功PNG；所有等待用例finally解除事件处理器。正常重复截图用例也检查该完成契约。新增日志可核对接受→释放→结论顺序。

FINAL保持暂缓至同提交云端及重复验证完成；a2首轮30/30不冒充本修复提交运行证据。

## 07851ef P2 修复实现记录（不改上节独立结论）

上节为独立复核结论，保持原文；本节只记录针对该 P2 的实现响应，最终是否闭环以同一 SHA 云端运行和独立终验为准。

- **前测异常即失败**：`DriveAsync` 的 `afterLoad` 回调改为 `FrameStyleEvidence` 载体；捕获前读取 `FrameStyleProbe` 的异常写入 `BeforeError` 并立即返回 `Harness` 失败结果，场景因此判失败，不再仅记 `AppLog` 后继续。
- **证据必须具体**：新增 `TryParseFrameStyle`，要求读取结果非空、可 `JsonDocument.Parse` 为对象、`missing != true`，且 `height`/`heightPriority`/`maxHeight`/`maxHeightPriority` 四项均为 JSON 字符串（允许空串）。空串、`{missing:true}`、字段缺失或类型不符都判为“证据无效”。
- **严格比较**：新增 `AssertFrameStyleRestored`，先校验前后两侧证据，再逐字段 `Ordinal` 比较；任一侧无效都直接写入失败详情，绝不跳过比较或宣称恢复通过。捕获后读取异常同样转为失败详情。
- **保留的断言**：真实点击/`saved`、PNG 存在与尺寸、首尾/接缝像素、身份与授权校验、跨源与 OOPIF 帧诊断均未改动，未放宽或删除任何成功场景断言。

### 基线运行暴露的 OOPIF 授权根因（新增实现响应）

对固定提交 `07851ef` 的云端运行 [35963761182](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35963761182) 复核发现：核心/构建/UI 契约/字体/导出/快照/打包/ZIP/根启动器 smoke 的原始 outcome 均为 `success`，但浏览器 E2E 原始 outcome 为 `failure`（被 `continue-on-error` 掩盖），门禁如实报 `browser=failure`；浏览器 26/27，唯一失败为 `cross-site-oopif-frame`：`查询结果未就绪：waiting|not-started`。

其帧诊断显示 OOPIF 上下文 `session=<子会话>;authorized=False;url=`（URL 为空），而 `monitor=True`。根因是 `BrowserValidation.OnAttachedToTarget` 未登记子 target 的 URL：OOPIF 的 `Page.frameNavigated` 在子会话启用 Page 之前触发会丢失，`Page.getFrameTree` 初始种子又可能早于 iframe 建立，于是该 frame 永远没有 URL、永远不通过 `IsAuthorizedFrameId`，身份探针从不进入该 frame。实现响应：attach 时登记 `targetInfo.url`，并在子会话 `Page.enable` 后查询一次 `Page.getFrameTree` 回填真实 URL（导航仍未提交时由随后到达的 `frameNavigated` 覆盖）。这只让真实 OOPIF URL 参与既有授权判定，不放宽授权规则；`cross-origin-frame`、身份/结果核验与像素断言均保留。


## 2426e1 失败产物定位：快速重试 2 未进入可见生产请求链

证据为 `/Users/leon1ca/Documents/Codex/2026-09-24/evidence-2426e12`，对应 run35971352756、固定2426e1239854031b762bea38477311fca871fab8。浏览器29/30，唯一失败仍是 `oopif-viewport-read-failure`，具体为快速重试第2次等待120秒无生产结论；没有完成事件持锁契约失败。本次只读日志、固定源码及目录，没有本机执行产品。

`browser/app.log` 112–129行给出连续序列：07:50:26接受首次请求；07:50:27注入三次视口读取失败、释放锁、error；07:50:27接受恢复请求，07:50:29释放锁、saved；07:50:29接受快速重试1，07:50:30释放锁、saved。之后直到07:52:42下一场景才有新接受日志（frame IDs已更换）。120秒缺口中没有新请求接受、忙拒绝、异常或生产结论；全日志无忙拒绝记录。失败目录恰好两张PNG，对应恢复与快速重试1，快速重试2没有文件。

**确定结论：** 已修gate顺序确实运行，当前失败没有“已接受后捕获卡死”的证据，也没有“持锁忙拒绝”的证据；应优先沿真正点击到绑定送达前的链路定位。不能继续把旧gate窗口当成本次失败的排他根因。

**现有点击证据不足以归因：** 固定源码BrowserValidation.cs的ClickCaptureButtonAsync读取一次按钮矩形，并读取LastClickHitTarget，但不据此拒绝错误命中；随后发送三条CDP鼠标指令即返回true。BrowserCaptureE2E.cs:130–148只等完成事件，超时未保存LastClickHitTarget、实际DOM click/binding次数或点击前后viewport/矩形。CanCapture检查按钮可用与有矩形，不能证明鼠标命中按钮。故本轮可确定CDP调用返回，不能确定DOM点击处理器执行，也不能证明Emulation恢复/合成层时序就是原因。

建议下一轮只为这条已有失败路径补精确诊断：每次真实点击记录坐标、按钮矩形/disabled、window.innerWidth/innerHeight/DPR、host及shadow内实际命中、DOM鼠标/click与binding发出计数，并在超时保留这些值和截图。视口恢复后若需要等待布局可点击，应以已恢复尺寸和稳定命中证据为条件；不要用固定长sleep或改成直接调用requestCapture掩盖失败。最终通过仍待同产品SHA云端重试可靠性复验。


## 2336f69 诊断轮：首次恢复点击即超时，host命中尚不足以归因

主路由提供的云端结果：固定产品checkpoint `2336f69`，run [35972919550](https://github.com/Leon1ca/customs-clearance-console/actions/runs/35972919550) 仍为浏览器29/30，唯一失败仍是 `oopif-viewport-read-failure`。本次失败提前到首次注入error后的recovered点击，尚未进入2426那轮的快速重试2；日志记录“点击命中：host”。该recovered分支未补WidgetDiagnostic，因此更细按钮/事件状态尚无证据。本节准确记录主路由已取得的运行结果，独立子路由尚未取得并重读本轮完整产物，不冒充独立日志复核。最终结论继续暂缓。

host命中仅证明卡片容器，不能证明closed shadow内capture按钮命中、实际DOM click发生或binding发出。iframe展开/恢复及widget隐藏/恢复后，Runtime.evaluate和CDP输入返回也不足以证明合成层命中已经更新；这是待验证的定位假说，不是已证实根因。

下一轮已交实现者的有界诊断方向：所有点击和超时分支（包括initial recovery）统一取证；在widget自身可访问closed shadow的范围内验证精确按钮命中，记录DOM click和binding计数；点击前最多有限帧/短截止时间等待viewport恢复、按钮可用、矩形连续帧稳定，然后只发一次真实mouse move/down/up。超时保留前后坐标/矩形、viewport/DPR、disabled/状态、命中与计数、现场画面。保留完成事件内gate已释放契约。不得以固定sleep、盲重试或直接调用内部requestCapture刷绿。

有界渲染准备可以消除harness点击尚未呈现按钮的歧义，但rAF与DOM命中仍不是合成层提交的独立证明；若产品显示可点击却实际丢失用户点击，仍需修产品完成/渲染时序，不能单靠推迟测试宣告闭环。等待新固定产品提交与对应云端证据，不扩大源码审查。
