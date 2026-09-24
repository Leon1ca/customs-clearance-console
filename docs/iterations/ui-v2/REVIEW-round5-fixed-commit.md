# 第五轮定点复核：6be4e7f

固定基线：`6be4e7f0fe4796eeb17f55f05959a104dea5bbb0`。仅以 git show 读取该提交，范围限定旧 preflight 四个数据/UI问题及浏览器 B1–B5；未读正在修改的产品文件，未本机编译/运行测试。以下是实际调用路径复核，不以新增测试或 IMPLEMENTATION 勾选代替结论。

结论：四个数据/UI问题中，精确显示与滚动坐标的原缺陷已在生产代码修复；数量误归属及“完整一致”判定仍有残留。B1–B5 有部分修复，但截图链路仍有明确阻断，不能整体关闭。

## 残留问题

### R5-1 / P1：查询在 iframe 时，顶层 waiting 提前结束稳定性验证

`BrowserValidation.cs:598–601` 的 VerifyStableAsync 将 `waiting|` 列入 acceptedMarkers；`975–976` 总是先在顶层执行，命中任一标记立即返回。正常“顶层外壳/卡片，查询表单和结果在 iframe”结构中，顶层 `verify.js:16` 因没有 queryAt 返回 `waiting|not-started`，不会继续访问已经 ready 的子框架。CaptureCore 在 `518–519` 随即拒绝保存。

此问题独立于已知 swapp 白名单缺项：补主机后仍会失败。应确定唯一授权的实际查询结果 frame，并对它持续执行 identity/verify；顶层没有结果只能表示继续查找，不能抢先成为最终 verdict。云端须有“顶层完全无查询和结果，只有 iframe”的真实点击成功用例。现有跨源 fixture 在 `BrowserCaptureE2E.cs:716–722` 特意把查询结果放在顶层，不能证明官方结构兼容。

### R5-2 / P1：准备失败被当作可继续，部分修改仍不会恢复

`PrepareContextsAsync` 在 `621–625,630–634` 只在收到 prepared 标记后登记上下文，其他失败记录日志后继续。`EvaluateContextAsync:955–961` 只读取 result.value、不处理 CDP 的 `exceptionDetails`，页面脚本异常会变为空串而非失败；CaptureCore 不校验准备结果，仍可截取并 saved。

`capture-prepare.js:7–24` 先直接修改所有滚动容器，直到 `41` 才将恢复记录写进 window。若处理后一元素时抛错，先前改动仅在局部 prev 中，恢复脚本完全找不到；若某子框架在 `window.scrollTo` 等后续阶段抛错，window 状态存在却没有 prepared 标记，该框架也不会进入 RestoreContexts。命令执行后、响应返回前取消同样会漏登记。

应在首次修改前创建恢复账本、每步先记后改；准备候选 frame 在发送执行前登记为需要清理，脚本异常/无成功标记应拒绝保存，并在独立恢复令牌下尝试清理所有已触达框架。CDP 的脚本异常字段是正常响应中的异常信息，不能只依赖协议顶层 error（参见官方 [Runtime 协议定义](https://raw.githubusercontent.com/ChromeDevTools/devtools-protocol/master/pdl/js_protocol.pdl) 的 evaluate/callFunctionOn）。

### R5-3 / P1：扩展 iframe 仅相信写入的高度，没有验证实际可见高度

`ExpandFramesAsync:743–751` 将 iframe.style.height 写成 document.scrollHeight，然后返回局部变量 target，用它判定成功，未检查真实 clientHeight/scrollHeight；也未移除 max-height 或处理父级裁切。构造主页面结果有效、子 iframe `height:400px;max-height:400px`、内部1200px内容，写 height!important 后 max-height 仍限制400px，但方法返回1200并允许 saved，底部内容仍被截断。`capture-prepare.js:35–36` 同源 iframe 分支同样只改 height。

应保存并暂时解除相关限制、等待布局完成，回读实际框架视口与内部展开尺寸；无法完整暴露时必须失败，不可凭目标数字宣告成功。CDP 扩展还应保存原 height 的 priority：`744` 仅保存值，`773` 恢复时一律清除 important，会改变原页面布局。云端用 max-height 约束、内滚动底部标记和原样式优先级复原验证。

### R5-4 / P1：框架授权并未贯穿读取和执行链路

白名单检查只在网页 binding 入口。`EvaluateAllContextsAsync:975–989,997–1012`、`PrepareContextsAsync:614–625`、`ExpandFramesAsync:697–733`、`EvaluateInAllFramesAsync:1028–1040` 均未调用 IsTargetFrameUrl/IsAuthorizedContext，遍历所有 frame/context。故授权顶层内的非授权 frame 仍可被读取、自动填入报关单号、修改，并可能贡献 ready 判定；绑定来源限制不能替代结果来源限制。

此外 `IsAuthorizedContext:177–179` 对未知 context ID 直接退回“顶层 URL 合法即授权”，违反未知来源应拒绝的要求。`ConnectAsync:254–257` 仅从初始 frameTree记录顶层而非全部子框架，导致已加载子框架没有来源登记；使用同一份完整 frame 注册表和准确来源授权应覆盖注入、自动填写、身份验证、准备、截图及 binding。已知官方 `swapp.singlewindow.cn` 查询 frame 缺项另已交第四轮，保持待修，本报告不重复展开策略。

### R5-5 / P2：数字型号仍可通过“单位子串”被提升为数量

裸型号2026、无任何单位的旧样例现在会保留为空，但 `DeclarationParser.cs:397–405` 允许多字符单位出现在任意单词内：`HEADSET` 包含 `SET`。在1000宽页面、价格列左界600、数量缺失、商品名称分为 `WIRELESS`、x450处 `2026`、其右侧 `HEADSET` 时，`488–497` 将后缀当合法单位，得到 Quantity=2026、Unit=HEADSET，并截短商品名。即便真实数量表头在别处也不会拒绝，因为条件是“在列内 或 有单位子串”。

应使用完整的单位 token/严格可解析的数量单位组合及相邻关系，不能以一般单词包含 SET/PCS/KG 为字段证据；优先使用可确认的表头边界，歧义留空。通过实际 AttachRowDetails 生产路径增加 HEADSET/RESET 等反例。

### R5-6 / P2：补齐自同一复核引擎的值仍被判定“完整一致”

`Models.cs:128–140` 的 IsFullyVerified 只要求 Verification 五字段有值。生产 `DeclarationReconciler.cs:234–255` 先将复核值写入 Verification*，再用同一来源填补主值的空缺。主引擎只读到金额100、描述/数量/单位/单价全部缺失，复核引擎读到金额100及完整字段时，合并后两侧相等、IsFullyVerified=true，导出仍为“一致”，但描述字段从未被两个引擎独立复核。

应保留原主字段存在性/来源，或在合并前记录逐字段复核覆盖率；填补不等于双引擎一致。纯数值100 vs99及旧历史完全无复核值的原案例已正确改成“存在差异/未完整复核”，但不能据此关闭完整业务路径。

金额口径亦须沿现有可靠性保持保守：`DeclarationReconciler.cs:147–150` 对“仅复核引擎识别”的行，把同一金额同时放在 Amount 和 VerificationAmount，IsReliable=false；新 `AmountVerification` (`Models.cs:144`) 却显示“金额一致”。应为单引擎/结构校验与真正双引擎对比保留区别，不能从两个存储槽相等推出校验完成。

## 逐项关闭状态

| 原问题 | 代码结论 |
| --- | --- |
| 数量误归属 | 部分修复，R5-5 残留 |
| 整项一致假结论 | 原金额差异/完全缺复核案例已修；R5-6 的实际合并路径仍误判 |
| 明细小数显示为零 | 原问题代码已关闭：Models.cs:83,103 使用 NumberFormats.Exact，DetailForm.cs:238–250 的完整 tooltip 也使用精确值；等待最终同提交视觉证据 |
| 汇总滚动坐标 | 原问题代码已关闭：UiV2Controls.cs:182 将 AutoScrollPosition.Y 加到文字、背景、图标使用的矩形，已不依赖 GDI+ TranslateTransform；等待云端实际滚到底截图 |
| B1 iframe完整性 | 未关闭：R5-1、R5-3，以及下述 OOPIF 边界 |
| B2 旧context/失败恢复 | destroyed/cleared 已清理（319–331），旧context固定泄漏已修；部分prepare失败仍未关闭，见R5-2 |
| B3 来源限制 | URL子串 example.com 绕过已修，frame执行/读取授权仍未关闭，见R5-4；官方swapp支持待第四轮 |
| B4 捕获中结果/导航变化 | 增加顶层generation、截图后fingerprint复核（560–583），可拒绝原永久改号/顶层导航案例；尚未绑定唯一结果frame，整体关闭受R5-1/R5-4制约。当前仅主frame导航增加generation，子frame导航不可当已覆盖 |
| B5 既有frame/早期DOM | monitor 对 documentElement 空值已防护；已有当前target默认context会执行monitor；但不是“所有允许frame”完整实现，授权/OOPIF仍待补。不能整体声明跨进程刷新注入通过 |

## OOPIF 与证据边界

本提交 CdpClient 只发送 `{id,method,params}`（BrowserValidation.cs:1162），没有 Target 自动附加、附加target事件或 sessionId 路由。ExpandFrames 的 fallback 仅在顶层会话调用 Page.createIsolatedWorld；即使拿到独立world，它只量文档高度，没有在该world运行 monitor 和内部滚动展开。不能据此证明独立进程 frame 的查询、prepare和恢复完成。现有 cross-origin fixture 使用同一127.0.0.1的不同端口（BrowserCaptureE2E.cs:401–407,598），且查询在顶层；不同源并不等于已经验证OOPIF，需记录实际子target/session及在子frame独立查询的证据。无法捕获时拒绝成功是必要保护，但不代表所需场景已完成。

云端只读状态检查：run `35958026991` 的 headSha 确为本提交；检查时 core regression 已成功，Windows job仍在准备字体，尚无本提交浏览器成功或最终产物证据。上述结论来自生产路径，未以 core 测试成功抵消缺陷。
