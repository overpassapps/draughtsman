import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'node',
    include: ['test/**/*.test.ts'],
    // The end-to-end test spawns the real bundle; give a slow CI runner room.
    testTimeout: 30_000,
  },
});
