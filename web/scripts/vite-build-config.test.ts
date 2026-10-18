import { describe, expect, it } from 'vitest';
import config from '../vite.config';

// PK-08: the SPA is served to a customer's browsers, and a source map would hand them the product's TypeScript.
describe('production build configuration', () => {
  it.skipIf(process.env.DRAUGHTSMAN_SOURCEMAP === 'true')('emits no source maps unless a developer asks for them', () => {
    expect(config.build?.sourcemap).toBe(false);
  });
});
