// Imported for its side effects by the DocumentViewer test files. Those tests mount the whole editor in jsdom,
// so on a starved CPU (several scripts/verify-local.sh legs at once) a test that takes ~1s idle can miss
// Vitest's 5s default timeout, and a waitFor its 1s default, with nothing actually wrong. Both are ceilings,
// not sleeps: a passing test still finishes as soon as its condition holds.
import { configure } from '@testing-library/react';
import { vi } from 'vitest';

vi.setConfig({ testTimeout: 60_000, hookTimeout: 60_000 });
configure({ asyncUtilTimeout: 15_000 });
