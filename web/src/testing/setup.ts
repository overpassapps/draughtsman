// Vitest setup for every test file (vite.config.ts `setupFiles`). A Testing Library `waitFor`/`findBy*` gives up after 1 s by default,
// a wall-clock ceiling that a busy Mac (other builds at load average 100+) overruns with nothing wrong. It is a ceiling, not a sleep: a
// passing wait returns as soon as its condition holds, and a condition that never holds still fails, after 15 s. src/pages/loadTolerance.ts
// does the same for the DocumentViewer files before this existed and is kept as it is.
import { configure } from '@testing-library/react';

configure({ asyncUtilTimeout: 15_000 });
