// ask_user 全链路端到端验证（任务 7）。
//
// 为什么需要它：VerifyWeb 只验到「后端广播了 type=ask-user 帧、POST /api/ask-user 受理」——
// **前端 JS 有没有真的弹卡、有没有真的把答案发回去**，那条 C# 测试看不见。
// 本脚本把 WebUiPage 的内嵌脚本放进 jsdom 真跑一遍：注入 SSE 帧 → 断言卡片 → 点选项/输入提交 → 断言回执。
//
// 用法（需 node ≥ 18）：
//   npm i jsdom                     # 或指定 JSDOM_PATH
//   node tools/ask-user-e2e.js      # 退 0 = 全过
//
// 说明：不需要启动宿主 —— fetch / EventSource 都是桩；测的是「前端收到帧之后的行为」。

const fs = require('fs');
const path = require('path');

const jsdomPath = process.env.JSDOM_PATH || 'jsdom';
let JSDOM, VirtualConsole;
try {
  ({ JSDOM, VirtualConsole } = require(jsdomPath));
} catch {
  console.error('缺少 jsdom：先 `npm i jsdom`，或用 JSDOM_PATH 指向已装的 jsdom');
  process.exit(2);
}

const htmlPath = path.join(__dirname, '..', 'src', 'AgentFramework.Host', 'Web', 'index.html');
const html = fs.readFileSync(htmlPath, 'utf8');

let pass = 0;
let fail = 0;
const check = (name, ok, detail) => {
  const suffix = detail ? `  (${detail})` : '';
  if (ok) {
    pass++;
    console.log(`  [PASS] ${name}${suffix}`);
  } else {
    fail++;
    console.log(`  [FAIL] ${name}${suffix}`);
  }
};

const posts = [];
const sources = [];
let askUserReply = { ok: true };

const payloadFor = (url) => {
  if (url.includes('/api/ask-user')) return askUserReply;
  if (url.includes('/api/status')) {
    return {
      sessionId: 'default', turnRunning: false, projectDir: '/tmp/ws', workspaceRoot: '/tmp/ws',
      tools: [], plugins: [], skipped: [], themes: [],
    };
  }
  if (url.includes('/api/sessions')) return { sessions: [], current: 'default' };
  if (url.includes('/api/pending')) return { pending: [] };
  if (url.includes('/api/modes')) return { modes: [] };
  if (url.includes('/api/toolsets')) return { toolsets: [] };
  if (url.includes('/api/skills')) return { skills: [] };
  if (url.includes('/api/subagents')) return { ok: true, agents: [] };
  return { ok: true };
};

const virtualConsole = new VirtualConsole();
virtualConsole.on('jsdomError', () => { /* 页面初始化里的杂音不当作失败 */ });

const dom = new JSDOM(html, {
  runScripts: 'dangerously',
  pretendToBeVisual: true,
  url: 'http://localhost:7803/',
  virtualConsole,
  beforeParse(window) {
    class FakeEventSource {
      constructor(url) {
        this.url = url;
        this.onmessage = null;
        this.onopen = null;
        this.onerror = null;
        sources.push(this);
      }
      addEventListener(type, handler) { if (type === 'message') this.onmessage = handler; }
      close() { }
    }

    window.EventSource = FakeEventSource;
    window.fetch = async (url, opts) => {
      posts.push({ url: String(url), body: opts && opts.body ? String(opts.body) : null });
      const payload = payloadFor(String(url));
      return { ok: true, status: 200, json: async () => payload, text: async () => JSON.stringify(payload) };
    };
    window.alert = (message) => { window.__alerts = (window.__alerts || []).concat(String(message)); };
    window.scrollTo = () => { };
    // jsdom 不实现 CSS.escape（真实浏览器都有）—— 补个够用的桩，免得它把测试挡在门外。
    window.CSS = window.CSS || {
      escape: (value) => String(value).replace(/[^a-zA-Z0-9_\u00a0-\uffff-]/g, (c) => '\\' + c),
    };
    window.requestAnimationFrame = (cb) => setTimeout(() => cb(Date.now()), 0);
    window.matchMedia = () => ({
      matches: false, addListener() { }, removeListener() { }, addEventListener() { }, removeEventListener() { },
    });
  },
});

const w = dom.window;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const frame = (payload) => ({ data: JSON.stringify(payload) });
const cards = () => [...w.document.querySelectorAll('.approval')];

(async () => {
  console.log('═══ ask_user 端到端验证（jsdom）═══');
  await sleep(400);

  const source = sources[0];
  check('页面加载后建立了 SSE 连接', !!source, `${sources.length} 个实例`);

  w.openWindow();
  await w.loadStatus().catch(() => { });
  await sleep(150);

  // ── 1. 外层帧 → 弹卡 ───────────────────────────────────
  source.onmessage(frame({
    type: 'ask-user', id: 'q1', question: '选哪个方案？', context: '这是上下文',
    options: ['甲方案', '乙方案'], sessionId: 'default',
  }));
  await sleep(50);

  const first = cards()[0];
  const firstText = first ? first.textContent : '';
  check('★ 模型提问 → 弹出提问卡片', cards().length === 1, `${cards().length} 张`);
  check('★ 卡片标题是「模型在问你」', firstText.includes('模型在问你'), firstText.slice(0, 24));
  check('★ 卡片显示问题与上下文', firstText.includes('选哪个方案？') && firstText.includes('这是上下文'));
  check('★ 给了选项就渲染成按钮（且不挡自由作答）',
    !!first && first.querySelectorAll('button.opt').length === 2 && !!first.querySelector('input'));

  // ── 2. 点选项 → 回执 ───────────────────────────────────
  first.querySelectorAll('button.opt')[0].click();
  await sleep(120);

  const optionPost = posts.find((p) => p.url.includes('/api/ask-user'));
  check('★ 点选项 → POST /api/ask-user（带 id 与答案）',
    !!optionPost && optionPost.body.includes('"id":"q1"') && optionPost.body.includes('甲方案'),
    optionPost ? optionPost.body : '没发出回执');
  check('★ 提交后卡片收口（按钮禁用、状态如实）',
    first.classList.contains('done') || first.querySelector('.state').textContent.includes('已回答'),
    first.querySelector('.state').textContent);

  // ── 3. 自由输入 → 回执 ─────────────────────────────────
  posts.length = 0;
  source.onmessage(frame({ type: 'ask-user', id: 'q2', question: '还有什么要补充？', sessionId: 'default' }));
  await sleep(50);

  const second = cards()[cards().length - 1];
  const secondInput = second.querySelector('input');
  secondInput.value = '补充一句：注意缓存';
  second.querySelector('button:not(.opt)').click();
  await sleep(120);

  const freePost = posts.find((p) => p.url.includes('/api/ask-user'));
  check('★ 自由输入 → POST /api/ask-user（带原文）',
    !!freePost && freePost.body.includes('补充一句：注意缓存'), freePost ? freePost.body : '没发出回执');

  // ── 4. 提交失败不静默（文案可诊断）─────────────────────
  askUserReply = { ok: false, error: '等待回答超时' };
  posts.length = 0;
  source.onmessage(frame({ type: 'ask-user', id: 'q3', question: '会失败的一次', sessionId: 'default' }));
  await sleep(50);

  const third = cards()[cards().length - 1];
  third.querySelector('input').value = '随便';
  third.querySelector('button:not(.opt)').click();
  await sleep(120);

  const thirdState = third.querySelector('.state').textContent;
  check('★ 提交失败给出可诊断文案（不是静默）',
    thirdState.includes('✗') && thirdState.includes('超时'), thirdState);
  askUserReply = { ok: true };

  // ── 5. 别处会话的提问：不串会话 ────────────────────────
  const beforeCount = cards().length;
  source.onmessage(frame({ type: 'ask-user', id: 'q4', question: '别的会话的提问', sessionId: 'other' }));
  await sleep(50);
  check('★ 别处会话的提问不在本页弹卡（不串会话）',
    cards().length === beforeCount, `${beforeCount} → ${cards().length}`);

  // ── 6. 老帧兼容：不带 sessionId 也算当前会话 ────────────
  source.onmessage(frame({ type: 'ask-user', id: 'q5', question: '无 sessionId 的帧' }));
  await sleep(50);
  check('老帧（不带 sessionId）按当前会话处理，照常弹卡',
    cards().length === beforeCount + 1, `${cards().length} 张`);

  console.log(`\n结果：${pass} 通过 / ${fail} 失败`);
  dom.window.close();
  process.exit(fail === 0 ? 0 : 1);
})();
