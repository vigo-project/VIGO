// Generates the application icon from the VIGO logo (Claude Design "VIGO Logo"): two branches converging on a merge
// commit. Writes src/Vigo.App/Assets/Vigo.ico (+ Vigo.png), and the web assets when a second argument is given.
// Usage: cd tools; npm i --no-save --no-package-lock sharp; node gen-app-icon.mjs [path-to-vigo-web]
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

// Dark tile (design "Sombre"). `small` = the 16-24 px variant: thicker strokes, larger dots.
function tile({ small }) {
  const w = small ? 7 : 5.5;
  const dot = small ? 8 : 6.5;
  const merge = small
    ? '<circle cx="32" cy="49" r="9" fill="#f3f3f5"/>'
    : '<circle cx="32" cy="49" r="8" fill="#1f1f22" stroke="#f3f3f5" stroke-width="5"/>';
  const scale = small ? 1.12 : 0.92;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64" fill="none" stroke-linecap="round">
<rect x="0.5" y="0.5" width="63" height="63" rx="${small ? 12 : 14}" fill="#1f1f22" stroke="#3a3a3e"/>
<g transform="translate(32 32.75) scale(${scale}) translate(-32 -32.75)">
<path d="M15 15 L32 49" stroke="#60cdff" stroke-width="${w}"/><path d="M49 15 L32 49" stroke="#a371f7" stroke-width="${w}"/>
<circle cx="15" cy="15" r="${dot}" fill="#60cdff"/><circle cx="49" cy="15" r="${dot}" fill="#a371f7"/>${merge}
</g></svg>
`;
}

const render = (size) => sharp(Buffer.from(tile({ small: size <= 24 })), { density: Math.max(72, (size / 64) * 72 * 4) })
  .resize(size, size).png({ compressionLevel: 9 }).toBuffer();

// ICO with PNG-compressed entries (Vista+).
function ico(images) {
  const head = Buffer.alloc(6);
  head.writeUInt16LE(1, 2);
  head.writeUInt16LE(images.length, 4);
  let offset = 6 + images.length * 16;
  const entries = images.map(({ size, data }) => {
    const e = Buffer.alloc(16);
    e[0] = size >= 256 ? 0 : size;
    e[1] = size >= 256 ? 0 : size;
    e.writeUInt16LE(1, 4);
    e.writeUInt16LE(32, 6);
    e.writeUInt32LE(data.length, 8);
    e.writeUInt32LE(offset, 12);
    offset += data.length;
    return e;
  });
  return Buffer.concat([head, ...entries, ...images.map((i) => i.data)]);
}

const sizes = [16, 24, 32, 48, 64, 128, 256];
const images = await Promise.all(sizes.map(async (size) => ({ size, data: await render(size) })));
const icon = ico(images);

const assets = join(root, 'src/Vigo.App/Assets');
await mkdir(assets, { recursive: true });
await writeFile(join(assets, 'Vigo.ico'), icon);
await writeFile(join(assets, 'Vigo.png'), images.at(-1).data);
await writeFile(join(root, 'tools/icon/vigo-logo.svg'), tile({ small: false }));

const web = process.argv[2];
if (web) {
  await writeFile(join(web, 'app/favicon.ico'), ico(images.filter((i) => i.size <= 64)));
  await writeFile(join(web, 'app/icon.svg'), tile({ small: false }));
  await writeFile(join(web, 'app/apple-icon.png'), await render(180));
  await writeFile(join(web, 'public/logo.svg'), tile({ small: false }));
}
console.log('icon written', icon.length, 'bytes');
