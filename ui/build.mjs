import { build } from 'esbuild';
import { mkdir, copyFile } from 'node:fs/promises';
const out = '../src/OssmSharp.Web.Common/wwwroot/astryx';
await mkdir(out, {recursive:true});
await build({entryPoints:['src/elements.jsx'], bundle:true, minify:true, sourcemap:false, format:'esm', target:['es2022'], outfile:`${out}/elements.js`, legalComments:'external', loader:{'.css':'text'}});
await copyFile('node_modules/@astryxdesign/core/dist/astryx.css', `${out}/core.css`);
await copyFile('node_modules/@astryxdesign/theme-neutral/dist/theme.css', `${out}/theme.css`);
