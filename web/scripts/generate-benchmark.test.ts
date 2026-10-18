import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { BENCHMARK_PATH, generate, serialise } from './generate-benchmark.ts';

describe('generate-benchmark', () => {
  it('reproduces the committed fixture byte for byte', () => {
    expect(readFileSync(BENCHMARK_PATH, 'utf8')).toBe(serialise(generate().document));
  });
});
