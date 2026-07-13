// Base render service for RandomGridSpawn.
//
// The Rust game server is headless and cannot render 3D, so it POSTs a base's data here
// (prefab + position + rotation + grade per piece). This service rebuilds the base from
// Facepunch's glTF model library (github.com/Facepunch/RustRelay.Assets), renders it in
// headless Chrome, and replies with the image URL the plugin puts on the base card.

import express from 'express';
import puppeteer from 'puppeteer';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));

const PORT = Number(process.env.PORT || 8080);
// Where RustRelay.Assets is unpacked (the folder that contains "assets/prefabs/...").
const ASSETS_DIR = process.env.ASSETS_DIR || path.join(HERE, 'assets');
// Public base URL of THIS service, as the game server will see it.
const PUBLIC_URL = (process.env.PUBLIC_URL || `http://localhost:${PORT}`).replace(/\/$/, '');
// Optional shared secret; must match "Render service API key" in the plugin config.
const API_KEY = process.env.API_KEY || '';

const RENDERS_DIR = path.join(HERE, 'renders');
fs.mkdirSync(RENDERS_DIR, { recursive: true });

// ---------------------------------------------------------------------------
// Model index: every .glb in the asset library, keyed by lowercased relative path.
// ---------------------------------------------------------------------------

const modelIndex = new Map();

function indexModels(dir, base = '') {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    const rel = base ? `${base}/${entry.name}` : entry.name;
    if (entry.isDirectory()) indexModels(full, rel);
    else if (entry.name.toLowerCase().endsWith('.glb')) modelIndex.set(rel.toLowerCase().replace(/\\/g, '/'), rel.replace(/\\/g, '/'));
  }
}

if (fs.existsSync(ASSETS_DIR)) {
  indexModels(ASSETS_DIR);
  console.log(`Indexed ${modelIndex.size} models from ${ASSETS_DIR}`);
} else {
  console.warn(`Asset folder not found: ${ASSETS_DIR} - unpack RustRelay.Assets there.`);
}

// Rust BuildingGrade 0..4 -> the suffix used in the model library.
const GRADES = ['twig', 'wood', 'stone', 'metal', 'toptier'];

// "assets/prefabs/building core/foundation/foundation.prefab" + grade 2
//   -> "assets/prefabs/Building Core/foundation/foundation.stone.glb"
//
// Index keys are relative to ASSETS_DIR, so the plugin's leading "assets/" is stripped;
// both spellings are tried in case ASSETS_DIR points one level higher.
function resolveModel(prefab, grade) {
  if (!prefab) return null;

  const full = prefab.toLowerCase().replace(/\.prefab$/, '');
  const stems = [full.replace(/^assets\//, ''), full];

  const candidates = [];
  for (const stem of stems) {
    if (grade >= 0 && grade < GRADES.length) candidates.push(`${stem}.${GRADES[grade]}.glb`);
    candidates.push(`${stem}.glb`);
  }

  for (const candidate of candidates) {
    const hit = modelIndex.get(candidate);
    if (hit) return `/assets/${hit.split('/').map(encodeURIComponent).join('/')}`;
  }

  // Same folder, any grade - better a wrong grade than a hole in the base.
  const leaf = stems[0].split('/').pop();
  for (const [key, value] of modelIndex) {
    if (key.includes(`/${leaf}.`) || key.endsWith(`/${leaf}.glb`)) {
      return `/assets/${value.split('/').map(encodeURIComponent).join('/')}`;
    }
  }

  return null;
}

// ---------------------------------------------------------------------------
// Web server
// ---------------------------------------------------------------------------

const app = express();
app.use(express.json({ limit: '32mb' }));

app.use('/assets', express.static(ASSETS_DIR, { maxAge: '7d' }));
app.use('/renders', express.static(RENDERS_DIR, { maxAge: '1h' }));
app.use('/vendor', express.static(path.join(HERE, 'node_modules', 'three'), { maxAge: '7d' }));
app.use(express.static(path.join(HERE, 'public')));

// Jobs waiting to be picked up by the browser page.
const jobs = new Map();
app.get('/job/:id', (req, res) => {
  const job = jobs.get(req.params.id);
  if (!job) return res.status(404).json({ error: 'unknown job' });
  res.json(job);
});

let browserPromise = null;
function getBrowser() {
  if (!browserPromise) {
    browserPromise = puppeteer.launch({
      headless: 'new',
      // SwiftShader gives WebGL on a GPU-less VPS.
      args: [
        '--no-sandbox',
        '--use-gl=swiftshader',
        '--enable-unsafe-swiftshader',
        '--disable-dev-shm-usage',
      ],
    });
  }
  return browserPromise;
}

app.post('/', async (req, res) => {
  if (API_KEY && req.get('authorization') !== `Bearer ${API_KEY}`) {
    return res.status(401).json({ error: 'bad api key' });
  }

  const { code, name, entities } = req.body || {};
  if (!code || !Array.isArray(entities) || entities.length === 0) {
    return res.status(400).json({ error: 'need code + entities' });
  }

  const pieces = [];
  const missing = new Set();
  for (const e of entities) {
    const url = resolveModel(e.prefab, typeof e.grade === 'number' ? e.grade : -1);
    if (!url) { missing.add(e.prefab); continue; }
    pieces.push({ url, pos: e.pos, rot: e.rot });
  }

  if (pieces.length === 0) {
    return res.status(422).json({ error: 'no models resolved', missing: [...missing] });
  }
  if (missing.size) console.warn(`[${code}] ${missing.size} prefab(s) had no model:`, [...missing].slice(0, 5));

  const jobId = `${code}-${Date.now()}`;
  jobs.set(jobId, { pieces });

  let page;
  try {
    const browser = await getBrowser();
    page = await browser.newPage();
    await page.setViewport({ width: 900, height: 600, deviceScaleFactor: 1 });
    page.on('console', (m) => console.log(`[page:${code}]`, m.text()));

    await page.goto(`http://127.0.0.1:${PORT}/viewer.html?job=${jobId}`, { waitUntil: 'networkidle0', timeout: 120000 });
    await page.waitForFunction('window.__renderDone === true', { timeout: 120000 });

    const dataUrl = await page.evaluate(() => document.querySelector('canvas').toDataURL('image/png'));
    const base64 = dataUrl.split(',')[1];
    const png = Buffer.from(base64, 'base64');
    fs.writeFileSync(path.join(RENDERS_DIR, `${code}.png`), png);

    console.log(`[${code}] rendered ${pieces.length} pieces (${(png.length / 1024) | 0} KB)`);

    // The PNG is returned inline as well as hosted: the game server stores the bytes in
    // its own file storage and never has to download the image (its image downloader
    // insists on TLS, which a plain-HTTP service can't offer).
    res.json({
      url: `${PUBLIC_URL}/renders/${code}.png?v=${Date.now()}`,
      png: base64,
    });
  } catch (err) {
    console.error(`[${code}] render failed:`, err.message);
    res.status(500).json({ error: err.message });
  } finally {
    jobs.delete(jobId);
    if (page) await page.close().catch(() => {});
  }
});

app.get('/health', (_req, res) => res.json({ ok: true, models: modelIndex.size }));

app.listen(PORT, () => {
  console.log(`Base render service on :${PORT}`);
  console.log(`Public URL: ${PUBLIC_URL}  (put this in the plugin's "Render service URL")`);
});
