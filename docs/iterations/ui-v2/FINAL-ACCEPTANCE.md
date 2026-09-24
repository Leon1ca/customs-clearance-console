# 最终验收记录（待云端产物）

当前固定源码基线：`6ef38baaa88c1aa31357cb5c77762ed23efe069f`。本轮仅用 git show 复核既有 R4/R5/R6，不读取移动工作区，不在本机运行产品、编译或测试。尚未收到这一提交的最终云端产物，以下是源码结论，不能表述为最终验收通过。

## 尚未关闭的确切问题

### P1 / 既有 R5-2：准备脚本把局部失败吞掉，仍返回 prepared

`BrowserScripts/capture-prepare.js` 已将 window 恢复账本前置，这是正确修复；`BrowserValidation.cs:870–900,1324–1328` 也已在发送前登记清理目标，并识别脚本 exceptionDetails、拒绝无 prepared 标记的响应。

但是 prepare 脚本滚动容器循环的内部 catch（紧随 `el.style.setProperty('overflow-y', …)`）只注释“保留账本”后继续；包围该循环的外层 catch 也继续执行，最终正常返回 `prepared:`。因此任一滚动容器在计算样式或写入 height/max-height/overflow-y 时失败，C# 并不会收到异常，未展开的内容仍可被截图并返回 saved。这正是旧 R5-2 要求拒绝的“部分准备失败却假成功”。iframe 循环也把除跨源不可读之外的写样式异常全部吞掉。

修复只需保持当前恢复账本，把意外准备失败记录并向调用方抛出，使 CaptureCore 的错误分支与 finally 恢复实际生效。跨源 contentDocument 无法读取属于预期分支，可明确跳过并交给 CDP；不能把元素修改失败与此混为一谈。云端在一个滚动容器成功修改、后一个修改抛错的受控页面验证：不保存、已改样式/滚动恢复、卡片仍可重试。无需增设新的产品需求。

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
| R5-2 prepare失败及恢复 | 恢复账本和C#路径已修；脚本吞局部异常残留如上，未关闭 |
| R5-3 iframe max-height截断及important丢失 | 已修原根因：保存并恢复height/max-height值与priority，写后等待并回读元素/内部viewport和content验证 |
| R5-4 框架授权/未知context放行 | 已修原主要执行路径：未知context拒绝；自动填写、身份读取、prepare/expand使用授权frame集合；官方swapp查询frame已加入。跨源和OOPIF实际结果待云端 |
| R5-5 HEADSET/RESET误作SET单位 | 已修该明确反例：取消任意substring，要求完整单位或严格数值前缀，数量附近的直接相邻证据 |
| R5-6 同源补值假一致 | 已修所报合并路径：持久化BackfilledFields并排除完整复核；仅复核金额使用AmountOnlyFromSecondary，标为未复核 |
| 旧明细精确值/汇总滚动 | 保留已有修复：画面/tooltip用Exact；滚动文字矩形明确加offset |
| R6 官方DOM缺失 | 已加入共用ResultSelectors，覆盖queryDetail/content-field/field-order，并加入官网错误文案；官方DOM iframe受控用例已加入，尚待实际运行结果 |

OOPIF 已增加 Target 自动附加、sessionId 命令路由、按session/context维护注册表，解决上一轮“根本没有子target路径”的实现缺失。新增跨站fixture并不能单独作为运行成功证据，本轮不扩大到新的边缘场景；待同一SHA云端报告确认该路径确实运行。

## 待补最终证据

等待最终固定SHA的云端构建/核心/原生UI/导出/浏览器E2E和便携包结果，再静态打开其PNG/XLSX/MD与环境清单复验。此处尚不批准交付。真实生产验证码/结果、真实125/150/200%显示器缩放未在本轮完成，后续报告应保持已验证与未验证的区别，不用自动环境测试替代。
