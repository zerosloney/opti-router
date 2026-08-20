// Shared JS for OptiRouter Blazor components (theme, drawTrendChart, copyToClipboard)

// 主题：默认浅色，localStorage 持久化，data-theme 属性驱动 CSS 变量切换。
// 本脚本在 <head> 内同步执行，先于首帧渲染 → 无闪烁。
(function () {
    var saved = null;
    try { saved = localStorage.getItem('optirouter-theme'); } catch (e) { }
    document.documentElement.setAttribute('data-theme', saved === 'dark' ? 'dark' : 'light');
})();

window.getTheme = function () {
    return document.documentElement.getAttribute('data-theme') || 'dark';
};

window.setTheme = function (theme) {
    var t = theme === 'light' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', t);
    try { localStorage.setItem('optirouter-theme', t); } catch (e) { }
    return t;
};

window.toggleTheme = function () {
    return window.setTheme(window.getTheme() === 'light' ? 'dark' : 'light');
};

// 复制文本到剪贴板。优先 navigator.clipboard（需 secure context）；
// 非安全上下文（http 非 localhost 访问）回退 textarea + execCommand，返回是否成功。
window.copyToClipboard = function(text) {
    if (navigator.clipboard && window.isSecureContext) {
        return navigator.clipboard.writeText(text).then(
            function() { return true; },
            function() { return legacyCopyToClipboard(text); });
    }
    return legacyCopyToClipboard(text);
};

function legacyCopyToClipboard(text) {
    var ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.top = '-9999px';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    try { return document.execCommand('copy'); }
    catch (e) { return false; }
    finally { document.body.removeChild(ta); }
}

window.drawTrendChart = function(canvas, data) {
    if (!canvas || !data || data.length === 0) return;
    var ctx = canvas.getContext('2d');
    var dpr = window.devicePixelRatio || 1;
    var rect = canvas.parentElement.getBoundingClientRect();
    canvas.width = rect.width * dpr; canvas.height = rect.height * dpr;
    // setTransform 重置后再 scale，避免多次调用累积缩放
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.scale(dpr, dpr);
    var W = rect.width, H = rect.height;
    var pad = {t: 20, r: 24, b: 32, l: 64};
    var cw = W - pad.l - pad.r, ch = H - pad.t - pad.b;
    ctx.clearRect(0, 0, W, H);
    var vals = data.map(function(d){ return d.amount; });
    var mx = Math.max.apply(null, vals.concat([0.001]));
    
    // Gridlines & Y-Axis Labels
    var gridColor = getComputedStyle(document.documentElement).getPropertyValue('--chart-grid').trim() || 'rgba(255, 255, 255, 0.04)';
    ctx.strokeStyle = gridColor;
    ctx.lineWidth = 1;
    for (var i = 0; i <= 4; i++) {
        var y = pad.t + ch * i / 4;
        ctx.beginPath();
        ctx.moveTo(pad.l, y);
        ctx.lineTo(W - pad.r, y);
        ctx.stroke();
        
        ctx.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--text-secondary').trim() || '#64748b';
        ctx.font = '11px "JetBrains Mono", monospace';
        ctx.textAlign = 'right';
        ctx.fillText('$' + (mx - mx * i / 4).toFixed(4), pad.l - 10, y + 3.5);
    }
    
    // Points calculation
    var pts = data.map(function(d, i){ 
        return { 
            x: pad.l + cw * i / Math.max(data.length - 1, 1), 
            y: pad.t + ch - (ch * d.amount / mx) 
        }; 
    });
    
    // Gradient fill under curve
    var grad = ctx.createLinearGradient(0, pad.t, 0, H - pad.b);
    grad.addColorStop(0, 'rgba(99, 102, 241, 0.16)');
    grad.addColorStop(1, 'rgba(99, 102, 241, 0.0)');
    ctx.beginPath();
    ctx.moveTo(pts[0].x, H - pad.b);
    pts.forEach(function(p){ ctx.lineTo(p.x, p.y); });
    ctx.lineTo(pts[pts.length - 1].x, H - pad.b);
    ctx.closePath();
    ctx.fillStyle = grad;
    ctx.fill();
    
    // Line stroke
    ctx.beginPath();
    ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue('--primary').trim() || '#6366f1';
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.lineCap = 'round';
    pts.forEach(function(p, i){ i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y); });
    ctx.stroke();
    
    // Data point dots
    pts.forEach(function(p){
        ctx.beginPath();
        ctx.arc(p.x, p.y, 3, 0, Math.PI * 2);
        ctx.fillStyle = '#6366f1';
        ctx.fill();
        ctx.lineWidth = 1.5;
        ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue('--primary').trim() || '#0f172a';
        ctx.stroke();
    });
    
    // X-Axis Labels
    ctx.fillStyle = '#64748b';
    ctx.font = '11px "JetBrains Mono", monospace';
    ctx.textAlign = 'center';
    data.forEach(function(d, i){
        var x = pad.l + cw * i / Math.max(data.length - 1, 1);
        ctx.fillText(new Date(d.date + 'T00:00:00Z').toLocaleDateString(undefined, {month: 'short', day: 'numeric'}), x, H - pad.b + 18);
    });
};

// 审计分析：按日请求量线图（整数 Y 轴，与 drawTrendChart 同风格）。
// data: [{day: 'YYYY-MM-DD', requests: n, successes: n, costUsd: x}]
window.drawAnalysisTrendChart = function(canvas, data) {
    if (!canvas || !data || data.length === 0) return;
    var ctx = canvas.getContext('2d');
    var dpr = window.devicePixelRatio || 1;
    var rect = canvas.parentElement.getBoundingClientRect();
    canvas.width = rect.width * dpr; canvas.height = rect.height * dpr;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.scale(dpr, dpr);
    var W = rect.width, H = rect.height;
    var pad = {t: 20, r: 24, b: 32, l: 56};
    var cw = W - pad.l - pad.r, ch = H - pad.t - pad.b;
    ctx.clearRect(0, 0, W, H);
    var mx = Math.max.apply(null, data.map(function(d){ return d.requests; }).concat([1]));

    var gridColor = getComputedStyle(document.documentElement).getPropertyValue('--chart-grid').trim() || 'rgba(255, 255, 255, 0.04)';
    ctx.strokeStyle = gridColor;
    ctx.lineWidth = 1;
    for (var i = 0; i <= 4; i++) {
        var y = pad.t + ch * i / 4;
        ctx.beginPath();
        ctx.moveTo(pad.l, y);
        ctx.lineTo(W - pad.r, y);
        ctx.stroke();

        ctx.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--text-secondary').trim() || '#64748b';
        ctx.font = '11px "JetBrains Mono", monospace';
        ctx.textAlign = 'right';
        ctx.fillText(String(Math.ceil(mx - mx * i / 4)), pad.l - 10, y + 3.5);
    }

    var pts = data.map(function(d, i) {
        return { x: pad.l + cw * i / Math.max(data.length - 1, 1), y: pad.t + ch - (ch * d.requests / mx) };
    });

    var grad = ctx.createLinearGradient(0, pad.t, 0, H - pad.b);
    grad.addColorStop(0, 'rgba(99, 102, 241, 0.16)');
    grad.addColorStop(1, 'rgba(99, 102, 241, 0.0)');
    ctx.beginPath();
    ctx.moveTo(pts[0].x, H - pad.b);
    pts.forEach(function(p){ ctx.lineTo(p.x, p.y); });
    ctx.lineTo(pts[pts.length - 1].x, H - pad.b);
    ctx.closePath();
    ctx.fillStyle = grad;
    ctx.fill();

    ctx.beginPath();
    ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue('--primary').trim() || '#6366f1';
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.lineCap = 'round';
    pts.forEach(function(p, i){ i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y); });
    ctx.stroke();

    pts.forEach(function(p){
        ctx.beginPath();
        ctx.arc(p.x, p.y, 3, 0, Math.PI * 2);
        ctx.fillStyle = '#6366f1';
        ctx.fill();
    });

    ctx.fillStyle = '#64748b';
    ctx.font = '11px "JetBrains Mono", monospace';
    ctx.textAlign = 'center';
    data.forEach(function(d, i){
        var x = pad.l + cw * i / Math.max(data.length - 1, 1);
        ctx.fillText(new Date(d.day + 'T00:00:00Z').toLocaleDateString(undefined, {month: 'short', day: 'numeric'}), x, H - pad.b + 18);
    });
};

// 审计分析：水平条形图（标签 + 数值文本自适应）。
// items: [{label: 'model-a', value: 123, text: '97.5% · $0.42'}]，调用方负责排序与截断。
window.drawAnalysisBarChart = function(canvas, items) {
    if (!canvas || !items || items.length === 0) return;
    var ctx = canvas.getContext('2d');
    var dpr = window.devicePixelRatio || 1;
    var rect = canvas.parentElement.getBoundingClientRect();
    canvas.width = rect.width * dpr; canvas.height = rect.height * dpr;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.scale(dpr, dpr);
    var W = rect.width, H = rect.height;
    var labelW = Math.min(180, W * 0.32);
    var rowH = Math.min(28, H / items.length);
    var barMax = W - labelW - 110;
    ctx.clearRect(0, 0, W, H);
    var mx = Math.max.apply(null, items.map(function(d){ return d.value; }).concat([0.001]));

    var primary = getComputedStyle(document.documentElement).getPropertyValue('--primary').trim() || '#6366f1';
    var textSecondary = getComputedStyle(document.documentElement).getPropertyValue('--text-secondary').trim() || '#64748b';

    items.forEach(function(it, i) {
        var cy = i * rowH + rowH / 2;
        // 标签（左对齐，超长截断）
        ctx.fillStyle = textSecondary;
        ctx.font = '11px "JetBrains Mono", monospace';
        ctx.textAlign = 'left';
        var label = it.label || '-';
        while (ctx.measureText(label).width > labelW - 12 && label.length > 4) label = label.slice(0, -2);
        ctx.fillText(label, 0, cy + 3.5);
        // 条
        var bw = Math.max(2, barMax * it.value / mx);
        ctx.fillStyle = primary;
        ctx.globalAlpha = 0.85;
        roundRect(ctx, labelW, cy - 6, bw, 12, 3);
        ctx.fill();
        ctx.globalAlpha = 1;
        // 数值文本
        ctx.fillStyle = textSecondary;
        ctx.textAlign = 'left';
        ctx.fillText(it.text || String(it.value), labelW + bw + 8, cy + 3.5);
    });

    function roundRect(c, x, y, w, h, r) {
        c.beginPath();
        c.moveTo(x + r, y);
        c.arcTo(x + w, y, x + w, y + h, r);
        c.arcTo(x + w, y + h, x, y + h, r);
        c.arcTo(x, y + h, x, y, r);
        c.arcTo(x, y, x + w, y, r);
        c.closePath();
    }
};
