// Replays a battle recorded by RobotBattle.Engine (see Replay.cs). One frame per simulation tick.
const TICKS_PER_SECOND = 30;
const COLORS = ['#e0524d', '#2f7fd1', '#2e9e5b', '#d98a1f', '#8a56c2', '#16a3a3'];
const EXPLOSION_TICKS = 8;

const $ = id => document.getElementById(id);
const canvas = $('arena');
const ctx = canvas.getContext('2d');

let replay = null;
let position = 0;       // fractional frame index
let playing = true;
let last = 0;
let finished = false;   // so onReplayFinished fires once per playthrough

const rad = d => d * Math.PI / 180;

window.setReplay = setReplay;
function setReplay(r) {
  replay = r;
  canvas.width = r.arenaWidth;
  canvas.height = r.arenaHeight;
  position = 0;
  playing = true;
  finished = false;
  $('play').textContent = 'Pause';
  $('scrub').max = r.frames.length - 1;
  $('result').hidden = true;
  $('roster').innerHTML = r.robots.map(b =>
    `<li id="r${b.id}"><div class="name"><span class="swatch" style="background:${COLORS[b.id % COLORS.length]}"></span>${esc(b.name)}</div>` +
    `<div class="bar"><div style="background:${COLORS[b.id % COLORS.length]}"></div></div><div class="bar ebar"><div style="background:#e6b800"></div></div></li>`).join('');
}

function esc(s) { return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

function draw() {
  if (!replay) return;
  const i = Math.min(Math.floor(position), replay.frames.length - 1);
  const f = replay.frames[i];
  ctx.clearRect(0, 0, canvas.width, canvas.height);

  const style = getComputedStyle(document.documentElement);
  const border = style.getPropertyValue('--border').trim();
  ctx.strokeStyle = border; ctx.lineWidth = 1;
  ctx.beginPath();
  for (let x = 0; x <= canvas.width; x += 50) { ctx.moveTo(x, 0); ctx.lineTo(x, canvas.height); }
  for (let y = 0; y <= canvas.height; y += 50) { ctx.moveTo(0, y); ctx.lineTo(canvas.width, y); }
  ctx.globalAlpha = .35; ctx.stroke(); ctx.globalAlpha = 1;

  // Obstacles
  for (const o of replay.obstacles) {
    ctx.beginPath(); ctx.arc(o.x, o.y, o.radius, 0, 7);
    ctx.fillStyle = border; ctx.fill();
    ctx.strokeStyle = style.getPropertyValue('--muted').trim(); ctx.stroke();
  }

  // Recent explosions (fade over a few frames)
  for (let k = Math.max(0, i - EXPLOSION_TICKS); k <= i; k++) {
    const age = i - k;
    for (const e of replay.frames[k].explosions) {
      const t = age / EXPLOSION_TICKS;
      ctx.beginPath(); ctx.arc(e.x, e.y, e.radius * (0.3 + 0.7 * t), 0, 7);
      ctx.fillStyle = `rgba(255,160,40,${0.55 * (1 - t)})`; ctx.fill();
      ctx.strokeStyle = `rgba(255,90,0,${1 - t})`; ctx.lineWidth = 2; ctx.stroke();
    }
  }

  const showScan = $('showScan').checked;
  for (const r of f.robots) {
    const color = COLORS[r.id % COLORS.length];
    if (showScan && r.alive && r.scanDir != null && r.scanArc != null) {
      ctx.beginPath(); ctx.moveTo(r.x, r.y);
      ctx.arc(r.x, r.y, Math.hypot(canvas.width, canvas.height), rad(r.scanDir - r.scanArc / 2), rad(r.scanDir + r.scanArc / 2));
      ctx.closePath();
      ctx.fillStyle = color + '22'; ctx.fill();
    }
    ctx.globalAlpha = r.alive ? 1 : 0.3;
    // Body (square, rotated by heading) with heading tick
    ctx.save(); ctx.translate(r.x, r.y); ctx.rotate(rad(r.heading));
    const s = replay.robotRadius;
    ctx.fillStyle = color; ctx.fillRect(-s, -s * 0.8, s * 2, s * 1.6);
    ctx.fillStyle = '#fff'; ctx.fillRect(s * 0.4, -3, s * 0.6, 6);
    ctx.restore();
    // Turret
    ctx.save(); ctx.translate(r.x, r.y); ctx.rotate(rad(r.turret));
    ctx.fillStyle = '#222'; ctx.beginPath(); ctx.arc(0, 0, s * 0.5, 0, 7); ctx.fill();
    ctx.fillRect(0, -2.5, s * 1.3, 5);
    ctx.restore();
    ctx.globalAlpha = 1;
    // Energy bar above robot
    if (r.alive) {
      ctx.fillStyle = '#0006'; ctx.fillRect(r.x - 20, r.y - s - 12, 40, 5);
      ctx.fillStyle = color; ctx.fillRect(r.x - 20, r.y - s - 12, 40 * Math.max(0, r.health) / 100, 5);
      ctx.fillStyle = '#e6b800'; ctx.fillRect(r.x - 20, r.y - s - 6, 40 * Math.max(0, r.energy) / 100, 3);
    }
  }

  // Bullets
  ctx.fillStyle = style.getPropertyValue('--text').trim();
  for (const b of f.bullets) { ctx.beginPath(); ctx.arc(b.x, b.y, 3, 0, 7); ctx.fill(); }

  // Sidebar + timeline
  for (const r of f.robots) {
    const li = $('r' + r.id);
    if (!li) continue;
    li.classList.toggle('dead', !r.alive);
    li.querySelector('.bar > div').style.width = Math.max(0, r.health) + '%';
    li.querySelector('.ebar > div').style.width = Math.max(0, r.energy) + '%';
  }
  $('scrub').value = i;
  $('tickLabel').textContent = `${i} / ${replay.frames.length - 1}`;

  const done = i >= replay.frames.length - 1;
  const res = $('result');
  res.hidden = !done;
  if (done) {
    const w = replay.robots.find(b => b.id === replay.winnerId);
    res.textContent = (w ? `Winner: ${w.name}` : 'Draw') + ` (${replay.endReason})`;
  }
}

function frame(now) {
  const dt = (now - last) / 1000; last = now;
  if (replay && playing) {
    position += dt * TICKS_PER_SECOND * parseFloat($('speed').value);
    const end = replay.frames.length - 1;
    if (position >= end) {
      position = end; playing = false; $('play').textContent = 'Replay';
      if (!finished) { finished = true; if (window.onReplayFinished) window.onReplayFinished(); }
    }
  }
  draw();
  requestAnimationFrame(frame);
}

$('play').onclick = () => {
  if (!replay) return;
  if (position >= replay.frames.length - 1) { position = 0; playing = true; finished = false; }
  else playing = !playing;
  $('play').textContent = playing ? 'Pause' : 'Play';
};
$('scrub').oninput = e => { finished = false; position = +e.target.value; playing = false; $('play').textContent = 'Play'; };

requestAnimationFrame(t => { last = t; frame(t); });
