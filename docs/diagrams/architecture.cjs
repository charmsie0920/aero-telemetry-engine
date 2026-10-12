// Generates the README hero diagram in two forms from one description:
//   architecture.svg         - what the README shows (hand-drawn look, font embedded)
//   architecture.excalidraw  - the same picture, editable at https://excalidraw.com
//
// To update it: change STATUS below as phases land, then run
//   npm install --no-save roughjs fontkit
//   node docs/diagrams/architecture.cjs
//
// The Virgil font (Excalidraw's hand-drawn font, OFL licence) is downloaded once
// and cached in the temp folder. Set VIRGIL_FONT to a local .woff2 to skip that.

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const rough = require('roughjs');
const fontkit = require('fontkit');

// 'built' | 'building' | 'planned'
const STATUS = {
  collector: 'building',
  database: 'building',
  detector: 'planned',
  archiver: 'planned',
  dashboard: 'planned',
  lake: 'planned',
};

const WIDTH = 1510;
const HEIGHT = 700;
const INK = '#1e1e1e';
const MUTED = '#495057';
const TECH = '#1864ab';
const NOTE = '#6741d9';
const LINE_HEIGHT = 1.25;
const FONT_URL = 'https://unpkg.com/@excalidraw/excalidraw@0.17.6/dist/excalidraw-assets/Virgil.woff2';

const STATUS_STYLE = {
  built: { fill: '#b2f2bb', dashed: false, label: 'built' },
  building: { fill: '#ffec99', dashed: false, label: 'building now' },
  planned: { fill: '#e7f5ff', dashed: true, label: 'planned' },
  external: { fill: '#ffffff', dashed: false },
};

// ---- the picture, as a flat list of shapes --------------------------------

const shapes = [];

const rect = (x, y, w, h, style = {}) => shapes.push({ kind: 'rect', x, y, w, h, stroke: INK, fill: 'transparent', ...style });
const ellipse = (x, y, w, h, style = {}) => shapes.push({ kind: 'ellipse', x, y, w, h, stroke: INK, fill: 'transparent', ...style });
const arrow = (x1, y1, x2, y2) => shapes.push({ kind: 'arrow', x1, y1, x2, y2, stroke: INK });
// x is the anchor point for the given alignment, y is the top of the first line.
const text = (x, y, lines, size, style = {}) =>
  shapes.push({ kind: 'text', x, y, lines: [].concat(lines), size, color: INK, align: 'center', ...style });

function box(x, y, w, h, status, lines, number) {
  const style = STATUS_STYLE[status];
  rect(x, y, w, h, { fill: style.fill, dashed: style.dashed });

  const sizes = [22, 16, 15];
  const colors = [INK, TECH, MUTED];
  const total = lines.reduce((sum, _, i) => sum + sizes[i] * LINE_HEIGHT, 0);
  let top = y + (h - total) / 2;
  lines.forEach((line, i) => {
    text(x + w / 2, top, line, sizes[i], { color: colors[i] });
    top += sizes[i] * LINE_HEIGHT;
  });

  if (number) {
    ellipse(x - 14, y - 14, 32, 32, { fill: INK });
    text(x + 2, y - 8, String(number), 16, { color: '#ffffff' });
  }
}

function note(x, y, w, heading, lines) {
  const h = 46 + lines.length * 16 * LINE_HEIGHT + 12;
  rect(x, y, w, h, { stroke: NOTE, fill: '#f3f0ff', thin: true });
  text(x + 18, y + 14, heading, 15, { color: NOTE, align: 'left' });
  text(x + 18, y + 42, lines, 16, { align: 'left' });
}

// Title and legend
text(40, 28, 'aero-telemetry-engine: how the data flows', 30, { align: 'left' });
text(40, 72, 'Live aircraft positions in, anomaly flags and a live map out. Near-real-time: one poll every 30 s.', 17, {
  color: MUTED,
  align: 'left',
});

let legendX = 1090;
for (const key of ['built', 'building', 'planned']) {
  const style = STATUS_STYLE[key];
  rect(legendX, 36, 26, 20, { fill: style.fill, dashed: style.dashed, thin: true });
  text(legendX + 34, 36, style.label, 16, { align: 'left' });
  legendX += key === 'building' ? 160 : 100;
}

// Notes along the top and bottom
note(40, 130, 520, 'KEY DESIGN DECISION', [
  'The Collector and the Detector never call each other.',
  'They only share the database, so if the ML side',
  'crashes, data collection carries on.',
]);
note(1000, 130, 470, 'HOW IT RUNS AND SHIPS', [
  'Services 1, 3, 4, 5: Docker Compose on one AWS EC2',
  'Database: hosted by Supabase, not on the server,',
  'so the server holds no data and can be rebuilt',
  'Terraform: creates the AWS side (EC2, S3, IAM)',
  'GitHub Actions: build, test, scan, deploy',
]);
note(40, 548, 520, 'HOW THE COLLECTOR SURVIVES FAILURES', [
  'Flight API down: retry with backoff, circuit breaker',
  'Database down: bounded buffer holds the batches',
  'Crash and replay: idempotent writes, no duplicates',
]);

// Main flow, left to right
box(40, 340, 180, 120, 'external', ['OpenSky', 'public flight data', 'live aircraft positions']);
box(350, 340, 210, 120, STATUS.collector, ['Collector', '.NET 10 worker', 'fetches + cleans positions'], 1);
box(700, 340, 240, 120, STATUS.database, ['Database', 'Supabase Postgres (hosted)', 'last 48 h, partitioned'], 2);
box(1080, 340, 200, 120, STATUS.dashboard, ['Dashboard', 'Grafana', 'live map, alerts, health'], 5);
ellipse(1380, 362, 96, 76, { fill: '#ffffff' });
text(1428, 387, 'Viewer', 20);

arrow(228, 400, 338, 400);
text(283, 368, 'every 30 s', 15, { color: MUTED });
arrow(568, 400, 688, 400);
text(628, 368, 'saves positions', 15, { color: MUTED });
arrow(948, 400, 1066, 400);
text(1008, 350, ['flights, scores,', 'health stats'], 15, { color: MUTED });
arrow(1288, 400, 1372, 400);

// Detector above the database, archiver below it
box(700, 130, 240, 110, STATUS.detector, ['Anomaly Detector', 'Python + ONNX model', 'flags unusual flights'], 3);
arrow(780, 332, 780, 250);
text(766, 272, ['every 60 s:', 'recent paths'], 15, { color: MUTED, align: 'right' });
arrow(860, 248, 860, 328);
text(874, 281, 'anomaly scores', 15, { color: MUTED, align: 'left' });

box(710, 560, 220, 100, STATUS.archiver, ['Archiver', 'Python', 'moves old data out'], 4);
arrow(820, 468, 820, 548);
text(834, 489, ['daily: data', 'older than 48 h'], 15, { color: MUTED, align: 'left' });
box(1080, 560, 240, 100, STATUS.lake, ['Long-term storage', 'AWS S3', 'Parquet files']);
arrow(938, 610, 1068, 610);
text(1003, 578, 'compressed files', 15, { color: MUTED });

// ---- output ----------------------------------------------------------------

async function loadFont() {
  const fontPath = process.env.VIRGIL_FONT ?? path.join(os.tmpdir(), 'Virgil.woff2');
  if (!fs.existsSync(fontPath)) {
    const response = await fetch(FONT_URL);
    if (!response.ok) throw new Error(`Could not download the Virgil font: HTTP ${response.status}`);
    fs.writeFileSync(fontPath, Buffer.from(await response.arrayBuffer()));
  }
  return fs.readFileSync(fontPath);
}

const escapeXml = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function roundedRectPath(x, y, w, h, r) {
  return `M ${x + r} ${y} L ${x + w - r} ${y} Q ${x + w} ${y} ${x + w} ${y + r} L ${x + w} ${y + h - r} ` +
    `Q ${x + w} ${y + h} ${x + w - r} ${y + h} L ${x + r} ${y + h} Q ${x} ${y + h} ${x} ${y + h - r} ` +
    `L ${x} ${y + r} Q ${x} ${y} ${x + r} ${y} Z`;
}

function toSvg(fontBytes) {
  const gen = rough.generator();
  const out = [];
  let seed = 1;

  const draw = (drawable, dashed) => {
    for (const p of gen.toPaths(drawable)) {
      const isOutline = p.fill === 'none' || !p.fill;
      const dash = dashed && isOutline ? ' stroke-dasharray="9 7"' : '';
      out.push(
        `<path d="${p.d}" stroke="${p.stroke}" stroke-width="${p.strokeWidth}" fill="${p.fill || 'none'}"` +
          ` stroke-linecap="round" stroke-linejoin="round"${dash}/>`,
      );
    }
  };

  for (const s of shapes) {
    const options = {
      seed: seed++,
      roughness: 1,
      stroke: s.stroke,
      strokeWidth: s.thin ? 1.5 : 2,
      ...(s.fill && s.fill !== 'transparent' ? { fill: s.fill, fillStyle: 'solid' } : {}),
    };

    if (s.kind === 'rect') {
      draw(gen.path(roundedRectPath(s.x, s.y, s.w, s.h, Math.min(12, s.h / 5)), options), s.dashed);
    } else if (s.kind === 'ellipse') {
      draw(gen.ellipse(s.x + s.w / 2, s.y + s.h / 2, s.w, s.h, options), false);
    } else if (s.kind === 'arrow') {
      const angle = Math.atan2(s.y2 - s.y1, s.x2 - s.x1);
      draw(gen.line(s.x1, s.y1, s.x2, s.y2, options), false);
      for (const spread of [-0.45, 0.45]) {
        const a = angle + Math.PI + spread;
        draw(gen.line(s.x2, s.y2, s.x2 + 14 * Math.cos(a), s.y2 + 14 * Math.sin(a), { ...options, roughness: 0.5 }), false);
      }
    } else if (s.kind === 'text') {
      const anchor = { left: 'start', center: 'middle', right: 'end' }[s.align];
      s.lines.forEach((line, i) => {
        const baseline = s.y + i * s.size * LINE_HEIGHT + s.size * 0.88;
        out.push(
          `<text x="${s.x}" y="${baseline.toFixed(1)}" font-size="${s.size}" fill="${s.color}" text-anchor="${anchor}">${escapeXml(line)}</text>`,
        );
      });
    }
  }

  return [
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${WIDTH} ${HEIGHT}" width="${WIDTH}" height="${HEIGHT}" role="img"`,
    ` aria-label="Architecture: OpenSky feeds a .NET collector, which writes to Postgres. An anomaly detector and an archiver read from Postgres; Grafana shows the result.">`,
    `<style>@font-face{font-family:"Virgil";src:url(data:font/woff2;base64,${fontBytes.toString('base64')}) format("woff2");}`,
    `text{font-family:"Virgil","Segoe Print","Comic Sans MS",cursive;}</style>`,
    `<rect width="${WIDTH}" height="${HEIGHT}" rx="16" fill="#ffffff"/>`,
    ...out,
    `</svg>`,
  ].join('\n');
}

function toExcalidraw(font) {
  const measure = (line, size) => (font.layout(line).advanceWidth / font.unitsPerEm) * size;
  let id = 0;

  const base = (s, extra) => ({
    id: `el-${++id}`,
    angle: 0,
    strokeColor: s.stroke ?? INK,
    backgroundColor: s.fill ?? 'transparent',
    fillStyle: 'solid',
    strokeWidth: s.thin ? 1 : 2,
    strokeStyle: s.dashed ? 'dashed' : 'solid',
    roughness: 1,
    opacity: 100,
    groupIds: [],
    frameId: null,
    roundness: null,
    seed: id * 7919,
    version: 1,
    versionNonce: id * 104729,
    isDeleted: false,
    boundElements: null,
    updated: 1,
    link: null,
    locked: false,
    ...extra,
  });

  const elements = shapes.map((s) => {
    if (s.kind === 'rect') {
      return base(s, { type: 'rectangle', x: s.x, y: s.y, width: s.w, height: s.h, roundness: { type: 3 } });
    }
    if (s.kind === 'ellipse') {
      return base(s, { type: 'ellipse', x: s.x, y: s.y, width: s.w, height: s.h });
    }
    if (s.kind === 'arrow') {
      const dx = s.x2 - s.x1;
      const dy = s.y2 - s.y1;
      return base(s, {
        type: 'arrow',
        x: s.x1,
        y: s.y1,
        width: Math.abs(dx),
        height: Math.abs(dy),
        points: [[0, 0], [dx, dy]],
        lastCommittedPoint: null,
        startBinding: null,
        endBinding: null,
        startArrowhead: null,
        endArrowhead: 'arrow',
      });
    }
    const width = Math.max(...s.lines.map((line) => measure(line, s.size)));
    const offset = { left: 0, center: width / 2, right: width }[s.align];
    return base(
      { stroke: s.color },
      {
        type: 'text',
        x: s.x - offset,
        y: s.y,
        width,
        height: s.lines.length * s.size * LINE_HEIGHT,
        text: s.lines.join('\n'),
        originalText: s.lines.join('\n'),
        fontSize: s.size,
        fontFamily: 1,
        textAlign: s.align,
        verticalAlign: 'top',
        containerId: null,
        lineHeight: LINE_HEIGHT,
        autoResize: true,
      },
    );
  });

  return JSON.stringify(
    {
      type: 'excalidraw',
      version: 2,
      source: 'https://github.com/cche0258/aero-telemetry-engine',
      elements,
      appState: { viewBackgroundColor: '#ffffff', gridSize: null },
      files: {},
    },
    null,
    2,
  );
}

async function main() {
  const fontBytes = await loadFont();
  const font = fontkit.create(fontBytes);

  for (const s of shapes.filter((shape) => shape.kind === 'text')) {
    for (const ch of s.lines.join('')) {
      if (!font.hasGlyphForCodePoint(ch.codePointAt(0))) {
        throw new Error(`The Virgil font has no glyph for "${ch}" (in "${s.lines.join(' ')}")`);
      }
    }
  }

  fs.writeFileSync(path.join(__dirname, 'architecture.svg'), toSvg(fontBytes));
  fs.writeFileSync(path.join(__dirname, 'architecture.excalidraw'), toExcalidraw(font));
  console.log('Wrote architecture.svg and architecture.excalidraw');
}

main().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
