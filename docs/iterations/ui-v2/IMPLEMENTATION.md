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

## 未完成 / 待云端验证

- [ ] 首次云端运行：修复编译与测试问题，取得准确 CI 结论（不得把未运行检查写成通过）。
- [ ] 真实单一窗口人工验证码流程与真机 DPI 缩放（125/150/200%）仍需人工验收。
- [ ] 独立验收子路由结论与缺陷闭环。
- [ ] Noto Sans SC 官方当前仅提供可变字体，已在 `fonts/FONTS.md` 说明；如需静态实例须另有官方来源。

## 决策记录

1. Excel 不引入 NuGet 依赖，使用 `ZipArchive` + `System.Xml.Linq` 手写 OOXML，避免离线构建风险。
2. `record.Totals` 对有关单分项的记录由可靠分项求和，属同源数据；明细结论因此显示“已识别可靠分项合计”，不宣称与独立票面总额一致（遵循 PRD §6 补充说明）。
3. 无边框窗口在顶栏右侧保留最小化/最大化/关闭按钮（设计稿未画），其余顶栏元素严格按设计；已在本文记录该偏差。
4. 浏览器 E2E 使用 `--headless=new` 受控实例与本机回环页面，不代表真实网站校验通过。
