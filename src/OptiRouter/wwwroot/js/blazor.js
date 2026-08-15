// Shared JS for OptiRouter Blazor components (drawTrendChart, copyToClipboard)

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
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.04)';
    ctx.lineWidth = 1;
    for (var i = 0; i <= 4; i++) {
        var y = pad.t + ch * i / 4;
        ctx.beginPath();
        ctx.moveTo(pad.l, y);
        ctx.lineTo(W - pad.r, y);
        ctx.stroke();
        
        ctx.fillStyle = '#64748b';
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
    ctx.strokeStyle = '#6366f1';
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
        ctx.strokeStyle = '#0f172a';
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
