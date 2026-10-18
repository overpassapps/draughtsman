import { writeFileSync } from 'node:fs';
import { editorSessionFixture } from '../src/model/editorSessionFixture.ts';
writeFileSync(new URL('../../docs/examples/editor-session.json', import.meta.url), JSON.stringify(editorSessionFixture(), null, 2) + '\n');
