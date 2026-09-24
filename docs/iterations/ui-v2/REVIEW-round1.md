# 独立子路由预审 R1（2026-09-24）

状态：静态审查，尚未云端验证。必须由 DeepSeek 修复并提供回归证据；最终验收仍针对最终提交和产物。

1. P1 DeclarationParser.AttachRowDetails 连续收集数字 token 不校验横向间距/列边界。例商品型号 2026 在 x300、数量 10 在 x500、价格列左界600，会得到 Quantity=202610。需要按数量列边界提取，仅拼确实相邻的数字碎片，无法定位则留空。
2. P1 下一项 rowTop 使用上一项币制中心，候选包含 >= rowTop-1，上一项同高数量/单位/商品尾行可污染下一项。需依据项号和行边界隔离，测试前项有值而后项缺失。
3. P1 Reconciler.MergeSecondaryFields 独立补 Quantity 和 Unit 会混来源：主10/空，副20/KG，得到不存在的10 KG。数量单位按同来源配对，不构造两个引擎都没有的组合，保留复核差异。
4. P2 Markdown 数量最多3位小数、单价最多6位造成不可恢复精度损失：0.0004 KG->0 KG，0.1234567->0.123457。导出保留实际有效小数位，不依赖 UI 缩略格式，Excel/MD 对齐。
5. P2 Excel 未导出 VerificationUnit，未写 HasSecondaryDifference，可把金额一致但单位不同显示为可靠/双引擎一致。补齐复核单位/差异说明，区分金额一致与整项一致。
6. 验证报告过度声称：ExcelListExporter.Validate 只验表名、行数、部分编号，ExportValidation.Run 却宣称数值金额/转义检查通过。增加真正独立的生产导出回读断言：单元格类型与准确位置/值、长编号和前导零、无公式节点、特殊字符与换行、缺值为空、完整主复核字段、币种汇总、>50条隐藏/分页/重复记录。
7. PRD 已澄清 record.Totals 来源于可靠分项，不是独立票面总额。同源自比不得无条件显示“一致”。应显示“已识别可靠分项合计”及限制。
8. UI 预警（需在最终实现复核）：DesignMenu 的宽度/Measure/EnsurePopup 须以实际 anchor.DeviceDpi 缩放；AppFonts 同时加载 variable/static 时须确保正式包实际静态字重生效。

最后浏览器 E2E 必须走生产 BrowserValidation 和网页按钮到文件保存与列表回填，不可用另一套截图代码替代。要求首中尾标记/嵌套滚动、卡片不入图且排除在单号匹配外、页面恢复、错号/无结果/验证码错/超长/拒写/并发/批次隔离。原生快照不能用附件 HTML 代替，DPI 须记录实际 DeviceDpi。

云端/资源补充：当前 IMPLEMENTATION 将可变 Noto 字体作为偏差留待用户，违反已采纳的静态字体要求；这无需另行用户确认。可在云端通过 fonttools 从官方变量字体实例化 400/500/700（核对 OFL Reserved Font Name 并遵守许可）或采用官方静态发行包。验证文件完整有效、静态字重、两种字体及原文许可实际存在，不能仅写文档声称齐全。云端打包 workflow 必须验证静态字体，并正确复制 root/tools 与 app 内 OCR 资源、第三方许可和 root LICENSE；干净解压启动器烟测后再打包。真实 DPI 可在云端通过正确 DPI 上下文或虚拟显示配置验证，记录实际 DeviceDpi；不允许图片放大冒充。
