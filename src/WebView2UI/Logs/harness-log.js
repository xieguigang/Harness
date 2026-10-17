/* ==========================================================================
   Harness 日志查看器 —— 渲染引擎
   所有数据都通过宿主对象 log_host 读取（host object 只返回字符串，json 由
   宿主侧解析好），页面本身只负责渲染，因此 5MB 的 turns.json 也能秒开。
   ========================================================================== */
(function () {
    'use strict';

    /* ------------------------------------------------------------ helpers */

    const $ = (id) => document.getElementById(id);

    const ESC = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

    function esc(v) {
        if (v === null || v === undefined) return '';
        return String(v).replace(/[&<>"']/g, (c) => ESC[c]);
    }

    function num(v, digits) {
        if (v === null || v === undefined || isNaN(v)) return '—';
        return Number(v).toLocaleString('en-US', { maximumFractionDigits: digits || 0 });
    }

    function ms(v) {
        if (v === null || v === undefined || isNaN(v)) return '—';
        if (v < 1000) return Number(v).toFixed(v < 10 ? 2 : 0) + ' ms';
        return (Number(v) / 1000).toFixed(2) + ' s';
    }

    function bytes(n) {
        if (!n) return '0 B';
        const u = ['B', 'KB', 'MB', 'GB'];
        let i = 0, v = Number(n);
        while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
        return (i === 0 ? v : v.toFixed(1)) + ' ' + u[i];
    }

    function clock(iso) {
        if (!iso) return '—';
        const d = new Date(iso);
        if (isNaN(d.getTime())) return iso;
        const p = (n) => String(n).padStart(2, '0');
        return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
    }

    function short(s, n) {
        if (!s) return '';
        s = String(s);
        return s.length <= n ? s : s.slice(0, n) + '…';
    }

    function badge(kind, text) {
        return `<span class="badge b-${kind}">${esc(text)}</span>`;
    }

    function statusBadge(s) {
        switch (s) {
            case 'ok': return badge('ok', 'ok');
            case 'error': return badge('error', 'error');
            case 'denied': return badge('denied', 'denied');
            case 'failed': return badge('failed', 'failed');
            case 'max_rounds_exceeded': return badge('denied', 'max rounds');
            default: return badge('muted', s || '—');
        }
    }

    function sourceBadge(s) {
        switch (s) {
            case 'sandbox': case 'registry': return badge('primary', s);
            case 'mcp': case 'tool_invoke': return badge('info', s);
            case 'skill': return badge('dsml', s);
            case 'missing': return badge('error', s);
            default: return badge('muted', s || '—');
        }
    }

    function roleBadge(r) {
        switch (r) {
            case 'user': return badge('primary', 'user');
            case 'assistant': return badge('ok', 'assistant');
            case 'tool': return badge('denied', 'tool');
            case 'system': return badge('muted', 'system');
            default: return badge('muted', r || '—');
        }
    }

    /* ------------------------------------------------------ host bridge */

    function getHost() {
        try {
            if (window.chrome && window.chrome.webview && window.chrome.webview.hostObjects) {
                return window.chrome.webview.hostObjects.log_host || null;
            }
        } catch (e) { /* not running inside webview2 */ }
        return null;
    }

    let hostReady = false;

    async function api(name) {
        const host = getHost();
        if (!host) throw new Error('页面没有连接到宿主：log_host 不可用');
        const args = Array.prototype.slice.call(arguments, 1);
        let raw;
        try {
            raw = host[name].apply(host, args);
        } catch (e) {
            throw new Error((e && e.message) || String(e));
        }
        if (raw && typeof raw.then === 'function') raw = await raw;
        if (typeof raw !== 'string') return raw;
        return JSON.parse(raw);
    }

    /* --------------------------------------------------------- ui state */

    const state = {
        root: '',
        sessions: [],
        textFiles: [],
        search: '',
        selected: null,          // {kind, sessionId}
        summary: null,
        linked: null,
        files: [],
        tab: 'overview',
        page: { name: '', offset: 0, limit: 30, total: 0 },
        filter: {},
        turns: {}                // seq -> full turn record
    };

    const TOOL_TABS = [
        { id: 'overview', label: '概览' },
        { id: 'calls', label: '工具调用', file: 'tool-calls.json' },
        { id: 'permissions', label: '权限', file: 'permissions.json' },
        { id: 'tools', label: '工具表', file: 'tools.json' },
        { id: 'raw', label: '原始 JSON' }
    ];

    const LLM_TABS = [
        { id: 'overview', label: '概览' },
        { id: 'turns', label: '对话' },
        { id: 'calls', label: '工具调用', file: 'tool-calls.json' },
        { id: 'messages', label: '消息', file: 'messages.json' },
        { id: 'usage', label: '用量', file: 'usage.json' },
        { id: 'raw', label: '原始 JSON' }
    ];

    function tabs() {
        const base = state.selected && state.selected.kind === 'llm-sessions' ? LLM_TABS : TOOL_TABS;
        if (state.textFiles.length === 0) return base;
        return base.concat([{ id: 'text', label: '文本日志' }]);
    }

    function tabDef(id) {
        return tabs().find((t) => t.id === id) || null;
    }

    /* ------------------------------------------------------- ui feedback */

    let errTimer = null;

    function showError(msg) {
        const bar = $('errorBar');
        bar.textContent = msg;
        bar.hidden = false;
        if (errTimer) clearTimeout(errTimer);
        errTimer = setTimeout(() => { bar.hidden = true; }, 6000);
    }

    function setStatus(kind, text) {
        const dot = $('statusDot');
        dot.className = 'dot dot-' + kind;
        $('statusText').textContent = text;
    }

    let busy = 0;

    async function work(label, fn) {
        busy++;
        $('progress').classList.add('on');
        setStatus('busy', label || '读取中…');
        try {
            return await fn();
        } finally {
            busy--;
            if (busy <= 0) {
                busy = 0;
                $('progress').classList.remove('on');
                setStatus('ok', '就绪');
            }
        }
    }

    /* ------------------------------------------------------------ boot */

    function bindHostMessages() {
        try {
            window.chrome.webview.addEventListener('message', function (event) {
                const msg = event.data || {};
                hostReady = true;

                if (msg.action === 'bind') {
                    state.root = msg.folder || '';
                    $('rootPath').textContent = state.root || '未选择日志目录';
                    loadSessions();
                } else if (msg.action === 'refresh') {
                    loadSessions();
                }
            });
        } catch (e) {
            // 在普通浏览器里调试时没有 webview 宿主：页面仍然渲染，只是读不到数据
            showError('页面没有运行在 WebView2 中，无法读取日志：' + e.message);
        }
    }

    bindHostMessages();

    $('btnReload').addEventListener('click', function () {
        loadSessions(true);
    });

    $('btnSidebar').addEventListener('click', function () {
        $('sidebar').classList.toggle('collapsed');
    });

    $('sessionSearch').addEventListener('input', function (e) {
        state.search = e.target.value || '';
        renderSessions();
    });

    /* -------------------------------------------------------- sessions */

    async function loadSessions(isRefresh) {
        await work('扫描日志目录…', async () => {
            try {
                const reply = await api('ListSessions');
                state.root = reply.root || state.root;
                state.sessions = reply.sessions || [];
                state.textFiles = reply.textFiles || [];
                $('rootPath').textContent = state.root || '未选择日志目录';

                if (!reply.exists) {
                    showError('日志目录不存在：' + (state.root || '(空)'));
                }
            } catch (e) {
                showError('扫描日志目录失败：' + e.message);
                return;
            }

            renderSessions();

            if (isRefresh) {
                if (state.selected) {
                    selectSession(state.selected.kind, state.selected.sessionId, true);
                }
            } else if (state.sessions.length > 0 && !state.selected) {
                const first = state.sessions[0];
                selectSession(first.kind, first.sessionId, false);
            }
        });
    }

    function sessionMatches(s) {
        const q = state.search.trim().toLowerCase();
        if (!q) return true;
        const text = (s.sessionId + ' ' + s.kind + ' ' + (s.summaryText || '')).toLowerCase();
        return text.indexOf(q) >= 0;
    }

    function renderSessions() {
        const box = $('sessionList');
        const groups = [
            { kind: 'llm-sessions', title: '会话日志 llm-sessions' },
            { kind: 'toolcalls', title: '工具调用 toolcalls' }
        ];

        let html = '';

        groups.forEach((g) => {
            const list = state.sessions.filter((s) => s.kind === g.kind && sessionMatches(s));
            if (list.length === 0) return;

            html += `<div class="session-group"><div class="session-group-title"><span>${esc(g.title)}</span><span>${list.length}</span></div>`;

            list.forEach((s) => {
                const active = state.selected && state.selected.kind === s.kind && state.selected.sessionId === s.sessionId;
                html += `<button type="button" class="session-card${active ? ' active' : ''}" data-kind="${esc(s.kind)}" data-sid="${esc(s.sessionId)}">
                    <div class="sid">${esc(s.sessionId)}</div>
                    <div class="meta">
                        <span>${clock(s.lastWrite)}</span>
                        <span><b>${s.files ? s.files.length : 0}</b> 个文件</span>
                        ${s.payloads ? `<span><b>${s.payloads}</b> payloads</span>` : ''}
                    </div>
                </button>`;
            });

            html += '</div>';
        });

        if (!html) {
            html = `<div class="empty-hint">没有匹配的会话。<br>会话来自 <code>${esc(state.root || '(未设置)')}</code> 下的<br><code>toolcalls\\*</code> 与 <code>llm-sessions\\*</code>。</div>`;
        }

        box.innerHTML = html;

        Array.prototype.forEach.call(box.querySelectorAll('.session-card'), (card) => {
            card.addEventListener('click', function () {
                selectSession(card.getAttribute('data-kind'), card.getAttribute('data-sid'), false);
            });
        });
    }

    async function selectSession(kind, sessionId, keepTab) {
        await work('打开会话…', async () => {
            let reply;
            try {
                reply = await api('OpenSession', kind, sessionId);
            } catch (e) {
                showError('打开会话失败：' + e.message);
                return;
            }

            if (reply.error) {
                showError('打开会话失败：' + reply.error);
                return;
            }

            state.selected = { kind: kind, sessionId: sessionId };
            state.summary = reply.summary || null;
            state.linked = reply.linked && reply.linked.sessionId ? reply.linked : null;
            state.files = reply.files || [];
            state.turns = {};
            state.page = { name: '', offset: 0, limit: 30, total: 0 };
            state.filter = {};
            state.tab = keepTab && tabDef(state.tab) ? state.tab : 'overview';

            renderSessions();
            renderTabs();
            render();
        });
    }

    /* ------------------------------------------------------------- tabs */

    function renderTabs() {
        const bar = $('tabs');
        bar.innerHTML = '';

        tabs().forEach((t) => {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'tab' + (state.tab === t.id ? ' active' : '');
            b.innerHTML = esc(t.label);
            b.addEventListener('click', function () {
                state.tab = t.id;
                state.page = { name: '', offset: 0, limit: 30, total: 0 };
                state.filter = {};
                renderTabs();
                render();
            });
            bar.appendChild(b);
        });
    }

    /* ------------------------------------------------------ filter bar */

    const FILTER_FIELDS = {
        calls: [
            { key: 'tool', label: '工具', type: 'text', ph: 'read / multi_edit' },
            { key: 'status', label: '状态', type: 'select', options: ['ok', 'error', 'denied', 'failed'] },
            { key: 'source', label: '来源', type: 'text', ph: 'sandbox / mcp / skill' },
            { key: 'keyword', label: '关键字', type: 'text', ph: '文件名 / 文本' }
        ],
        permissions: [
            { key: 'operation', label: '操作', type: 'text', ph: 'Read / Write' },
            { key: 'granted', label: '结果', type: 'select', options: [{ v: 1, t: '放行' }, { v: 0, t: '拒绝' }] },
            { key: 'keyword', label: '关键字', type: 'text', ph: '目标 / 理由' }
        ],
        messages: [
            { key: 'role', label: '角色', type: 'select', options: ['system', 'user', 'assistant', 'tool'] },
            { key: 'keyword', label: '关键字', type: 'text', ph: '内容' }
        ],
        turns: [
            { key: 'status', label: '状态', type: 'select', options: ['ok', 'failed', 'max_rounds_exceeded'] },
            { key: 'keyword', label: '关键字', type: 'text', ph: '提问 / 回答' }
        ]
    };

    function renderFilter() {
        const bar = $('filterBar');
        const def = tabDef(state.tab);
        const fields = FILTER_FIELDS[state.tab];

        if (!def || !def.file || !fields) {
            bar.innerHTML = '';
            return;
        }

        let html = '';

        fields.forEach((f) => {
            const value = state.filter[f.key];
            if (f.type === 'select') {
                const opts = f.options.map((o) => {
                    const v = (o && o.v !== undefined) ? o.v : o;
                    const t = (o && o.t !== undefined) ? o.t : o;
                    return `<option value="${esc(v)}"${String(value) === String(v) ? ' selected' : ''}>${esc(t)}</option>`;
                }).join('');
                html += `<label class="field">${esc(f.label)}<select data-key="${esc(f.key)}"><option value="">全部</option>${opts}</select></label>`;
            } else {
                html += `<label class="field">${esc(f.label)}<input type="text" data-key="${esc(f.key)}" value="${esc(value || '')}" placeholder="${esc(f.ph || '')}"></label>`;
            }
        });

        html += `<button type="button" class="btn btn-ghost" id="filterClear">清除</button>`;

        bar.innerHTML = html;

        Array.prototype.forEach.call(bar.querySelectorAll('input,select'), (el) => {
            el.addEventListener('change', function () {
                state.filter[el.getAttribute('data-key')] = el.value;
                state.page.offset = 0;
                render();
            });
            el.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') {
                    state.filter[el.getAttribute('data-key')] = el.value;
                    state.page.offset = 0;
                    render();
                }
            });
        });

        $('filterClear').addEventListener('click', function () {
            state.filter = {};
            state.page.offset = 0;
            render();
        });
    }

    /* ----------------------------------------------------------- pager */

    function renderPager(total, offset, limit, onPage) {
        const bar = $('pager');
        if (!total && total !== 0) { bar.innerHTML = ''; return; }

        const pages = Math.max(1, Math.ceil(total / limit));
        const cur = Math.floor(offset / limit) + 1;

        bar.innerHTML = `
            <span>共 <b>${num(total)}</b> 条 · 第 <b>${cur}</b> / ${pages} 页</span>
            <label class="field">每页
                <select id="pageSize">
                    <option value="30"${limit === 30 ? ' selected' : ''}>30</option>
                    <option value="50"${limit === 50 ? ' selected' : ''}>50</option>
                    <option value="100"${limit === 100 ? ' selected' : ''}>100</option>
                </select>
            </label>
            <span class="spacer"></span>
            <button type="button" class="btn btn-ghost" id="pagePrev"${offset <= 0 ? ' disabled' : ''}>上一页</button>
            <button type="button" class="btn btn-ghost" id="pageNext"${offset + limit >= total ? ' disabled' : ''}>下一页</button>`;

        $('pageSize').addEventListener('change', function (e) {
            state.page.limit = parseInt(e.target.value, 10) || 30;
            state.page.offset = 0;
            render();
        });
        $('pagePrev').addEventListener('click', function () {
            state.page.offset = Math.max(0, offset - limit);
            render();
        });
        $('pageNext').addEventListener('click', function () {
            state.page.offset = offset + limit;
            render();
        });
    }

    /* ------------------------------------------------------- renderers */

    function render() {
        if (!state.selected) {
            $('content').innerHTML = `<div class="card"><div class="card-body muted">从左侧选择一个会话开始查看。</div></div>`;
            $('filterBar').innerHTML = '';
            $('pager').innerHTML = '';
            return;
        }

        renderFilter();

        switch (state.tab) {
            case 'overview': renderOverview(); break;
            case 'turns': renderTurns(); break;
            case 'calls': renderArray('tool-calls.json', renderCallRow); break;
            case 'permissions': renderArray('permissions.json', renderPermissionRow); break;
            case 'messages': renderArray('messages.json', renderMessageRow); break;
            case 'usage': renderUsage(); break;
            case 'tools': renderArray('tools.json', renderToolRow); break;
            case 'raw': renderRaw(); break;
            case 'text': renderText(); break;
            default: renderOverview();
        }
    }

    /* --------------------------------------------------------- overview */

    function renderOverview() {
        $('pager').innerHTML = '';
        const s = state.summary;
        const box = $('content');

        if (!s) {
            box.innerHTML = `<div class="card"><div class="card-body muted">这个会话没有 session.json。</div></div>`;
            return;
        }

        let html = '';

        if (state.selected.kind === 'toolcalls') {
            const byStatus = s.callsByStatus || {};
            const statusText = Object.keys(byStatus).map((k) => `${k} ${byStatus[k]}`).join(' · ') || '—';

            html += `<div class="kpis">
                <div class="kpi accent"><div class="k">工具调用</div><div class="v">${num(s.totalCalls)}</div></div>
                <div class="kpi"><div class="k">状态分布</div><div class="v" style="font-size:13px">${esc(statusText)}</div></div>
                <div class="kpi"><div class="k">注册工具</div><div class="v">${num(s.registeredTools)}</div></div>
                <div class="kpi"><div class="k">权限事件</div><div class="v">${num(s.permissionEvents)}</div></div>
                <div class="kpi"><div class="k">工具耗时合计</div><div class="v">${ms(s.totalToolDurationMs)}</div></div>
                <div class="kpi"><div class="k">会话时长</div><div class="v">${ms(s.durationMs)}</div></div>
            </div>`;

            const byTool = s.callsByTool || {};
            const max = Math.max.apply(null, Object.keys(byTool).map((k) => byTool[k]).concat([1]));
            const rows = Object.keys(byTool).sort((a, b) => byTool[b] - byTool[a]).map((k) =>
                `<div class="bar-row"><span class="name" title="${esc(k)}">${esc(k)}</span>
                 <span class="track"><span class="fill" style="width:${(byTool[k] / max * 100).toFixed(1)}%"></span></span>
                 <span class="n">${byTool[k]}</span></div>`).join('');

            html += `<div class="card"><div class="card-head"><h3>按工具统计</h3><span class="muted tiny">${Object.keys(byTool).length} 个工具</span></div><div class="card-body">${rows || '<span class="muted">没有调用记录</span>'}</div></div>`;

            const errors = s.errors || [];
            html += `<div class="card"><div class="card-head"><h3>错误清单</h3><span class="muted tiny">${errors.length} 条</span></div><div class="card-body">` +
                (errors.length ? errors.map((e) => `<div class="err-item"><div class="h"><span>#${num(e.seq)}</span>${badge('muted', e.tool || '')}${statusBadge(e.status)}</div><div class="m">${esc(e.message)}</div></div>`).join('') : '<span class="muted">没有错误</span>') +
                `</div></div>`;
        } else {
            html += `<div class="kpis">
                <div class="kpi accent"><div class="k">对话轮次</div><div class="v">${num(s.turns)}</div></div>
                <div class="kpi"><div class="k">模型请求</div><div class="v">${num(s.rounds)}</div></div>
                <div class="kpi"><div class="k">工具调用</div><div class="v">${num(s.toolCalls)}</div></div>
                <div class="kpi"><div class="k">输入 token</div><div class="v">${num(s.promptTokens)}</div></div>
                <div class="kpi"><div class="k">输出 token</div><div class="v">${num(s.completionTokens)}</div></div>
                <div class="kpi"><div class="k">缓存命中</div><div class="v">${num(s.cacheHitTokens)}</div></div>
                <div class="kpi"><div class="k">会话时长</div><div class="v">${ms(s.durationMs)}</div></div>
            </div>`;

            html += `<div class="card"><div class="card-head"><h3>会话配置</h3></div><div class="card-body"><dl class="kv">
                <dt>模型</dt><dd>${esc(s.model)}</dd>
                <dt>端点</dt><dd>${esc(s.endpoint)}</dd>
                <dt>温度</dt><dd>${esc(s.temperature)}</dd>
                <dt>最大轮次</dt><dd>${esc(s.maxRounds)}</dd>
                <dt>保留记忆</dt><dd>${s.preserveMemory ? '是' : '否'}</dd>
                <dt>system prompt</dt><dd>${esc(s.systemMessage ? num(s.systemMessage.length) + ' 字符' : '—')}</dd>
            </dl></div></div>`;

            const events = s.events || [];
            if (events.length) {
                html += `<div class="card"><div class="card-head"><h3>会话事件</h3><span class="muted tiny">${events.length} 条</span></div><div class="card-body">` +
                    events.map((e) => `<div class="err-item"><div class="h"><span>#${num(e.seq)}</span>${badge('info', e.kind)}</div><div class="m">${esc(e.summary || '')}</div></div>`).join('') +
                    `</div></div>`;
            }
        }

        // 通用：会话信息 / 文件清单 / 关联会话
        html += `<div class="card"><div class="card-head"><h3>会话信息</h3></div><div class="card-body"><dl class="kv">
            <dt>会话 id</dt><dd>${esc(s.sessionId)}</dd>
            <dt>开始</dt><dd>${clock(s.startedUtc)}</dd>
            <dt>结束</dt><dd>${clock(s.closedUtc)}</dd>
            <dt>工作区</dt><dd>${esc(s.workspace || '—')}</dd>
            <dt>安全策略</dt><dd>${esc(s.securityPolicy || '—')}</dd>
            <dt>机器 / 用户</dt><dd>${esc(s.machine || '')} / ${esc(s.user || '')}</dd>
            <dt>进程</dt><dd>${esc(s.processId || '')} · clr ${esc(s.clrVersion || '')}</dd>
            <dt>持久化</dt><dd>${s.persistent ? '已写入磁盘' : '仅内存'}</dd>
        </dl></div></div>`;

        if (state.linked) {
            html += `<div class="card"><div class="card-head"><h3>关联会话</h3></div><div class="card-body">
                <button type="button" class="link-btn" id="jumpLinked">${esc(state.linked.kind)} / ${esc(state.linked.sessionId)}</button>
                <span class="muted tiny"> —— 两侧日志共享同一个会话 id，可对照阅读</span></div></div>`;
        }

        html += `<div class="card"><div class="card-head"><h3>日志文件</h3><span class="muted tiny">${state.files.length} 个</span></div><div class="card-body">
            <div class="table-wrap"><table class="grid"><thead><tr><th>文件</th><th class="mono" style="text-align:right">大小</th><th>操作</th></tr></thead><tbody>` +
            state.files.map((f) => `<tr><td class="mono">${esc(f.name)}</td><td class="num">${bytes(f.size)}</td>
                <td><button type="button" class="link-btn" data-raw="${esc(f.name)}">查看原始 JSON</button></td></tr>`).join('') +
            `</tbody></table></div></div></div>`;

        box.innerHTML = html;

        const jump = $('jumpLinked');
        if (jump) {
            jump.addEventListener('click', function () {
                selectSession(state.linked.kind, state.linked.sessionId, false);
            });
        }
        Array.prototype.forEach.call(box.querySelectorAll('[data-raw]'), (b) => {
            b.addEventListener('click', function () {
                state.tab = 'raw';
                state.rawFile = b.getAttribute('data-raw');
                renderTabs();
                render();
            });
        });
    }

    /* ------------------------------------------------------------ turns */

    async function renderTurns() {
        const box = $('content');
        box.innerHTML = `<div class="card"><div class="card-body muted">读取对话轮次…</div></div>`;

        let reply;
        try {
            reply = await api('Records', 'turns.json', state.page.offset, state.page.limit, JSON.stringify(state.filter));
        } catch (e) {
            showError('读取对话轮次失败：' + e.message);
            return;
        }

        if (reply.error) {
            showError('读取对话轮次失败：' + reply.error);
            return;
        }

        state.page.total = reply.total;
        renderPager(reply.total, state.page.offset, state.page.limit);

        if (!reply.items.length) {
            box.innerHTML = `<div class="card"><div class="card-body muted">这个会话没有对话轮次。</div></div>`;
            return;
        }

        box.innerHTML = reply.items.map((t) => `
            <div class="turn" data-seq="${t.seq}">
                <div class="turn-head">
                    <span class="chev">▶</span>
                    <span class="t-seq">#${num(t.seq)}</span>
                    <span class="t-prompt" title="${esc(t.promptPreview || '')}">${esc(short(t.promptPreview || '(空)', 90))}</span>
                    <span class="t-meta">
                        ${statusBadge(t.status)}
                        <span>${num(t.roundCount)} 轮</span>
                        <span>${num(t.toolCallCount)} 次工具</span>
                        <span>${ms(t.durationMs)}</span>
                        <span class="hide-sm">${clock(t.timestampUtc)}</span>
                    </span>
                </div>
                <div class="turn-body"><div class="muted tiny">展开中…</div></div>
            </div>`).join('');

        Array.prototype.forEach.call(box.querySelectorAll('.turn-head'), (head) => {
            head.addEventListener('click', function () {
                const turn = head.parentElement;
                const seq = parseInt(turn.getAttribute('data-seq'), 10);
                if (turn.classList.contains('open')) {
                    turn.classList.remove('open');
                    return;
                }
                turn.classList.add('open');
                loadTurnBody(turn, seq);
            });
        });
    }

    async function loadTurnBody(turnEl, seq) {
        const body = turnEl.querySelector('.turn-body');

        if (state.turns[seq]) {
            body.innerHTML = turnBodyHtml(state.turns[seq]);
            wireTurn(body, seq);
            return;
        }

        body.innerHTML = `<div class="muted tiny">读取完整轮次…</div>`;

        let turn;
        try {
            turn = await api('Turn', seq);
        } catch (e) {
            body.innerHTML = `<div class="err-item"><div class="m">读取失败：${esc(e.message)}</div></div>`;
            return;
        }

        if (turn.error) {
            body.innerHTML = `<div class="err-item"><div class="m">${esc(turn.error)}</div></div>`;
            return;
        }

        state.turns[seq] = turn;
        body.innerHTML = turnBodyHtml(turn);
        wireTurn(body, seq);
    }

    function turnBodyHtml(turn) {
        const rounds = turn.rounds || [];

        let html = `<dl class="kv">
            <dt>提问</dt><dd>${esc(short(turn.prompt ? (turn.prompt.text || '') : '', 160))}</dd>
            <dt>状态</dt><dd>${statusBadge(turn.status)}</dd>
            <dt>轮次</dt><dd>${num(turn.roundCount)}</dd>
            <dt>工具调用</dt><dd>${num(turn.toolCallCount)}</dd>
            <dt>token</dt><dd>in ${num(turn.promptTokens)} · out ${num(turn.completionTokens)}</dd>
            <dt>耗时</dt><dd>${ms(turn.durationMs)}</dd>
            <dt>模型</dt><dd>${esc(turn.model || '—')}</dd>
        </dl>`;

        html += textBlock('最终回答 output', turn.output);

        if (turn.think && turn.think.length) {
            html += textBlock('思考过程 think', turn.think);
        }

        if (turn.errors && turn.errors.length) {
            html += `<div class="card" style="box-shadow:none"><div class="card-head"><h3>失败与重试</h3></div><div class="card-body">` +
                turn.errors.map((e) => `<div class="err-item"><div class="h"><span>#${num(e.seq)}</span>${badge('info', e.kind)}</div><div class="m">${esc(e.summary || '')}</div></div>`).join('') +
                `</div></div>`;
        }

        html += `<div class="muted tiny" style="margin:10px 0 6px">共 ${rounds.length} 轮模型请求</div>`;

        html += rounds.map((r) => {
            const head = `<div class="round-head">
                <span class="r-seq">round ${num(r.seq)}</span>
                ${r.dsmlFallback ? badge('dsml', 'DSML 兜底') : ''}
                ${statusBadge(r.status)}
                <span class="chip">首 token ${ms(r.firstTokenMs)}</span>
                <span class="chip">chunk ${num(r.chunkCount)}</span>
                <span class="chip">流式 ${ms(r.streamMs)}</span>
                ${r.usage ? `<span class="chip">in ${num(r.usage.promptTokens)} · out ${num(r.usage.completionTokens)}</span>` : ''}
                ${r.usage && r.usage.cacheHitTokens ? `<span class="chip">缓存命中 ${num(r.usage.cacheHitTokens)}</span>` : ''}
                <span class="chip">${clock(r.timestampUtc)}</span>
            </div>`;

            const think = (r.think && r.think.length) ? textBlock('本轮思考', r.think) : '';
            const out = (r.output && r.output.length) ? textBlock('本轮正文', r.output) : '';
            const requested = (r.requestedToolCalls && r.requestedToolCalls.length)
                ? `<div class="fold"><div class="fold-head"><span class="chev">▶</span>请求的工具调用 (${r.requestedToolCalls.length})</div><div class="fold-body">` +
                r.requestedToolCalls.map((c) => `<div class="chip" style="margin:0 4px 4px 0">${esc(c.name)} · ${esc(c.id || '')}</div>`).join('') + `</div></div>`
                : '';
            const msgs = (r.requestMessages && r.requestMessages.length)
                ? `<div class="fold"><div class="fold-head"><span class="chev">▶</span>完整请求消息 (${r.requestMessages.length})</div><div class="fold-body">` +
                r.requestMessages.map(messageHtml).join('') + `</div></div>`
                : '';

            return `<div class="round">${head}${think}${out}${requested}${msgs}</div>`;
        }).join('');

        return html;
    }

    function wireTurn(body, seq) {
        Array.prototype.forEach.call(body.querySelectorAll('.fold-head'), (h) => {
            h.addEventListener('click', function () {
                h.parentElement.classList.toggle('open');
            });
        });
        Array.prototype.forEach.call(body.querySelectorAll('[data-payload]'), (b) => {
            b.addEventListener('click', function () {
                openPayload(b.getAttribute('data-payload'), parseInt(b.getAttribute('data-len') || '0', 10), b.getAttribute('data-sha'));
            });
        });
    }

    /* -------------------------------------------------------- text block */

    function textBlock(title, logged) {
        if (!logged) return '';
        const truncated = logged.truncated;
        const preview = truncated ? (logged.preview || '') : (logged.text || '');
        const body = `<pre class="code">${esc(preview)}</pre>` + (truncated
            ? `<div class="trunc-note">正文过长，仅显示前 ${num(preview.length)} 字符（共 ${num(logged.length)} 字符）</div>`
            : '');

        const open = truncated && logged.payloadFile
            ? `<button type="button" class="link-btn" data-payload="${esc(logged.payloadFile)}" data-len="${num(logged.length)}" data-sha="${esc(logged.sha256 || '')}">打开完整原文</button>`
            : '';

        return `<div class="fold"><div class="fold-head"><span class="chev">▶</span>${esc(title)}${logged.length ? ` <span class="muted tiny">(${num(logged.length)} 字符)</span>` : ''}</div>
            <div class="fold-body">${open}${body}</div></div>`;
    }

    /* ---------------------------------------------------------- message */

    function messageHtml(m) {
        const calls = (m.toolCalls && m.toolCalls.length)
            ? `<div class="tool-calls">` + m.toolCalls.map((c) =>
                `<div class="chip">${esc(c.name)} · ${esc(c.id || '')} · ${esc(JSON.stringify(c.arguments || {}))}</div>`).join('') + `</div>`
            : '';

        const content = m.content
            ? `<pre>${esc(m.content.truncated ? (m.content.preview || '') : (m.content.text || ''))}</pre>` +
            (m.content.truncated && m.content.payloadFile
                ? `<button type="button" class="link-btn" data-payload="${esc(m.content.payloadFile)}" data-len="${num(m.content.length)}">打开完整原文</button>`
                : '')
            : '';

        return `<div class="msg ${esc(m.role)}"><span class="role">${esc(m.role)}</span>
            <div class="body">${content}${calls}${m.toolCallId ? `<div class="cell-sub">tool_call_id: ${esc(m.toolCallId)}</div>` : ''}</div></div>`;
    }

    /* ---------------------------------------------------- array tables */

    async function renderArray(file, rowRenderer) {
        const box = $('content');
        box.innerHTML = `<div class="card"><div class="card-body muted">读取 ${esc(file)} …</div></div>`;

        let reply;
        try {
            reply = await api('Records', file, state.page.offset, state.page.limit, JSON.stringify(state.filter));
        } catch (e) {
            showError('读取 ' + file + ' 失败：' + e.message);
            return;
        }

        if (reply.error) {
            showError('读取 ' + file + ' 失败：' + reply.error);
            return;
        }

        state.page.total = reply.total;
        renderPager(reply.total, state.page.offset, state.page.limit);

        if (!reply.items.length) {
            box.innerHTML = `<div class="card"><div class="card-body muted">没有记录${Object.keys(state.filter).length ? '（当前过滤条件下）' : ''}。</div></div>`;
            return;
        }

        box.innerHTML = rowRenderer(reply.items);
        wireTable(box, file);
    }

    function wireTable(box, file) {
        Array.prototype.forEach.call(box.querySelectorAll('tbody tr'), (tr) => {
            tr.addEventListener('click', function () {
                const idx = parseInt(tr.getAttribute('data-index'), 10);
                const item = currentItems[idx];
                openDrawer(file, item);
            });
        });
        Array.prototype.forEach.call(box.querySelectorAll('[data-payload]'), (b) => {
            b.addEventListener('click', function (e) {
                e.stopPropagation();
                openPayload(b.getAttribute('data-payload'), parseInt(b.getAttribute('data-len') || '0', 10), b.getAttribute('data-sha'));
            });
        });
    }

    let currentItems = [];

    function renderCallRow(items) {
        currentItems = items;
        return `<div class="table-wrap"><table class="grid"><thead><tr>
            <th class="mono">#</th><th>工具</th><th>来源</th><th>状态</th><th class="mono hide-sm">turn/round</th>
            <th class="num">耗时</th><th class="num hide-sm">返回</th><th>结果预览</th></tr></thead><tbody>` +
            items.map((c, i) => `<tr data-index="${i}">
                <td class="mono">${num(c.seq)}</td>
                <td><b>${esc(c.tool || c.name)}</b>${c.server ? `<div class="cell-sub">${esc(c.server)}${c.originalName && c.originalName !== (c.tool || c.name) ? ' · ' + esc(c.originalName) : ''}</div>` : ''}</td>
                <td>${sourceBadge(c.source)}</td>
                <td>${statusBadge(c.status)}</td>
                <td class="mono hide-sm">${c.turnSeq || c.roundSeq ? num(c.turnSeq) + '/' + num(c.roundSeq) : '—'}</td>
                <td class="num">${ms(c.durationMs)}</td>
                <td class="num hide-sm">${c.result ? num(c.result.length) : '—'}</td>
                <td><div class="cell-text" title="${esc(resultPreview(c))}">${esc(short(resultPreview(c), 110))}</div></td>
            </tr>`).join('') + `</tbody></table></div>`;
    }

    function resultPreview(c) {
        const r = c.result;
        if (!r) return '';
        return r.truncated ? (r.preview || '') : (r.text || '');
    }

    function renderPermissionRow(items) {
        currentItems = items;
        return `<div class="table-wrap"><table class="grid"><thead><tr>
            <th class="mono">#</th><th>操作</th><th>结果</th><th>目标</th><th class="hide-sm">模式</th><th class="hide-sm">理由</th><th>说明</th></tr></thead><tbody>` +
            items.map((p, i) => `<tr data-index="${i}">
                <td class="mono">${num(p.seq)}</td>
                <td><b>${esc(p.operation)}</b></td>
                <td>${p.granted ? badge('ok', '放行') : badge('error', '拒绝')}</td>
                <td><div class="cell-text" title="${esc(p.target)}">${esc(short(p.target, 80))}</div>${p.insideWorkspace === false ? '<div class="cell-sub">工作区外</div>' : ''}</td>
                <td class="hide-sm">${p.requestedPermission ? badge('denied', p.requestedPermission) : '<span class="muted">—</span>'}</td>
                <td class="hide-sm"><div class="cell-text" title="${esc(p.justification || '')}">${esc(short(p.justification || '', 60))}</div></td>
                <td><div class="cell-text" title="${esc(p.reason || '')}">${esc(short(p.reason || '', 60))}</div></td>
            </tr>`).join('') + `</tbody></table></div>`;
    }

    function renderMessageRow(items) {
        currentItems = items;
        return `<div class="table-wrap"><table class="grid"><thead><tr>
            <th class="mono">#</th><th>角色</th><th class="mono hide-sm">turn/round</th><th>内容</th></tr></thead><tbody>` +
            items.map((m, i) => `<tr data-index="${i}">
                <td class="mono">${num(m.seq)}</td>
                <td>${roleBadge(m.role)}</td>
                <td class="mono hide-sm">${num(m.turnSeq)}/${num(m.roundSeq)}</td>
                <td>${messageHtml(m)}</td>
            </tr>`).join('') + `</tbody></table></div>`;
    }

    function renderToolRow(items) {
        currentItems = items;
        return `<div class="table-wrap"><table class="grid"><thead><tr>
            <th>工具</th><th>来源</th><th>说明</th><th class="hide-sm">参数</th></tr></thead><tbody>` +
            items.map((t, i) => `<tr data-index="${i}">
                <td><b>${esc(t.tool)}</b>${t.server ? `<div class="cell-sub">${esc(t.server)} · ${esc(t.originalName || '')}</div>` : ''}</td>
                <td>${sourceBadge(t.source)}</td>
                <td><div class="cell-text" title="${esc(t.description || '')}">${esc(short(t.description || '', 90))}</div></td>
                <td class="hide-sm">${(t.parameters || []).map((p) => `<span class="chip" style="margin:0 3px 3px 0">${esc(p.name)}:${esc(p.type)}${p.required ? '*' : ''}</span>`).join('')}</td>
            </tr>`).join('') + `</tbody></table></div>`;
    }

    /* ------------------------------------------------------------- usage */

    async function renderUsage() {
        const box = $('content');
        box.innerHTML = `<div class="card"><div class="card-body muted">读取用量…</div></div>`;

        let reply;
        try {
            reply = await api('Records', 'usage.json', state.page.offset, state.page.limit, JSON.stringify(state.filter));
        } catch (e) {
            showError('读取用量失败：' + e.message);
            return;
        }

        if (reply.error) {
            showError('读取用量失败：' + reply.error);
            return;
        }

        state.page.total = reply.total;
        renderPager(reply.total, state.page.offset, state.page.limit);

        const items = reply.items || [];

        if (!items.length) {
            box.innerHTML = `<div class="card"><div class="card-body muted">没有用量记录。</div></div>`;
            return;
        }

        box.innerHTML = usageChart(items) + `<div class="table-wrap"><table class="grid"><thead><tr>
            <th class="mono">#</th><th class="mono hide-sm">turn/round</th><th class="num">输入</th><th class="num">输出</th>
            <th class="num hide-sm">缓存命中</th><th class="num hide-sm">缓存未中</th><th class="num hide-sm">命中率</th>
            <th class="num hide-sm">累计输入</th><th>时间</th></tr></thead><tbody>` +
            items.map((u) => `<tr>
                <td class="mono">${num(u.seq)}</td>
                <td class="mono hide-sm">${num(u.turnSeq)}/${num(u.roundSeq)}</td>
                <td class="num">${num(u.promptTokens)}</td>
                <td class="num">${num(u.completionTokens)}</td>
                <td class="num hide-sm">${num(u.cacheHitTokens)}</td>
                <td class="num hide-sm">${num(u.cacheMissTokens)}</td>
                <td class="num hide-sm">${(u.hitRate * 100).toFixed(1)}%</td>
                <td class="num hide-sm">${num(u.cumulativePromptTokens)}</td>
                <td class="mono tiny">${clock(u.timestampUtc)}</td>
            </tr>`).join('') + `</tbody></table></div>`;
    }

    function usageChart(items) {
        const rows = items.slice(0, 60);
        const w = 900, h = 220, pad = { l: 56, r: 46, t: 16, b: 26 };
        const iw = w - pad.l - pad.r, ih = h - pad.t - pad.b;
        const maxTok = Math.max.apply(null, rows.map((r) => r.promptTokens + r.completionTokens).concat([1]));
        const bw = iw / rows.length;

        let bars = '';
        rows.forEach((r, i) => {
            const x = pad.l + i * bw;
            const ph = (r.promptTokens / maxTok) * ih;
            const ch = (r.completionTokens / maxTok) * ih;
            bars += `<rect x="${(x + bw * .18).toFixed(1)}" y="${(pad.t + ih - ph).toFixed(1)}" width="${(bw * .64).toFixed(1)}" height="${Math.max(ph, .5).toFixed(1)}" fill="#2563EB" opacity=".85"><title>round ${r.roundSeq} · in ${num(r.promptTokens)}</title></rect>`;
            bars += `<rect x="${(x + bw * .18).toFixed(1)}" y="${(pad.t + ih - ph - ch).toFixed(1)}" width="${(bw * .64).toFixed(1)}" height="${Math.max(ch, .5).toFixed(1)}" fill="#60A5FA"><title>round ${r.roundSeq} · out ${num(r.completionTokens)}</title></rect>`;
        });

        const pts = rows.map((r, i) => `${(pad.l + i * bw + bw / 2).toFixed(1)},${(pad.t + ih - (r.hitRate * ih)).toFixed(1)}`).join(' ');
        const grid = [0, .25, .5, .75, 1].map((f) =>
            `<line x1="${pad.l}" y1="${(pad.t + ih * f).toFixed(1)}" x2="${w - pad.r}" y2="${(pad.t + ih * f).toFixed(1)}" stroke="#EEF1F5"/>
             <text x="${pad.l - 8}" y="${(pad.t + ih * f + 4).toFixed(1)}" text-anchor="end" font-size="9" fill="#6B7280">${num(maxTok * (1 - f))}</text>`).join('');

        return `<div class="chart">
            <svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="xMidYMid meet">
                ${grid}${bars}
                <polyline points="${pts}" fill="none" stroke="#16A34A" stroke-width="1.6" opacity=".9"/>
                <text x="${w - pad.r + 6}" y="${pad.t + 10}" font-size="9" fill="#16A34A">命中率</text>
            </svg>
            <div class="legend">
                <span><i style="background:#2563EB"></i>输入 token</span>
                <span><i style="background:#60A5FA"></i>输出 token</span>
                <span><i style="background:#16A34A"></i>缓存命中率</span>
                <span class="muted">最近 ${rows.length} 次请求${rows.length < items.length ? `（共 ${items.length} 次）` : ''}</span>
            </div>
        </div>`;
    }

    /* ---------------------------------------------------------- raw json */

    async function renderRaw() {
        const box = $('content');
        $('pager').innerHTML = '';

        if (!state.rawFile) {
            state.rawFile = (state.files[0] || {}).name || 'session.json';
        }

        const opts = state.files.map((f) =>
            `<option value="${esc(f.name)}"${f.name === state.rawFile ? ' selected' : ''}>${esc(f.name)} (${bytes(f.size)})</option>`).join('');

        box.innerHTML = `<div class="card"><div class="card-head"><h3>原始 JSON</h3>
            <label class="field">文件<select id="rawSelect">${opts}</select></label></div>
            <div class="card-body" id="rawBody"><span class="muted">读取中…</span></div></div>`;

        $('rawSelect').addEventListener('change', function (e) {
            state.rawFile = e.target.value;
            render();
        });

        let reply;
        try {
            reply = await api('RawFile', state.rawFile, 512 * 1024);
        } catch (e) {
            $('rawBody').innerHTML = `<div class="err-item"><div class="m">${esc(e.message)}</div></div>`;
            return;
        }

        if (reply.error) {
            $('rawBody').innerHTML = `<div class="err-item"><div class="m">${esc(reply.error)}</div></div>`;
            return;
        }

        const lines = reply.text.split('\n');
        const limit = 1500;
        const shown = lines.slice(0, limit).map((l, i) => `<span class="ln">${i + 1}</span>${esc(l)}`).join('\n');

        $('rawBody').innerHTML = `<pre class="code">${shown}</pre>` +
            (reply.truncated || lines.length > limit
                ? `<div class="trunc-note">仅显示前 ${Math.min(lines.length, limit)} 行 / ${num(reply.size)} 字符，完整内容请直接打开文件：${esc(state.selected ? state.selected.sessionId : '')}\\${esc(reply.name)}</div>`
                : '');
    }

    /* ------------------------------------------------------- text logs */

    state.textPage = { offset: 0, limit: 300 };

    async function renderText() {
        const box = $('content');
        $('pager').innerHTML = '';

        if (!state.textFiles.length) {
            box.innerHTML = `<div class="card"><div class="card-body muted">日志目录下没有纯文本日志（rscript\\*.log 等）。</div></div>`;
            return;
        }

        if (!state.textFile) state.textFile = state.textFiles[0].name;

        const list = state.textFiles.map((f) =>
            `<option value="${esc(f.name)}"${f.name === state.textFile ? ' selected' : ''}>${esc(f.name)} (${bytes(f.size)})</option>`).join('');

        box.innerHTML = `<div class="card"><div class="card-head"><h3>文本日志</h3>
            <label class="field">文件<select id="textSelect">${list}</select></label></div>
            <div class="card-body" id="textBody"><span class="muted">读取中…</span></div></div>
            <div class="pager" id="textPager"></div>`;

        $('textSelect').addEventListener('change', function (e) {
            state.textFile = e.target.value;
            state.textPage.offset = 0;
            render();
        });

        let reply;
        try {
            reply = await api('TextFile', state.textFile, state.textPage.offset, state.textPage.limit);
        } catch (e) {
            $('textBody').innerHTML = `<div class="err-item"><div class="m">${esc(e.message)}</div></div>`;
            return;
        }

        if (reply.error) {
            $('textBody').innerHTML = `<div class="err-item"><div class="m">${esc(reply.error)}</div></div>`;
            return;
        }

        const lines = (reply.text || '').split('\n');
        const start = state.textPage.offset;
        const shown = lines.map((l, i) => `<span class="ln">${start + i + 1}</span>${esc(l)}`).join('\n');

        $('textBody').innerHTML = `<pre class="code">${shown}</pre>`;

        $('textPager').innerHTML = `
            <span>共 <b>${num(reply.lines)}</b> 行 · 当前 ${start + 1} – ${start + lines.length}</span>
            <span class="spacer"></span>
            <button type="button" class="btn btn-ghost" id="textPrev"${start <= 0 ? ' disabled' : ''}>上一段</button>
            <button type="button" class="btn btn-ghost" id="textNext"${start + lines.length >= reply.lines ? ' disabled' : ''}>下一段</button>`;

        $('textPrev').addEventListener('click', function () {
            state.textPage.offset = Math.max(0, start - state.textPage.limit);
            render();
        });
        $('textNext').addEventListener('click', function () {
            state.textPage.offset = start + state.textPage.limit;
            render();
        });
    }

    /* ----------------------------------------------------------- drawer */

    function openDrawer(file, item) {
        const drawer = $('drawer');
        const isCall = file === 'tool-calls.json';

        let html = `<div class="drawer-head"><h3>${esc(isCall ? (item.tool || item.name) : file)}</h3>
            <button type="button" class="btn btn-ghost" id="drawerClose">关闭</button></div><div class="drawer-body">`;

        if (isCall) {
            html += `<dl class="kv">
                <dt>序号</dt><dd>${num(item.seq)}</dd>
                <dt>工具</dt><dd>${esc(item.tool || item.name)}</dd>
                <dt>来源</dt><dd>${sourceBadge(item.source)}</dd>
                <dt>状态</dt><dd>${statusBadge(item.status)}</dd>
                <dt>调用 id</dt><dd>${esc(item.callId || '—')}</dd>
                <dt>所属轮次</dt><dd>${num(item.turnSeq)} / ${num(item.roundSeq)}</dd>
                <dt>耗时</dt><dd>${ms(item.durationMs)}</dd>
                <dt>时间</dt><dd>${clock(item.timestampUtc)}</dd>
                ${item.threadId ? `<dt>线程</dt><dd>${num(item.threadId)} ${esc(item.threadName || '')}</dd>` : ''}
            </dl>`;

            const args = item.arguments || {};
            const keys = Object.keys(args);
            html += `<div class="card"><div class="card-head"><h3>入参</h3></div><div class="card-body">` +
                (keys.length ? keys.map((k) => `<dl class="kv" style="grid-template-columns:110px 1fr"><dt>${esc(k)}</dt><dd>${esc(args[k])}</dd></dl>`).join('') : '<span class="muted">无</span>') +
                `</div></div>`;

            if (item.parameters && item.parameters.length) {
                html += `<div class="card"><div class="card-head"><h3>参数诊断</h3></div><div class="card-body"><div class="table-wrap"><table class="grid"><thead><tr>
                    <th>参数</th><th>类型</th><th>必填</th><th>来源</th><th>值</th></tr></thead><tbody>` +
                    item.parameters.map((p) => `<tr><td class="mono">${esc(p.name)}</td><td class="mono">${esc(p.type || '')}</td>
                        <td>${p.required ? badge('error', '必填') : '<span class="muted">可选</span>'}</td>
                        <td>${p.origin === 'model' ? badge('primary', '模型传入') : badge('muted', '默认值')}</td>
                        <td class="cell-text" title="${esc(p.value || '')}">${esc(short(p.value || '', 60))}</td></tr>`).join('') +
                    `</tbody></table></div></div></div>`;
            }

            if (item.missingRequired && item.missingRequired.length) {
                html += `<div class="err-item"><div class="h">${badge('error', '缺失必填参数')}</div><div class="m">${esc(item.missingRequired.join(', '))}</div></div>`;
            }
            if (item.unknownArguments && item.unknownArguments.length) {
                html += `<div class="err-item"><div class="h">${badge('denied', '未声明的参数')}</div><div class="m">${esc(item.unknownArguments.join(', '))}</div></div>`;
            }

            if (item.errorMessage) {
                html += `<div class="card"><div class="card-head"><h3>异常</h3></div><div class="card-body">
                    <dl class="kv"><dt>类型</dt><dd>${esc(item.errorType || '')}</dd><dt>消息</dt><dd>${esc(item.errorMessage)}</dd></dl>
                    ${item.errorStackTrace ? `<pre class="code">${esc(item.errorStackTrace)}</pre>` : ''}</div></div>`;
            }

            if (item.result) {
                html += `<div class="card"><div class="card-head"><h3>返回结果</h3><span class="muted tiny">${num(item.result.length)} 字符${item.result.sha256 ? ' · sha256 ' + esc(short(item.result.sha256, 12)) : ''}</span></div>
                    <div class="card-body" id="drawerResult">${resultHtml(item.result)}</div></div>`;
            }
        } else {
            html += `<pre class="code">${esc(JSON.stringify(item, null, 2))}</pre>`;
        }

        html += `</div>`;
        drawer.innerHTML = html;
        drawer.hidden = false;

        $('drawerClose').addEventListener('click', closeDrawer);
        Array.prototype.forEach.call(drawer.querySelectorAll('[data-payload]'), (b) => {
            b.addEventListener('click', function () {
                openPayload(b.getAttribute('data-payload'), parseInt(b.getAttribute('data-len') || '0', 10), b.getAttribute('data-sha'));
            });
        });
    }

    function resultHtml(result) {
        if (result.truncated) {
            return `<pre class="code">${esc(result.preview || '')}</pre>
                <div class="trunc-note">正文过长，仅显示前 ${num((result.preview || '').length)} 字符（共 ${num(result.length)} 字符）</div>
                ${result.payloadFile ? `<button type="button" class="link-btn" data-payload="${esc(result.payloadFile)}" data-len="${num(result.length)}" data-sha="${esc(result.sha256 || '')}">打开完整原文</button>` : ''}`;
        }
        return `<pre class="code">${esc(result.text || '')}</pre>`;
    }

    function closeDrawer() {
        $('drawer').hidden = true;
        $('drawer').innerHTML = '';
    }

    /* ---------------------------------------------------- payload modal */

    const payloadState = { file: '', offset: 0, limit: 64 * 1024, size: 0 };

    async function openPayload(relativePath, length, sha) {
        payloadState.file = relativePath;
        payloadState.offset = 0;
        payloadState.size = length || 0;

        const modal = $('modal');
        modal.innerHTML = `<div class="modal-box">
            <div class="modal-head"><h3>${esc(relativePath)}</h3><button type="button" class="btn btn-ghost" id="modalClose">关闭</button></div>
            <div class="modal-body" id="payloadBody"><span class="muted">读取中…</span></div>
            <div class="modal-foot">
                <span id="payloadInfo">—</span><span class="spacer"></span>
                <button type="button" class="btn btn-ghost" id="payloadPrev">上一段</button>
                <button type="button" class="btn btn-ghost" id="payloadNext">下一段</button>
            </div></div>`;
        modal.hidden = false;

        $('modalClose').addEventListener('click', closeModal);
        $('payloadPrev').addEventListener('click', function () {
            payloadState.offset = Math.max(0, payloadState.offset - payloadState.limit);
            loadPayload();
        });
        $('payloadNext').addEventListener('click', function () {
            payloadState.offset = payloadState.offset + payloadState.limit;
            loadPayload();
        });

        await loadPayload();
    }

    async function loadPayload() {
        let reply;
        try {
            reply = await api('Payload', payloadState.file, payloadState.offset, payloadState.limit);
        } catch (e) {
            $('payloadBody').innerHTML = `<div class="err-item"><div class="m">${esc(e.message)}</div></div>`;
            return;
        }

        if (reply.error) {
            $('payloadBody').innerHTML = `<div class="err-item"><div class="m">${esc(reply.error)}</div></div>`;
            return;
        }

        const lines = (reply.text || '').split('\n');
        const start = payloadState.offset;
        const shown = lines.map((l, i) => `<span class="ln">${start + i + 1}</span>${esc(l)}`).join('\n');

        $('payloadBody').innerHTML = `<pre class="code">${shown}</pre>`;
        $('payloadInfo').textContent =
            `第 ${num(start + 1)} – ${num(start + reply.read)} 字符 · 共 ${num(payloadState.size || reply.size)} 字符` +
            (payloadState.file.indexOf('payloads/') === 0 ? ' · ' + payloadState.file : '');

        $('payloadPrev').disabled = start <= 0;
        $('payloadNext').disabled = (start + reply.read) >= (payloadState.size || reply.size);
    }

    function closeModal() {
        $('modal').hidden = true;
        $('modal').innerHTML = '';
    }

    /* -------------------------------------------------------- status ok */

    setStatus('ok', '等待宿主…');
})();
