# 独立子路由浏览器预审 R2（待修复）

1. P1 BrowserValidation.cs 的截图核心及 ProbeIdentityAsync 调用 BrowserScript("identity") 未传 DeclarationNo。helper把占位符变成null，identity.js中 no=null，正常18位queryNo永远 mismatch|query-number。两处均须传会话单号。
2. P1 identity.js bodyNumbers回退错误：结果无编号而页眉含正确单号可被当ready；旧probe的验证码错误/loading/baseline/稳定性保护丢失，旧结果未变的新查询失败可能保存假成功。只接受真实结果范围内单号，并保留失败/稳定性/新查询结果约束。
3. P1 网页截图按钮只在.ccc-s-idle，error/mismatch/saved状态隐藏idle且无重试/重新截图。过早点击后完成查询也无法再次点。状态恢复与重试/重新截图必须可用。
4. iframe路径待核对：身份探测在isolated world读取不到default world monitor，capture prepare只操作顶层，iframe的嵌套滚动结果可能漏截。必须增加真实frame场景并通过生产通路。

以上为只读静态检查，最终需云端回归。后续完整预审反馈将补充。

补充完整预审：
5. P1 MainForm 的 Saved 事件 BeginInvoke 委托执行时仅按单号更新当前 records。切批次取消订阅不能撤回排队委托；新批次有同号会收到旧截图。携带批次代次，执行时检查 sender 会话仍有效。
6. P1 Page.addScriptToEvaluateOnNewDocument 注入所有文档，binding处理未校验来源context/URL；WaitForPage甚至回退任意page。导航离开目标站仍能请求截图/打开目录。严格限制目标URL/测试URL、context、会话；离开禁用，不连接无关page。
7. P2 widget 文档初始化时直接 document.documentElement.appendChild，根尚未存在会异常却提前installed标记。等待挂载节点，只在成功安装后标记。
8. P2 prepare 没存内部滚动容器scrollTop/scrollLeft；finally恢复使用已取消token；断线仅显示正在恢复但无恢复任务。需各容器恢复、独立恢复超时、有效重连及刷新后的真实按钮测试。
9. E2E不足：当前页面不足12000px未走拼接；只检首尾不检内部末端/iframe/接缝；只检查自报JSON true不能证明卡片排除与页面恢复；ClickCaptureButtonAsync调用隐藏按钮可掩盖无可见重试入口。必须模拟可见可点击用户入口并断言输出实际像素/生产状态，不点不保存、缺号/验证码错/旧结果、拒写、导航、断线、同号回填、切批次都要覆盖。

截图前后都检查身份；iframe同源/跨源用正确默认执行上下文读取monitor，逐frame准备与恢复，frame内容完整捕获，不能用isolated world读取另一个world的JS状态。
