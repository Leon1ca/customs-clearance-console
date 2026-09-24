# 最终验收记录（待云端产物）

当前固定源码基线：`a9cf172fef08b939396db2961accbfd5847f55b5`。前轮对 `6ef38baaa88c1aa31357cb5c77762ed23efe069f` 的既有 R4/R5/R6 结论沿用；本次只用 git show/diff 核对保留的 R5-2 修复及相关差异。不读取移动产品工作区，不在本机运行产品、编译或测试。尚未收到当前提交的最终云端产物，以下是源码结论，不能表述为最终验收通过。

## R5-2 补修源码结论：已关闭所报阻断

前轮保留的问题是：prepare 脚本吞掉滚动容器的局部错误，仍返回 prepared，C# 因而可能保存未完整展开的截图。

本提交 `BrowserScripts/capture-prepare.js` 保持修改前发布恢复账本，并将滚动容器扫描、样式读取/写入和 iframe 非预期错误加入 failures；存在错误时在返回 prepared 之前抛出 `prepare-failed`。跨源 DOM 不可访问的预期分支继续交给 CDP。

实际调用路径已核对：`BrowserValidation.cs:870–900` 在发送前登记恢复目标；`1324–1328` 将 exceptionDetails 转成异常；`709–716` 返回准备错误并阻止截图保存；`773–780` 的 finally 用独立令牌调用恢复脚本。恢复脚本按账本写回 height/max-height/overflow-y 的值与优先级、容器滚动位置及卡片状态。所报“局部准备失败仍 saved”的源码阻断已关闭。

新增 `RunPrepareFailureRestoresAsync` 使用页面中的一次性样式写入异常：反向遍历先展开正常容器，再在坏容器失败；经真实卡片点击检查准备错误、无 PNG、已改样式与 scrollTop=300 恢复、账本清理及再次点击保存。该用例路径与所报缺陷相符，运行通过与否仍待云端证据，不能以测试代码存在替代结果。

## 既有问题的源码关闭状态

| 既有问题 | 定点源码结论 |
| --- | --- |
| R4-1 按钮文字丢失 | 已修：Theme 工厂传递 text，Style 设置 Text 与 AccessibleName |
| R4-2 工具栏/页脚高度溢出 | 已修原根因：嵌套单行 TableLayoutPanel 显式 Percent 行，SearchField 垂直内边距调整；实际可见文字待同提交抓屏 |
| R4-3 KPI 重叠/待识别文件数错误 | 已修原根因：RequiredHeight 与实际绘制矩形一致，摘要高度取 KPI 下限；BatchTotalForDisplay 区分载入/处理中/完成 |
| R4-4 标题状态未接线 | 已修：UpdateSummary 调用 UpdateTitleBlock，再调用 TitleBlock.Set |
| R4-5 假四档截图尺寸覆盖 | 已修断言路径：Show 后无条件设 ClientSize，核对实际像素；加入实际屏幕抓图、屏幕尺寸报告与桌面准备。是否真正取得合格尺寸须等产物，不能凭源码关闭运行证据 |
| R4-6 Excel 小数量显示为零 | 已修原格式：数量/单价使用28位可选小数格式，并回读格式检查精度；等待实际 xlsx |
| R5-1 顶层 waiting 阻断 iframe | 已修原提前退出：VerifyStableAsync 不再把 waiting 列为终止标记，继续查授权子frame |
| R5-2 prepare失败及恢复 | a9cf172 已补错误收集并抛出，连接至拒绝保存与finally恢复；所报源码阻断已关闭，新增失败注入E2E待运行结果 |
| R5-3 iframe max-height截断及important丢失 | 已修原根因：保存并恢复height/max-height值与priority，写后等待并回读元素/内部viewport和content验证 |
| R5-4 框架授权/未知context放行 | 已修原主要执行路径：未知context拒绝；自动填写、身份读取、prepare/expand使用授权frame集合；官方swapp查询frame已加入。跨源和OOPIF实际结果待云端 |
| R5-5 HEADSET/RESET误作SET单位 | 已修该明确反例：取消任意substring，要求完整单位或严格数值前缀，数量附近的直接相邻证据 |
| R5-6 同源补值假一致 | 已修所报合并路径：持久化BackfilledFields并排除完整复核；仅复核金额使用AmountOnlyFromSecondary，标为未复核 |
| 旧明细精确值/汇总滚动 | 保留已有修复：画面/tooltip用Exact；滚动文字矩形明确加offset |
| R6 官方DOM缺失 | 已加入共用ResultSelectors，覆盖queryDetail/content-field/field-order，并加入官网错误文案；官方DOM iframe受控用例已加入，尚待实际运行结果 |

OOPIF 已增加 Target 自动附加、sessionId 命令路由、按session/context维护注册表，解决上一轮“根本没有子target路径”的实现缺失。新增跨站fixture并不能单独作为运行成功证据，本轮不扩大到新的边缘场景；待同一SHA云端报告确认该路径确实运行。

## 待补最终证据

等待最终固定SHA的云端构建/核心/原生UI/导出/浏览器E2E和便携包结果，再静态打开其PNG/XLSX/MD与环境清单复验。此处尚不批准交付。真实生产验证码/结果、真实125/150/200%显示器缩放未在本轮完成，后续报告应保持已验证与未验证的区别，不用自动环境测试替代。
