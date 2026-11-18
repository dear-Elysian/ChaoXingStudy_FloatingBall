const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../app/resources/web/app.js'), 'utf8').replace(/\r\n/g, '\n');
const html = fs.readFileSync(path.join(__dirname, '../app/resources/web/index.html'), 'utf8');
assert.match(html, /id="set-cache-enabled" checked> 启用清理缓存/);
assert.match(html, /class="cache-actions"><button id="btn-cache-clean" class="btn btn-danger">立即清理/);
const elements = new Map();
const $ = id => {
  if (!elements.has(id)) elements.set(id, { checked: true, disabled: false, value: '7' });
  return elements.get(id);
};
const radios = [{value:'time', checked:true}, {value:'size', checked:false}];
let confirmed = false;
let calls = 0;
let nextKind = 'done';
let nextReason;
let prompt;
const toasts = [];
const context = vm.createContext({
  $, S: {settings:{}}, document: {
    querySelectorAll: () => radios,
    querySelector: () => radios.find(r=>r.checked),
  },
  cxConfirm: async (...args) => { prompt = args; return confirmed; },
  bridge: { call: async action => { assert.equal(action,'cache.clean'); calls++; return {kind:nextKind,reason:nextReason,sizeMB:0,lastCleanAt:123}; } },
  saveSettings: async patch => Object.assign(context.S.settings, patch),
  toast: msg => toasts.push(msg),
});
const start = source.indexOf('// §7.6 缓存清理' + '\nlet cacheCleaning');
const end = source.indexOf('// §7.4.1 消息展示范围', start);
vm.runInContext(source.slice(start,end), context);
(async () => {
  await $('btn-cache-clean').onclick();
  assert.equal(calls,0,'取消不能发清理请求');
  assert.equal(prompt[1],true,'必须是危险确认框');
  assert.match(prompt[0],/重新计时/);
  assert.doesNotMatch(prompt[0],/保留登录态|API 配置|延后清理/);
  assert.equal(prompt[2],'确定');
  confirmed = true;
  await $('btn-cache-clean').onclick();
  assert.equal(calls,1);
  assert.match(toasts.at(-1),/重新计时/);
  assert.equal($('cache-size').textContent,'0 MB');
  nextKind = 'pending';
  nextReason = 'active_task';
  await $('btn-cache-clean').onclick();
  assert.match(toasts.at(-1),/AI帮做或刷课正在运行/);
  nextReason = 'files_in_use';
  await $('btn-cache-clean').onclick();
  assert.match(toasts.at(-1),/已清理可删除的缓存/);
  $('set-cache-enabled').checked = false;
  await $('set-cache-enabled').onchange({target:$('set-cache-enabled')});
  assert.equal(context.S.settings.cacheEnabled,false);
  assert.ok($('btn-cache-clean').disabled && $('set-cache-days').disabled && $('set-cache-limit').disabled);
  assert.ok(radios.every(r=>r.disabled));
  await $('btn-cache-clean').onclick();
  assert.equal(calls,3,'总开关关闭时不能发手动请求');
  $('set-cache-enabled').checked = true;
  await $('set-cache-enabled').onchange({target:$('set-cache-enabled')});
  assert.equal($('set-cache-days').disabled,false);
  assert.equal($('set-cache-limit').disabled,true);
  // 使用真实 saveSettings，核对保存缓存模式后重新查询统计，而非继续显示旧占用。
  let saveActions = [];
  context.bridge.call = async (action) => {
    saveActions.push(action);
    if (action === 'settings.set') return { cacheMode:'size', cacheLimitMB:400 };
    if (action === 'cache.stats') return { sizeMB:12, pending:false };
    throw new Error(action);
  };
  context.renderPanel = () => {};
  const saveStart = source.indexOf('async function saveSettings(patch) {');
  const saveEnd = source.indexOf('// ---------- 窗口缩放',saveStart);
  vm.runInContext(source.slice(saveStart,saveEnd),context);
  await context.saveSettings({cacheMode:'size'});
  assert.deepEqual(saveActions,['settings.set','cache.stats']);
  assert.equal($('cache-size').textContent,'12 MB');
  console.log('PASS: manual cache confirmation, cancel, pending messaging, enable switch and controls');
})().catch(e=>{ console.error(e); process.exitCode=1; });

