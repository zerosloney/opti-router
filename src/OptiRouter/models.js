// OptiRouter 模型配置页 JavaScript

var modelConfigs = [];
var editingName = null; // 当前内联编辑的模型名称

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

// 把当前 key 透传到顶部导航链接，跳页时保持鉴权。
function propagateKeyToNav() {
    var kp = getKeyOpts();
    if (!kp.key) return;
    document.querySelectorAll('.nav-link').forEach(function(a) {
        var href = a.getAttribute('href');
        if (href && href.indexOf('/dashboard') === 0 || href && href.indexOf('/models') === 0) {
            a.setAttribute('href', href + (href.indexOf('?') >= 0 ? '&' : '?') + 'key=' + encodeURIComponent(kp.key));
        }
    });
}

async function loadModelConfigs() {
    try {
        var resp = await fetch(buildUrl('/api/models'), authOpts());
        if (!resp.ok) return;
        modelConfigs = await resp.json();
        renderModelConfigs();
    } catch (err) { console.error('load models error:', err); }
}

function toggleAddForm() {
    var form = document.getElementById('add-model-form');
    var btn = document.getElementById('toggle-add-form-btn');
    if (!form) return;
    var isVisible = form.style.display !== 'none';
    if (isVisible) {
        form.style.display = 'none';
        if (btn) btn.textContent = '+ 添加模型';
    } else {
        // Reset form
        ['add-name','add-baseurl','add-apikey'].forEach(function(id){ var el = document.getElementById(id); if(el) el.value = ''; });
        var tierEl = document.getElementById('add-tier'); if (tierEl) tierEl.value = 'Medium';
        var ctxEl = document.getElementById('add-ctx'); if (ctxEl) ctxEl.value = '8192';
        var toEl = document.getElementById('add-timeout'); if (toEl) toEl.value = '120';
        var retryEl = document.getElementById('add-retry'); if (retryEl) retryEl.value = '0';
        var inpEl = document.getElementById('add-inp'); if (inpEl) inpEl.value = '0';
        var outEl = document.getElementById('add-out'); if (outEl) outEl.value = '0';
        var enEl = document.getElementById('add-enabled'); if (enEl) enEl.value = 'true';
        var errEl = document.getElementById('add-form-error'); if (errEl) { errEl.style.display = 'none'; errEl.textContent = ''; }
        form.style.display = 'block';
        if (btn) btn.textContent = '- 取消添加';
    }
}

async function submitAddModel() {
    var errEl = document.getElementById('add-form-error');
    errEl.style.display = 'none';
    var name = (document.getElementById('add-name') || {}).value || '';
    var baseUrl = (document.getElementById('add-baseurl') || {}).value || '';
    if (!name.trim()) { errEl.textContent = '名称不能为空'; errEl.style.display = 'block'; return; }
    if (!baseUrl.trim()) { errEl.textContent = 'BaseUrl 不能为空'; errEl.style.display = 'block'; return; }
    var body = {
        name: name.trim(), baseUrl: baseUrl.trim(),
        apiKey: (document.getElementById('add-apikey') || {}).value || null,
        tier: (document.getElementById('add-tier') || {}).value || 'Medium',
        maxContextTokens: parseInt((document.getElementById('add-ctx') || {}).value) || 8192,
        timeoutSeconds: parseInt((document.getElementById('add-timeout') || {}).value) || 120,
        maxRetries: parseInt((document.getElementById('add-retry') || {}).value) || 0,
        enabled: (document.getElementById('add-enabled') || {}).value === 'true',
        inputPricePerMillion: parseFloat((document.getElementById('add-inp') || {}).value) || 0,
        outputPricePerMillion: parseFloat((document.getElementById('add-out') || {}).value) || 0,
        tags: []
    };
    try {
        var resp = await fetch(buildUrl('/api/models'), { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
        if (!resp.ok) { var e = await resp.json(); throw new Error(e.error || '创建失败'); }
        toggleAddForm();
        showToast('模型 "' + name + '" 添加成功', 'success');
        loadModelConfigs();
    } catch(err) { errEl.textContent = err.message; errEl.style.display = 'block'; }
}

// 启动行内编辑
function startEditRow(name) {
    var m = modelConfigs.find(function(x){ return x.name === name; });
    if (!m) return;
    editingName = name;
    renderModelConfigs();
}

// 取消行内编辑
function cancelEditRow() {
    editingName = null;
    renderModelConfigs();
}

async function submitEditRow(name) {
    var body = {
        baseUrl: (document.getElementById('edit-baseurl') || {}).value || '',
        apiKey: (document.getElementById('edit-apikey') || {}).value || null,
        tier: (document.getElementById('edit-tier') || {}).value || 'Medium',
        maxContextTokens: parseInt((document.getElementById('edit-ctx') || {}).value) || 0,
        timeoutSeconds: parseInt((document.getElementById('edit-timeout') || {}).value) || 0,
        maxRetries: parseInt((document.getElementById('edit-retry') || {}).value) || 0,
        enabled: (document.getElementById('edit-enabled') || {}).value === 'true',
        inputPricePerMillion: parseFloat((document.getElementById('edit-inp') || {}).value) || 0,
        outputPricePerMillion: parseFloat((document.getElementById('edit-out') || {}).value) || 0
    };
    if (!body.baseUrl.trim()) { showToast('BaseUrl 不能为空', 'error'); return; }
    try {
        var resp = await fetch(buildUrl('/api/models/' + encodeURIComponent(name)), { method:'PUT', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
        if (!resp.ok) { var e = await resp.json(); throw new Error(e.error || '更新失败'); }
        editingName = null;
        showToast('模型 "' + name + '" 更新成功', 'success');
        loadModelConfigs();
    } catch(err) { showToast(err.message, 'error'); }
}

async function deleteModel(name) {
    if (!confirm('确定要删除模型 "' + name + '" 吗？\n此操作不可撤销。')) return;
    try {
        var resp = await fetch(buildUrl('/api/models/' + encodeURIComponent(name)), { method:'DELETE' });
        if (!resp.ok) { var e = await resp.json(); throw new Error(e.error || '删除失败'); }
        showToast('模型 "' + name + '" 已删除', 'success');
        loadModelConfigs();
    } catch(err) { showToast(err.message, 'error'); }
}

function renderModelConfigs() {
    var tbody = document.getElementById('config-body');
    if (!tbody) return;
    if (!modelConfigs || modelConfigs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="11" style="text-align:center; color:var(--text-secondary); padding:1.5rem;">暂无模型配置，点击上方"+ 添加模型"开始添加。</td></tr>';
        return;
    }
    tbody.innerHTML = '';
    modelConfigs.forEach(function(m, idx) {
        // escHtml：文本显示字段（<td>、title 属性）的 HTML 转义，防存储型 XSS。
        // escHandler：内联 onclick 的 JS 单引号字符串转义（先 HTML 转义 <>&" 防破属性，再转义 \ 与 ' 防破 JS 串）。
        var escHtml = function(s) {
            return String(s == null ? '' : s).replace(/[&<>"']/g, function(c) {
                return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
            });
        };
        var escHandler = function(s) {
            var e = String(s == null ? '' : s)
                .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
            return e.replace(/\\/g, '\\\\').replace(/'/g, "\\'");
        };
        var esc = escHandler(m.name);
        var nameHtml = escHtml(m.name);
        var baseUrlHtml = escHtml(m.baseUrl||'');
        var tierHtml = escHtml(m.tier||'');
        var urlShort = m.baseUrl ? (m.baseUrl.length > 25 ? m.baseUrl.substring(0,25)+'...' : m.baseUrl) : '-';
        urlShort = escHtml(urlShort);
        var keyMask = m.hasApiKey ? '\u2022\u2022\u2022\u2022\u2022\u2022' : '<span style="color:var(--danger)">未配置</span>';

        if (editingName === m.name) {
            // 内联编辑行
            var tr = document.createElement('tr');
            tr.style.background = 'rgba(99,102,241,0.05)';
            tr.innerHTML =
                '<td><strong>' + nameHtml + '</strong></td>' +
                '<td><input type="text" id="edit-baseurl" value="' + escHtml(m.baseUrl||'') + '" style="font-size:0.8rem; width:120px;"></td>' +
                '<td><input type="password" id="edit-apikey" placeholder="(不变更则留空)" style="font-size:0.8rem; width:80px;"></td>' +
                '<td><select id="edit-tier" style="font-size:0.8rem;">' +
                    ['Strong','Medium','Cheap'].map(function(t){ return '<option value="'+t+'"' + (m.tier===t?' selected':'') + '>'+t+'</option>'; }).join('') +
                '</select></td>' +
                '<td><input type="number" id="edit-ctx" value="' + (m.maxContextTokens||8192) + '" min="1" style="font-size:0.8rem; width:70px;"></td>' +
                '<td><input type="number" id="edit-timeout" value="' + (m.timeoutSeconds||120) + '" min="1" style="font-size:0.8rem; width:60px;"></td>' +
                '<td><input type="number" id="edit-retry" value="' + (m.maxRetries||0) + '" min="0" style="font-size:0.8rem; width:50px;"></td>' +
                '<td><select id="edit-enabled" style="font-size:0.8rem;">' +
                    '<option value="true"' + (m.enabled?' selected':'') + '>是</option>' +
                    '<option value="false"' + (!m.enabled?' selected':'') + '>否</option>' +
                '</select></td>' +
                '<td><input type="number" id="edit-inp" value="' + (m.inputPricePerMillion||0) + '" min="0" step="0.001" style="font-size:0.8rem; width:60px;"></td>' +
                '<td><input type="number" id="edit-out" value="' + (m.outputPricePerMillion||0) + '" min="0" step="0.001" style="font-size:0.8rem; width:60px;"></td>' +
                '<td>' +
                    '<button class="action-btn" onclick="submitEditRow(\'' + esc + '\')">保存</button>' +
                    '<button class="del-btn" onclick="cancelEditRow()">取消</button>' +
                '</td>';
            tbody.appendChild(tr);
        } else {
            // 显示行
            var enText = m.enabled ? '是' : '否';
            var tr = document.createElement('tr');
            tr.innerHTML =
                '<td><strong>' + nameHtml + '</strong></td>' +
                '<td title="'+baseUrlHtml+'" style="max-width:120px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:0.8rem;">' + urlShort + '</td>' +
                '<td style="font-size:0.8rem;">' + keyMask + '</td>' +
                '<td style="font-size:0.8rem;">' + tierHtml + '</td>' +
                '<td style="font-size:0.8rem;">' + ((m.maxContextTokens||0)/1000+'K') + '</td>' +
                '<td style="font-size:0.8rem;">' + (m.timeoutSeconds||120) + 's</td>' +
                '<td style="font-size:0.8rem;">' + (m.maxRetries||0) + '</td>' +
                '<td style="font-size:0.8rem; color:' + (m.enabled?'var(--success)':'var(--text-secondary)') + ';">' + enText + '</td>' +
                '<td style="font-size:0.8rem;">$' + (m.inputPricePerMillion||0).toFixed(3) + '</td>' +
                '<td style="font-size:0.8rem;">$' + (m.outputPricePerMillion||0).toFixed(3) + '</td>' +
                '<td>' +
                    '<button class="action-btn" onclick="startEditRow(\'' + esc + '\')">编辑</button>' +
                    '<button class="del-btn" onclick="deleteModel(\'' + esc + '\')">删除</button>' +
                '</td>';
            tbody.appendChild(tr);
        }
    });
}

function showToast(msg, type) {
    var t = document.getElementById('config-toast');
    if (!t) return;
    t.textContent = msg; t.className = 'config-toast ' + type;
    setTimeout(function(){ t.className = 'config-toast'; }, 3000);
}

window.addEventListener('DOMContentLoaded', function() {
    propagateKeyToNav();
    loadModelConfigs();
});
