// 离线验证状态分类、状态颜色与定时重绘，不启动浏览器或请求网络。
const fs = require("node:fs");
const vm = require("node:vm");
const assert = require("node:assert/strict");
const path = require("node:path");
const source = fs.readFileSync(path.join(__dirname, "../app/resources/web/app.js"), "utf8");
const start = source.indexOf("function classify() {");
const end = source.indexOf("\nfunction classifyPractices", start);
assert.ok(start >= 0 && end > start);
const unknown = { type: "assignment", courseId: "1", title: "查询失败", personalTask: true,
  submissionQueryFailed: true, status: "提交状态未知", deadline: 1 };
const completed = { ...unknown, title: "已提交", status: "已完成", submissionQueryFailed: false };
const pending = { ...unknown, title: "未提交", status: "未完成", submissionQueryFailed: false };
const elements = new Map();
const element = () => ({ className: "", innerHTML: "", children: [], appendChild(child) { this.children.push(child); } });
const context = vm.createContext({
  S: { snapshot: { items: [unknown, completed, pending] }, account: { state: {} }, tab: "all" },
  Date, Infinity, Set,
  ignoredSet: () => new Set(), ignoredTs: () => 0,
  itemKey: (it) => it.title, isUnfinished: (it) => ["未完成", "未交", "待做"].includes(it.status),
  assignmentDeadline: (it) => it.deadline,
  $: (id) => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); },
  document: { createElement: element }, courseNameOf: () => "测试课程", escapeHtml: (s) => s,
  timeText: () => "已截止",
});
vm.runInContext(source.slice(start, end), context);
vm.runInContext(`
  const result = classify();
  if (result.unfinished.length !== 0 || result.expired.length !== 1 || result.all.length !== 3)
    throw new Error("提交状态未知/已提交不得进入已过期");
`, context);
const renderStart = source.indexOf("function renderAssignments() {");
const renderEnd = source.indexOf("\n// 随堂练习", renderStart);
vm.runInContext(source.slice(renderStart, renderEnd), context);
vm.runInContext("renderAssignments()", context);
const html = elements.get("assign-list").children.map((el) => el.innerHTML).join("\n");
assert.match(html, /item-status-unknown[^>]*>提交状态未知/);
assert.doesNotMatch(html, /item-status-done[^>]*>提交状态未知/);
const timerStart = source.indexOf("setInterval(() => {", source.indexOf("// 截止状态会随时间变化"));
const timerEnd = source.indexOf("}, 60000);", timerStart) + "}, 60000);".length;
let callback;
const calls = [];
const timerContext = vm.createContext({
  setInterval: (cb, ms) => { assert.equal(ms, 60000); callback = cb; },
  $: () => ({ classList: { contains: () => false } }), S: { snapshot: {}, refreshing: false },
  renderAssignments: () => calls.push("assignment"), renderPractices: () => calls.push("practice"),
  renderGroupTasks: () => calls.push("groupTask"), classify: () => ({ unfinished: [] }),
  bridge: { call: (name) => { assert.equal(name, "badge"); } },
});
vm.runInContext(source.slice(timerStart, timerEnd), timerContext);
callback();
assert.deepEqual(calls, ["assignment", "practice", "groupTask"]);
timerContext.S.refreshing = true;
calls.length = 0;
callback();
assert.deepEqual(calls, []);
console.log("PASS: unknown submission classification, styling and local timer");
