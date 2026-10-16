// Page logic: login, robot upload, battle picking and polling. Playback lives in viewer.js.
// Wrapped in its own scope so its helpers do not clash with the top-level names in viewer.js.
(() => {
const $ = id => document.getElementById(id);
const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

async function api(path, opts = {}) {
  const res = await fetch(path, {
    ...opts,
    headers: opts.body ? { 'Content-Type': 'application/json' } : undefined,
    body: opts.body ? JSON.stringify(opts.body) : undefined,
  });
  if (res.status === 401) { showLogin(); throw new Error('Not signed in'); }
  return res;
}

function showLogin() {
  $('login').hidden = false; $('app').hidden = true; $('who').hidden = true;
  $('loginName').focus();
}

async function start() {
  const res = await fetch('/api/me');
  if (!res.ok) return showLogin();
  const me = await res.json();
  $('login').hidden = true; $('app').hidden = false; $('who').hidden = false;
  $('whoName').textContent = me.name;
  await Promise.all([refreshRobots(), refreshBattles(), refreshLeaderboard(), loadSampleList()]);
  if (!$('source').value) await loadSample('Hunter');
}

$('loginForm').onsubmit = async e => {
  e.preventDefault();
  $('loginError').textContent = '';
  const res = await fetch('/api/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: $('loginName').value }) });
  if (!res.ok) { $('loginError').textContent = (await res.json()).error || 'Could not sign in.'; return; }
  start();
};
$('logout').onclick = async () => { await fetch('/api/logout', { method: 'POST' }); location.reload(); };

// ---- robots ----
async function refreshRobots() {
  const robots = await (await api('/api/robots')).json();
  $('robots').innerHTML = robots.length ? robots.map(r =>
    `<li><label><input type="checkbox" value="${r.id}"> <strong>${esc(r.label)}</strong> <span class="muted">by ${esc(r.owner)}</span></label>` +
    (r.mine ? ` <button class="link" data-edit="${r.id}" title="Load into editor">edit</button> <button class="link danger" data-del="${r.id}" title="Delete">delete</button>` : '') + '</li>'
  ).join('') : '<li class="muted">No robots yet. Write one below!</li>';
}

$('robots').onclick = async e => {
  const del = e.target.dataset.del, edit = e.target.dataset.edit;
  if (del && confirm('Delete this robot?')) { await api('/api/robots/' + del, { method: 'DELETE' }); refreshRobots(); }
  if (edit) {
    const res = await api(`/api/robots/${edit}/source`);
    if (res.ok) { $('source').value = await res.text(); $('source').scrollIntoView({ behavior: 'smooth' }); }
  }
};

// ---- samples ----
async function loadSampleList() {
  const names = await (await fetch('/api/samples')).json();
  $('samples').innerHTML = '<option value="">Choose…</option>' + names.map(n => `<option>${esc(n)}</option>`).join('');
}
async function loadSample(name) {
  const res = await fetch('/api/samples/' + encodeURIComponent(name));
  if (res.ok) { $('source').value = await res.text(); if (!$('label').value) $('label').value = name; }
}
$('samples').onchange = async e => {
  if (!e.target.value) return;
  if (!$('source').value.trim() || confirm('Replace the editor contents with this sample?')) await loadSample(e.target.value);
  e.target.value = '';
};

$('upload').onclick = async () => {
  const diag = $('diagnostics');
  diag.textContent = 'Compiling…'; diag.className = 'diag';
  const res = await api('/api/robots', { method: 'POST', body: { label: $('label').value, source: $('source').value } });
  const body = await res.json();
  if (res.ok) { diag.textContent = `Uploaded "${body.label}". Tick it in the list and fight!`; diag.className = 'diag ok'; refreshRobots(); return; }
  diag.className = 'diag bad';
  diag.textContent = (body.diagnostics || []).map(d => (d.line ? `line ${d.line}:${d.column}  ` : '') + d.message).join('\n') || body.error;
};

// ---- battles ----
function selectedRobots() { return [...$('robots').querySelectorAll('input:checked')].map(i => +i.value); }

let match = null;        // { rounds: [replay...], result } for the battle on screen
let currentRound = 0;

async function watch(id) {
  const res = await api(`/api/battles/${id}/replay`);
  if (!res.ok) { $('fightStatus').textContent = 'Replay not available.'; return; }
  const data = await res.json();
  match = data.rounds ? data : { rounds: [data], result: null };   // tolerate single-round replays
  $('placeholder').hidden = true;
  renderMatch();
  playRound(0);
  $('arena').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

function playRound(i) {
  currentRound = i;
  window.setReplay(match.rounds[i]);
  for (const b of $('rounds').querySelectorAll('button')) b.classList.toggle('active', +b.dataset.round === i);
}

function roundLabel(i) {
  const r = match.rounds[i];
  const strip = name => name.replace(/ \(.*\)$/, '');   // drop the "(owner)" suffix
  // Battles recorded before rounds existed have no placings, only a winner id.
  if (!r.places || !r.places.length) {
    const w = r.robots.find(x => x.id === r.winnerId);
    return w ? strip(w.name) : 'draw';
  }
  const firsts = r.places.reduce((n, p) => n + (p === 1 ? 1 : 0), 0);
  if (firsts !== 1) return 'draw';
  return strip(r.robots[r.places.indexOf(1)].name);
}

function renderMatch() {
  const n = match.rounds.length;
  $('rounds').hidden = n < 1;
  $('rounds').innerHTML = `<span class="muted">Round</span> ` + match.rounds.map((_, i) =>
    `<button data-round="${i}" title="Winner: ${esc(roundLabel(i))}">${i + 1}</button>`).join('') +
    `<label class="check"><input id="autoNext" type="checkbox" checked> Play next round automatically</label>`;

  const result = match.result;
  $('match').hidden = !result;
  if (!result) return;
  const rows = [...result.robots].sort((a, b) => a.place - b.place);
  $('match').innerHTML = `<h2>Match result · ${n} round${n === 1 ? '' : 's'}</h2>
    <table><thead><tr><th>#</th><th>Robot</th><th>Points</th><th>Round wins</th><th>Rating</th></tr></thead><tbody>` +
    rows.map(r => {
      const d = Math.round(r.ratingAfter - r.ratingBefore);
      return `<tr><td>${r.place}</td><td>${esc(r.label)} <span class="muted">${esc(r.owner)}</span></td><td>${r.points}</td><td>${r.roundWins}</td>` +
        `<td>${Math.round(r.ratingAfter)} <span class="${d >= 0 ? 'up' : 'down'}">${d >= 0 ? '+' : '−'}${Math.abs(d)}</span></td></tr>`;
    }).join('') + '</tbody></table>';
}

$('rounds').onclick = e => { if (e.target.dataset.round !== undefined) playRound(+e.target.dataset.round); };

// viewer.js calls this once a round has played to its last frame.
window.onReplayFinished = () => {
  const auto = $('autoNext');
  const next = currentRound + 1;
  if (match && auto && auto.checked && next < match.rounds.length) {
    const shown = currentRound;
    setTimeout(() => { if (currentRound === shown && $('autoNext')?.checked) playRound(next); }, 1500);
  }
};

async function refreshBattles() {
  const battles = await (await api('/api/battles')).json();
  $('battles').innerHTML = battles.length ? battles.map(b => {
    const what = esc(b.robots.join(' vs '));
    const rounds = `${b.rounds} round${b.rounds === 1 ? '' : 's'}`;
    if (b.status === 'done') {
      const scores = (b.scores || []).map(s => `${esc(s.label)} ${s.points}`).join(', ');
      return `<li><button class="link" data-watch="${b.id}">${what}</button> <span class="muted">${rounds}: ${b.winner ? 'won by ' + esc(b.winner) : 'drawn'}</span>` +
        (scores ? `<div class="muted">${scores}</div>` : '') + (b.error ? `<div class="error">${esc(b.error)}</div>` : '') + '</li>';
    }
    const progress = b.status === 'running' ? `round ${Math.min(b.roundsDone + 1, b.rounds)} of ${b.rounds}` : b.status;
    return `<li>${what} <span class="muted">${progress}${b.error ? ': ' + esc(b.error) : ''}</span></li>`;
  }).join('') : '<li class="muted">No battles yet.</li>';
}
$('battles').onclick = e => { if (e.target.dataset.watch) watch(e.target.dataset.watch); };

// ---- leaderboard ----
async function refreshLeaderboard() {
  const rows = await (await api('/api/leaderboard')).json();
  $('leaderboardBody').innerHTML = rows.length ? rows.map(r =>
    `<tr${r.mine ? ' class="mine"' : ''}><td>${r.rank}</td><td>${esc(r.label)}</td><td>${esc(r.owner)}</td><td><strong>${r.rating}</strong></td>` +
    `<td>${r.roundWins}–${r.roundDraws}–${r.roundLosses}</td><td>${r.matchWins}/${r.matches}</td></tr>`
  ).join('') : '<tr><td colspan="6" class="muted">No ranked robots yet. Fight a battle to get on the board.</td></tr>';
}

$('fight').onclick = async () => {
  const ids = selectedRobots();
  const status = $('fightStatus');
  if (ids.length < 2) { status.textContent = 'Tick at least two robots.'; return; }
  $('fight').disabled = true;
  try {
    const res = await api('/api/battles', { method: 'POST', body: { robotIds: ids, rounds: +$('roundCount').value } });
    if (!res.ok) { status.textContent = res.status === 429 ? 'The queue is full, try again shortly.' : ((await res.json()).error || 'Could not start battle.'); return; }
    const { id } = await res.json();
    status.textContent = 'Battle queued…';
    for (let i = 0; i < 300; i++) {
      await new Promise(r => setTimeout(r, 1000));
      const b = await (await api('/api/battles/' + id)).json();
      if (b.status === 'done') { status.textContent = b.error || ''; refreshBattles(); refreshLeaderboard(); return watch(id); }
      if (b.status === 'failed') { status.textContent = 'Battle failed: ' + (b.error || 'unknown error'); refreshBattles(); return; }
      status.textContent = b.status === 'running' ? `Fighting round ${Math.min(b.roundsDone + 1, b.rounds)} of ${b.rounds}…` : 'Waiting in queue…';
    }
    status.textContent = 'Still running; check Recent battles shortly.';
  } finally { $('fight').disabled = false; }
};

$('demo').onclick = async () => {
  const res = await fetch('/api/demo');
  if (res.ok) { match = null; $('rounds').hidden = true; $('match').hidden = true; $('placeholder').hidden = true; window.setReplay(await res.json()); }
};

start();
})();
