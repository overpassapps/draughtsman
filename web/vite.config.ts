import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';
import { searchForWorkspaceRoot, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import { packagesOf, writeReport } from './scripts/bundled-packages.ts';

// The server's default port is 5180 (src/Draughtsman.Server/Configuration/DraughtsmanConfig.cs).
// Override with DRAUGHTSMAN_API when running the server elsewhere.
const apiTarget = process.env.DRAUGHTSMAN_API ?? 'http://localhost:5180';

// After `vite build`, records which npm packages the bundles contain, for THIRD-PARTY-NOTICES.txt
// (scripts/generate-notices.mjs reads web/.build-report/bundled-packages.json). The app and its worker are separate Rollup
// builds (each gets its own plugin instance), so the module ids are collected in one shared set.
const bundledIds = new Set<string>();
function reportBundledPackages(): Plugin {
  return {
    name: 'draughtsman-bundled-packages-report',
    apply: 'build',
    generateBundle(_options, bundle) {
      for (const output of Object.values(bundle)) {
        if (output.type === 'chunk') for (const id of Object.keys(output.modules)) bundledIds.add(id);
      }
      writeReport(fileURLToPath(new URL('./.build-report/bundled-packages.json', import.meta.url)), packagesOf(bundledIds));
    },
  };
}

export default defineConfig({
  plugins: [react(), reportBundledPackages()],
  worker: { plugins: () => [reportBundledPackages()] },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: false },
      '/ws': { target: apiTarget, ws: true },
    },
    // The SPA imports docs/schema and docs/examples from the repository root, and the generic icon library from
    // src/Draughtsman.Core/Schemas (the same file the C# renderer embeds), one or two levels above web/; nothing else
    // outside web/ (in particular the instance data folder) is served.
    fs: { allow: [searchForWorkspaceRoot(process.cwd()), '../docs', '../src/Draughtsman.Core/Schemas'] },
  },
  build: {
    outDir: 'dist',
    // Off for anything that ships: a source map embeds the original TypeScript of a closed-source product and adds 3.4 MB
    // to the bundle customers download (PK-08). DRAUGHTSMAN_SOURCEMAP=true builds them for debugging a production bundle.
    sourcemap: process.env.DRAUGHTSMAN_SOURCEMAP === 'true',
  },
  test: {
    // Component tests opt into jsdom with a `// @vitest-environment jsdom` docblock.
    environment: 'node',
    globals: true,
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx', 'scripts/**/*.test.ts'],
    // Vitest's 5 s default is a wall-clock limit on CPU-bound tests (the 2,000-node flight renders ~500 frames), so it failed whole files
    // on a Mac running other builds at load average 100+ while the same tests pass alone in a few seconds. It is a hang guard here, not a
    // performance assertion: the timed ones scale their own limits (src/testing/loadScale.ts).
    setupFiles: ['src/testing/setup.ts'],
    testTimeout: 60_000,
    hookTimeout: 60_000,
  },
});
