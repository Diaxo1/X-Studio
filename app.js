const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const toast = $('#toast');
let toastTimer;

function showToast(message) {
  toast.textContent = message;
  toast.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove('show'), 2500);
}

const pages = {
  scenes: {
    title: 'Scene library', kicker: 'SCENE LIBRARY / 02', heading: 'Set the scene.',
    intro: 'Give every part of your broadcast its own look, then move between them with a tap.',
    note: 'Scenes will hold the sources, layout, and transitions you choose for each moment.',
    rows: ['Starting soon', 'Gameplay', 'Just chatting']
  },
  destinations: {
    title: 'Destinations', kicker: 'OUTPUT ROUTING / 03', heading: 'Pick your places.',
    intro: 'Choose where the show goes. Each destination can be started and monitored on its own.',
    note: 'Connection setup will live here, with a clear status for each platform and stream.',
    rows: ['TikTok LIVE · Setup required', 'YouTube Live · Setup required', 'Twitch · Setup required']
  },
  plugins: {
    title: 'Extensions', kicker: 'COMMUNITY TOOLS / 04', heading: 'Make room for more.',
    intro: 'X Studio will have its own extension system for community-made tools and integrations.',
    note: 'The extension interface will follow once the core source and effect formats are set.',
    rows: ['New capture sources', 'Effects and transitions', 'Chat and platform tools']
  },
  settings: {
    title: 'Preferences', kicker: 'STUDIO PREFERENCES / 05', heading: 'Tune your setup.',
    intro: 'Shape the studio around your devices, your quality target, and the PC you have.',
    note: 'The first native build will focus on capture, audio, output quality, and low overhead.',
    rows: ['Quiet mode', 'Video and encoder', 'Audio devices']
  }
};

function openPage(name) {
  const studio = $('#studio-view');
  const alternate = $('#alternate-view');
  $$('.nav-item[data-view]').forEach(button => button.classList.toggle('active', button.dataset.view === name));
  if (name === 'studio') {
    studio.hidden = false;
    alternate.hidden = true;
    $('#page-name').textContent = 'LIVE DESK';
    return;
  }
  const page = pages[name];
  if (!page) return;
  studio.hidden = true;
  alternate.hidden = false;
  $('#page-name').textContent = page.title.toUpperCase();
  alternate.innerHTML = `<p class="eyebrow"><span class="eyebrow-rule"></span>${page.kicker}</p><h1>${page.heading}</h1><p>${page.intro}</p><section class="alternate-card"><h2>${page.title}</h2><p>${page.note}</p><div class="alternate-list">${page.rows.map(row => `<div class="alternate-row"><span>✳</span>${row}</div>`).join('')}</div><button class="outline-button back-studio" style="margin-top:16px">← Return to live desk</button></section>`;
  $('.back-studio', alternate).addEventListener('click', () => openPage('studio'));
}

$$('.nav-item[data-view],.text-button[data-view],.outline-button[data-view]').forEach(button => {
  button.addEventListener('click', () => openPage(button.dataset.view));
});

const sourceModal = $('#source-modal');
const castModal = $('#cast-modal');
function openSourcePicker() { sourceModal.showModal(); }
$('#add-source').addEventListener('click', openSourcePicker);
$('#empty-add').addEventListener('click', openSourcePicker);
$('#cast-phone').addEventListener('click', () => castModal.showModal());
$$('.close-modal').forEach(button => button.addEventListener('click', () => button.closest('dialog').close()));

const sourceGlyphs = { 'Screen capture': '▣', Camera: '◉', 'Phone cast': '▯', Image: '▧' };
$$('.source-option').forEach(button => button.addEventListener('click', event => {
  event.preventDefault();
  const name = button.value;
  const list = $('#source-list');
  $('.source-empty', list)?.remove();
  const row = document.createElement('div');
  row.className = 'source-item';
  row.innerHTML = `<span class="source-glyph">${sourceGlyphs[name]}</span><span>${name}</span><small>Added to this scene</small><button class="source-remove" aria-label="Remove ${name}">×</button>`;
  list.append(row);
  $('.source-remove', row).addEventListener('click', () => {
    row.remove();
    if (!$('.source-item', list)) {
      list.innerHTML = '<div class="source-empty"><span class="empty-spark">✳</span><span><b>The stage is clear.</b><small>Add a camera, display, phone, or graphic to begin.</small></span><button class="mini-add" id="empty-add">Choose source <i>↗</i></button></div>';
      $('#empty-add').addEventListener('click', openSourcePicker);
    }
  });
  sourceModal.close();
  if (name === 'Phone cast') castModal.showModal();
  else showToast(`${name} added to this scene`);
}));

function updateDestinationCount() {
  const count = $$('.destination-check:checked').length;
  $('.output-count').innerHTML = `${count} <i>SELECTED</i>`;
  $$('.platform-row').forEach(row => {
    const input = $('.destination-check', row);
    $('.platform-copy small', row).textContent = input.checked ? 'Selected · setup needed' : 'Setup required';
    $('.connect-state', row).innerHTML = input.checked ? 'SELECTED <i>✓</i>' : 'ADD <i>＋</i>';
  });
}
$$('.destination-check').forEach(input => input.addEventListener('change', updateDestinationCount));

const streamModal = $('#stream-setup-modal');
const streamForm = $('#stream-form');
const streamFeedback = $('#stream-feedback');
let streamRunning = false;
let streamBusy = false;

async function requestApi(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || `X Studio request failed (${response.status}).`);
  return data;
}

function setStreamButton(running, state = 'idle') {
  streamRunning = running;
  const button = $('#go-live');
  if (running) {
    button.innerHTML = '<span class="broadcast-icon">■</span><span>Stop broadcast</span><span class="button-arrow">×</span>';
    button.classList.add('is-live');
  } else {
    button.innerHTML = '<span class="broadcast-icon">◉</span><span>Start broadcast</span><span class="button-arrow">↗</span>';
    button.classList.remove('is-live');
  }
  const label = state === 'streaming' ? 'Streaming' : state === 'starting' ? 'Starting stream' : state === 'stopping' ? 'Stopping' : state === 'error' ? 'Stream error' : 'Engine idle';
  $('.engine-state').innerHTML = `<i class="state-${state}"></i>${label}`;
}

async function refreshEngineStatus() {
  try {
    const status = await requestApi('/api/status');
    setStreamButton(['starting', 'streaming', 'fallback', 'stopping'].includes(status.state), status.state);
    if (status.state === 'error' && status.error) {
      streamFeedback.textContent = status.error;
      streamFeedback.classList.add('has-error');
      streamModal.showModal();
    }
  } catch {
    setStreamButton(false, 'idle');
  }
}

async function loadAudioInputs() {
  const select = $('#audio-device');
  try {
    const { devices } = await requestApi('/api/audio-devices');
    select.replaceChildren(new Option('No microphone audio', ''));
    devices.forEach(name => select.add(new Option(name, name)));
    if (!devices.length) select.add(new Option('No microphone detected', ''));
  } catch {
    select.replaceChildren(new Option('Start X Studio to list microphones', ''));
  }
}

$('#go-live').addEventListener('click', async () => {
  if (streamRunning) {
    try {
      await requestApi('/api/stream/stop', { method: 'POST', body: '{}' });
      showToast('Stopping the broadcast…');
    } catch (error) {
      showToast(error.message);
    }
    refreshEngineStatus();
    return;
  }
  streamFeedback.textContent = '';
  streamFeedback.classList.remove('has-error');
  streamModal.showModal();
  loadAudioInputs();
});

$('.cancel-stream').addEventListener('click', () => streamModal.close());
streamForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (streamBusy) return;
  streamBusy = true;
  const submit = $('#stream-submit');
  submit.disabled = true;
  submit.textContent = 'Starting…';
  streamFeedback.textContent = '';
  streamFeedback.classList.remove('has-error');
  const payload = {
    platform: $('#stream-platform').value,
    serverUrl: $('#stream-server').value.trim(),
    streamKey: $('#stream-key').value,
    audioDevice: $('#audio-device').value,
    quality: $('#stream-quality').value,
    encoder: $('#stream-encoder').value
  };
  try {
    await requestApi('/api/stream/start', { method: 'POST', body: JSON.stringify(payload) });
    $('#stream-key').value = '';
    streamModal.close();
    showToast('Screen capture is starting.');
    refreshEngineStatus();
  } catch (error) {
    streamFeedback.textContent = error.message;
    streamFeedback.classList.add('has-error');
  } finally {
    streamBusy = false;
    submit.disabled = false;
    submit.innerHTML = '<span>◉</span> Start stream';
  }
});

refreshEngineStatus();
setInterval(refreshEngineStatus, 1600);

$$('.method').forEach(button => button.addEventListener('click', () => {
  $$('.method').forEach(method => method.classList.toggle('selected', method === button));
}));
$('#start-pairing').addEventListener('click', () => showToast('Phone pairing will be available in the native app.'));

const previewCanvas = $('#preview-canvas');
$$('.preview-mode').forEach(button => button.addEventListener('click', () => {
  const isTikTok = button.dataset.preview === 'tiktok';
  $$('.preview-mode').forEach(mode => {
    const active = mode === button;
    mode.classList.toggle('active', active);
    mode.setAttribute('aria-pressed', String(active));
  });
  previewCanvas.classList.toggle('tiktok-mode', isTikTok);
  $('#preview-resolution').innerHTML = isTikTok ? '9:16 <b>·</b> 1080 × 1920' : '16:9 <b>·</b> 1080P';
  $('#preview-status').textContent = isTikTok ? 'TIKTOK LAYOUT' : 'NO SIGNAL';
  $('.tiktok-overlay').setAttribute('aria-hidden', String(!isTikTok));
}));
