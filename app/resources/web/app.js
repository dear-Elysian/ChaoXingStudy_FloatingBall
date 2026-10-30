/* 前端 ↔ C# Bridge 封装 + 主面板逻辑（协议见 Bridge.cs） */
"use strict";

// ---------- Bridge ----------
const bridge = (() => {
  let seq = 0;
  const pending = new Map();
  const listeners = {};
  window.chrome.webview.addEventListener("message", (e) => {
    const m = e.data;
    if (m.id && pending.has(m.id)) {
      const p = pending.get(m.id);
      pending.delete(m.id);
      m.ok ? p.resolve(m.payload) : p.reject(m.error || { code: "INTERNAL", message: "未知错误" });
    } else if (m.event) {
      (listeners[m.event] || []).forEach((f) => f(m.payload));
    }
  });
  return {
    call(action, payload = {}) {
      const id = "u" + ++seq;
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        window.chrome.webview.postMessage({ id, action, payload });
      });
    },
    on(event, f) { (listeners[event] ??= []).push(f); },
  };
})();

// ---------- 状态 ----------
const S = {
  settings: null,
  account: null,       // {id, meta, state}
  snapshot: null,      // {fetchedAt, courses, items, failures}
  noticeDaysUsed: null, // 本次快照实际使用的时间范围：首次登录固定 30，之后取设置值
  tab: "unfinished",   // 作业 Tab：unfinished | expired | ignored | all
  practiceTab: "unfinished",   // 随堂练习 Tab：unfinished | expired | ignored | all
  examTab: "unfinished",       // 考试 Tab：unfinished | all
  discussionTab: "unfinished", // 主题讨论 Tab：unfinished（未结束）| all
  refreshing: false,
};
const $ = (id) => document.getElementById(id);
const itemKey = (it) => `${it.type}|${it.courseId}|${it.title}`;
// 忽略列表兼容两种格式：旧版纯字符串 key、新版 {key,ts}（§8.2.1 按操作时间排序）
const ignoredEntries = () => (S.account?.state?.ignored || []);
const entryKey = (e) => (typeof e === "string" ? e : e?.key);
const ignoredSet = () => new Set(ignoredEntries().map(entryKey).filter(Boolean));
const ignoredTs = (key) => {
  const e = ignoredEntries().find((x) => entryKey(x) === key);
  return e && typeof e === "object" ? (e.ts ?? 0) : 0;
};
const UNFINISHED = ["未交", "未完成", "待做"];
const isUnfinished = (it) => UNFINISHED.includes(it.status);

// ---------- 视图切换 ----------
function show(view) {
  ["view-login", "view-panel", "view-settings"].forEach((v) => $(v).classList.add("hidden"));
  $(view).classList.remove("hidden");
}

// 主面板 ↔ 设置 切换动画（§7.8）：进设置向右滑入，返回向左滑入
function switchView(fromId, toId) {
  $(fromId).classList.add("hidden");
  const el = $(toId);
  el.classList.remove("hidden", "slide-in-left", "slide-in-right");
  void el.offsetWidth;   // 强制重排，保证动画可重复播放
  el.classList.add(toId === "view-settings" ? "slide-in-right" : "slide-in-left");
}

// ---------- Toast ----------
let toastTimer = null;
function toast(msg, ms = 2600) {
  const t = $("toast");
  t.textContent = msg;
  t.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add("hidden"), ms);
}
// 立即收起 Toast：长提示（如 AI帮做进度）结束或失败时必须调用，避免残留在界面上
function hideToast() {
  clearTimeout(toastTimer);
  const t = $("toast");
  t.classList.add("hidden");
  t.textContent = "";
}

// ---------- 通用确认弹窗（替代原生 confirm：自适应内容、位于面板内容上层；danger=红色确定键） ----------
function cxConfirm(msg, danger = false) {
  return new Promise((resolve) => {
    const overlay = $("confirm-overlay");
    $("confirm-text").textContent = msg;
    const ok = $("btn-confirm-ok");
    ok.className = "btn btn-sm " + (danger ? "btn-danger" : "btn-primary");
    overlay.classList.remove("hidden");
    const done = (v) => { overlay.classList.add("hidden"); resolve(v); };
    ok.onclick = () => done(true);
    $("btn-confirm-cancel").onclick = () => done(false);
    overlay.onclick = (e) => { if (e.target === overlay) done(false); };
  });
}

// ---------- 作业分类（§v1.4） ----------
function classify() {
  const now = Date.now();
  const ignored = ignoredSet();
  const deselected = new Set(S.account?.state?.deselectedCourses || []);   // §7.4：未选课程不展示
  const r = { unfinished: [], expired: [], ignored: [], all: [] };
  for (const it of (S.snapshot?.items || [])) {
    if (it.type !== "assignment") continue;
    if (deselected.has(it.courseId)) continue;
    if (ignored.has(itemKey(it))) { r.ignored.push(it); continue; }
    r.all.push(it);
    if (isUnfinished(it)) {
      // deadline=0 表示已过期但无精确时间（附录 A.2），不能用真假值判断
      (it.deadline != null && it.deadline <= now ? r.expired : r.unfinished).push(it);
    }
  }
  // deadline=0 是「无精确时间」的哨兵值，排序时按无截止时间处理（排最后）
  const byDeadline = (a, b) => (a.deadline || Infinity) - (b.deadline || Infinity);
  r.unfinished.sort(byDeadline);
  r.expired.sort(byDeadline);
  // 已忽略：操作时间近→远（§8.2.1；旧数据无时间戳则排在后）
  r.ignored.sort((a, b) => ignoredTs(itemKey(b)) - ignoredTs(itemKey(a)));
  // 全部：未完成置顶（§v1.4）
  r.all.sort((a, b) => (isUnfinished(b) ? 1 : 0) - (isUnfinished(a) ? 1 : 0) || byDeadline(a, b));
  return r;
}

// ---------- 随堂练习分类（与作业一致的四 Tab） ----------
function classifyPractices() {
  const now = Date.now();
  const ignored = ignoredSet();
  const deselected = new Set(S.account?.state?.deselectedCourses || []);   // §7.4：未选课程不展示
  const r = { unfinished: [], expired: [], ignored: [], all: [] };
  for (const it of (S.snapshot?.items || [])) {
    if (it.type !== "practice") continue;
    if (deselected.has(it.courseId)) continue;
    if (ignored.has(itemKey(it))) { r.ignored.push(it); continue; }
    r.all.push(it);
    // 已完成：只出现在「全部」，不算未完成、也不算已过期（§8.2.1.1）
    if (it.status === "已完成") continue;
    // 已过期 = 未完成 且 已结束/截止时间已过
    if (it.status === "已结束" || (it.deadline && it.deadline <= now)) r.expired.push(it);
    else r.unfinished.push(it);
  }
  const byStart = (a, b) => (a.publishedAt || Infinity) - (b.publishedAt || Infinity);
  const rank = (it) => it.status === "进行中" ? 0 : it.status === "未开始" ? 1 : 2;
  r.unfinished.sort(byStart);                                      // 进行中在前，未开始按开始时间近→远
  r.expired.sort((a, b) => (b.deadline || 0) - (a.deadline || 0)); // 最近结束的在前
  r.ignored.sort((a, b) => ignoredTs(itemKey(b)) - ignoredTs(itemKey(a)));
  // 全部：未完成置顶；其余（已完成 / 已结束）按时间从最新到最早（§8.2.1.1）
  const isDone = (it) => it.status === "已完成" || it.status === "已结束";
  r.all.sort((a, b) =>
    (isDone(a) ? 1 : 0) - (isDone(b) ? 1 : 0)
    || (isDone(a) ? (b.deadline || 0) - (a.deadline || 0) : rank(a) - rank(b) || byStart(a, b)));
  return r;
}

// ---------- 时间文本（§7.5：两个独立开关，都不勾 = 不显示时间） ----------
function timeText(it) {
  if (!it.deadline) return "";
  const mode = S.settings?.timeDisplay ?? "remainingHours";
  if (mode === "none") return "";
  const d = new Date(it.deadline);
  const p = (n) => String(n).padStart(2, "0");
  const deadlineStr = `截止 ${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  if (mode === "deadline") return deadlineStr;
  const h = Math.floor((it.deadline - Date.now()) / 3600000);   // 向下取整（§v1.4）
  const remainStr = h >= 0 ? `剩 ${h} 小时` : "已截止";
  return mode === "both" ? `${remainStr} · ${deadlineStr}` : remainStr;
}

function courseNameOf(it) {
  return it.courseName || (S.snapshot?.courses || []).find((c) => c.courseId === it.courseId)?.courseName || "";
}

// ---------- 背景图（§7.5：cxball.assets 虚拟主机映射到 data/assets） ----------
function applyBackground() {
  const bg = S.settings?.background || {};
  const layer = $("bg-layer");
  if (bg.image) {
    layer.style.backgroundImage = `url("https://cxball.assets/${bg.image}")`;
    layer.style.opacity = ((bg.opacity ?? 75) / 100).toString();
    layer.classList.remove("hidden");
  } else {
    layer.classList.add("hidden");
  }
}

// ---------- 模块透明度（§7.5：统一调所有卡片背景，文字不跟随） ----------
function applyModuleOpacity() {
  const v = S.settings?.moduleOpacity ?? 100;
  document.documentElement.style.setProperty("--card-alpha", (v / 100).toString());
}

// ---------- 渲染：主面板 ----------
function renderPanel() {
  applyBackground();
  applyModuleOpacity();
  const meta = S.account?.meta || {};
  $("nickname").textContent = meta.nickname || (meta.uid ? `账号 ${meta.uid}` : "已登录");
  const av = $("avatar");
  if (meta.avatar) { av.style.backgroundImage = `url("${meta.avatar}")`; av.textContent = ""; }
  else { av.style.backgroundImage = ""; av.textContent = "学"; }
  $("refresh-time").textContent = S.snapshot?.fetchedAt
    ? "上次刷新 " + new Date(S.snapshot.fetchedAt).toLocaleString("zh-CN", { hour12: false })
    : "尚未刷新";

  const mods = S.settings?.modules || {};
  $("mod-practice").classList.toggle("hidden", mods.practice === false);
  $("mod-notice").classList.toggle("hidden", mods.notice === false);
  $("mod-exam").classList.toggle("hidden", mods.exam === false);
  $("mod-discussion").classList.toggle("hidden", mods.discussion === false);

  renderAssignments();
  renderPractices();
  renderNotices();
  renderExams();
  renderDiscussions();

  const cls = classify();
  bridge.call("badge", { unfinished: cls.unfinished.length });
  updateRefreshCooldown();
}

// ---------- 刷新冷却倒计时（§4.2：冷却中禁用并显示剩余秒数） ----------
let cdTimer = null;
function updateRefreshCooldown() {
  clearInterval(cdTimer); cdTimer = null;
  const btn = $("btn-refresh");
  const last = S.account?.state?.lastManualRefreshAt ?? 0;
  const throttle = (S.settings?.manualThrottleSec ?? 15) * 1000;
  const tick = () => {
    if (S.refreshing) return;   // 刷新中文案由 doRefresh / fetch.progress 控制
    const remain = Math.ceil((last + throttle - Date.now()) / 1000);
    if (remain > 0) { btn.disabled = true; btn.textContent = `冷却 ${remain}s`; }
    else {
      btn.disabled = false; btn.textContent = "刷新";
      clearInterval(cdTimer); cdTimer = null;
    }
  };
  tick();
  if (btn.disabled) cdTimer = setInterval(tick, 1000);
}

function renderAssignments() {
  const cls = classify();
  const tabs = [
    ["unfinished", `未完成 ${cls.unfinished.length}`],
    ["expired", `已过期 ${cls.expired.length}`],
    ["ignored", `已忽略 ${cls.ignored.length}`],
    ["all", `全部 ${cls.all.length}`],
  ];
  $("assign-tabs").innerHTML = "";
  for (const [key, label] of tabs) {
    const el = document.createElement("span");
    el.className = "tab" + (S.tab === key ? " active" : "");
    el.textContent = label;
    el.onclick = () => { S.tab = key; renderAssignments(); };
    $("assign-tabs").appendChild(el);
  }

  const list = $("assign-list");
  list.innerHTML = "";
  const items = cls[S.tab];
  if (!items.length) {
    list.innerHTML = `<div class="empty-tip">暂无作业</div>`;
    return;
  }
  const ignored = ignoredSet();
  for (const it of items) {
    const el = document.createElement("div");
    el.className = "item";
    const t = isUnfinished(it) && it.deadline ? timeText(it) : "";
    const deadlineHtml = t ? `<span class="item-deadline">${t}</span>` : "";
    const statusCls = isUnfinished(it) ? "" : " item-status-done";
    el.innerHTML = `
      <div class="item-title" title="${escapeHtml(it.title)}">${escapeHtml(it.title)}</div>
      <div class="item-meta">
        <span>${escapeHtml(courseNameOf(it))}</span>
        ${deadlineHtml || `<span class="${statusCls.trim()}">${escapeHtml(it.status || "")}</span>`}
      </div>`;
    el.oncontextmenu = (e) => {
      e.preventDefault();
      showCtxMenu(e.clientX, e.clientY, it, ignored.has(itemKey(it)));
    };
    list.appendChild(el);
  }
}

// 随堂练习右侧状态：进行中不写「进行中」，改写剩余时长（§8.2.1.1）
function practiceStatusText(it) {
  if (it.status !== "进行中" || !it.deadline) return it.status || "";
  const ms = it.deadline - Date.now();
  if (ms <= 0) return "已截止";
  const h = Math.floor(ms / 3600000);
  if (h >= 1) return `剩 ${h} 小时`;
  return `剩 ${Math.max(1, Math.floor(ms / 60000))} 分钟`;
}

function renderPractices() {
  const cls = classifyPractices();
  const tabs = [
    ["unfinished", `未完成 ${cls.unfinished.length}`],
    ["expired", `已过期 ${cls.expired.length}`],
    ["ignored", `已忽略 ${cls.ignored.length}`],
    ["all", `全部 ${cls.all.length}`],
  ];
  $("practice-tabs").innerHTML = "";
  for (const [key, label] of tabs) {
    const el = document.createElement("span");
    el.className = "tab" + (S.practiceTab === key ? " active" : "");
    el.textContent = label;
    el.onclick = () => { S.practiceTab = key; renderPractices(); };
    $("practice-tabs").appendChild(el);
  }

  const list = $("practice-list");
  list.innerHTML = "";
  const items = cls[S.practiceTab];
  if (!items.length) {
    list.innerHTML = `<div class="empty-tip">暂无随堂练习</div>`;
    return;
  }
  const ignored = ignoredSet();
  for (const it of items) {
    const el = document.createElement("div");
    el.className = "item";
    // 已结束且未提交 = 已过期，统一用红字写「已过期」（不回显接口的「已结束」）
    const expired = it.status === "已结束";
    const statusText = expired ? "已过期" : practiceStatusText(it);
    const statusCls = (it.status === "进行中" || expired) ? "item-deadline" : "item-status-done";
    const end = it.deadline
      ? new Date(it.deadline).toLocaleString("zh-CN", { hour12: false, month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" })
      : "";
    el.innerHTML = `
      <div class="item-title" title="${escapeHtml(it.title)}">${escapeHtml(it.title)}</div>
      <div class="item-meta">
        <span>${escapeHtml(courseNameOf(it))}</span>
        <span><span class="${statusCls}">${escapeHtml(statusText)}</span>${end ? " · " + end : ""}</span>
      </div>`;
    el.oncontextmenu = (e) => {
      e.preventDefault();
      showPracticeCtxMenu(e.clientX, e.clientY, it, ignored.has(itemKey(it)));
    };
    list.appendChild(el);
  }
}

// 考试：两 Tab（未完成 / 全部），右键仅「详情」
function renderExams() {
  const deselected = new Set(S.account?.state?.deselectedCourses || []);   // §7.4：未选课程不展示
  const all = (S.snapshot?.items || []).filter((i) =>
    i.type === "exam" && !deselected.has(i.courseId));
  const unfinished = all.filter((i) => i.status !== "已完成");

  const tabs = [
    ["unfinished", `未完成 ${unfinished.length}`],
    ["all", `全部 ${all.length}`],
  ];
  $("exam-tabs").innerHTML = "";
  for (const [key, label] of tabs) {
    const el = document.createElement("span");
    el.className = "tab" + (S.examTab === key ? " active" : "");
    el.textContent = label;
    el.onclick = () => { S.examTab = key; renderExams(); };
    $("exam-tabs").appendChild(el);
  }

  const list = $("exam-list");
  list.innerHTML = "";
  const items = S.examTab === "unfinished" ? unfinished : all;
  if (!items.length) {
    list.innerHTML = `<div class="empty-tip">暂无考试</div>`;
    return;
  }
  if (S.examTab === "all") {
    items.sort((a, b) => (a.status === "已完成" ? 1 : 0) - (b.status === "已完成" ? 1 : 0));
  }
  for (const it of items) {
    const el = document.createElement("div");
    el.className = "item";
    const done = it.status === "已完成";
    el.innerHTML = `
      <div class="item-title" title="${escapeHtml(it.title)}">${escapeHtml(it.title)}</div>
      <div class="item-meta">
        <span>${escapeHtml(courseNameOf(it))}</span>
        <span class="${done ? "item-status-done" : "item-deadline"}">${escapeHtml(it.status || "")}</span>
      </div>`;
    el.oncontextmenu = (e) => {
      e.preventDefault();
      showExamCtxMenu(e.clientX, e.clientY, it);
    };
    list.appendChild(el);
  }
}

// 主题讨论：两 Tab（未结束 / 全部），右键仅「详情」（用设置里的浏览器打开讨论详情页）
function renderDiscussions() {
  const deselected = new Set(S.account?.state?.deselectedCourses || []);   // §7.4：未选课程不展示
  const all = (S.snapshot?.items || []).filter((i) =>
    i.type === "discussion" && !deselected.has(i.courseId));
  const now = Date.now();
  // 未结束 = 未被关闭且未到截止时间；无截止时间信息的主题视为未结束（仍可回复）
  const ended = (it) => it.ended === true || (it.endAt > 0 && it.endAt <= now);
  const unfinished = all.filter((it) => !ended(it));

  const tabs = [
    ["unfinished", `未结束 ${unfinished.length}`],
    ["all", `全部 ${all.length}`],
  ];
  $("discussion-tabs").innerHTML = "";
  for (const [key, label] of tabs) {
    const el = document.createElement("span");
    el.className = "tab" + (S.discussionTab === key ? " active" : "");
    el.textContent = label;
    el.onclick = () => { S.discussionTab = key; renderDiscussions(); };
    $("discussion-tabs").appendChild(el);
  }

  const list = $("discussion-list");
  list.innerHTML = "";
  const items = S.discussionTab === "unfinished" ? unfinished : all;
  if (!items.length) {
    list.innerHTML = `<div class="empty-tip">暂无主题讨论</div>`;
    return;
  }
  // 新发布的在前；「全部」中已结束的置后
  items.sort((a, b) => {
    if (S.discussionTab === "all") {
      const d = (ended(a) ? 1 : 0) - (ended(b) ? 1 : 0);
      if (d) return d;
    }
    return (b.publishedAt || 0) - (a.publishedAt || 0);
  });
  for (const it of items) {
    const el = document.createElement("div");
    el.className = "item";
    const time = it.publishedAt
      ? new Date(it.publishedAt).toLocaleString("zh-CN", { hour12: false, month: "2-digit", day: "2-digit" })
      : "";
    const reply = it.replyCount != null ? `${it.replyCount} 回复` : (it.status || "");
    el.innerHTML = `
      <div class="item-title" title="${escapeHtml(it.title)}">${escapeHtml(it.title)}</div>
      <div class="item-meta">
        <span>${escapeHtml(courseNameOf(it))}</span>
        <span>${escapeHtml(reply)}${time ? " · " + time : ""}${ended(it) ? " · 已结束" : ""}</span>
      </div>`;
    el.oncontextmenu = (e) => {
      e.preventDefault();
      showDiscussionCtxMenu(e.clientX, e.clientY, it);
    };
    list.appendChild(el);
  }
}

function renderNotices() {
  const list = $("notice-list");
  list.innerHTML = "";
  const deselected = new Set(S.account?.state?.deselectedCourses || []);   // §7.4：未选课程不展示
  // §7.4.1：只展示最近 N 天；首次登录固定 30 天，之后按设置值。无发布时间的历史消息保留展示
  const days = S.noticeDaysUsed ?? S.settings?.noticeDays ?? 30;
  const cutoff = Date.now() - days * 86400000;
  const items = (S.snapshot?.items || []).filter((i) =>
    i.type === "notice" && !deselected.has(i.courseId) && (!i.publishedAt || i.publishedAt >= cutoff));
  if (!items.length) {
    list.innerHTML = `<div class="empty-tip">暂无消息</div>`;
    return;
  }
  for (const it of items) {
    const el = document.createElement("div");
    el.className = "item" + (it.status === "未读" ? " unread" : "");
    const time = it.publishedAt
      ? new Date(it.publishedAt).toLocaleString("zh-CN", { hour12: false, month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" })
      : "";
    el.innerHTML = `
      <div class="item-title" title="${escapeHtml(it.title)}">${it.status === "未读" ? '<span class="unread-dot"></span>' : ""}${escapeHtml(it.title)}</div>
      <div class="item-meta"><span>${escapeHtml(it.courseName || "")}</span><span>${time}</span></div>`;
    el.oncontextmenu = (e) => {
      e.preventDefault();
      showNoticeCtxMenu(e.clientX, e.clientY, it);
    };
    list.appendChild(el);
  }
}

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

// ---------- 右键菜单（§v1.4：忽略/取消忽略；§8.2.1：详情打开作业网页；§7.9：AI帮做） ----------
function showCtxMenu(x, y, it, isIgnored) {
  const menu = $("ctx-menu");
  menu.innerHTML = "";
  const mk = (label, fn, disabled = false) => {
    const el = document.createElement("div");
    el.className = "ctx-item" + (disabled ? " disabled" : "");
    el.textContent = label;
    if (!disabled) el.onclick = () => { hideCtxMenu(); fn(); };
    menu.appendChild(el);
  };
  mk(isIgnored ? "取消忽略" : "忽略", async () => {
    const r = await bridge.call("ignore.set", { key: itemKey(it), ignored: !isIgnored });
    (S.account.state ??= {}).ignored = r.ignored || [];
    renderAssignments();
  });
  mk("详情", () => openItemDetail(it));
  if (shouldOfferAiSolve(it)) mk("AI帮做", () => openAiSolve(it));
  menu.style.left = Math.min(x, innerWidth - 130) + "px";
  menu.style.top = Math.min(y, innerHeight - 150) + "px";
  menu.classList.remove("hidden");
}

// 「哪些作业允许 AI帮做」的判定规则暂未细化，当前对所有作业条目开放（§7.9）
function shouldOfferAiSolve(it) {
  return it.type === "assignment";
}

// 消息（收件箱）右键：仅「详情」，打开消息原文页面
function showNoticeCtxMenu(x, y, it) {
  const menu = $("ctx-menu");
  menu.innerHTML = "";
  const el = document.createElement("div");
  el.className = "ctx-item";
  el.textContent = "详情";
  el.onclick = () => { hideCtxMenu(); openItemDetail(it); };
  menu.appendChild(el);
  menu.style.left = Math.min(x, innerWidth - 130) + "px";
  menu.style.top = Math.min(y, innerHeight - 44) + "px";
  menu.classList.remove("hidden");
}

// 随堂练习右键：忽略 / 取消忽略 / 详情（详情用本机配置的浏览器打开对应网页）
function showPracticeCtxMenu(x, y, it, isIgnored) {
  const menu = $("ctx-menu");
  menu.innerHTML = "";
  const mk = (label, fn) => {
    const el = document.createElement("div");
    el.className = "ctx-item";
    el.textContent = label;
    el.onclick = () => { hideCtxMenu(); fn(); };
    menu.appendChild(el);
  };
  mk(isIgnored ? "取消忽略" : "忽略", async () => {
    const r = await bridge.call("ignore.set", { key: itemKey(it), ignored: !isIgnored });
    (S.account.state ??= {}).ignored = r.ignored || [];
    renderPractices();
  });
  mk("详情", () => openItemDetail(it));
  mk("AI帮做", () => openAiSolve(it));
  menu.style.left = Math.min(x, innerWidth - 130) + "px";
  menu.style.top = Math.min(y, innerHeight - 150) + "px";
  menu.classList.remove("hidden");
}

// 考试右键：仅「详情」，用设置里的浏览器打开考试页
function showExamCtxMenu(x, y, it) {
  const menu = $("ctx-menu");
  menu.innerHTML = "";
  const el = document.createElement("div");
  el.className = "ctx-item";
  el.textContent = "详情";
  el.onclick = () => { hideCtxMenu(); openItemDetail(it); };
  menu.appendChild(el);
  menu.style.left = Math.min(x, innerWidth - 130) + "px";
  menu.style.top = Math.min(y, innerHeight - 44) + "px";
  menu.classList.remove("hidden");
}

// 主题讨论右键：仅「详情」，用设置里的浏览器打开讨论详情页
function showDiscussionCtxMenu(x, y, it) {
  showExamCtxMenu(x, y, it);
}

// 作业/消息共用的「详情」：系统浏览器打开对应网页
async function openItemDetail(it) {
  if (!it.link) { toast("该条目缺少详情链接，请刷新后重试"); return; }
  try { await bridge.call("detail.open", { url: it.link }); }
  catch (e) {
    if (e.code === "BROWSER_NOT_SET") {
      // 未配置/失效：用内置模态框提示，可直接前往设置填写或点「自动」
      if (await cxConfirm("还没有可用的浏览器地址。\n请到设置页的「详情浏览器」填写浏览器路径，或点击「自动」探测本机浏览器。\n是否现在前往设置？")) {
        renderSettings();
        switchView("view-panel", "view-settings");
      }
    } else toast(e.message || "无法打开详情");
  }
}
function hideCtxMenu() { $("ctx-menu").classList.add("hidden"); }
document.addEventListener("click", hideCtxMenu);

// AI帮做（§7.9）：打开作答页 → 只读抓题 → 一次批量请求模型 → 自动填入答案。
// 不自动提交；作业与随堂练习共用入口，随堂练习走专用题型范围与系统提示词（判定在 C# 侧完成）。
async function openAiSolve(it) {
  if (!it.link) { toast("该条目缺少详情链接，请刷新后重试"); return; }
  if (S.aiSolving) { toast("AI 正在作答，请稍候…"); return; }
  S.aiSolving = true;
  toast("正在准备 AI帮做…", 120000);
  try {
    const kind = it.type === "practice" ? "practice" : "assignment";
    const r = await bridge.call("ai.solve", { url: it.link, kind });
    hideToast();   // 结束即收起进度长提示，随后只显示结果
    let msg = r.message || `AI 已填写 ${r.filled || 0} 题。请在浏览器中检查答案后手动提交。`;
    const used = (r.promptTokens || 0) + (r.completionTokens || 0);
    if (used > 0) msg += `\n本次消耗 ${used} tokens（输入 ${r.promptTokens || 0} / 输出 ${r.completionTokens || 0}）。`;
    const notes = (r.notes || []).filter(Boolean);
    if (notes.length) await cxConfirm(msg + "\n\n" + notes.join("\n"));
    else toast(msg, 8000);
  } catch (e) {
    hideToast();   // 失败时同样立即收起进度长提示，避免残留在界面上
    if (e.code === "AI_DISABLED" || e.code === "AI_NOT_CONFIGURED") {
      if (await cxConfirm(e.message + "\n是否现在前往设置配置？")) {
        renderSettings();
        switchView("view-panel", "view-settings");
      }
    } else if (e.code === "BROWSER_NOT_SET") {
      if (await cxConfirm("还没有可用的浏览器地址。\n请到设置页的「详情浏览器」填写浏览器路径，或点击「自动」探测本机浏览器。\n是否现在前往设置？")) {
        renderSettings();
        switchView("view-panel", "view-settings");
      }
    } else if (e.code === "SESSION_EXPIRED") {
      // 登录态失效：先让用户完成登录，再重新发起 AI帮做
      if (await cxConfirm((e.message || "学习通登录态已失效") + "\n是否现在登录？")) $("btn-login").click();
    } else if (e.code === "AI_BROWSER_FAILED" || e.code === "AI_UNSUPPORTED_BROWSER" ||
               e.code === "AI_ALREADY_SUBMITTED" || e.code === "AI_PAGE_TIMEOUT" || e.code === "AI_NO_QUESTIONS" ||
               e.code === "AI_QUIZ_CLOSED" || e.code === "AI_QUIZ_NOT_STARTED" || e.code === "AI_QUIZ_NO_PERMISSION") {
      await cxConfirm(e.message || "AI帮做失败");
    } else {
      toast(e.message || "AI帮做失败");
    }
  } finally {
    S.aiSolving = false;
  }
}

// ---------- 刷新 ----------
// opts.login=true → 登录事件触发的刷新（§4.1/§4.2，走登录冷却，不计入手动节流）
async function doRefresh(opts = {}) {
  const login = !!opts.login;
  if (S.refreshing) return;
  S.refreshing = true;
  const btn = $("btn-refresh");
  btn.disabled = true;
  btn.textContent = "刷新中…";
  try {
    const r = await bridge.call("refresh", { login });
    S.snapshot = r.snapshot;
    if (r.noticeDays != null) S.noticeDaysUsed = r.noticeDays;   // C# 决定首次固定 30 还是按设置
    if (!login) (S.account.state ??= {}).lastManualRefreshAt = Date.now();   // 本地同步冷却时间戳
    renderPanel();
    const fails = r.snapshot?.failures?.length || 0;
    if (fails) toast(`刷新完成，${fails} 项获取失败`);
    else if (!opts.quiet) toast("刷新完成");
  } catch (e) {
    if (e.code !== "RATE_LIMITED" && !login)                    // 手动刷新失败也占冷却（§4.3）
      (S.account.state ??= {}).lastManualRefreshAt = Date.now();
    if (!(login && e.code === "RATE_LIMITED" && opts.quiet))     // 登录冷却命中时静默沿用旧快照
      toast(e.message || "刷新失败");
  } finally {
    S.refreshing = false;
    updateRefreshCooldown();
  }
}
bridge.on("fetch.progress", (p) => {
  const btn = $("btn-refresh");
  if (!S.refreshing || !p) return;
  // 优先用跨采集器的全程进度（作业→随堂练习连续计数），避免看起来又刷了一轮
  if (p.overallTotal > 0) {
    const done = Math.min((p.overallDone ?? 0) + 1, p.overallTotal);
    btn.textContent = `${p.currentTitle || p.stage} ${done}/${p.overallTotal}`;
  } else {
    btn.textContent = `${p.currentTitle || p.stage} ${p.done}/${p.total}`;
  }
});
// AI帮做阶段提示（§7.9）：准备浏览器/加载页面/请求模型/填写答案；长 Toast 覆盖整个流程
bridge.on("ai.solve.progress", (p) => {
  if (p?.text) toast(p.text, 120000);
});

// ---------- AI 模型设置（§7.9，BYOK）----------
// 前端只拿 {enabled, endpoint, keySet, model, thinking}；API Key 永不返回，已保存时以星号占位展示。
function applyAiStatus(st) {
  const enabled = st?.enabled === true;
  $("set-ai-enabled").checked = enabled;
  $("set-ai-thinking").checked = st?.thinking === true;
  $("ai-fields").classList.toggle("disabled", !enabled);
  for (const id of ["set-ai-endpoint", "set-ai-key", "set-ai-thinking", "btn-ai-detect", "btn-ai-test"]) $(id).disabled = !enabled;
  $("set-ai-endpoint").value = st?.endpoint || "";
  $("set-ai-key").value = "";
  $("set-ai-key").placeholder = st?.keySet ? "************（已保存）" : "未设置";
  $("ai-model").textContent = st?.model || "未知模型";
}

async function refreshAiStatus() {
  try { applyAiStatus(await bridge.call("ai.status")); }
  catch { applyAiStatus(null); }
}

let aiDetecting = false;
async function detectAiModel(silent) {
  if (aiDetecting) return;
  aiDetecting = true;
  const btn = $("btn-ai-detect");
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "检测中…";
  try {
    const st = await bridge.call("ai.detect");
    S.settings.aiModel = st.model;
    $("ai-model").textContent = st.model || "未知模型";
    if (!silent) toast(st.model ? "已检测到模型：" + st.model : "未能识别模型，已显示未知模型");
  } catch (err) {
    $("ai-model").textContent = "未知模型";
    if (!silent) toast(err?.message || "模型检测失败");
  } finally {
    aiDetecting = false;
    btn.textContent = label;
    btn.disabled = S.settings?.aiEnabled !== true;
  }
}

// 测试模型（§7.9）：用最小对话请求验证接口地址、API Key 与模型是否真正可用。
// 未检测过模型时先自动探测；成功只消耗极少量 token（max_tokens=8）。
let aiTesting = false;
async function testAiModel() {
  if (aiTesting) return;
  aiTesting = true;
  const btn = $("btn-ai-test");
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "测试中…";
  try {
    const r = await bridge.call("ai.test");
    if (r.model) {
      S.settings.aiModel = r.model;
      $("ai-model").textContent = r.model;
    }
    toast(`API 可用：${r.model}（${r.latencyMs} ms）`, 3400);
  } catch (err) {
    toast("API 测试失败：" + (err?.message || "未知错误"), 4600);
  } finally {
    aiTesting = false;
    btn.textContent = label;
    btn.disabled = S.settings?.aiEnabled !== true;
  }
}

// ---------- 设置视图 ----------
async function renderSettings() {
  const s = S.settings;
  $("set-mod-practice").checked = s.modules?.practice !== false;
  $("set-mod-notice").checked = s.modules?.notice !== false;
  $("set-mod-exam").checked = s.modules?.exam !== false;
  $("set-mod-discussion").checked = s.modules?.discussion !== false;
  $("set-throttle").value = String(s.manualThrottleSec ?? 15);
  // §7.2 登录自动刷新
  $("set-login-auto").checked = s.loginAutoRefresh !== false;
  $("set-login-interval").value = String(s.loginRefreshIntervalSec ?? 15);
  $("set-login-interval").disabled = s.loginAutoRefresh === false;
  // §7.6 缓存清理（互斥置灰）
  const cmode = s.cacheMode ?? "time";
  document.querySelector(`input[name="cache-mode"][value="${cmode}"]`).checked = true;
  $("set-cache-days").value = String(s.cacheDays ?? 7);
  $("set-cache-limit").value = s.cacheLimitMB ?? 200;
  $("set-cache-days").disabled = cmode !== "time";
  $("set-cache-limit").disabled = cmode !== "size";
  bridge.call("cache.stats").then((st) => { $("cache-size").textContent = st.sizeMB + " MB"; }).catch(() => {});
  // §7.1 账号信息
  const meta2 = S.account?.meta || {};
  $("acct-nick").textContent = meta2.nickname || (meta2.uid ? `账号 ${meta2.uid}` : "已登录");
  $("acct-school").textContent = meta2.school || "";
  const av2 = $("acct-avatar");
  if (meta2.avatar) { av2.style.backgroundImage = `url("${meta2.avatar}")`; av2.textContent = ""; }
  else { av2.style.backgroundImage = ""; av2.textContent = "学"; }
  const td = s.timeDisplay ?? "remainingHours";
  $("set-time-remaining").checked = td === "remainingHours" || td === "both";
  $("set-time-deadline").checked = td === "deadline" || td === "both";

  // 悬浮球图案预览（§7.5）：未自定义时展示程序内置默认图案
  const bp = $("ball-preview");
  if (s.ballImage) {
    bp.style.backgroundImage = `url("https://cxball.assets/${s.ballImage}")`;
    bp.textContent = "";
  } else {
    bp.style.backgroundImage = 'url("https://cxball.app/icon.png")';
    bp.style.backgroundSize = "cover";
    bp.textContent = "";
  }

  // 个性化：背景图与透明度
  $("bg-current").textContent = s.background?.image ? `当前：${s.background.image}` : "未设置背景图";
  $("set-bg-opacity").value = s.background?.opacity ?? 75;
  $("bg-opacity-val").textContent = (s.background?.opacity ?? 75) + "%";
  $("set-mod-opacity").value = s.moduleOpacity ?? 100;
  $("mod-opacity-val").textContent = (s.moduleOpacity ?? 100) + "%";
  $("set-autostart").checked = s.autostart === true;   // §7.5 开机自启动，默认不勾选
  $("set-browser-path").value = s.browserPath || "";    // §8.2.1 作业详情浏览器路径
  $("set-notice-days").value = String(s.noticeDays ?? 30);   // §7.4.1 消息展示范围，默认 30 天
  await refreshAiStatus();   // §7.9 AI 模型：仅取启用状态与星号占位，不取 API Key

  // 账号清除键状态（§7.7：仅 1 个账号时「清除多余账号」置灰）
  try {
    const st = await bridge.call("accounts.stats");
    S.acctStats = st;
    $("btn-clear-surplus").disabled = !st.others;
    $("btn-clear-accounts").disabled = !st.total;
    $("btn-clear-all").disabled = !st.total && !st.ignored;
  } catch { S.acctStats = { total: 0, others: 0, ignored: 0 }; }

  // 课程选择（§7.4）：已选 / 未选 两组，未选默认折叠
  renderCourseGroups();
}

/// 渲染课程两组：已选中在上、未选中在下（后者默认折叠）。勾选切换即时移动条目。
function renderCourseGroups() {
  const courses = S.snapshot?.courses || [];
  const deselected = new Set(S.account?.state?.deselectedCourses || []);
  const checkedBox = $("course-checked"), uncheckedBox = $("course-unchecked");
  checkedBox.innerHTML = ""; uncheckedBox.innerHTML = "";
  let nChecked = 0, nUnchecked = 0;
  for (const c of courses) {
    const on = !deselected.has(c.courseId);
    const row = document.createElement("label");
    row.className = "row";
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.checked = on;
    cb.onchange = () => toggleCourse(c.courseId, cb.checked);
    row.appendChild(cb);
    row.appendChild(document.createTextNode(" " + (c.courseName || c.courseId)));
    (on ? checkedBox : uncheckedBox).appendChild(row);
    if (on) nChecked++; else nUnchecked++;
  }
  $("checked-count").textContent = nChecked;
  $("unchecked-count").textContent = nUnchecked;
  if (!courses.length) checkedBox.innerHTML = `<div class="empty-tip">暂无课程数据，请先刷新</div>`;
  // 未选为空时不给折叠入口（避免空分组）
  $("group-unchecked-wrap").style.display = nUnchecked ? "" : "none";
}

/// 单课程勾选切换：本地先行更新并即时移动分组，再落盘（失败不回滚，下次刷新以后端为准）。
async function toggleCourse(cid, checked) {
  const st = (S.account.state ??= {});
  const set = new Set(st.deselectedCourses || []);
  if (checked) set.delete(cid); else set.add(cid);
  st.deselectedCourses = [...set];
  renderCourseGroups();
  renderPanel();
  try {
    await bridge.call("courses.setDeselected", { courseIds: st.deselectedCourses });
  } catch { /* 静默：保持本地状态 */ }
}

// ---------- 登录进度条（§8.1）----------
// 登录窗操作期间不显示；用户完成登录、回到程序后才由首个 login.phase 启动。
// 宽度只由真实阶段（login.phase）与逐课程抓取进度（fetch.progress）驱动：
// 18% 回到程序 → 26% 校验 → 34% 账号信息 → 40% 课程列表
// → 45% 起按作业抓取 (done+1)/total 推进（最高 98%）→ 99% 链路完成 → 100% 前端重载完成。
// 累计进度只增不减；登录刷新只执行一次（C# login.open 内完成），前端不再重复 refresh。
let _loginProgressActive = false;
let _loginProgressPct = 0;   // 累计进度：只增不减，任何迟到/重复事件都不会让进度条回退

function setLoginProgress(percent) {
  if (!_loginProgressActive) return;
  const bar = $("login-progress-bar");
  if (!bar) return;
  _loginProgressPct = Math.max(_loginProgressPct, Math.min(100, percent));
  bar.style.width = _loginProgressPct.toFixed(1) + "%";
}

function startLoginProgress(percent = 0) {
  const wrap = $("login-progress");
  if (!wrap) return;
  _loginProgressActive = true;
  _loginProgressPct = Math.max(0, Math.min(100, percent));
  wrap.classList.remove("hidden");
  $("login-progress-bar").style.width = _loginProgressPct.toFixed(1) + "%";
}

function finishLoginProgress() {
  if (!_loginProgressActive) return;
  setLoginProgress(100);
  setTimeout(() => {
    const wrap = $("login-progress"), bar = $("login-progress-bar");
    if (wrap) wrap.classList.add("hidden");
    if (bar) bar.style.width = "0%";
    _loginProgressActive = false;
  }, 460);
}

/// 登录取消/失败：直接收起进度条，不显示「假完成」
function resetLoginProgress() {
  _loginProgressActive = false;
  _loginProgressPct = 0;
  const wrap = $("login-progress"), bar = $("login-progress-bar");
  if (wrap) wrap.classList.add("hidden");
  if (bar) bar.style.width = "0%";
}

bridge.on("login.phase", (p) => {
  const percent = p?.percent ?? 0;
  if (!_loginProgressActive) startLoginProgress(percent);
  else setLoginProgress(percent);
});
bridge.on("fetch.progress", (p) => {
  // 逐课程进度（stage=course，一门课一步）推进登录进度条；overall 字段跨模块累计
  if (!_loginProgressActive || !(p?.overallTotal > 0)) return;
  setLoginProgress(45 + 53 * Math.min(1, (p.overallDone + 1) / p.overallTotal));
});

// ---------- 事件绑定 ----------
$("btn-login").onclick = async () => {
  $("btn-login").disabled = true;
  try {
    const r = await bridge.call("login.open");
    if (r.loggedIn) {
      // §4.1：登录事件刷新已由 C# login.open 在返回前完成，这里只重载本地状态；
      // 不能再发一次 refresh，否则长刷新时冷却已过期，会重复抓取并让进度条回退。
      await reloadInit();
      S.noticeDaysUsed = r.noticeDays ?? S.settings?.noticeDays ?? 30;
      finishLoginProgress();
      await new Promise((res) => setTimeout(res, 320));   // 让 100% 可见后再切换主面板
      show("view-panel");
      renderPanel();
    } else resetLoginProgress();
  } catch (e) { toast(e.message || "登录失败"); resetLoginProgress(); }
  finally { $("btn-login").disabled = false; }
};

$("btn-refresh").onclick = () => doRefresh();
// 账号卡「详情」：用设置里的浏览器打开已登录的学习通首页（§8.2.9）
$("btn-home-web").onclick = () => openItemDetail({ link: "https://i.chaoxing.com/" });
$("btn-settings").onclick = () => { renderSettings(); switchView("view-panel", "view-settings"); };
$("btn-back").onclick = () => { switchView("view-settings", "view-panel"); renderPanel(); };

// 模块折叠（消息默认折叠，§8.2.2）：切换内容显隐并同步标题箭头
function toggleCollapse(arrowId, contentId) {
  const list = $(contentId);
  list.classList.toggle("hidden");
  const arrow = $(arrowId);
  if (arrow) arrow.textContent = list.classList.contains("hidden") ? "▸" : "▾";
}
$("exam-title").onclick = () => toggleCollapse("exam-arrow", "exam-body");
$("discussion-title").onclick = () => toggleCollapse("discussion-arrow", "discussion-body");
$("practice-title").onclick = () => toggleCollapse("practice-arrow", "practice-body");
$("notice-title").onclick = () => toggleCollapse("notice-arrow", "notice-list");

// 悬浮球右键菜单动作（§8.3：刷新 / 设置 / 退出登录）
bridge.on("menu.action", (p) => {
  if (p?.name === "refresh") doRefresh();
  else if (p?.name === "settings") { renderSettings(); switchView("view-panel", "view-settings"); }
  else if (p?.name === "logout") $("btn-logout").onclick();
});

// 账号元数据后台补抓完成（§3.1）：更新昵称/头像/学校
bridge.on("account.updated", (p) => {
  if (!p?.meta || !S.account) return;
  S.account.meta = p.meta;
  renderPanel();
  // 若用户正停留在设置页，账号卡同步刷新
  if (!$("view-settings").classList.contains("hidden")) renderSettings();
});

$("set-mod-practice").onchange = (e) => saveSettings({ modules: { practice: e.target.checked } });
$("set-mod-notice").onchange = (e) => saveSettings({ modules: { notice: e.target.checked } });
$("set-mod-exam").onchange = (e) => saveSettings({ modules: { exam: e.target.checked } });
$("set-mod-discussion").onchange = (e) => saveSettings({ modules: { discussion: e.target.checked } });
// §7.2 登录自动刷新
$("set-login-auto").onchange = (e) => {
  $("set-login-interval").disabled = !e.target.checked;
  saveSettings({ loginAutoRefresh: e.target.checked });
};
$("set-login-interval").onchange = (e) =>
  saveSettings({ loginRefreshIntervalSec: parseInt(e.target.value, 10) });
// §7.6 缓存清理
document.querySelectorAll('input[name="cache-mode"]').forEach((r) => {
  r.onchange = (e) => {
    const m = e.target.value;
    $("set-cache-days").disabled = m !== "time";
    $("set-cache-limit").disabled = m !== "size";
    saveSettings({ cacheMode: m });
  };
});
$("set-cache-days").onchange = (e) => saveSettings({ cacheDays: parseInt(e.target.value, 10) });
$("set-cache-limit").onchange = (e) => {
  const v = parseInt(e.target.value, 10);
  if (isNaN(v) || v < 100) {
    toast("缓存大小最小 100 MB");
    e.target.value = String(S.settings.cacheLimitMB ?? 200);
    return;
  }
  saveSettings({ cacheLimitMB: v });
};
// §7.4.1 消息展示范围：仅影响主面板消息栏，落盘后立即重新渲染
$("set-notice-days").onchange = (e) => {
  const days = parseInt(e.target.value, 10);
  S.noticeDaysUsed = days;   // 用户手动改设置即时预览
  saveSettings({ noticeDays: days }).then(renderNotices);
};
$("set-throttle").onchange = async (e) => {
  const v = parseInt(e.target.value, 10);
  if (v <= 10 && !(await cxConfirm("不建议：刷新间隔过短会增大平台风控判定风险。\n仍要设置为 " + v + " 秒吗？"))) {
    e.target.value = String(S.settings.manualThrottleSec ?? 15);
    return;
  }
  await saveSettings({ manualThrottleSec: v });
};
// 作业时间显示：两个勾选框组合成 remainingHours | deadline | both | none
const saveTimeDisplay = () => {
  const r = $("set-time-remaining").checked, d = $("set-time-deadline").checked;
  saveSettings({ timeDisplay: r && d ? "both" : r ? "remainingHours" : d ? "deadline" : "none" });
};
$("set-time-remaining").onchange = saveTimeDisplay;
$("set-time-deadline").onchange = saveTimeDisplay;
$("course-all").onclick = async () => {
  const st = (S.account.state ??= {});
  st.deselectedCourses = [];
  renderCourseGroups();
  try { await bridge.call("courses.setDeselected", { courseIds: [] }); } catch {}
  renderPanel();
};
$("course-none").onclick = async () => {
  const st = (S.account.state ??= {});
  st.deselectedCourses = (S.snapshot?.courses || []).map((c) => c.courseId).filter(Boolean);
  renderCourseGroups();
  try { await bridge.call("courses.setDeselected", { courseIds: st.deselectedCourses }); } catch {}
  renderPanel();
};
// 未选中分组：点击标题折叠 / 展开（默认折叠）
$("group-unchecked-title").onclick = () => {
  const wrap = $("group-unchecked-wrap");
  wrap.classList.toggle("collapsed");
  const collapsed = wrap.classList.contains("collapsed");
  $("group-unchecked-title").innerHTML =
    `未选中 (<span id="unchecked-count">${$("unchecked-count").textContent}</span>) ${collapsed ? "▸" : "▾"}`;
};

// ---------- 图片裁剪（§7.5）----------
// 流程：选图 → 内存载入（FileReader 只读字节）→ 弹窗裁剪 → 确定 → canvas 导出 → assets.upload。
// 全程不向源文件路径写入，用户原始图片零改动（无临时落盘、无需事后清理）。
const CROP_PRESET = {
  ball:       { title: "裁剪悬浮球图案", ratio: 1,     outCap: 512, mime: "image/png",  outName: "ball.png" },
  background: { title: "裁剪背景图",     ratio: 5 / 8, outCap: 800, mime: "image/jpeg", outName: "background.jpg" },
};
const CROP = { kind: null, img: null, ratio: 1, scale: 1, dispW: 0, dispH: 0, x: 0, y: 0, w: 0, h: 0, drag: null };

function openCrop(kind, file) {
  const preset = CROP_PRESET[kind];
  const fr = new FileReader();
  fr.onerror = () => toast("无法读取该图片");
  fr.onload = () => {
    const img = new Image();
    img.onerror = () => toast("无法读取该图片");
    img.onload = () => {
      if (!img.naturalWidth || !img.naturalHeight) { toast("无法读取该图片"); return; }
      CROP.kind = kind; CROP.img = img; CROP.ratio = preset.ratio;
      // 等比缩放到弹窗可视尺寸（最长边 320 × 300），显示坐标靠 scale 映射回原图
      CROP.scale = Math.min(320 / img.naturalWidth, 300 / img.naturalHeight, 1);
      CROP.dispW = Math.round(img.naturalWidth * CROP.scale);
      CROP.dispH = Math.round(img.naturalHeight * CROP.scale);
      $("crop-title").textContent = preset.title;
      $("crop-img").src = img.src;
      const stage = $("crop-stage");
      stage.style.width = CROP.dispW + "px";
      stage.style.height = CROP.dispH + "px";
      resetCropFrame();
      $("crop-overlay").classList.remove("hidden");
    };
    img.src = String(fr.result);
  };
  fr.readAsDataURL(file);
}

function resetCropFrame() {
  const r = CROP.ratio;
  let w = CROP.dispW * 0.9, h = w / r;
  if (h > CROP.dispH * 0.9) { h = CROP.dispH * 0.9; w = h * r; }
  CROP.w = Math.round(w); CROP.h = Math.round(h);
  CROP.x = Math.round((CROP.dispW - CROP.w) / 2);
  CROP.y = Math.round((CROP.dispH - CROP.h) / 2);
  paintCropFrame();
}

function paintCropFrame() {
  const f = $("crop-frame");
  f.style.left = CROP.x + "px";  f.style.top = CROP.y + "px";
  f.style.width = CROP.w + "px"; f.style.height = CROP.h + "px";
}

// 四角拖拽：锁定比例、以对角为锚点；用【位移增量】驱动尺寸，
// 而不是把指针绝对坐标当作新角点——否则按下的瞬间框会被吸附到指针处，出现跳变。
const CROP_CORNER = { se: [1, 1], ne: [1, -1], sw: [-1, 1], nw: [-1, -1] };
function resizeCropFrame(corner, d, dx, dy) {
  const [sx, sy] = CROP_CORNER[corner];
  const r = CROP.ratio;
  const ax = sx > 0 ? d.x : d.x + d.w;   // 锚点 x（对角，拖拽期间固定）
  const ay = sy > 0 ? d.y : d.y + d.h;   // 锚点 y
  const maxW = Math.min(sx > 0 ? CROP.dispW - ax : ax, (sy > 0 ? CROP.dispH - ay : ay) * r);
  // 取位移较大的轴驱动尺寸（统一换算成宽度量纲），dx=dy=0 时结果恰为 d.w，按下不跳变
  let w = Math.abs(dx) >= Math.abs(dy) * r ? d.w + sx * dx : d.w + sy * dy * r;
  w = Math.max(Math.min(w, maxW), Math.min(36, maxW));
  const h = w / r;
  CROP.w = Math.round(w); CROP.h = Math.round(h);
  CROP.x = Math.round(sx > 0 ? ax : ax - w);
  CROP.y = Math.round(sy > 0 ? ay : ay - h);
  paintCropFrame();
}

const cropStage = $("crop-stage");
cropStage.onpointerdown = (e) => {
  const handle = e.target.classList?.contains("crop-handle") ? e.target.dataset.h : null;
  CROP.drag = handle
    ? { mode: "resize", corner: handle, px: e.clientX, py: e.clientY, x: CROP.x, y: CROP.y, w: CROP.w, h: CROP.h }
    : { mode: "move", px: e.clientX, py: e.clientY, x: CROP.x, y: CROP.y };
  cropStage.setPointerCapture(e.pointerId);
  e.preventDefault();
};
cropStage.onpointermove = (e) => {
  const d = CROP.drag; if (!d) return;
  const dx = e.clientX - d.px, dy = e.clientY - d.py;
  if (d.mode === "move") {
    CROP.x = Math.round(Math.min(Math.max(d.x + dx, 0), CROP.dispW - CROP.w));
    CROP.y = Math.round(Math.min(Math.max(d.y + dy, 0), CROP.dispH - CROP.h));
    paintCropFrame();
  } else resizeCropFrame(d.corner, d, dx, dy);
};
cropStage.onpointerup = cropStage.onpointercancel = () => { CROP.drag = null; };

$("btn-crop-cancel").onclick = () => $("crop-overlay").classList.add("hidden");
$("btn-crop-reset").onclick = resetCropFrame;
$("btn-crop-ok").onclick = async () => {
  if (!CROP.img) return;
  const preset = CROP_PRESET[CROP.kind];
  // 显示坐标 → 原图自然坐标
  const sx = CROP.x / CROP.scale, sy = CROP.y / CROP.scale;
  const sw = CROP.w / CROP.scale, sh = CROP.h / CROP.scale;
  // 输出分辨率：取裁剪区实际像素，超过上限则等比缩小
  const ow = Math.min(Math.round(sw), preset.outCap);
  const oh = Math.round(ow / CROP.ratio);
  const cv = document.createElement("canvas");
  cv.width = ow; cv.height = oh;
  cv.getContext("2d").drawImage(CROP.img, sx, sy, sw, sh, 0, 0, ow, oh);
  const dataBase64 = cv.toDataURL(preset.mime, 0.92).split(",")[1] || "";
  $("crop-overlay").classList.add("hidden");
  try {
    const r = await bridge.call("assets.upload", { kind: CROP.kind, name: preset.outName, dataBase64 });
    if (CROP.kind === "ball") S.settings.ballImage = r.file;
    else (S.settings.background ??= {}).image = r.file;
    renderSettings();
    if (CROP.kind === "background") applyBackground();
    toast(CROP.kind === "ball" ? "悬浮球图案已更新" : "背景图已更新");
  } catch (err) { toast(err.message || "上传失败"); }
};

$("btn-ball-upload").onclick = () => $("file-ball").click();
$("file-ball").onchange = (e) => {
  const f = e.target.files[0];
  e.target.value = "";
  if (!f) return;
  if (f.size > 15 * 1024 * 1024) { toast("悬浮球图案最大 15MB，请更换图片"); return; }
  openCrop("ball", f);
};
$("btn-ball-reset").onclick = async () => {
  await bridge.call("assets.reset", { kind: "ball" });
  S.settings.ballImage = null;
  renderSettings();
  toast("已恢复默认图案");
};

$("btn-bg-upload").onclick = () => $("file-bg").click();
$("file-bg").onchange = async (e) => {
  const f = e.target.files[0];
  e.target.value = "";
  if (!f) return;
  if (f.size > 30 * 1024 * 1024 &&
      !(await cxConfirm("图片超过 30MB，建议更换以获得更流畅的体验。\n仍要继续使用吗？"))) return;
  openCrop("background", f);
};
$("btn-bg-reset").onclick = async () => {
  await bridge.call("assets.reset", { kind: "background" });
  if (S.settings.background) S.settings.background.image = null;
  renderSettings();
  applyBackground();
};

// 透明度滑块：拖动实时预览，松手才落盘（§7.5 默认 75%）
$("set-bg-opacity").oninput = (e) => {
  const v = parseInt(e.target.value, 10);
  $("bg-opacity-val").textContent = v + "%";
  (S.settings.background ??= {}).opacity = v;
  applyBackground();
};
$("set-bg-opacity").onchange = (e) =>
  saveSettings({ background: { opacity: parseInt(e.target.value, 10) } });

// 模块透明度滑块：拖动实时预览，松手才落盘（§7.5 默认 100%）
$("set-mod-opacity").oninput = (e) => {
  const v = parseInt(e.target.value, 10);
  $("mod-opacity-val").textContent = v + "%";
  S.settings.moduleOpacity = v;
  applyModuleOpacity();
};
$("set-mod-opacity").onchange = (e) =>
  saveSettings({ moduleOpacity: parseInt(e.target.value, 10) });

// 开机自启动（§7.5 默认不勾选）：直接写注册表并落盘
$("set-autostart").onchange = async (e) => {
  const enabled = e.target.checked;
  try {
    const s = await bridge.call("app.autostart", { enabled });
    S.settings.autostart = s.autostart === true;
    e.target.checked = S.settings.autostart;
    toast(enabled ? "已开启开机自启动" : "已关闭开机自启动");
  } catch (err) {
    e.target.checked = !enabled;
    toast("设置失败：" + (err?.message || "未知错误"));
  }
};

// 作业详情浏览器（§8.2.1）：手动填写路径即时落盘；「选择」打开文件对话框
$("set-browser-path").onchange = async (e) => {
  try {
    const s = await bridge.call("settings.set", { patch: { browserPath: e.target.value.trim() } });
    S.settings.browserPath = s.browserPath || "";
    e.target.value = S.settings.browserPath;
    toast(S.settings.browserPath ? "已保存浏览器地址" : "已清空浏览器地址");
  } catch (err) { toast("保存失败：" + (err?.message || "未知错误")); }
};
$("btn-browser-pick").onclick = async () => {
  try {
    const r = await bridge.call("browser.pick");
    if (r.path) {
      S.settings.browserPath = r.path;
      $("set-browser-path").value = r.path;
      toast("已设置浏览器：" + r.path.split("\\").pop());
    }
  } catch (e) { toast(e.message || "选择失败"); }
};

// 详情浏览器「自动」（§8.2.1）：显式探测本机浏览器并写入路径；找不到用内置弹窗提示手动填写
$("btn-browser-auto").onclick = async () => {
  try {
    const r = await bridge.call("browser.detect");
    if (r.path) {
      S.settings.browserPath = r.path;
      $("set-browser-path").value = r.path;
      toast("已自动找到浏览器：" + r.path.split("\\").pop());
    } else {
      await cxConfirm("未能自动找到浏览器。\n请手动填写浏览器程序路径，或点击「选择」指定 exe 文件。");
    }
  } catch (e) { toast(e.message || "自动探测失败"); }
};

// AI 模型（§7.9）：启用开关默认不勾选；关闭时模块内以下内容全部置灰不可操作。
// 启用前置条件：详情浏览器地址不能为空（AI帮做要用它打开作业网页，§8.2.1）。
$("set-ai-enabled").onchange = async (e) => {
  const on = e.target.checked;
  if (on) {
    const path = ($("set-browser-path").value || "").trim();
    if (!path) {
      e.target.checked = false;
      await cxConfirm("请先在「详情浏览器」中填写浏览器地址，然后再启用 AI 模型。");
      $("set-browser-path").focus();
      return;
    }
    // 输入框里刚填写但尚未失焦保存的路径，先落盘，避免启用时读到旧值
    if (path !== (S.settings.browserPath || "")) {
      try {
        const s = await bridge.call("settings.set", { patch: { browserPath: path } });
        S.settings.browserPath = s.browserPath || "";
        $("set-browser-path").value = S.settings.browserPath;
      } catch (err) {
        e.target.checked = false;
        toast("保存浏览器地址失败：" + (err?.message || "未知错误"));
        return;
      }
    }
  }
  try {
    const st = await bridge.call("ai.set", { enabled: on });
    S.settings.aiEnabled = st.enabled;
    applyAiStatus(st);
    toast(st.enabled ? "已启用 AI 模型" : "已关闭 AI 模型");
  } catch (err) {
    e.target.checked = !on;
    if (err?.code === "BROWSER_NOT_SET") await cxConfirm(err.message);
    else toast("设置失败：" + (err?.message || "未知错误"));
  }
};

// 启用思考链：默认不勾选；勾选时先提醒可能增加 token 消耗与思考时间，取消则回退
$("set-ai-thinking").onchange = async (e) => {
  const on = e.target.checked;
  if (on && !(await cxConfirm("开启思考链后可能会增加 token 消耗，思考时间也可能会增加。"))) {
    e.target.checked = false;
    return;
  }
  try {
    const st = await bridge.call("ai.set", { thinking: on });
    S.settings.aiThinking = st.thinking === true;
    applyAiStatus(st);
    toast(on ? "已开启思考链" : "已关闭思考链");
  } catch (err) {
    e.target.checked = !on;
    toast("设置失败：" + (err?.message || "未知错误"));
  }
};

// 接口地址：缺少协议时由 C# 自动补默认协议；无法归一化则保存失败并保留原值
$("set-ai-endpoint").onchange = async (e) => {
  try {
    const st = await bridge.call("ai.set", { endpoint: e.target.value.trim() });
    S.settings.aiEndpoint = st.endpoint;
    applyAiStatus(st);
    toast(st.endpoint ? "已保存 AI 接口地址" : "已清空 AI 接口地址");
    if (st.endpoint && st.keySet) detectAiModel(true);
  } catch (err) {
    e.target.value = S.settings.aiEndpoint || "";
    if (err?.code === "AI_BAD_ENDPOINT") await cxConfirm(err.message);
    else toast(err?.message || "接口地址无效");
  }
};

// API Key：只提交给 C# 加密保存；成功后输入框清空，界面仅保留星号占位，永不回显明文
$("set-ai-key").onchange = async (e) => {
  const key = e.target.value.trim();
  if (!key) { e.target.value = ""; return; }
  try {
    const st = await bridge.call("ai.key.set", { key });
    S.settings.aiModel = st.model;
    applyAiStatus(st);
    toast("API Key 已加密保存");
    if (st.endpoint) detectAiModel(true);
  } catch (err) {
    toast("保存失败：" + (err?.message || "未知错误"));
  }
};

$("btn-ai-detect").onclick = () => detectAiModel(false);
$("btn-ai-test").onclick = testAiModel;

// ---------- 三个清除键（§7.7：2 / 2 / 3 次确认） ----------
$("btn-clear-surplus").onclick = async () => {
  const n = S.acctStats?.others ?? 0;
  const nick = S.account?.meta?.nickname || S.account?.meta?.uid || "当前账号";
  if (!(await cxConfirm(`确认后本地只保留当前账号「${nick}」的登录数据，其余 ${n} 个账号的会话、作业快照与配置将全部删除，不可恢复。`, true))) return;
  if (!(await cxConfirm(`再次确认：将清除 ${n} 个多余账号，删除后不可恢复。`, true))) return;
  try {
    const r = await bridge.call("clear.surplus");
    toast(`已清除 ${r.removed ?? 0} 个多余账号`);
    renderSettings();
  } catch (e) { toast(e.message || "清除失败"); }
};

$("btn-clear-accounts").onclick = async () => {
  if (!(await cxConfirm("确认后本地全部账号数据将被删除，当前账号会被强制退出，需要重新登录才能使用。此操作不可恢复。", true))) return;
  if (!(await cxConfirm("再次确认：删除全部账号并强制退出登录？", true))) return;
  try {
    await bridge.call("clear.allAccounts");
    S.account = null;
    S.snapshot = null;
    show("view-login");
    toast("已清除全部账号，请重新登录");
  } catch (e) { toast(e.message || "清除失败"); }
};

$("btn-clear-all").onclick = async () => {
  const n = S.acctStats?.total ?? 0, m = S.acctStats?.ignored ?? 0;
  if (!(await cxConfirm(`将删除全部账号、设置、隐藏规则与日志，软件回到初始状态。\n此操作会删除 ${n} 个账号、${m} 条隐藏规则、全部浏览缓存。`, true))) return;
  if (!(await cxConfirm("删除后无法恢复，也无法通过重新登录找回设置与规则。\n作业快照与账号数据将永久丢失。", true))) return;
  if (!(await cxConfirm("确认执行删除？这是最后一次确认。\n点击后立即开始删除，无法中止。", true))) return;
  try {
    await bridge.call("clear.allData");
    toast("正在清除全部数据，软件即将退出", 6000);
  } catch (e) { toast(e.message || "清除失败"); }
};

$("btn-logout").onclick = async () => {
  if (!(await cxConfirm("退出登录将清除当前会话，需要重新登录。确定退出吗？", true))) return;
  await bridge.call("logout");
  S.account = null;
  S.snapshot = null;
  show("view-login");
};

async function saveSettings(patch) {
  S.settings = await bridge.call("settings.set", { patch });
  renderPanel();
}

// ---------- 窗口缩放（§8.2.6）：边缘/角命中后交给系统原生缩放（WebView2 挡住了 WPF 边框） ----------
document.querySelectorAll("#resize-layer .rz").forEach((el) => {
  el.addEventListener("mousedown", (e) => {
    if (e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    bridge.call("window.resize", { edge: el.dataset.edge }).catch(() => {});
  });
});

// ---------- 启动 ----------
async function reloadInit() {
  const r = await bridge.call("app.init");
  S.settings = r.settings;
  if (r.loggedIn) {
    S.account = r.account;
    S.snapshot = r.snapshot || S.snapshot;
    (S.account.state ??= {});
  }
  return r;
}

(async () => {
  const r = await reloadInit();
  if (r.loggedIn) {
    show("view-panel");
    renderPanel();
    // §3.3 / §4.1：启动恢复会话算一次登录事件 → 按设置自动刷新（登录冷却内静默沿用旧快照）
    if (S.settings?.loginAutoRefresh !== false) doRefresh({ login: true, quiet: true });
  } else {
    if (r.sessionExpired) toast("登录状态已失效，请重新登录");
    show("view-login");
  }
})();
