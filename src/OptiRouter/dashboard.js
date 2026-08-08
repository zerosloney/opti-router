// OptiRouter 监控 Dashboard JavaScript（模型配置已迁移至 /models 页）

var trendDays = 7;
var logOffset = 0;
var logLimit = 50;
var logTotal = 0;
var dismissedAlerts = new Set();
var pendingAlerts = [];

function getKeyOpts() {
    var params = new URLSearchParams(window.location.search);
    var key = params.get('key');
    return key ? { key: key } : {};
}

function authOpts() {
    var opts = getKeyOpts();
    return opts.key ? {} : { headers: { 'Authorization': 'Bearer ' + (window.__apiKey || '') } };
}

function buildUrl(path, extraParams) {
    var kp = getKeyOpts();
    var qs = new URLSearchParams();
    if (kp.key) qs.set('key', kp.key);
    if (extraParams) {
        for (var k in extraParams) qs.set(k, extraParams[k]);
    }
    return path + (qs.toString() ? '?' + qs.toString() : '');
}

// 把当前 key 透传到顶部 nav 链接（如"模型配置"），跳页时保持鉴权。
function propagateKeyToNav() {
    var kp = getKeyOpts();
    if (!kp.key) return;
    document.querySelectorAll('header a[href]').forEach(function(a) {
        var href = a.getAttribute('href');
        if (href && (href.indexOf('/dashboard') === 0 || href.indexOf('/models') === 0)) {
            a.setAttribute('href', href + (href.indexOf('?') >= 0 ? '&' : '?') + 'key=' + encodeURIComponent(kp.key));
        }
    });
}

async function fetchMetrics() {
    try {
        var resp = await fetch(buildUrl('/api/dashboard/metrics'), authOpts());
        if (!resp.ok) return;
        renderDashboard(await resp.json());
    } catch (err) { console.error('metrics error:', err); }
}

function renderDashboard(data) {
    var sys = data.system;
    var t = new Date(sys.time);
    document.getElementById('utc-time').textContent = 'UTC: ' + t.toISOString().split('T')[1].substring(0, 8);
    var bud = sys.budget;
    var pct = bud.dailyBudgetUsd > 0 ? (bud.dailySpend || 0) / bud.dailyBudgetUsd * 100 : 0;
    document.getElementById('daily-spend').textContent = '$' + (bud.dailySpend || 0).toFixed(6);
    document.getElementById('total-spend').textContent = '$' + (bud.totalSpend || 0).toFixed(6);
    document.getElementById('budget-bar-fill').style.width = Math.min(pct, 100) + '%';
    document.getElementById('budget-percent').textContent = pct.toFixed(2) + '% 已用';
    document.getElementById('budget-limit').textContent = '上限 $' + (bud.dailyBudgetUsd || 0).toFixed(2);
    ['qps','requests','tokens'].forEach(function(k) {
        var val = sys['total' + k.charAt(0).toUpperCase() + k.slice(1)];
        if (document.getElementById('stat-' + k)) document.getElementById('stat-' + k).textContent = val;
        if (document.getElementById('card-' + k)) document.getElementById('card-' + k).textContent = val;
    });
    if (document.getElementById('stat-latency')) document.getElementById('stat-latency').textContent = sys.avgLatencyMs + ' ms';
    if (document.getElementById('card-latency')) document.getElementById('card-latency').textContent = sys.avgLatencyMs + ' ms';
    var pol = sys.routingPolicy;
    var toggle = function(id, val) {
        if (!document.getElementById(id)) return;
        document.getElementById(id).textContent = val ? '启用' : '禁用';
        document.getElementById(id).style.color = val ? 'var(--success)' : 'var(--text-secondary)';
    };
    toggle('engine-failover', pol.enableFailover);
    toggle('engine-budget', pol.enableBudgetGuard);
    toggle('engine-classifier', pol.enableRuleClassifier);
    renderAlerts(sys.alerts || []);
    renderModels(data.models);
    fetchTrends();
    loadLogs();
}

function renderAlerts(alerts) {
    pendingAlerts = alerts.filter(function(a) { return !dismissedAlerts.has(a.id); });
    if (pendingAlerts.length === 0) {
        var b = document.getElementById('alert-banner');
        if (b) b.style.display = 'none';
        return;
    }
    var w = pendingAlerts[0];
    var b = document.getElementById('alert-banner');
    if (!b) return;
    b.style.display = 'flex';
    b.style.background = w.level === 'critical' ? 'var(--danger-glow)' : 'var(--warning-glow)';
    b.style.borderColor = w.level === 'critical' ? 'rgba(239,68,68,0.3)' : 'rgba(245,158,11,0.3)';
    b.style.color = w.level === 'critical' ? 'var(--danger)' : 'var(--warning)';
    var msg = document.getElementById('alert-message');
    if (msg) msg.textContent = pendingAlerts.map(function(a){ return a.message; }).join(' | ');
}

// HTML 转义服务端/API 返回的可信外字符串（模型名、BaseUrl、tag 等），防止注入进 innerHTML 形成存储型 XSS。
function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function(c) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
}

function renderModels(models) {
    var grid = document.getElementById('models-grid');
    if (!grid) return;
    grid.innerHTML = '';
    models.forEach(function(m) {
        var sc = (m.circuitState || 'Closed').toLowerCase();
        var card = document.createElement('div');
        card.className = 'glass-card model-card state-' + sc;
        var badgeColor = sc === 'open' ? 'open' : sc === 'halfopen' ? 'halfopen' : 'closed';
        var badgeText = sc === 'open' ? '熔断' : sc === 'halfopen' ? '半开' : '正常';
        var tagsText = (m.tags && m.tags.length) ? m.tags.join(', ') : '-';
        // 延迟感知统计：无数据（冷启动/低流量）显示 '--'。
        var avgLat = (m.avgLatencyMs != null) ? Math.round(m.avgLatencyMs) + ' ms' : '--';
        var samples = m.latencySamples || 0;
        var nameE = esc(m.name), tierE = esc(m.tier || ''), baseUrlE = esc(m.baseUrl || ''), tagsE = esc(tagsText);
        card.innerHTML = '<div class="card-header-row"><div><div class="model-name">' + nameE + '</div><div style="font-size:0.75rem; color:var(--text-secondary); margin-top:0.15rem;">' + tierE + ' Tier</div></div><span class="status-badge ' + badgeColor + '">' + badgeText + '</span></div><div class="info-grid"><span class="info-label">BaseUrl</span><span class="info-val" style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:160px;">' + baseUrlE + '</span><span class="info-label">输入 $/M</span><span class="info-val">$' + (m.inputPricePerMillion||0).toFixed(2) + '</span><span class="info-label">输出 $/M</span><span class="info-val">$' + (m.outputPricePerMillion||0).toFixed(2) + '</span><span class="info-label">最大上下文</span><span class="info-val">' + (m.maxContextTokens||0).toLocaleString() + '</span><span class="info-label">Tags</span><span class="info-val" style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:160px;" title="' + tagsE + '">' + tagsE + '</span></div><div class="metrics-row"><div class="metric-item"><span class="metric-lbl">失败次数</span><span class="metric-val" style="color:' + ((m.failureCount||0)>0 ? 'var(--danger)' : 'var(--text-secondary)') + ';">' + (m.failureCount||0) + '</span></div><div class="metric-item" style="text-align:center;"><span class="metric-lbl">平均延迟</span><span class="metric-val" style="color:' + (samples > 0 ? 'var(--primary)' : 'var(--text-secondary)') + ';">' + avgLat + '</span></div><div class="metric-item" style="text-align:center;"><span class="metric-lbl">延迟样本</span><span class="metric-val" style="color:' + (samples > 0 ? 'var(--text-secondary)' : 'var(--text-secondary)') + ';">' + samples + '</span></div><div class="metric-item" style="text-align:right;"><span class="metric-lbl">活跃探测</span><span class="metric-val" style="color:' + ((m.activeProbes||0)>0 ? 'var(--warning)' : 'var(--text-secondary)') + ';">' + (m.activeProbes||0) + '</span></div></div>';
        grid.appendChild(card);
    });
}

async function fetchTrends() {
    try {
        var resp = await fetch(buildUrl('/api/dashboard/trends', { days: trendDays }), authOpts());
        if (!resp.ok) return;
        drawTrendChart(await resp.json());
    } catch (err) { console.error('trends error:', err); }
}

function setTrendDays(days, btn) {
    trendDays = days;
    document.querySelectorAll('.trend-controls button').forEach(function(b){ b.classList.remove('active'); });
    if (btn) btn.classList.add('active');
    fetchTrends();
}

function drawTrendChart(data) {
    var canvas = document.getElementById('trend-chart');
    if (!canvas || !data || data.length === 0) return;
    var ctx = canvas.getContext('2d');
    var dpr = window.devicePixelRatio || 1;
    var rect = canvas.parentElement.getBoundingClientRect();
    canvas.width = rect.width * dpr; canvas.height = rect.height * dpr;
    ctx.scale(dpr, dpr);
    var W = rect.width, H = rect.height;
    var pad = {t:20,r:20,b:30,l:60};
    var cw = W-pad.l-pad.r, ch = H-pad.t-pad.b;
    ctx.clearRect(0,0,W,H);
    var vals = data.map(function(d){ return d.amount; });
    var mx = Math.max.apply(null, vals.concat([0.01]));
    ctx.strokeStyle = 'rgba(255,255,255,0.04)'; ctx.lineWidth = 1;
    for (var i=0; i<=4; i++) {
        var y = pad.t + ch*i/4;
        ctx.beginPath(); ctx.moveTo(pad.l,y); ctx.lineTo(W-pad.r,y); ctx.stroke();
        ctx.fillStyle='#9ca3af'; ctx.font='11px JetBrains Mono'; ctx.textAlign='right';
        ctx.fillText('$'+(mx - mx*i/4).toFixed(4), pad.l-8, y+4);
    }
    var pts = data.map(function(d,i){ return { x:pad.l+cw*i/Math.max(data.length-1,1), y:pad.t+ch-(ch*d.amount/mx) }; });
    var grad = ctx.createLinearGradient(0,pad.t,0,H-pad.b);
    grad.addColorStop(0,'rgba(99,102,241,0.3)'); grad.addColorStop(1,'rgba(99,102,241,0)');
    ctx.beginPath(); ctx.moveTo(pts[0].x,H-pad.b);
    pts.forEach(function(p){ ctx.lineTo(p.x,p.y); });
    ctx.lineTo(pts[pts.length-1].x,H-pad.b); ctx.closePath(); ctx.fillStyle=grad; ctx.fill();
    ctx.beginPath(); ctx.strokeStyle='#6366f1'; ctx.lineWidth=2; ctx.lineJoin='round';
    pts.forEach(function(p,i){ i===0?ctx.moveTo(p.x,p.y):ctx.lineTo(p.x,p.y); }); ctx.stroke();
    pts.forEach(function(p){ ctx.beginPath(); ctx.arc(p.x,p.y,3,0,Math.PI*2); ctx.fillStyle='#6366f1'; ctx.fill(); });
    ctx.fillStyle='#9ca3af'; ctx.font='10px JetBrains Mono'; ctx.textAlign='center';
    data.forEach(function(d,i){
        var x = pad.l+cw*i/Math.max(data.length-1,1);
        ctx.fillText(new Date(d.date+'T00:00:00Z').toLocaleDateString(undefined,{month:'short',day:'numeric'}), x, H-pad.b+16);
    });
}

async function loadLogs() {
    var modelEl = document.getElementById('log-filter-model');
    var model = modelEl ? modelEl.value.trim() : '';
    var limitEl = document.getElementById('log-limit');
    logLimit = limitEl ? (parseInt(limitEl.value) || 50) : 50;
    try {
        var params = { limit: logLimit, offset: logOffset };
        if (model) params.model = model;
        var resp = await fetch(buildUrl('/api/dashboard/requests', params), authOpts());
        if (!resp.ok) return;
        var data = await resp.json();
        logTotal = data.totalCount || 0;
        var tbody = document.getElementById('log-body');
        if (!tbody) return;
        tbody.innerHTML = '';
        (data.items||[]).forEach(function(item){
            var tr = document.createElement('tr');
            var costText = '$' + (item.cost||0).toFixed(6) + (item.isEstimated ? ' <span style="color:var(--warning); font-size:0.7rem;">预估</span>' : '');
            tr.innerHTML = '<td>' + new Date(item.timestamp).toLocaleTimeString() + '</td><td>' + esc(item.model||'') + '</td><td>' + ((item.promptTokens||0)+(item.completionTokens||0)) + '</td><td>' + costText + '</td><td>' + (item.latencyMs||0) + 'ms</td><td class="' + (item.success?'success':'failure') + '">' + (item.success?'成功':'失败') + '</td><td>' + (item.isStreaming?'是':'否') + '</td>';
            tbody.appendChild(tr);
        });
        var infoEl = document.getElementById('log-info');
        if (infoEl) infoEl.textContent = '显示 ' + (data.items||[]).length + ' 条，共 ' + logTotal + ' 条';
        var prevEl = document.getElementById('log-prev');
        var nextEl = document.getElementById('log-next');
        if (prevEl) prevEl.disabled = logOffset === 0;
        if (nextEl) nextEl.disabled = logOffset + logLimit >= logTotal;
    } catch (err) { console.error('logs error:', err); }
}

function logPage(delta) {
    logOffset = Math.max(0, logOffset + delta * logLimit);
    loadLogs();
}

// Auto refresh
setInterval(fetchMetrics, 2000);
window.addEventListener('DOMContentLoaded', function() {
    propagateKeyToNav();
    fetchMetrics();
});
