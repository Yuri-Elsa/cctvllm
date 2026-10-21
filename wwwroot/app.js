const $ = id => document.getElementById(id);
const escapeHtml = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const duration = s => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
let state = { jobs: [], running: false, settings: {} }, cameras = [], scan = {}, selected = null, queueFilter = 'all', cameraFilter = 'all', queueKey = '', detailKey = '', cameraOptionsKey = '', cameraPanelHtml = '', seenScan, uploading = false, timer;
function toast(text) { $('toast').textContent = text; $('toast').className = 'visible'; clearTimeout(timer); timer = setTimeout(() => $('toast').className = '', 7000) }
async function api(path, options = {}) { const r = await fetch(path, options); if (r.status === 401) { location.href = '/Account/Login'; throw Error('Signed out. Opening the sign-in page…') } const data = await r.json(); if (!r.ok) throw Error(data.detail || 'Request failed.'); return data }
async function refresh() {
    try {
        const [s, c, f] = await Promise.all([api('/api/state'), api('/api/cameras').catch(() => cameras), api('/api/folder/status').catch(() => scan)]);
        state = s; cameras = c; scan = f; render(); renderCameras(); trackScan()
    } catch { $('queueNote').textContent = 'Server disconnected.'; $('runStat').textContent = 'Offline'; $('modelStatus').textContent = 'Server offline' }
}
const isFlagged = j => j.status === 'completed' && (j.suspicious === 'yes' || j.weapon === 'yes');
const isDropped = j => j.status === 'cancelled' && j.stage === 'Dropped (backlog)';
const NO_CAMERA = '::none';
const segmentTime = iso => iso ? new Date(iso).toLocaleString([], { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '';
const ago = iso => { if (!iso) return 'No data yet'; const s = Math.max(0, Math.round((Date.now() - new Date(iso)) / 1000)); return s < 5 ? 'just now' : s < 60 ? `${s}s ago` : s < 3600 ? `${Math.floor(s / 60)}m ago` : s < 86400 ? `${Math.floor(s / 3600)}h ago` : `${Math.floor(s / 86400)}d ago` };
const REVIEW = { confirmed: 'Confirmed', false_alarm: 'False alarm' };
function notifyInfo(j) {
    if (j.notifiedAt) return { cls: j.notifyError ? 'warn' : 'ok', text: `Sent ${segmentTime(j.notifiedAt)}` + (j.notifyError ? ` · some channels failed: ${j.notifyError}` : '') };
    if (j.notifyError) return { cls: 'fail', text: `Not delivered: ${j.notifyError}` };
    const agent = j.screening && j.screening.agent;
    if (agent && agent.action === 'ignore') return { cls: 'ok', text: `Agent chose not to notify${agent.reason ? ': ' + agent.reason : ''}` };
    if (isFlagged(j) || (j.status === 'failed' && j.sourcePath)) return { cls: 'pending', text: 'No delivery recorded yet (alerts are grouped per camera).' };
    return null;
}
const scoreTag = j => { const s = j.screening; if (!s || s.suspiciousScore == null) return ''; return `<span title="Probability the model answered yes, and the threshold used">p(yes) ${Number(s.suspiciousScore).toFixed(2)} · threshold ${Number(s.suspiciousThreshold).toFixed(2)}</span>` };
const agentSummary = j => { const a = j.screening && j.screening.agent; if (!a || a.fallbackUsed || !a.summary) return ''; return `<p class="notify-line ok"><strong>Agent</strong> ${escapeHtml(a.action)} · ${escapeHtml(a.urgency || '')} — ${escapeHtml(a.summary)}</p>` };
const notifyLine = j => { const n = notifyInfo(j); return (n ? `<p class="notify-line ${n.cls}"><strong>Notification</strong> ${escapeHtml(n.text)}</p>` : '') + agentSummary(j) };
const reviewChip = j => j.reviewState && REVIEW[j.reviewState] ? `<span class="review-chip ${escapeHtml(j.reviewState)}">${REVIEW[j.reviewState]}</span>` : '';
const notifyChip = j => { const n = notifyInfo(j); return n && (n.cls === 'fail' || n.cls === 'warn') ? '<span class="notify-chip">Notify failed</span>' : '' };
const reviewBar = j => `<div class="review" role="group" aria-label="Review this alert"><span class="review-label">Your review</span>${Object.entries(REVIEW).map(([value, label]) => `<button type="button" class="review-${value}" data-review="${j.id}" data-state="${value}" aria-pressed="${j.reviewState === value}">${label}</button>`).join('')}</div>`;
const matchesCamera = j => cameraFilter === 'all' || (cameraFilter === NO_CAMERA ? !j.cameraName : j.cameraName === cameraFilter);
const cameraChip = j => j.cameraName ? `<span class="camera-chip">${escapeHtml(j.cameraName)}</span>` : '';
const segmentLabel = j => j.segmentStart ? `<time datetime="${escapeHtml(j.segmentStart)}">${escapeHtml(segmentTime(j.segmentStart))}</time>` : '';
function syncCameraFilter() {
    const names = [...new Set([...cameras.map(c => c.camera), ...state.jobs.map(j => j.cameraName).filter(Boolean)])].sort((a, b) => a.localeCompare(b));
    const hasNone = names.length > 0 && state.jobs.some(j => !j.cameraName);
    if (cameraFilter !== 'all' && !(cameraFilter === NO_CAMERA ? hasNone : names.includes(cameraFilter))) cameraFilter = 'all';
    const key = JSON.stringify([names, hasNone]);
    if (key !== cameraOptionsKey) {
        cameraOptionsKey = key;
        $('cameraFilter').innerHTML = '<option value="all">All cameras</option>' + names.map(n => `<option value="${escapeHtml(n)}">${escapeHtml(n)}</option>`).join('') + (hasNone ? `<option value="${NO_CAMERA}">Uploads (no camera)</option>` : '');
    }
    $('cameraFilter').value = cameraFilter;
    $('cameraFilter').hidden = !names.length;
}
const CAMERA_STATE = { running: 'Running', retrying: 'Retrying', starting: 'Starting' };
function renderCameras() {
    $('cameraPanel').hidden = !cameras.length;
    if (!cameras.length) return;
    const running = cameras.filter(c => c.state === 'running').length;
    $('cameraSummary').textContent = `${running} of ${cameras.length} running`;
    const html = cameras.map(c => `<div class="camera-card ${escapeHtml(c.state)}"><div class="camera-card-head"><strong>${escapeHtml(c.camera)}</strong><span class="camera-state ${escapeHtml(c.state)}">${escapeHtml(CAMERA_STATE[c.state] ?? c.state)}</span></div><dl><div><dt>Last data</dt><dd${c.lastDataAt ? ` title="${escapeHtml(new Date(c.lastDataAt).toLocaleString())}"` : ''}>${escapeHtml(ago(c.lastDataAt))}</dd></div><div><dt>Restarts</dt><dd>${Number(c.restarts) || 0}</dd></div></dl>${c.lastError ? `<p class="camera-error" title="${escapeHtml(c.lastError)}">${escapeHtml(c.lastError)}</p>` : ''}</div>`).join('');
    if (html !== cameraPanelHtml) { cameraPanelHtml = html; $('cameraList').innerHTML = html }
}
function setQueueFilter(filter) {
    queueFilter = filter; render();
    $('videoQueue').scrollIntoView({ block: 'start' });
}
function render() {
    syncCameraFilter();
    const visibleJobs = state.jobs.filter(j => matchesCamera(j) && (queueFilter !== 'flagged' || isFlagged(j)));
    $('showFlagged').setAttribute('aria-pressed', String(queueFilter === 'flagged'));
    $('filterAll').setAttribute('aria-pressed', String(queueFilter === 'all'));
    $('filterFlagged').setAttribute('aria-pressed', String(queueFilter === 'flagged'));
    if (!visibleJobs.some(j => j.id === selected)) selected = visibleJobs[0]?.id ?? null;
    $('count').textContent = visibleJobs.length;
    $('totalStat').textContent = state.jobs.length;
    $('readyStat').textContent = state.jobs.filter(j => j.status === 'completed').length;
    $('flaggedStat').textContent = state.jobs.filter(isFlagged).length;
    $('runStat').textContent = state.running ? 'Processing' : state.jobs.some(j => j.status === 'queued') ? 'Paused' : 'Idle';
    $('modelStatus').textContent = state.settings.mode === 'local' ? state.settings.model : 'Notebook GPU';
    $('start').textContent = state.running ? 'Pause queue' : 'Generate descriptions';
    $('start').disabled = !state.running && !state.jobs.some(j => j.status === 'queued');
    $('queueNote').textContent = state.running ? 'Running. Pause after current video.' : 'One video at a time.';
    const linked = state.linkedFolder || null;
    $('linkedPath').textContent = linked || 'None';
    $('linkedPath').classList.toggle('none', !linked);
    $('linkFolder').textContent = linked ? 'Change' : 'Link a folder';
    $('refreshFolder').hidden = !linked;
    $('unlinkFolder').hidden = !linked;
    const key = JSON.stringify([selected, queueFilter, cameraFilter, state.jobs.map(j => [j.id, j.status, j.stage, j.suspicious, j.weapon, j.reviewState, j.notifiedAt, j.notifyError, j.screening?.agent?.action])]);
    if (key !== queueKey) { queueKey = key; $('queue').innerHTML = visibleJobs.length ? visibleJobs.map(j => `<div class="queue-item ${selected === j.id ? 'selected' : ''}" role="button" tabindex="0" data-select="${j.id}" aria-label="View video ${state.jobs.indexOf(j) + 1}: ${escapeHtml(j.cameraName ? j.cameraName + ', ' : '')}${escapeHtml(j.name)}"><span class="queue-number" aria-hidden="true">${state.jobs.indexOf(j) + 1}</span><img src="/api/videos/${j.id}/poster.jpg" alt=""><div class="row-info"><span class="row-name">${escapeHtml(j.name)}</span><div class="row-meta">${cameraChip(j)}${segmentLabel(j)}<span>${duration(j.duration)} · ${(j.bytes / 1048576).toFixed(1)} MB</span>${reviewChip(j)}${notifyChip(j)}</div></div><span class="queue-status ${isDropped(j) ? 'dropped' : isFlagged(j) ? 'flagged' : j.status}">${isDropped(j) ? 'Dropped' : isFlagged(j) ? 'Flagged' : j.status === 'completed' ? 'Ready' : j.status === 'processing' ? 'Processing' : j.status === 'queued' ? 'Queued' : j.status === 'failed' ? 'Failed' : 'Cancelled'}</span><button class="remove" data-id="${j.id}" data-action="${['queued', 'processing'].includes(j.status) ? 'cancel' : 'delete'}" aria-label="${['queued', 'processing'].includes(j.status) ? 'Cancel' : 'Remove'} ${escapeHtml(j.name)}">×</button></div>`).join('') : `<div class="empty">${queueFilter === 'flagged' ? 'No flagged videos' + (cameraFilter === 'all' ? '.' : ' for this camera.') : cameraFilter !== 'all' ? 'No videos for this camera yet.' : 'No videos added.'}</div>` }
    const job = state.jobs.find(j => j.id === selected); const detail = JSON.stringify(job ?? null); if (detail === detailKey) return; detailKey = detail;
    $('status').textContent = job?.stage ?? '';
    if (job && $('detail').dataset.id === job.id) { $('result').innerHTML = result(job); return }
    $('detail').dataset.id = job?.id ?? '';
    $('detail').innerHTML = job ? `<div class="video-wrap"><video controls playsinline preload="metadata" src="/api/videos/${job.id}/video" poster="/api/videos/${job.id}/poster.jpg"></video><div class="video-meta">${escapeHtml(job.name)} · ${duration(job.duration)}${job.cameraName || job.segmentStart ? `<div class="video-meta-sub">${cameraChip(job)}${segmentLabel(job)}</div>` : ''}</div></div><div class="result" id="result">${result(job)}</div>` : '<div class="placeholder">Select a video</div><div class="result"><h3>Description</h3><p class="waiting">No description yet.</p></div>';
}
function result(j) {
    if (j.status === 'completed') return `<h3>Description</h3><p>${escapeHtml(j.description)}</p><div class="tags"><span class="flag-${j.suspicious === 'yes' ? 'yes' : 'no'}">Suspicious: ${escapeHtml(j.suspicious ?? 'unknown')}</span><span class="flag-${j.weapon === 'yes' ? 'yes' : 'no'}">Weapon: ${escapeHtml(j.weaponEnabled === false ? 'Disabled' : j.weapon ?? 'unknown')}</span><span>${j.timestamps.length} frames · ${j.elapsed}s${j.personLabels ? ' · Person labels' : ''}</span>${scoreTag(j)}</div><div class="result-actions"><button data-copy="${j.id}">Copy</button><button data-download="${j.id}">Download</button></div>${isFlagged(j) ? reviewBar(j) : ''}${notifyLine(j)}`;
    if (j.status === 'failed') return `<h3>Generation failed</h3><p class="error">${escapeHtml(j.error)}</p><div class="result-actions"><button data-id="${j.id}" data-action="retry">Retry</button></div>${notifyLine(j)}`;
    if (isDropped(j)) return `<h3>Skipped: too many videos waiting</h3><p>${j.cameraName ? `Camera <strong>${escapeHtml(j.cameraName)}</strong> had` : 'This camera had'} more videos waiting than the model could process, so this older recording was skipped in favor of newer footage. It was <strong>not analysed</strong>: having no description does not mean nothing happened in this clip.</p><p class="waiting">The file is kept for now. Requeue it to analyse it anyway; if the queue is still full it may be dropped again.${j.error ? `<br><small>${escapeHtml(j.error)}</small>` : ''}</p><div class="result-actions"><button data-id="${j.id}" data-action="retry">Requeue</button></div>`;
    if (j.status === 'cancelled') return `<h3>Cancelled</h3><div class="result-actions"><button data-id="${j.id}" data-action="retry">Requeue</button></div>`;
    return `<h3>Description</h3><p class="waiting">${j.status === 'processing' ? escapeHtml(j.stage) + '…' : 'Queued. Click Generate descriptions to start.'}</p>`;
}
const isVideo = file => file.type.startsWith('video/') || /\.(mp4|mov|webm|m4v|avi|mkv|mpeg|mpg|mts|m2ts|wmv|3gp|ogv)$/i.test(file.name);
async function upload(files, { skipped = 0 } = {}) {
    if (uploading) { toast('Wait for the current upload to finish.'); return }
    if (!files.length) { toast('No videos found in this folder.'); $('folder').value = ''; return }
    uploading = true; $('addFolder').disabled = true; $('files').disabled = true;
    $('uploadErrors').hidden = true;
    let added = 0; const errors = [];
    try {
        for (let i = 0; i < files.length; i++) {
            const file = files[i], name = file.webkitRelativePath || file.name;
            $('uploadStatus').textContent = `Adding ${i + 1}/${files.length}: ${name}`;
            if (file.size > 500 * 1024 * 1024) { errors.push(`${name}: maximum file size is 500 MB.`); continue }
            try { const j = await api('/api/videos', { method: 'POST', headers: { 'Content-Type': 'application/octet-stream', 'X-Filename': encodeURIComponent(file.name) }, body: file }); selected = j.id; added++; await refresh() } catch (e) { errors.push(`${name}: ${e.message}`) }
        }
    } finally {
        uploading = false; $('addFolder').disabled = false; $('files').disabled = false;
        $('files').value = ''; $('folder').value = '';
        $('uploadStatus').textContent = `${added} added${skipped ? ' · ' + skipped + ' non-video files skipped' : ''}${errors.length ? ' · ' + errors.length + ' failed' : ''}`;
        $('uploadErrors').hidden = !errors.length;
        $('uploadErrorSummary').textContent = `${errors.length} videos could not be added`;
        $('uploadErrorList').innerHTML = errors.map(message => `<li>${escapeHtml(message)}</li>`).join('');
    }
}
$('addFolder').onclick = () => $('folder').click();
$('folder').onchange = e => {
    const all = [...e.target.files];
    if (!all.length) return;
    const videos = all.filter(isVideo).sort((a, b) => (a.webkitRelativePath || a.name).localeCompare(b.webkitRelativePath || b.name, undefined, { numeric: true }));
    upload(videos, { skipped: all.length - videos.length });
};
$('files').onchange = e => upload([...e.target.files]);
$('dropzone').onkeydown = e => { if (['Enter', ' '].includes(e.key)) { e.preventDefault(); $('files').click() } };
for (const event of ['dragenter', 'dragover']) $('dropzone').addEventListener(event, e => { e.preventDefault(); $('dropzone').classList.add('drag') });
for (const event of ['dragleave', 'drop']) $('dropzone').addEventListener(event, e => { e.preventDefault(); $('dropzone').classList.remove('drag') });
$('dropzone').ondrop = e => upload([...e.dataTransfer.files]);
$('start').onclick = async () => { try { await api(`/api/queue/${state.running ? 'pause' : 'start'}`, { method: 'POST' }); await refresh() } catch (e) { toast(e.message) } };
function scanResult(data) {
    const bits = [`${data.added} added`];
    if (!data.added && !data.skipped && !data.errors.length) bits[0] = 'No videos found in this folder';
    if (data.skipped) bits.push(`${data.skipped} already in the queue`);
    if (data.errors.length) bits.push(`${data.errors.length} skipped`);
    $('linkStatus').textContent = bits.join(' · ');
    $('uploadErrors').hidden = !data.errors.length;
    $('uploadErrorSummary').textContent = `${data.errors.length} videos could not be added`;
    $('uploadErrorList').innerHTML = data.errors.map(m => `<li>${escapeHtml(m)}</li>`).join('');
}
async function postFolder(path) {
    const busy = path === null ? 'unlinkFolder' : 'linkFolder';
    $(busy).disabled = true; $('linkStatus').textContent = path === null ? 'Unlinking…' : 'Linking…';
    try {
        // Returns at once: the server imports the folder in the background (see trackScan).
        const data = await api('/api/folder', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path }) });
        $('linkDialog').close(); $('linkStatus').textContent = data.importing ? 'Folder linked. Importing videos in the background…' : ''; await refresh();
    } catch (e) { $('linkStatus').textContent = ''; throw e }
    finally { $(busy).disabled = false }
}
// Shows import progress for a linked folder, then its result once the background scan is done.
function trackScan() {
    const finished = scan.last?.finishedAt ?? null;
    if (seenScan === undefined) seenScan = finished;   // first poll: do not replay a result from before this page was opened
    if (scan.pending || scan.scanning) { $('linkStatus').textContent = 'Importing videos in the background…'; return }
    if (finished && finished !== seenScan) { seenScan = finished; scanResult(scan.last) }
}
$('linkFolder').onclick = () => { $('linkPath').value = state.linkedFolder || ''; $('linkError').textContent = ''; $('linkDialog').showModal() };
$('linkClose').onclick = () => $('linkDialog').close();
$('linkForm').onsubmit = async e => { e.preventDefault(); $('linkError').textContent = ''; try { await postFolder($('linkPath').value) } catch (err) { $('linkError').textContent = err.message } };
$('unlinkFolder').onclick = async () => { try { await postFolder(null); $('linkStatus').textContent = 'Folder unlinked. Videos already added stay in the queue.' } catch (e) { toast(e.message) } };
$('refreshFolder').onclick = async () => {
    $('refreshFolder').disabled = true;
    try { await api('/api/folder/refresh', { method: 'POST' }); $('linkStatus').textContent = 'Importing videos in the background…'; await refresh() }
    catch (e) { $('linkStatus').textContent = ''; toast(e.message) }
    finally { $('refreshFolder').disabled = false }
};
$('cameraFilter').onchange = e => { cameraFilter = e.target.value; render() };
function settingsNote() { const local = $('mode').value === 'local'; $('thresholdNote').textContent = 'Person threshold: YOLO box confidence, or LLM p(yes). Suspicious threshold: flag when p(yes) is at least this value; needs token probabilities from the backend, otherwise the model\'s own yes/no is used. The agent and the LLM detector need Ollama mode. Applies to new runs.'; $('personLabelsLabel').hidden = false; $('modelLabel').style.display = local ? 'block' : 'none'; $('model').required = local; $('settingsNote').textContent = local ? `Custom prompt · ${state.frameLimit} frames · 600 output tokens. Smaller models may produce different results.` : 'Qwen3 notebook backend · 150 frames · Screening settings included.' }
let modelListRequest = 0;
function setModelOptions(models, current) {
    const choices = [...new Set([...(current ? [current] : []), ...models])];
    const chosen = current || choices[0] || '';
    $('model').value = chosen;
    $('modelChoices').innerHTML = choices.length ? choices.map(id => `<label class="model-choice"><input type="radio" name="modelChoice" value="${escapeHtml(id)}" ${id === chosen ? 'checked' : ''}><span>${escapeHtml(id)}${id === current && !models.includes(id) ? ' (saved)' : ''}</span></label>`).join('') : '<p>No models installed</p>';
}
$('modelChoices').onchange = e => { if (e.target.name === 'modelChoice') $('model').value = e.target.value };
async function loadModels() {
    const request = ++modelListRequest;
    if ($('mode').value !== 'local') return;
    const current = $('model').value || state.settings.model;
    $('refreshModels').disabled = true; $('settingsError').textContent = '';
    try {
        const data = await api('/api/models?endpoint=' + encodeURIComponent($('endpoint').value));
        if (request !== modelListRequest) return;
        setModelOptions(data.models, $('model').value || current);
        if (!data.models.length) $('settingsError').textContent = 'No models installed on this server.';
    } catch (e) { if (request === modelListRequest) $('settingsError').textContent = e.message }
    finally { if (request === modelListRequest) $('refreshModels').disabled = false }
}
$('settingsButton').onclick = () => {
    $('mode').value = state.settings.mode; $('endpoint').value = state.settings.endpoint;
    setModelOptions([], state.settings.model); $('personLabels').checked = state.settings.personLabels !== false;
    $('batchProcessing').checked = state.settings.batchProcessing === true;
    $('personDetector').value = state.settings.personDetector || 'yolo';
    $('personThreshold').value = state.settings.personThreshold ?? 0.25;
    $('suspiciousThreshold').value = state.settings.suspiciousThreshold ?? 0.5;
    $('agentEnabled').checked = state.settings.agentEnabled !== false;
    $('flagActivity').value = state.settings.flagActivity || '';
    $('siteContextEnabled').checked = state.settings.siteContextEnabled === true;
    $('siteContext').value = state.settings.siteContext || '';
    $('siteContext').disabled = !$('siteContextEnabled').checked;
    $('weaponEnabled').checked = state.settings.weaponEnabled !== false;
    $('settingsError').textContent = ''; settingsNote(); $('settings').showModal(); loadModels(); loadPreflight(false);
};
$('refreshModels').onclick = loadModels;
$('endpoint').onchange = loadModels;
$('mode').onchange = () => { modelListRequest++; $('refreshModels').disabled = false; $('endpoint').value = $('mode').value === 'local' ? 'http://127.0.0.1:11434/v1' : ''; settingsNote(); loadModels() };
$('close').onclick = () => $('settings').close();
const thresholdValue = id => { const v = parseFloat($(id).value); if (!Number.isFinite(v) || v < 0 || v > 1) throw Error('Thresholds must be numbers between 0 and 1.'); return v };
$('settingsForm').onsubmit = async e => { e.preventDefault(); try { await api('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mode: $('mode').value, endpoint: $('endpoint').value, model: $('model').value, personLabels: $('personLabels').checked, batchProcessing: $('batchProcessing').checked, flagActivity: $('flagActivity').value, siteContextEnabled: $('siteContextEnabled').checked, siteContext: $('siteContext').value, weaponEnabled: $('weaponEnabled').checked, personDetector: $('personDetector').value, personThreshold: thresholdValue('personThreshold'), suspiciousThreshold: thresholdValue('suspiciousThreshold'), agentEnabled: $('agentEnabled').checked }) }); $('settings').close(); await refresh(); toast('Settings saved.') } catch (e) { $('settingsError').textContent = e.message } };
document.addEventListener('click', async e => {
    const action = e.target.closest('[data-action]'); if (action) { try { await api(`/api/videos/${action.dataset.id}/${action.dataset.action}`, { method: 'POST' }); await refresh() } catch (e) { toast(e.message) } return }
    const review = e.target.closest('[data-review]');
    if (review) {
        const job = state.jobs.find(j => j.id === review.dataset.review); if (!job) return;
        const next = job.reviewState === review.dataset.state ? null : review.dataset.state;   // pressing the active verdict clears it
        try { await api(`/api/videos/${job.id}/review`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ state: next }) }); await refresh() } catch (err) { toast(err.message) }
        return
    }
    const row = e.target.closest('[data-select]'); if (row) { selected = row.dataset.select; render(); return }
    const copy = e.target.closest('[data-copy]'); if (copy) { try { await navigator.clipboard.writeText(state.jobs.find(j => j.id === copy.dataset.copy).description); toast('Copied.') } catch { toast('Select the description and copy it manually.') } return }
    const download = e.target.closest('[data-download]'); if (download) { const job = state.jobs.find(j => j.id === download.dataset.download); const url = URL.createObjectURL(new Blob([job.description], { type: 'text/plain' })); const a = document.createElement('a'); a.href = url; a.download = job.name.replace(/\.[^.]+$/, '') + '-description.txt'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000) }
});
document.addEventListener('keydown', e => { const row = e.target.closest('[data-select]'); if (row && e.target === row && ['Enter', ' '].includes(e.key)) { e.preventDefault(); selected = row.dataset.select; render() } });
{ const h = location.hash.match(/job=([0-9a-f]{32})/); if (h) selected = h[1] }
refresh(); setInterval(refresh, 1200);

$('navSettings').onclick = () => $('settingsButton').click();

$('showFlagged').onclick = () => setQueueFilter('flagged');
$('filterFlagged').onclick = () => setQueueFilter('flagged');
$('filterAll').onclick = () => setQueueFilter('all');

$('siteContextEnabled').onchange = () => { $('siteContext').disabled = !$('siteContextEnabled').checked };

$('testNotify').onclick = async () => { const out = $('testNotifyResult'); out.textContent = 'Sending…'; try { const r = await api('/api/notifications/test', { method: 'POST' }); out.textContent = Object.entries(r.results).map(([k, v]) => `${k}: ${v}`).join(' · ') } catch (e) { out.textContent = e.message } };

const PREFLIGHT_ICON = { ok: '✓', warn: '!', fail: '✕' };
function renderPreflight(r) {
    $('preflightList').innerHTML = r.checks.map(c => `<li class="pf-${escapeHtml(c.status)}"><span class="pf-icon" aria-hidden="true">${PREFLIGHT_ICON[c.status] ?? '?'}</span><span class="pf-name">${escapeHtml(c.name)}</span><span class="pf-detail">${escapeHtml(c.detail)}</span></li>`).join('');
    $('preflightStamp').textContent = `Checked ${ago(r.checkedAt)}`;
}
async function loadPreflight(refresh) {
    const button = $('runPreflight'); button.disabled = true;
    $('preflightStamp').textContent = refresh ? 'Checking…' : '';
    try { const r = await api('/api/preflight' + (refresh ? '?refresh=true' : '')); renderPreflight(r); return r }
    catch (e) { $('preflightStamp').textContent = e.message }
    finally { button.disabled = false }
}
$('runPreflight').onclick = () => loadPreflight(true);
(async () => {
    try {
        const r = await api('/api/preflight');
        const bad = r.checks.filter(c => c.status !== 'ok');
        if (bad.length) toast(`Setup check: ${bad.map(c => c.name).join(', ')} ${bad.length > 1 ? 'need' : 'needs'} attention. Open Settings > System check.`);
    } catch { }
})();

function exportJobs(format) {
    const q = new URLSearchParams({ format });
    if (cameraFilter !== 'all') q.set('camera', cameraFilter);
    if (queueFilter === 'flagged') q.set('flagged', 'true');
    const a = document.createElement('a'); a.href = '/api/export?' + q; a.download = ''; document.body.appendChild(a); a.click(); a.remove();
}
$('exportCsv').onclick = () => exportJobs('csv');
$('exportJson').onclick = () => exportJobs('json');