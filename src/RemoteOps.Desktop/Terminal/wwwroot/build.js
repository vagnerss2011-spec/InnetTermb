// Build script: bundles xterm.js + FitAddon into local browser files.
// Run: npm ci && node build.js (or npm run build after npm ci)
// Output: js/terminal.bundle.js + css/xterm.css

const esbuild = require('esbuild');
const fs = require('fs');
const path = require('path');

fs.mkdirSync('js', { recursive: true });
fs.mkdirSync('css', { recursive: true });

esbuild.buildSync({
  stdin: {
    contents: `
      const { Terminal } = require('xterm');
      const { FitAddon } = require('xterm-addon-fit');
      window.Terminal = Terminal;
      window.FitAddon = FitAddon;
    `,
    resolveDir: __dirname,
  },
  bundle: true,
  outfile: 'js/terminal.bundle.js',
  platform: 'browser',
  format: 'iife',
  minify: false,
  logLevel: 'info',
});

const xtermCss = path.join(__dirname, 'node_modules', 'xterm', 'css', 'xterm.css');
fs.copyFileSync(xtermCss, path.join(__dirname, 'css', 'xterm.css'));

console.log('Terminal UI build complete.');
