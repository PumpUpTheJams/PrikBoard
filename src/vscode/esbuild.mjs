// Bundles the extension, including the API client and types it shares with the web app (src/web/src/api).
import { build, context } from 'esbuild'

const production = process.argv.includes('--production')
const watch = process.argv.includes('--watch')

const options = {
  entryPoints: ['src/extension.ts'],
  outfile: 'dist/extension.js',
  bundle: true,
  platform: 'node',
  format: 'cjs',
  target: 'node20',
  external: ['vscode'],
  sourcemap: !production,
  minify: production,
  logLevel: 'info',
}

if (watch) {
  await (await context(options)).watch()
} else {
  await build(options)
}
