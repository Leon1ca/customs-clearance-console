# 独立预验收：数据与 UI / 打包复核

审查基线：`4d54daf88176714cdc7138cc97a7d219a0fc7a17`。方式：只读源码审查；未运行本机编译或测试。浏览器和云端产物不在本轮范围，以下不是最终验收通过声明。

## 仍需处理的高优先级问题

### 1. 缺失数量时仍可能把商品型号当数量

`DeclarationParser.cs:417–455` 仍以“价格列左侧页面宽度 18% 内最靠右的数字”识别数量，不要求数量列表头、数量单元格或单位证据。横向碎片间距修复只防止两个数拼接，没有消除错误字段归属。例如页面宽 1000，价格列左界 600，商品名称中的型号 `2026` 位于 x450，而数量缺失时，型号满足范围条件并成为 Quantity=2026；商品名称还会被截到该型号左侧。PRD 要求不可定位字段留空。需要生产解析 token 样本覆盖“靠右数字型号、数量空缺”，并采用明确列边界或保守拒绝歧义。

### 2. Excel 的“整项一致”会给金额冲突或缺少复核的行假结论

`ExcelListExporter.cs:422` 仅依据 `HasSecondaryDifference` 写“存在差异/一致”，而 `Models.cs:106–110` 的该属性不比较 Amount/VerificationAmount，也不要求任何复核值存在。金额主值 100、复核 99、描述字段相同，会导出“整项一致=一致、金额确认=金额未确认”；只有单引擎值的旧历史也会导出“整项一致=一致”。应分开“已完整复核且一致”“有差异”“未完整复核”，金额差异必须计入整体差异。

### 3. 明细屏幕和全文提示仍把小数量显示为零

`Models.cs:81,100` 的 DisplayQuantity/DisplayUnitPrice 仍限制三/六位小数。`DetailForm.cs:236–237,283–284` 在画面和全文 tooltip 都使用 Display 版本。例如 Quantity=0.0004、Unit=KG 显示及提示为 0 KG；UnitPrice=0.1234567 显示为 0.123457。Markdown 已改 Exact，但明细用户无法读取源值。应至少全文提示使用 Exact，低于显示精度的非零数量不得呈现成无标记的零。

### 4. 金额汇总的 GDI 文本没有跟随滚动坐标

`UiV2Controls.cs:160–180` 仅调用 Graphics.TranslateTransform(0, AutoScrollPosition.Y)，随后 TextRenderer.DrawText 的坐标仍是未偏移的 rowColumns，标志也未包含 PreserveGraphicsTranslateTransform。TextRenderer 使用 GDI 绘制，不能靠默认 GDI+ 变换移动这些文字；背景与文本可能错位，底部币种仍无法正常滚动阅读。应对实际矩形应用 scroll offset，或显式保留 GDI 文本所需变换和裁剪。云端必须滚到最后一个币种并验证文字确实移动和可见，不能只断言 AutoScrollMinSize。

## 已见源码修复，仍待同提交云端证据

复制保留真实值；按币种绘制明细和汇总；未确认金额按币种包含部分确认记录；数量/单位配对合并；Markdown 精确数值；Excel 复核单位；字体云端静态化及加载检查；包根 tools 搬移、GPL/第三方许可复制和解压后根启动器测试，均已存在对应实现。不能仅凭这些实现代码把运行验收标记通过。

真实 125/150/200% DPI 仍被代码明确列为未验证项；这不是已通过证据。最终需审查准确提交对应的 Actions 日志、实际字体报告、原生截图、导出样例和便携 ZIP。

## 浏览器追加预审（72212a3）

基线：`72212a3a60da497573fda13aa849060a5b27ff0d`。本节只审浏览器源码和测试实现，尚未消费该提交的云端报告。此前 4d54daf 数据/UI 问题的关闭情况应以后续专项复核为准。

### B1. 跨源 iframe 仍会截断并返回 saved

`BrowserScripts/capture-prepare.js:29–37` 对无法读取 contentDocument 的 iframe 直接跳过，未扩展父级 iframe，也未单独捕获/合成框架完整内容。父级截图因此仍只包含原 400px iframe 可见区。当前跨源 fixture 的内容超过该高度，但 `BrowserCaptureE2E.cs:349–353` 仅断言整图顶部 500px 内的蓝色顶部标记，就宣告“跨源 frame 内容被合成进截图”；未检查紫色底部标记。需实现完整捕获，或在无法完整捕获时明确拒绝保存成功，不能用首屏证据宣告长截图通过。

### B2. 刷新后旧执行上下文会使实际截图失败

`BrowserValidation.cs:183–185,223–232` 只订阅 executionContextCreated，没有处理 executionContextDestroyed / executionContextsCleared；旧 id 留在 `_defaultContexts`。`EvaluateAllContextsAsync` 的准备重载（636–651）会执行所有旧 id，并在 bestEffort=false 时将“context 不存在”异常直接抛出。刷新测试（BrowserCaptureE2E.cs:167–182）只确认卡片存在，没有刷新后查询并截图，无法发现此问题。另 prepare 中途失败时 prepared 仍为 false（408–409），已修改的框架也不会进入 finally 恢复。需维护活跃上下文并对每个已准备上下文可靠清理。

### B3. 目标网址限制仍可被无关网页绕过

`BrowserValidation.cs:123–128` 使用 URL 包含 `singlewindow` 作为生产白名单，`https://example.com/?singlewindow` 同样被接受。Binding 检查（305–309）仅验证 context id 已知与顶层 URL 字符串，不验证实际触发框架来源；`_frameUrls` 未用于授权。需解析 Uri 并验证协议、准确主机、允许的路由及触发 context。不能以 URL 子串当目标网站身份。

### B4. 截图过程中改变单号/导航仍可能保存到原单号

`BrowserValidation.cs:385–402` 在准备前作两次稳定性检查，之后直到 `SaveAtomically`（447）不再核验单号、页面 URL、查询代次或结果快照。用户在准备等待或分片期间发起另一单号查询/导航，后续截图仍可能保存为原会话单号并回填。需在捕获期间跟踪结果/导航变化并在原子落盘前拒绝失效捕获；云端增加捕获中切换结果的可控场景。

### B5. 初次已加载框架及刷新注入仍有缺口

`InjectShellAsync`（263–269）只在顶层安装 monitor/widget；已在连接前加载完的子框架不会因为 addScriptToEvaluateOnNewDocument 而补装 monitor，框架内查询无法建立 queryAt。另 monitor.js:7–8 在新文档注入时无保护地读取 document.documentElement.scrollHeight；根元素未建立时会中断整个 shell，使后面的 widget 延迟安装逻辑根本不执行。应对已有允许框架逐一安装，并让 monitor 与 widget 一起等待有效 DOM。

### 本轮证据不足

- 成功场景仍只读 `window.__cccLastCapture` 自报的 widgetWasHidden；未比较截图中卡片像素、内滚动条原 scrollTop/scrollLeft、样式和窗口滚动位置。
- CanCapture 检查矩形存在，点击仍走 `window.__cccWidget.requestCapture()` 程序接口；尚未通过实际浏览器鼠标坐标验证可见按钮命中、覆盖或移出视口。
- 缺号场景在 DriveAsync 等待超时后可直接返回测试侧 error，尚未点击生产卡片；其“拒绝保存”结果不能证明真实点击链路。
- 未见导航离开/返回、断线恢复后截图、失效上下文、捕获中结果变化、关闭时清理、当前批次回填和切批次已排队回调的完整 E2E。当前并发用例只检查两个独立目录的文件名。

已见修复：identity 传会话单号、只从结果元素找编号、错误/loading/稳定性判断、常驻重试按钮、独立恢复 timeout、回填前检查活动会话引用。这些修复方向正确，但不能覆盖以上剩余阻断。
