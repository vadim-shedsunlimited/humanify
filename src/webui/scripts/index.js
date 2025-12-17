const el = (id) => document.getElementById(id);

// LocalStorage fallback for profiles when /api/profiles is unreachable
const LOCAL_PROFILES_KEY = 'humanify.profiles.v1';

function loadProfilesFromLocal() {
    try {
        const s = localStorage.getItem(LOCAL_PROFILES_KEY);
        if (!s) return [];
        const parsed = JSON.parse(s);
        return Array.isArray(parsed) ? parsed : [];
    } catch (e) {
        console.warn('Failed to read local profiles', e);
        return [];
    }
}

function saveProfilesToLocal(profilesArr) {
    try {
        localStorage.setItem(LOCAL_PROFILES_KEY, JSON.stringify(profilesArr || []));
    } catch (e) {
        console.warn('Failed to save local profiles', e);
    }
}

let profiles = [];
let selectedId = null;
let pollTimer = null;
const startButton = el('start');
const stopButton = el('stop');

function currentForm() {
    const inputs = el('inputs').value.split(/\r?\n/).map(function (s) { return s.trim(); }).filter(Boolean);
    return {
        id: selectedId || null,
        name: el('profileName').value.trim() || 'Untitled',
        provider: el('provider').value,
        model: el('model').value.trim() || undefined,
        contextSize: Number(el('contextSize').value || 0),
        agents: Number(el('agents').value || 1),
        outputDir: el('outputDir').value.trim(),
        resume: !!el('resume').checked,
        continueOnError: !!el('continueOnError').checked,
        inputs,
        baseURL: el('baseURL').value.trim() || undefined,
        apiKey: el('apiKey').value.trim() || undefined,
        seed: el('seed').value.trim() || undefined,
        disableGpu: !!el('disableGpu').checked,
    };
}

function setForm(p) {
    selectedId = p && p.id ? p.id : null;
    el('profileName').value = (p && p.name) || '';
    el('provider').value = (p && p.provider) || 'copilot';
    el('model').value = (p && p.model) || '';
    el('contextSize').value = String((p && p.contextSize) || 4096);
    el('agents').value = String((p && p.agents) || 4);
    el('outputDir').value = (p && p.outputDir) || '';
    el('resume').checked = !!(p && p.resume);
    el('continueOnError').checked = !!(p && p.continueOnError);
    el('inputs').value = (p && Array.isArray(p.inputs) ? p.inputs : []).join('\n');
    el('baseURL').value = (p && p.baseURL) || '';
    el('apiKey').value = (p && p.apiKey) || '';
    el('seed').value = (p && p.seed) || '';
    el('disableGpu').checked = !!(p && p.disableGpu);
}

function refreshProfileSelect() {
    const s = el('profileSelect');
    s.innerHTML = '';
    const optNone = document.createElement('option');
    optNone.value = '';
    optNone.textContent = '(select profile)';
    s.appendChild(optNone);
    for (const p of profiles) {
        const opt = document.createElement('option');
        opt.value = p.id;
        opt.textContent = p.name;
        s.appendChild(opt);
    }
    s.value = selectedId || '';
}

async function loadAll() {
    try {
        const r = await fetch('/api/profiles');
        if (!r.ok) throw new Error('fetch /api/profiles failed');
        const j = await r.json();
        profiles = j.profiles || [];
        // persist a copy locally so UI works offline
        try { saveProfilesToLocal(profiles); } catch (e) { }
    } catch (err) {
        console.warn('Could not load /api/profiles, falling back to localStorage', err);
        profiles = loadProfilesFromLocal();
    }
    // If there's no selectedId from prior activity, select the first profile so the form is populated.
    if (!selectedId && profiles.length) {
        selectedId = profiles[0].id;
    }
    refreshProfileSelect();
    // If a selectedId exists, populate the form with that profile
    if (selectedId) {
        const p = profiles.find(function (x) { return x.id === selectedId; });
        setForm(p || null);
    } else {
        setForm(null);
    }

    renderStatus({ status: 'idle' }, []);
}

async function saveProfile() {
    const p = currentForm();
    try {
        const r = await fetch('/api/profiles', {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ profile: p })
        });
        const j = await r.json();
        if (!r.ok) { alert(j.error || 'Failed to save'); return; }
        profiles = j.profiles || [];
        selectedId = j.selectedId || selectedId;
        refreshProfileSelect();
        saveProfilesToLocal(profiles);
        return;
    } catch (err) {
        console.warn('Failed to save profile via API, saving locally instead', err);
    }

    // Local-only save path
    if (!p.id) p.id = String(Date.now());
    const idx = profiles.findIndex(function (x) { return x.id === p.id; });
    if (idx >= 0) profiles[idx] = p; else profiles.push(p);
    selectedId = p.id;
    refreshProfileSelect();
    saveProfilesToLocal(profiles);
}

async function deleteProfile() {
    if (!selectedId) return;
    try {
        const r = await fetch('/api/profiles/' + encodeURIComponent(selectedId), { method: 'DELETE' });
        const j = await r.json();
        if (!r.ok) { alert(j.error || 'Failed to delete'); return; }
        profiles = j.profiles || [];
        selectedId = null;
        setForm(null);
        refreshProfileSelect();
        saveProfilesToLocal(profiles);
        return;
    } catch (err) {
        console.warn('Failed to delete profile via API, deleting locally instead', err);
    }

    // Local-only delete path
    profiles = profiles.filter(function (p) { return p.id !== selectedId; });
    selectedId = null;
    setForm(null);
    refreshProfileSelect();
    saveProfilesToLocal(profiles);
}

async function startRun() {
    const cfg = currentForm();
    el('startHint').textContent = '';
    const r = await fetch('/api/run/start', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ config: cfg })
    });
    const j = await r.json();
    if (!r.ok) { alert(j.error || 'Failed to start'); return; }
    el('startHint').textContent = j.warning || '';
    await pollOnce();
    if (!pollTimer) pollTimer = setInterval(pollOnce, 1000);
}

async function stopRun() {
    const r = await fetch('/api/run/stop', { method: 'POST' });
    const j = await r.json();
    if (!r.ok) { alert(j.error || 'Failed to stop'); return; }
    await pollOnce();
}

function formatTime(value) {
    if (!value) return '';
    const date = new Date(value);
    if (isNaN(date.getTime())) return '';
    return date.toLocaleString();
}

function joinParts(parts) {
    return parts.filter(function (p) { return p; }).join(' | ');
}

function renderRuntimeMeta(state) {
    const meta = el('runtimeMeta');
    if (!state || state.status === 'idle') {
        meta.innerHTML = '<p class="muted small">Command details will appear once a run is active.</p>';
        return;
    }

    const rows = [];
    if (state.command) rows.push({ label: 'Command', value: state.command });
    if (Array.isArray(state.args) && state.args.length) rows.push({ label: 'Arguments', value: state.args.join(' ') });
    if (state.cwd) rows.push({ label: 'Working Dir', value: state.cwd });
    if (state.outputDir) rows.push({ label: 'Output Dir', value: state.outputDir });
    if (state.pid) rows.push({ label: 'PID', value: String(state.pid) });
    if (typeof state.exitCode === 'number') rows.push({ label: 'Exit Code', value: String(state.exitCode) });

    meta.innerHTML = rows.map(function (row) {
        return '<div class="metaList__row"><span>' + row.label + '</span><span>' + row.value + '</span></div>';
    }).join('');
}

function renderTaskState(taskState, runState) {
    const percentEl = el('progressPercent');
    const progressBar = el('progressBar');
    const progressMeta = el('progressMeta');
    const grid = el('taskGrid');
    const agentsContainer = el('agentsContainer');

    if (!taskState || !taskState.total) {
        percentEl.textContent = '--';
        progressBar.style.setProperty('--progress', '0%');
        progressMeta.textContent = 'Waiting for .humanify.tasks.json';
        grid.innerHTML = '<div class="metric"><div class="metric__value">0</div><div class="metric__label">Total Tasks</div></div>';
        if (agentsContainer) agentsContainer.innerHTML = '';
        renderTaskList(taskState, runState && runState.outputDir);
        return;
    }

    const total = taskState.total;
    const completion = total ? Math.round((taskState.completed / total) * 100) : 0;
    percentEl.textContent = completion + '%';
    progressBar.style.setProperty('--progress', completion + '%');
    progressMeta.textContent = taskState.updatedAt ? 'Task file updated ' + formatTime(taskState.updatedAt) : 'Live metrics';

    const metrics = [
        { label: 'Total', value: taskState.total },
        { label: 'Pending', value: taskState.pending },
        { label: 'In Progress', value: taskState.in_progress },
        { label: 'Completed', value: taskState.completed },
        { label: 'Changed', value: taskState.changed },
        { label: 'Failed', value: taskState.failed }
    ];

    grid.innerHTML = metrics.map(function (m) {
        return '<div class="metric"><div class="metric__value">' + m.value + '</div><div class="metric__label">' + m.label + '</div></div>';
    }).join('');

    // Render per-agent progress
    if (agentsContainer) {
        const agents = Array.isArray(taskState.agents) ? taskState.agents : [];
        if (agents.length > 0) {
            agentsContainer.innerHTML = agents.map(function (agent) {
                const fileName = agent.file ? agent.file.split(/[\\\/]/).pop() : 'unknown';
                const progress = agent.progress || 0;
                return '<div class="agentCard">' +
                    '<div class="agentHeader"><span class="agentId">Agent ' + agent.workerId + '</span><span class="agentFile">' + escapeHtml(fileName) + '</span></div>' +
                    '<div class="agentProgress"><span class="agentProgressBar" style="width: ' + progress + '%;"></span></div>' +
                    '<div class="agentStats">' + progress + '%</div>' +
                    '<div class="agentPath small muted">' + escapeHtml(agent.taskPath) + '</div>' +
                    '<button class="agentDiffBtn" data-output-dir="' + escapeHtmlAttr((runState && runState.outputDir) || '') + '" data-task-path="' + escapeHtmlAttr(agent.taskPath) + '">View Diff</button>' +
                    '</div>';
            }).join('');

            // Add event listeners to diff buttons
            document.querySelectorAll('.agentDiffBtn').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    const outputDir = this.getAttribute('data-output-dir');
                    const taskPath = this.getAttribute('data-task-path');
                    showDiff(outputDir, taskPath);
                });
            });
        } else {
            agentsContainer.innerHTML = '';
        }
    }

    // Render task list
    renderTaskList(taskState, runState && runState.outputDir);
}

function escapeHtml(text) {
    const map = {
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#039;'
    };
    return text.replace(/[&<>"']/g, function (c) { return map[c]; });
}

function escapeHtmlAttr(text) {
    return text.replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function renderTaskList(taskState, outputDir) {
    const taskListContainer = el('taskListContainer');
    if (!taskListContainer) return;

    if (!taskState || !taskState.tasks || taskState.tasks.length === 0) {
        taskListContainer.innerHTML = '<div class="noTasks">No tasks to display</div>';
        return;
    }

    const statusColorMap = {
        'pending': '#757575',
        'in_progress': '#579bfc',
        'completed': '#00c875',
        'failed': '#df2f4a'
    };

    const rows = taskState.tasks.map(function (task, idx) {
        const fileName = task.taskPath ? task.taskPath.split(/[\\\/]/).pop() : 'unknown';
        const status = task.status || 'pending';
        const statusColor = statusColorMap[status] || '#757575';
        const changedBadge = task.changed ? '<span class="taskChangedBadge">Changed</span>' : '';

        // Only enable diff button for completed and failed tasks
        const diffBtn = (status === 'completed' || status === 'failed')
            ? '<button class="taskDiffBtn" data-output-dir="' + escapeHtmlAttr(outputDir || '') + '" data-task-path="' + escapeHtmlAttr(task.taskPath) + '">View Diff</button>'
            : '<button class="taskDiffBtn" disabled>View Diff</button>';

        return '<tr class="taskRow" data-status="' + escapeHtmlAttr(status) + '">' +
            '<td class="taskName">' + escapeHtml(fileName) + '</td>' +
            '<td class="taskPath small muted">' + escapeHtml(task.taskPath) + '</td>' +
            '<td class="taskStatus"><span class="taskStatusBadge" style="background-color: ' + statusColor + ';">' + status + '</span>' + changedBadge + '</td>' +
            '<td class="taskAction">' + diffBtn + '</td>' +
            '</tr>';
    }).join('');

    taskListContainer.innerHTML = '<table class="taskListTable"><thead><tr><th>File</th><th>Path</th><th>Status</th><th>Action</th></tr></thead><tbody>' + rows + '</tbody></table>';

    // Add event listeners to diff buttons
    document.querySelectorAll('.taskDiffBtn:not([disabled])').forEach(function (btn) {
        btn.addEventListener('click', function () {
            const outputDir = this.getAttribute('data-output-dir');
            const taskPath = this.getAttribute('data-task-path');
            showDiff(outputDir, taskPath);
        });
    });
}

function renderStatus(state, logs) {
    const badge = el('statusBadge');
    const title = el('statusTitle');
    const meta = el('statusMeta');
    const time = el('statusTime');
    const heroLabel = el('heroStatusLabel');
    const logsBox = el('logs');

    const phase = state && state.status ? state.status : 'idle';
    badge.textContent = phase.toUpperCase();
    badge.dataset.state = phase;
    heroLabel.textContent = phase.charAt(0).toUpperCase() + phase.slice(1);

    if (phase === 'running') {
        title.textContent = 'Working through your bundle';
        meta.textContent = state.outputDir ? 'Writing to ' + state.outputDir : 'Processing bundle inputs...';
        time.textContent = joinParts([
            state.startedAt ? 'Started ' + formatTime(state.startedAt) : '',
            state.lastActivityAt ? 'Last activity ' + formatTime(state.lastActivityAt) : ''
        ]);
    } else if (phase === 'finished') {
        title.textContent = 'Run complete';
        const exit = typeof state.exitCode === 'number' ? 'Exit code ' + state.exitCode : 'Exit code unknown';
        meta.textContent = exit + (state.outputDir ? ' | Output at ' + state.outputDir : '');
        time.textContent = joinParts([
            state.startedAt ? 'Started ' + formatTime(state.startedAt) : '',
            state.finishedAt ? 'Finished ' + formatTime(state.finishedAt) : ''
        ]);
    } else {
        title.textContent = 'Waiting for the next run';
        meta.textContent = 'Start a run to see live telemetry and logs.';
        time.textContent = 'Idle';
    }

    renderRuntimeMeta(state);
    renderTaskState(state && state.taskState ? state.taskState : null, state);

    const output = Array.isArray(logs) && logs.length ? logs.join('\\n') : 'No output yet.';
    logsBox.textContent = output;
    logsBox.scrollTop = logsBox.scrollHeight;

    startButton.disabled = phase === 'running';
    stopButton.disabled = phase !== 'running';
}

async function pollOnce() {
    try {
        const r = await fetch('/api/run/status');
        if (!r.ok) return;
        const j = await r.json();
        renderStatus(j.status, j.logs || []);
    } catch (err) {
        console.warn('Failed to poll status', err);
    }
}

async function showDiff(outputDir, taskPath) {
    try {
        // Support simple filename query
        let url;
        // If outputDir is empty string, treat it as not provided
        const hasOutputDir = outputDir && outputDir.trim().length > 0;

        if (hasOutputDir && taskPath) {
            // Full path query with outputDir and taskPath
            const params = new URLSearchParams({ outputDir, taskPath });
            url = '/api/diff?' + params;
        } else if (taskPath) {
            // Simplified query with just filename/path
            url = '/api/diff?' + encodeURIComponent(taskPath);
        } else {
            alert('Missing file path');
            return;
        }

        const r = await fetch(url);
        if (!r.ok) {
            const err = await r.json();
            alert('Failed to load diff: ' + (err.error || 'Unknown error'));
            return;
        }
        const data = await r.json();

        const modal = document.createElement('div');
        modal.className = 'diffModal';
        modal.innerHTML =
            '<div class="diffModalContent">' +
            '<div class="diffModalHeader">' +
            '<h2>Diff: ' + escapeHtml(data.taskPath) + '</h2>' +
            '<button class="diffModalClose">&times;</button>' +
            '</div>' +
            '<div class="diffContainer">' +
            '<div class="diffPanel">' +
            '<div class="diffPanelHeader">Original</div>' +
            '<div class="diffCodeContainer" data-panel="original">' +
            '<code class="language-javascript diffCode">' + escapeHtml(data.original) + '</code>' +
            '</div>' +
            '</div>' +
            '<div class="diffPanel">' +
            '<div class="diffPanelHeader">Modified</div>' +
            '<div class="diffCodeContainer" data-panel="modified">' +
            '<code class="language-javascript diffCode">' + escapeHtml(data.modified) + '</code>' +
            '</div>' +
            '</div>' +
            '</div>' +
            '</div>';

        document.body.appendChild(modal);

        // Apply syntax highlighting if hljs is available
        if (window.hljs) {
            modal.querySelectorAll('code.language-javascript').forEach(function (block) {
                window.hljs.highlightElement(block);
            });
        }

        // Set up synchronized scrolling
        const containers = modal.querySelectorAll('.diffCodeContainer');
        if (containers.length === 2) {
            const origContainer = containers[0];
            const modContainer = containers[1];

            origContainer.addEventListener('scroll', function () {
                modContainer.scrollTop = origContainer.scrollTop;
                modContainer.scrollLeft = origContainer.scrollLeft;
            });

            modContainer.addEventListener('scroll', function () {
                origContainer.scrollTop = modContainer.scrollTop;
                origContainer.scrollLeft = modContainer.scrollLeft;
            });
        }

        modal.querySelector('.diffModalClose').addEventListener('click', function () {
            modal.remove();
        });

        modal.addEventListener('click', function (e) {
            if (e.target === modal) modal.remove();
        });
    } catch (err) {
        alert('Error: ' + err.message);
    }
}

el('profileSelect').addEventListener('change', function () {
    const id = el('profileSelect').value;
    const p = profiles.find(function (x) { return x.id === id; });
    setForm(p || null);
});

el('saveProfile').addEventListener('click', saveProfile);
el('newProfile').addEventListener('click', function () {
    setForm({ name: '', provider: 'copilot', inputs: [], outputDir: '', resume: false, continueOnError: false, agents: 4, contextSize: 4096, seed: '', disableGpu: false });
    refreshProfileSelect();
});
el('deleteProfile').addEventListener('click', deleteProfile);
startButton.addEventListener('click', startRun);
stopButton.addEventListener('click', stopRun);

loadAll().then(function () {
    pollOnce();
    if (!pollTimer) pollTimer = setInterval(pollOnce, 1000);
});

// When user presses Ctrl/Cmd + A and focus is not inside an editable field,
// select the logs content so Ctrl+A selects logs by default.
document.addEventListener('keydown', function (ev) {
    try {
        const isSelectAll = (ev.ctrlKey || ev.metaKey) && (ev.key === 'a' || ev.key === 'A');
        if (!isSelectAll) return;

        const active = document.activeElement;
        const tag = active && active.tagName ? active.tagName.toUpperCase() : '';
        const isEditable = tag === 'INPUT' || tag === 'TEXTAREA' || (active && active.isContentEditable);
        if (isEditable) return; // let native behavior for inputs/textareas

        const logsEl = el('logs');
        if (!logsEl) return;

        // Prevent the browser's default global "select all" behavior and
        // stop other listeners from overriding our selection.
        ev.preventDefault();
        try { ev.stopImmediatePropagation(); } catch (e) { }
        try { ev.stopPropagation(); } catch (e) { }
        // focus logs so keyboard users know where selection is
        try { logsEl.focus(); } catch { }

        // If element supports select(), use it (not the case for <pre>), otherwise use Range selection
        if (typeof logsEl.select === 'function') {
            try { logsEl.select(); return; } catch (e) { }
        }

        const range = document.createRange();
        range.selectNodeContents(logsEl);
        const sel = window.getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
    } catch (e) {
        // ignore errors from selection operations
        console.warn('select-logs failed', e);
    }
}, true);