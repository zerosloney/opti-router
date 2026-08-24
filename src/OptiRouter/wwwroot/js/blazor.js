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

// ── 辅助：三阶贝塞尔样条插值 ──
function drawSmoothSpline(ctx, pts) {
    if (!pts || pts.length === 0) return;
    if (pts.length === 1) {
        ctx.lineTo(pts[0].x, pts[0].y);
        return;
    }
    if (pts.length === 2) {
        ctx.lineTo(pts[1].x, pts[1].y);
        return;
    }
    for (var i = 0; i < pts.length - 1; i++) {
        var p0 = i > 0 ? pts[i - 1] : pts[i];
        var p1 = pts[i];
        var p2 = pts[i + 1];
        var p3 = i < pts.length - 2 ? pts[i + 2] : p2;
        
        var cp1x = p1.x + (p2.x - p0.x) / 6;
        var cp1y = p1.y + (p2.y - p0.y) / 6;
        var cp2x = p2.x - (p3.x - p1.x) / 6;
        var cp2y = p2.y - (p3.y - p1.y) / 6;
        
        ctx.bezierCurveTo(cp1x, cp1y, cp2x, cp2y, p2.x, p2.y);
    }
}

function roundRect(c, x, y, w, h, r) {
    if (w < 2 * r) r = w / 2;
    if (h < 2 * r) r = h / 2;
    c.beginPath();
    c.moveTo(x + r, y);
    c.arcTo(x + w, y, x + w, y + h, r);
    c.arcTo(x + w, y + h, x, y + h, r);
    c.arcTo(x, y + h, x, y, r);
    c.arcTo(x, y, x + w, y, r);
    c.closePath();
}

window.drawTrendChart = function(canvas, data) {
    if (!canvas || !data || data.length === 0) return;
    
    // 自动清理与绑定交互事件
    canvas._trendData = data;
    if (!canvas._hasInteractiveEvents) {
        canvas._hasInteractiveEvents = true;
        canvas._hoverIndex = -1;
        
        var handleMove = function(e) {
            var rect = canvas.getBoundingClientRect();
            var mx = e.clientX - rect.left;
            var curData = canvas._trendData;
            if (!curData || curData.length === 0) return;
            
            var pad = {t: 24, r: 24, b: 36, l: 68};
            var cw = rect.width - pad.l - pad.r;
            var step = cw / Math.max(curData.length - 1, 1);
            var nearestIdx = Math.round((mx - pad.l) / step);
            nearestIdx = Math.max(0, Math.min(curData.length - 1, nearestIdx));
            
            if (canvas._hoverIndex !== nearestIdx) {
                canvas._hoverIndex = nearestIdx;
                render();
            }
        };
        
        var handleLeave = function() {
            if (canvas._hoverIndex !== -1) {
                canvas._hoverIndex = -1;
                render();
            }
        };
        
        canvas.addEventListener('mousemove', handleMove);
        canvas.addEventListener('mouseleave', handleLeave);
        
        if (window.ResizeObserver) {
            new ResizeObserver(function() {
                if (canvas._trendData) render();
            }).observe(canvas.parentElement);
        }
    }
    
    function render() {
        var curData = canvas._trendData;
        if (!curData || curData.length === 0) return;
        var ctx = canvas.getContext('2d');
        var dpr = window.devicePixelRatio || 1;
        var rect = canvas.parentElement.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        
        canvas.width = rect.width * dpr;
        canvas.height = rect.height * dpr;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.scale(dpr, dpr);
        
        var W = rect.width, H = rect.height;
        var pad = {t: 24, r: 24, b: 36, l: 68};
        var cw = W - pad.l - pad.r, ch = H - pad.t - pad.b;
        ctx.clearRect(0, 0, W, H);
        
        var isDark = document.documentElement.getAttribute('data-theme') !== 'light';
        var primaryColor = getComputedStyle(document.documentElement).getPropertyValue('--primary').trim() || '#6366f1';
        var textSecondary = getComputedStyle(document.documentElement).getPropertyValue('--text-secondary').trim() || '#64748b';
        var gridColor = isDark ? 'rgba(255, 255, 255, 0.05)' : 'rgba(15, 23, 42, 0.06)';
        
        var vals = curData.map(function(d){ return d.amount; });
        var mx = Math.max.apply(null, vals.concat([0.001]));
        
        // Gridlines & Y-Axis
        ctx.save();
        ctx.strokeStyle = gridColor;
        ctx.lineWidth = 1;
        ctx.setLineDash([3, 4]);
        for (var i = 0; i <= 4; i++) {
            var y = pad.t + ch * i / 4;
            ctx.beginPath();
            ctx.moveTo(pad.l, y);
            ctx.lineTo(W - pad.r, y);
            ctx.stroke();
            
            ctx.fillStyle = textSecondary;
            ctx.font = '11px "JetBrains Mono", monospace';
            ctx.textAlign = 'right';
            ctx.fillText('$' + (mx - mx * i / 4).toFixed(4), pad.l - 12, y + 3.5);
        }
        ctx.restore();
        
        // Calculate points
        var pts = curData.map(function(d, i){ 
            return { 
                x: pad.l + cw * i / Math.max(curData.length - 1, 1), 
                y: pad.t + ch - (ch * d.amount / mx),
                data: d
            }; 
        });
        
        // Area Gradient Fill
        var grad = ctx.createLinearGradient(0, pad.t, 0, H - pad.b);
        grad.addColorStop(0, isDark ? 'rgba(99, 102, 241, 0.32)' : 'rgba(99, 102, 241, 0.22)');
        grad.addColorStop(0.5, isDark ? 'rgba(99, 102, 241, 0.10)' : 'rgba(99, 102, 241, 0.06)');
        grad.addColorStop(1, 'rgba(99, 102, 241, 0.0)');
        
        ctx.beginPath();
        ctx.moveTo(pts[0].x, H - pad.b);
        ctx.lineTo(pts[0].x, pts[0].y);
        drawSmoothSpline(ctx, pts);
        ctx.lineTo(pts[pts.length - 1].x, H - pad.b);
        ctx.closePath();
        ctx.fillStyle = grad;
        ctx.fill();
        
        // Smooth Spline Stroke with subtle glow
        ctx.save();
        ctx.beginPath();
        ctx.moveTo(pts[0].x, pts[0].y);
        drawSmoothSpline(ctx, pts);
        ctx.strokeStyle = primaryColor;
        ctx.lineWidth = 2.5;
        ctx.lineJoin = 'round';
        ctx.lineCap = 'round';
        ctx.shadowColor = isDark ? 'rgba(99, 102, 241, 0.5)' : 'rgba(99, 102, 241, 0.3)';
        ctx.shadowBlur = 8;
        ctx.stroke();
        ctx.restore();
        
        // Data point dots
        pts.forEach(function(p, i){
            var isHovered = (canvas._hoverIndex === i);
            ctx.beginPath();
            ctx.arc(p.x, p.y, isHovered ? 5.5 : 3.5, 0, Math.PI * 2);
            ctx.fillStyle = isHovered ? '#ffffff' : primaryColor;
            ctx.fill();
            ctx.lineWidth = isHovered ? 3 : 2;
            ctx.strokeStyle = primaryColor;
            ctx.stroke();
        });
        
        // X-Axis Labels
        ctx.fillStyle = textSecondary;
        ctx.font = '11px "JetBrains Mono", monospace';
        ctx.textAlign = 'center';
        curData.forEach(function(d, i){
            var x = pad.l + cw * i / Math.max(curData.length - 1, 1);
            var dateStr = new Date(d.date + 'T00:00:00Z').toLocaleDateString(undefined, {month: 'short', day: 'numeric'});
            ctx.fillText(dateStr, x, H - pad.b + 20);
        });
        
        // Interactive Hover Tooltip & Crosshair
        if (canvas._hoverIndex >= 0 && canvas._hoverIndex < pts.length) {
            var hp = pts[canvas._hoverIndex];
            
            // Vertical Crosshair
            ctx.save();
            ctx.strokeStyle = isDark ? 'rgba(255, 255, 255, 0.3)' : 'rgba(15, 23, 42, 0.25)';
            ctx.lineWidth = 1;
            ctx.setLineDash([2, 3]);
            ctx.beginPath();
            ctx.moveTo(hp.x, pad.t);
            ctx.lineTo(hp.x, H - pad.b);
            ctx.stroke();
            ctx.restore();
            
            // Tooltip Box
            var tipTextDate = hp.data.date;
            var tipTextCost = '支出: $' + hp.data.amount.toFixed(4);
            ctx.font = '11px "JetBrains Mono", monospace';
            var boxW = Math.max(ctx.measureText(tipTextDate).width, ctx.measureText(tipTextCost).width) + 20;
            var boxH = 44;
            var boxX = Math.min(Math.max(hp.x - boxW / 2, 8), W - boxW - 8);
            var boxY = Math.max(hp.y - boxH - 12, 8);
            
            ctx.save();
            ctx.shadowColor = 'rgba(0, 0, 0, 0.28)';
            ctx.shadowBlur = 12;
            ctx.shadowOffsetY = 4;
            ctx.fillStyle = isDark ? 'rgba(15, 23, 42, 0.92)' : 'rgba(255, 255, 255, 0.96)';
            ctx.strokeStyle = isDark ? 'rgba(255, 255, 255, 0.12)' : 'rgba(0, 0, 0, 0.08)';
            ctx.lineWidth = 1;
            roundRect(ctx, boxX, boxY, boxW, boxH, 6);
            ctx.fill();
            ctx.stroke();
            ctx.restore();
            
            ctx.fillStyle = textSecondary;
            ctx.font = '10px "JetBrains Mono", monospace';
            ctx.textAlign = 'left';
            ctx.fillText(tipTextDate, boxX + 10, boxY + 16);
            
            ctx.fillStyle = isDark ? '#38bdf8' : '#0284c7';
            ctx.font = 'bold 11px "JetBrains Mono", monospace';
            ctx.fillText(tipTextCost, boxX + 10, boxY + 34);
        }
    }
    
    render();
};

window.drawAnalysisTrendChart = function(canvas, data) {
    if (!canvas || !data || data.length === 0) return;
    
    canvas._analysisTrendData = data;
    if (!canvas._hasInteractiveEvents) {
        canvas._hasInteractiveEvents = true;
        canvas._hoverIndex = -1;
        
        var handleMove = function(e) {
            var rect = canvas.getBoundingClientRect();
            var mx = e.clientX - rect.left;
            var curData = canvas._analysisTrendData;
            if (!curData || curData.length === 0) return;
            
            var pad = {t: 24, r: 24, b: 36, l: 60};
            var cw = rect.width - pad.l - pad.r;
            var step = cw / Math.max(curData.length - 1, 1);
            var nearestIdx = Math.round((mx - pad.l) / step);
            nearestIdx = Math.max(0, Math.min(curData.length - 1, nearestIdx));
            
            if (canvas._hoverIndex !== nearestIdx) {
                canvas._hoverIndex = nearestIdx;
                render();
            }
        };
        
        var handleLeave = function() {
            if (canvas._hoverIndex !== -1) {
                canvas._hoverIndex = -1;
                render();
            }
        };
        
        canvas.addEventListener('mousemove', handleMove);
        canvas.addEventListener('mouseleave', handleLeave);
        
        if (window.ResizeObserver) {
            new ResizeObserver(function() {
                if (canvas._analysisTrendData) render();
            }).observe(canvas.parentElement);
        }
    }
    
    function render() {
        var curData = canvas._analysisTrendData;
        if (!curData || curData.length === 0) return;
        var ctx = canvas.getContext('2d');
        var dpr = window.devicePixelRatio || 1;
        var rect = canvas.parentElement.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        
        canvas.width = rect.width * dpr;
        canvas.height = rect.height * dpr;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.scale(dpr, dpr);
        
        var W = rect.width, H = rect.height;
        var pad = {t: 24, r: 24, b: 36, l: 60};
        var cw = W - pad.l - pad.r, ch = H - pad.t - pad.b;
        ctx.clearRect(0, 0, W, H);
        
        var isDark = document.documentElement.getAttribute('data-theme') !== 'light';
        var primaryColor = getComputedStyle(document.documentElement).getPropertyValue('--primary').trim() || '#6366f1';
        var textSecondary = getComputedStyle(document.documentElement).getPropertyValue('--text-secondary').trim() || '#64748b';
        var gridColor = isDark ? 'rgba(255, 255, 255, 0.05)' : 'rgba(15, 23, 42, 0.06)';
        
        var mx = Math.max.apply(null, curData.map(function(d){ return d.requests; }).concat([1]));
        
        // Gridlines
        ctx.save();
        ctx.strokeStyle = gridColor;
        ctx.lineWidth = 1;
        ctx.setLineDash([3, 4]);
        for (var i = 0; i <= 4; i++) {
            var y = pad.t + ch * i / 4;
            ctx.beginPath();
            ctx.moveTo(pad.l, y);
            ctx.lineTo(W - pad.r, y);
            ctx.stroke();
            
            ctx.fillStyle = textSecondary;
            ctx.font = '11px "JetBrains Mono", monospace';
            ctx.textAlign = 'right';
            ctx.fillText(String(Math.ceil(mx - mx * i / 4)), pad.l - 12, y + 3.5);
        }
        ctx.restore();
        
        var pts = curData.map(function(d, i) {
            return {
                x: pad.l + cw * i / Math.max(curData.length - 1, 1),
                y: pad.t + ch - (ch * d.requests / mx),
                data: d
            };
        });
        
        // Area Gradient
        var grad = ctx.createLinearGradient(0, pad.t, 0, H - pad.b);
        grad.addColorStop(0, isDark ? 'rgba(99, 102, 241, 0.32)' : 'rgba(99, 102, 241, 0.22)');
        grad.addColorStop(0.5, isDark ? 'rgba(99, 102, 241, 0.10)' : 'rgba(99, 102, 241, 0.06)');
        grad.addColorStop(1, 'rgba(99, 102, 241, 0.0)');
        
        ctx.beginPath();
        ctx.moveTo(pts[0].x, H - pad.b);
        ctx.lineTo(pts[0].x, pts[0].y);
        drawSmoothSpline(ctx, pts);
        ctx.lineTo(pts[pts.length - 1].x, H - pad.b);
        ctx.closePath();
        ctx.fillStyle = grad;
        ctx.fill();
        
        // Smooth Spline
        ctx.save();
        ctx.beginPath();
        ctx.moveTo(pts[0].x, pts[0].y);
        drawSmoothSpline(ctx, pts);
        ctx.strokeStyle = primaryColor;
        ctx.lineWidth = 2.5;
        ctx.lineJoin = 'round';
        ctx.lineCap = 'round';
        ctx.shadowColor = isDark ? 'rgba(99, 102, 241, 0.5)' : 'rgba(99, 102, 241, 0.3)';
        ctx.shadowBlur = 8;
        ctx.stroke();
        ctx.restore();
        
        // Dots
        pts.forEach(function(p, i){
            var isHovered = (canvas._hoverIndex === i);
            ctx.beginPath();
            ctx.arc(p.x, p.y, isHovered ? 5.5 : 3.5, 0, Math.PI * 2);
            ctx.fillStyle = isHovered ? '#ffffff' : primaryColor;
            ctx.fill();
            ctx.lineWidth = isHovered ? 3 : 2;
            ctx.strokeStyle = primaryColor;
            ctx.stroke();
        });
        
        // X-Axis Labels
        ctx.fillStyle = textSecondary;
        ctx.font = '11px "JetBrains Mono", monospace';
        ctx.textAlign = 'center';
        curData.forEach(function(d, i){
            var x = pad.l + cw * i / Math.max(curData.length - 1, 1);
            var dateStr = new Date(d.day + 'T00:00:00Z').toLocaleDateString(undefined, {month: 'short', day: 'numeric'});
            ctx.fillText(dateStr, x, H - pad.b + 20);
        });
        
        // Hover Tooltip
        if (canvas._hoverIndex >= 0 && canvas._hoverIndex < pts.length) {
            var hp = pts[canvas._hoverIndex];
            
            ctx.save();
            ctx.strokeStyle = isDark ? 'rgba(255, 255, 255, 0.3)' : 'rgba(15, 23, 42, 0.25)';
            ctx.lineWidth = 1;
            ctx.setLineDash([2, 3]);
            ctx.beginPath();
            ctx.moveTo(hp.x, pad.t);
            ctx.lineTo(hp.x, H - pad.b);
            ctx.stroke();
            ctx.restore();
            
            var tipDate = hp.data.day;
            var tipReq = '请求数: ' + hp.data.requests;
            ctx.font = '11px "JetBrains Mono", monospace';
            var boxW = Math.max(ctx.measureText(tipDate).width, ctx.measureText(tipReq).width) + 20;
            var boxH = 44;
            var boxX = Math.min(Math.max(hp.x - boxW / 2, 8), W - boxW - 8);
            var boxY = Math.max(hp.y - boxH - 12, 8);
            
            ctx.save();
            ctx.shadowColor = 'rgba(0, 0, 0, 0.28)';
            ctx.shadowBlur = 12;
            ctx.shadowOffsetY = 4;
            ctx.fillStyle = isDark ? 'rgba(15, 23, 42, 0.92)' : 'rgba(255, 255, 255, 0.96)';
            ctx.strokeStyle = isDark ? 'rgba(255, 255, 255, 0.12)' : 'rgba(0, 0, 0, 0.08)';
            ctx.lineWidth = 1;
            roundRect(ctx, boxX, boxY, boxW, boxH, 6);
            ctx.fill();
            ctx.stroke();
            ctx.restore();
            
            ctx.fillStyle = textSecondary;
            ctx.font = '10px "JetBrains Mono", monospace';
            ctx.textAlign = 'left';
            ctx.fillText(tipDate, boxX + 10, boxY + 16);
            
            ctx.fillStyle = isDark ? '#38bdf8' : '#0284c7';
            ctx.font = 'bold 11px "JetBrains Mono", monospace';
            ctx.fillText(tipReq, boxX + 10, boxY + 34);
        }
    }
    
    render();
};

window.drawAnalysisBarChart = function(canvas, items) {
    if (!canvas || !items || items.length === 0) return;
    
    canvas._analysisBarItems = items;
    if (!canvas._hasBarResizeObserver) {
        canvas._hasBarResizeObserver = true;
        if (window.ResizeObserver && canvas.parentElement) {
            new ResizeObserver(function() {
                if (canvas._analysisBarItems) render();
            }).observe(canvas.parentElement);
        }
    }

    function render() {
        var curItems = canvas._analysisBarItems;
        if (!curItems || curItems.length === 0) return;
        var ctx = canvas.getContext('2d');
        var dpr = window.devicePixelRatio || 1;
        var rect = canvas.parentElement.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        
        canvas.width = rect.width * dpr;
        canvas.height = rect.height * dpr;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.scale(dpr, dpr);
        
        var W = rect.width, H = rect.height;
        var labelW = Math.min(180, W * 0.32);
        var rowH = Math.min(30, Math.max(22, H / curItems.length));
        var totalContentH = curItems.length * rowH;
        var startY = totalContentH < H ? (H - totalContentH) / 2 : 0;
        var barMax = Math.max(40, W - labelW - 140);
        ctx.clearRect(0, 0, W, H);
        
        var isDark = document.documentElement.getAttribute('data-theme') !== 'light';
        var primary = getComputedStyle(document.documentElement).getPropertyValue('--primary').trim() || '#6366f1';
        var textSecondary = getComputedStyle(document.documentElement).getPropertyValue('--text-secondary').trim() || '#64748b';
        var trackBg = isDark ? 'rgba(255, 255, 255, 0.05)' : 'rgba(15, 23, 42, 0.05)';
        
        var mx = Math.max.apply(null, curItems.map(function(d){ return d.value; }).concat([0.001]));

        curItems.forEach(function(it, i) {
            var cy = startY + i * rowH + rowH / 2;
            
            // 标签（左对齐，超长截断）
            ctx.fillStyle = textSecondary;
            ctx.font = '11px "JetBrains Mono", monospace';
            ctx.textAlign = 'left';
            var label = it.label || '-';
            while (ctx.measureText(label).width > labelW - 12 && label.length > 4) label = label.slice(0, -2);
            ctx.fillText(label, 0, cy + 3.5);
            
            // 底层轨道 (Track)
            ctx.fillStyle = trackBg;
            roundRect(ctx, labelW, cy - 6, barMax, 12, 6);
            ctx.fill();
            
            // 前景条 (Gradient Bar)
            var bw = Math.max(4, barMax * it.value / mx);
            var barGrad = ctx.createLinearGradient(labelW, 0, labelW + bw, 0);
            barGrad.addColorStop(0, primary);
            barGrad.addColorStop(1, isDark ? '#38bdf8' : '#0284c7');
            
            ctx.fillStyle = barGrad;
            roundRect(ctx, labelW, cy - 6, bw, 12, 6);
            ctx.fill();
            
            // 数值与指标文本
            ctx.fillStyle = isDark ? '#e2e8f0' : '#334155';
            ctx.font = '11px "JetBrains Mono", monospace';
            ctx.textAlign = 'left';
            ctx.fillText(it.text || String(it.value), labelW + barMax + 12, cy + 3.5);
        });
    }

    render();
};

// ===== Blazor Server 会话保活与断线恢复 =====
// 管理台是 Blazor Server：页面加载后浏览器不再发任何 HTTP 请求（全走 WebSocket），
// 登录 Cookie 的 8h 滑动过期无请求可续期，面板常开超 8h 必然掉登录。

// 会话保活：每 30 分钟带 Cookie 请求一次授权 ping 端点，让滑动续期真正生效。
// 响应无需处理：Cookie 已失效时拿到 401，由下方重连终态恢复兜底转登录页。
setInterval(function () {
    fetch('/api/dashboard/session/ping', { credentials: 'same-origin', cache: 'no-store' })
        .catch(function () { });
}, 30 * 60 * 1000);

// 重连终态自动恢复：断线重连彻底失败（components-reconnect-failed，如 Cookie 过期后
// negotiate 被 302 到 /login）或电路被拒（components-reconnect-rejected，如服务重启）
// 时，内置横幅会永久卡住。此处代替用户点"重新加载"：Cookie 有效则整页刷新自愈，
// 已过期则被服务端 302 到 /login 重新登录。
// intentional-simple: 1s 轮询 modal class（元素由 blazor.server.js 断线时动态创建），
// 终态几秒内被捕捉即可；60s 内只自动刷新一次（sessionStorage 标记）防循环刷新。
(function () {
    var MARK = 'optirouter-reconnect-reload-at';
    setInterval(function () {
        var m = document.getElementById('components-reconnect-modal');
        if (!m) return;
        var c = m.className;
        if (c.indexOf('components-reconnect-failed') < 0 && c.indexOf('components-reconnect-rejected') < 0) return;
        var last = 0;
        try { last = +(sessionStorage.getItem(MARK) || 0); } catch (e) { }
        if (Date.now() - last < 60000) return;
        try { sessionStorage.setItem(MARK, String(Date.now())); } catch (e) { }
        window.location.reload();
    }, 1000);
})();

// BFCache 恢复直接刷新：浏览器把页面冻结进 Back-Forward Cache 时会杀掉 WebSocket
// （控制台 1006），返回该页时 Blazor 内置重连要走完整个协议——电路已销毁时要等
// 重试耗尽进终态才被上面兜底，期间横幅卡住。pageshow.persisted 即"自 BFCache 恢复"，
// 直接整页刷新拿新鲜电路：Cookie 有效秒级自愈，过期被 302 到 /login。
// 每次恢复都刷新是正确行为（需用户一次前进/后退触发，不构成刷新循环，不设防环标记）。
window.addEventListener('pageshow', function (e) {
    if (e.persisted) window.location.reload();
});

