# 官方查询页面的静态结构补充（2026-09-24）

主路由仅下载公开HTML/JS作静态核对，未运行本机产品、未提交关单/验证码、未获取真实查询结果。以下是网站源码事实，不能替代生产人工验收。

## P1：当前结果选择器不覆盖官网实际DOM

- 官方 frame：`https://swapp.singlewindow.cn/qspserver/sw/qsp/query/view/queryDecStatus?ngBasePath=https%3A%2F%2Fswapp.singlewindow.cn%3A443%2Fqspserver%2F&topParent=trade`。
- 公开HTML的表单为 `#queryForm`，单号输入 `input#entryId[name=entryId]`，验证码 `#randomcode`，查询按钮 `#queryBtn`。单号输入本身无placeholder，邻近label为“报关单号”；现有autofill按label找并标data-customs-declaration可保留。
- 点击查询会清空 `#queryDetail`，请求成功且 `respCode === "200"` 且非空respData时，`addNotesHtml` 创建 `#release/#inspection/#cusDeclare` 等节点及 `.display-content`，再用 `processData` 渲染。
- renderer 官方来源：`https://swapp.singlewindow.cn/qspserver/static/js/qsp/util/statistics.js`。`processData` 向每个 `.display-content` 写 `.content-field`，内部包含 `.field-title/.field-title-date`、`.field-order`（响应content对象的key）、`.field-time`、`.field-text`。这些并非table、timeline li或el/ant timeline。
- 当前 `identity.js` / `verify.js` 结果选择器都是table rows或timeline item，没有 `#queryDetail .content-field`，因此即使官网已有正确查询结果，也会判无结果。所有synthetic fixture若只造table，无法证明正式页面兼容。

## 修复与证据要求

1. identity/verify共用明确且一致的结果区域定义，支持官网 `#queryDetail .display-content .content-field`，只从实际可见结果读取单号（如field-order中的18位号）；输入、页面标题、卡片仍不能当结果身份。
2. 增加仿官网DOM的受控frame fixture：entryId无placeholder+相邻label、queryBtn、queryDetail+display-content/content-field/field-order/time/text，结果在iframe中、顶层无查询结果。真实点击查询与卡片并验证完整顶部/底部。至少成功、错号、空结果、查询失败/验证码错误、结果变化后拒绝。
3. 官网明确的错误文案还有“验证码无效”“验证码输入错误”“没有符合条件的数据”，应识别为失败并可重试。不能通过放宽到输入框的号码来让官网测试通过。
4. 静态源码无法保证所有真实响应key必为18位号；若确有不含号码的结果，需要基于已授权官方端点成功请求/响应与当前查询的可靠关联另行支持，不能仅凭输入值认定成功，也不能虚称已现场验证。

本机静态资料（不要把网页反爬挑战token或整页原样提交进仓库）：
- `/Users/leon1ca/Documents/Codex/2026-09-24/planning/singlewindow-frame.html`
- `/Users/leon1ca/Documents/Codex/2026-09-24/planning/singlewindow-statistics.js`
- `/Users/leon1ca/Documents/Codex/2026-09-24/planning/official-query-reference.md`
