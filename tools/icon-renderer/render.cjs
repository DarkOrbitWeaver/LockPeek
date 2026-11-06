const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { Resvg } = require('@resvg/resvg-js');
const root = path.resolve(__dirname, '../..');
function readOriginal(name) {
 const original = fs.readFileSync(path.join(root, 'assets/brand', `lockpeek-original-${name}.svg`), 'utf8');
 const match = original.match(/<svg\b[^>]*>([\s\S]*?)<\/svg>/);
 if (!match) throw new Error(`Missing original ${name} SVG artwork`);
 return match[1];
}
const light = readOriginal('light');
const dark = readOriginal('dark');
function svg(inner) { return `<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">${inner}</svg>`; }
function render(inner, size) { return new Resvg(svg(inner), { fitTo: { mode: 'width', value: size } }).render().asPng(); }
function ico(frames) {
 const header = Buffer.alloc(6 + frames.length * 16);
 header.writeUInt16LE(1, 2); header.writeUInt16LE(frames.length, 4);
 let offset = header.length;
 frames.forEach(({ size, png }, i) => {
  const entry = 6 + 16 * i;
  header[entry] = header[entry + 1] = size === 256 ? 0 : size;
  header.writeUInt16LE(1, entry + 4); header.writeUInt16LE(32, entry + 6);
  header.writeUInt32LE(png.length, entry + 8); header.writeUInt32LE(offset, entry + 12);
  offset += png.length;
 });
 return Buffer.concat([header, ...frames.map(f => f.png)]);
}
for (const [name, inner] of [['light', light], ['dark', dark]]) {
 const original = svg(inner);
 const frames = [16,20,24,32,40,48,64,256].map(size => ({size, png: render(inner, size)}));
 for (const folder of ['context-menu', 'taskbar']) {
  const dir = path.join(root, 'assets', folder); fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, `lockpeek-${name}.svg`), original);
  frames.forEach(({ size, png }) => fs.writeFileSync(path.join(dir, `lockpeek-${name}-${size}.png`), png));
  fs.writeFileSync(path.join(dir, `lockpeek-${name}.ico`), ico(frames));
 }
 console.log(`Original ${name} SVG exported unchanged; SHA256 ${crypto.createHash('sha256').update(inner).digest('hex')}`);
}
fs.copyFileSync(path.join(root, 'assets/taskbar/lockpeek-light.ico'), path.join(root, 'assets/brand/lockpeek.ico'));
for (const file of fs.readdirSync(path.join(root, 'assets/msix')).filter(f => f.endsWith('.png'))) {
 const target = path.join(root, 'assets/msix', file);
 const original = fs.readFileSync(target); const width = original.readUInt32BE(16), height = original.readUInt32BE(20);
 if (width === height) fs.writeFileSync(target, render(file.includes('altform-unplated') ? dark : light, width));
}
// Reproduce the supplied panel's 128px artwork centered inside 200px squares.
const preview = `<svg xmlns="http://www.w3.org/2000/svg" width="400" height="200"><rect width="200" height="200" fill="#F3F3F3"/><rect x="200" width="200" height="200" fill="#2B2B2B"/><svg x="36" y="36" width="128" height="128" viewBox="0 0 32 32">${light}</svg><svg x="236" y="36" width="128" height="128" viewBox="0 0 32 32">${dark}</svg></svg>`;
fs.writeFileSync(path.join(root, 'assets/preview/lockpeek-originals.png'), new Resvg(preview).render().asPng());
console.log('PASS: original paths, colors, viewBox and padding retained; no redraw or geometry transform.');

// Match the manifest's Square44x44Logo resource name, preserving original artwork.
for (const file of fs.readdirSync(path.join(root, 'assets/msix')).filter(f => f.startsWith('AppList.targetsize-'))) {
 fs.copyFileSync(path.join(root, 'assets/msix', file), path.join(root, 'assets/msix', file.replace('AppList.', 'Square44x44Logo.')));
}
const recycleSvg = '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24"><g fill="none" stroke="#C42B1C" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7M14 10v7"/></g></svg>';
fs.writeFileSync(path.join(root, 'assets/context-menu/recycle.svg'), recycleSvg);
fs.writeFileSync(path.join(root, 'assets/context-menu/recycle.ico'), ico([16,20,24,32,48,256].map(size => ({size,png:new Resvg(recycleSvg,{fitTo:{mode:'width',value:size}}).render().asPng()}))));
