import { build } from 'esbuild';
import { copyFile, mkdir, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.dirname(fileURLToPath(import.meta.url));
const dist = path.join(root, 'dist');

await rm(dist, { recursive: true, force: true });
await mkdir(dist, { recursive: true });

await build({
  entryPoints: [path.join(root, 'src', 'terminal.js')],
  bundle: true,
  minify: true,
  sourcemap: false,
  target: ['chrome120'],
  outfile: path.join(dist, 'terminal.js'),
  legalComments: 'none',
});

await copyFile(path.join(root, 'src', 'index.html'), path.join(dist, 'index.html'));
