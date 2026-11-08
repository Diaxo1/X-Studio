'use strict';

const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');

const HOST = '127.0.0.1';
const PORT = Number.parseInt(process.env.XSTUDIO_PORT || '4170', 10);
const ROOT = __dirname;
const FFMPEG = process.env.FFMPEG_PATH || 'ffmpeg';
const STATIC_FILES = new Map([
  ['/', ['index.html', 'text/html; charset=utf-8']],
  ['/index.html', ['index.html', 'text/html; charset=utf-8']],
  ['/styles.css', ['styles.css', 'text/css; charset=utf-8']],
  ['/app.js', ['app.js', 'text/javascript; charset=utf-8']]
]);

let ffmpegEncoders;
let audioDevicesCache = { fetchedAt: 0, names: [] };
let activeProcess = null;
let stopRequested = false;
let lastError = '';
let streamState = 'idle';
let selectedEncoder = '';
let startedAt = 0;
let latestProgress = '';

function json(res, status, value) {
  const body = JSON.stringify(value);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff'
  });
  res.end(body);
}

function requestIsLocal(req) {
  const host = String(req.headers.host || '').toLowerCase();
  const origin = req.headers.origin;
  if (host !== `${HOST}:${PORT}` && host !== `localhost:${PORT}`) return false;
  if (!origin) return true;
  try {
    const parsed = new URL(origin);
    return (parsed.hostname === HOST || parsed.hostname === 'localhost') && Number(parsed.port || 80) === PORT;
  } catch {
    return false;
  }
}

function runCommand(command, args, timeoutMs = 9000) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error('FFmpeg command timed out.'));
    }, timeoutMs);
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', error => {
      clearTimeout(timer);
      reject(error);
    });
    child.on('close', code => {
      clearTimeout(timer);
      resolve({ code, stdout, stderr });
    });
  });
}

async function getEncoders() {
  if (ffmpegEncoders) return ffmpegEncoders;
  const result = await runCommand(FFMPEG, ['-hide_banner', '-encoders']);
  if (result.code !== 0) throw new Error('FFmpeg could not list its encoders.');
  ffmpegEncoders = result.stdout + result.stderr;
  return ffmpegEncoders;
}

async function getAudioDevices(force = false) {
  if (!force && Date.now() - audioDevicesCache.fetchedAt < 15000) return audioDevicesCache.names;
  const result = await runCommand(FFMPEG, ['-hide_banner', '-list_devices', 'true', '-f', 'dshow', '-i', 'dummy']);
  const text = result.stderr || result.stdout;
  const audioSection = text.split(/DirectShow audio devices/i)[1]?.split(/DirectShow video devices/i)[0] || '';
  const names = [...audioSection.matchAll(/^\s*"([^"\r\n]+)"\s*$/gm)]
    .map(match => match[1].trim())
    .filter(name => name && !name.startsWith('@device_'));
  audioDevicesCache = { fetchedAt: Date.now(), names: [...new Set(names)] };
  return audioDevicesCache.names;
}

function sanitizeError(text, target) {
  let safe = String(text || '').replace(/rtmps?:\/\/[^\s]+/gi, '[stream address hidden]');
  if (target) safe = safe.split(target).join('[stream address hidden]');
  return safe.split(/\r?\n/).map(line => line.trim()).filter(Boolean).slice(-8).join('\n').slice(-1100);
}

function buildTarget(serverUrl, streamKey) {
  let parsed;
  try { parsed = new URL(serverUrl); } catch { throw new Error('Enter a valid RTMP server URL.'); }
  if (!['rtmp:', 'rtmps:'].includes(parsed.protocol) || !parsed.hostname || parsed.username || parsed.password || parsed.search || parsed.hash) {
    throw new Error('Use a clean rtmp:// or rtmps:// ingest URL without a key or query string.');
  }
  const base = serverUrl.replace(/\/+$/, '');
  const key = streamKey.trim();
  if (!key || key.length > 512 || /[\r\n]/.test(key)) throw new Error('Enter a valid stream key.');
  return `${base}/${encodeURIComponent(key)}`;
}

function makeArgs(config, encoder) {
  const { width, height, fps, bitrate } = config.quality;
  const args = [
    '-hide_banner', '-nostdin', '-loglevel', 'warning', '-stats_period', '1', '-progress', 'pipe:2',
    '-thread_queue_size', '512', '-f', 'gdigrab', '-framerate', String(fps), '-draw_mouse', '1', '-i', 'desktop'
  ];
  if (config.audioDevice) {
    args.push('-thread_queue_size', '512', '-f', 'dshow', '-i', `audio=${config.audioDevice}`);
  }
  args.push('-map', '0:v:0', '-vf', `scale=${width}:${height}:force_original_aspect_ratio=decrease,pad=${width}:${height}:(ow-iw)/2:(oh-ih)/2,fps=${fps}`);
  if (encoder === 'hardware') {
    args.push('-c:v', 'h264_mf', '-hw_encoding', '1', '-scenario', 'live_streaming', '-rate_control', 'cbr');
  } else {
    args.push('-c:v', 'libx264', '-preset', 'ultrafast', '-tune', 'zerolatency', '-profile:v', 'baseline');
  }
  args.push('-b:v', bitrate, '-maxrate', bitrate, '-bufsize', `${Number.parseInt(bitrate, 10) * 2}k`, '-g', String(fps * 2), '-pix_fmt', 'yuv420p');
  if (config.audioDevice) {
    args.push('-map', '1:a:0', '-c:a', 'aac', '-b:a', '128k', '-ar', '44100');
  } else {
    args.push('-an');
  }
  args.push('-f', 'flv', config.target);
  return args;
}

function looksLikeHardwareFailure(text) {
  return /h264_mf|media.?foundation|d3d11|mft|hardware.{0,20}(fail|error|device)|error initializing output stream|failed to create.*device/i.test(text);
}

function launchFFmpeg(config, encoder) {
  selectedEncoder = encoder === 'hardware' ? 'Media Foundation hardware' : 'x264 ultrafast software';
  streamState = 'starting';
  stopRequested = false;
  latestProgress = '';
  const args = makeArgs(config, encoder);
  const child = spawn(FFMPEG, args, { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  activeProcess = child;
  let stderr = '';
  let frameReported = false;
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', chunk => {
    stderr = (stderr + chunk).slice(-12000);
    const frameMatch = stderr.match(/frame=(\d+)/g)?.at(-1);
    if (frameMatch) {
      frameReported = true;
      streamState = 'streaming';
      if (!startedAt) startedAt = Date.now();
    }
    const fpsMatch = stderr.match(/fps=([^\r\n]+)/g)?.at(-1);
    if (fpsMatch) latestProgress = fpsMatch.replace(/\s+/g, ' ').trim();
  });
  child.on('error', error => {
    if (activeProcess !== child) return;
    activeProcess = null;
    streamState = 'error';
    lastError = error.code === 'ENOENT' ? 'FFmpeg was not found. Add it to PATH or set FFMPEG_PATH.' : error.message;
  });
  child.on('close', (code, signal) => {
    if (activeProcess !== child) return;
    activeProcess = null;
    if (stopRequested) {
      streamState = 'idle';
      startedAt = 0;
      lastError = '';
      return;
    }
    if (encoder === 'hardware' && !frameReported && looksLikeHardwareFailure(stderr)) {
      streamState = 'fallback';
      lastError = 'Hardware encoder unavailable; switching to the low-latency software encoder.';
      launchFFmpeg(config, 'software');
      return;
    }
    streamState = 'error';
    startedAt = 0;
    lastError = sanitizeError(stderr, config.target) || `FFmpeg stopped (${signal || `exit ${code}`}). Check the ingest URL, stream key, and audio device.`;
  });
}

async function startStream(body) {
  if (activeProcess) throw Object.assign(new Error('A stream is already running.'), { statusCode: 409 });
  const target = buildTarget(String(body.serverUrl || ''), String(body.streamKey || ''));
  const qualities = {
    '720p30': { width: 1280, height: 720, fps: 30, bitrate: '2500k' },
    '480p30': { width: 854, height: 480, fps: 30, bitrate: '1200k' }
  };
  const quality = qualities[body.quality] || qualities['720p30'];
  const audioDevice = String(body.audioDevice || '');
  if (audioDevice) {
    const devices = await getAudioDevices(true);
    if (!devices.includes(audioDevice)) throw new Error('That microphone is no longer available. Refresh the device list and choose it again.');
  }
  const encoders = await getEncoders();
  if (!encoders.includes('h264_mf') && !encoders.includes('libx264')) throw new Error('This FFmpeg build has no supported H.264 encoder.');
  if (!encoders.includes('h264_mf')) {
    if (!encoders.includes('libx264')) throw new Error('This FFmpeg build has no supported H.264 encoder.');
    launchFFmpeg({ target, quality, audioDevice }, 'software');
  } else {
    launchFFmpeg({ target, quality, audioDevice }, body.encoder === 'software' ? 'software' : 'hardware');
  }
  lastError = '';
}

function stopStream() {
  if (!activeProcess) {
    streamState = 'idle';
    startedAt = 0;
    return;
  }
  stopRequested = true;
  streamState = 'stopping';
  const child = activeProcess;
  child.kill('SIGTERM');
  setTimeout(() => {
    if (activeProcess === child) child.kill('SIGKILL');
  }, 2500).unref();
}

async function readJson(req) {
  let raw = '';
  for await (const part of req) {
    raw += part;
    if (raw.length > 12000) throw Object.assign(new Error('Request is too large.'), { statusCode: 413 });
  }
  try { return JSON.parse(raw || '{}'); }
  catch { throw Object.assign(new Error('The request body must be valid JSON.'), { statusCode: 400 }); }
}

const server = http.createServer(async (req, res) => {
  if (!requestIsLocal(req)) return json(res, 403, { error: 'Local X Studio requests only.' });
  const pathname = new URL(req.url, `http://${HOST}:${PORT}`).pathname;
  try {
    if (req.method === 'GET' && pathname === '/api/status') {
      const encoders = await getEncoders().catch(() => '');
      return json(res, 200, {
        state: streamState,
        ffmpegAvailable: Boolean(encoders),
        encoder: selectedEncoder,
        elapsedSeconds: startedAt ? Math.floor((Date.now() - startedAt) / 1000) : 0,
        progress: latestProgress,
        error: lastError
      });
    }
    if (req.method === 'GET' && pathname === '/api/audio-devices') {
      const names = await getAudioDevices(true);
      return json(res, 200, { devices: names });
    }
    if (req.method === 'POST' && pathname === '/api/stream/start') {
      const body = await readJson(req);
      await startStream(body);
      return json(res, 202, { state: streamState, message: 'Capture is starting.' });
    }
    if (req.method === 'POST' && pathname === '/api/stream/stop') {
      stopStream();
      return json(res, 202, { state: streamState, message: 'Stream is stopping.' });
    }
    if (req.method === 'GET' && STATIC_FILES.has(pathname)) {
      const [file, contentType] = STATIC_FILES.get(pathname);
      const absolutePath = path.join(ROOT, file);
      const content = await fs.promises.readFile(absolutePath);
      res.writeHead(200, { 'Content-Type': contentType, 'Content-Length': content.length, 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
      return res.end(content);
    }
    return json(res, 404, { error: 'Not found.' });
  } catch (error) {
    return json(res, error.statusCode || 500, { error: error.message || 'X Studio could not complete that request.' });
  }
});

server.listen(PORT, HOST, () => {
  console.log(`X Studio local engine ready at http://${HOST}:${PORT}`);
  console.log('This listener is bound to this PC only. Press Ctrl+C to quit.');
});

function shutDown() {
  stopStream();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 3000).unref();
}
process.on('SIGINT', shutDown);
process.on('SIGTERM', shutDown);
