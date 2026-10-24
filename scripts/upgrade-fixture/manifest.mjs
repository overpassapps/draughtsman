#!/usr/bin/env node
// Writes the manifests of an upgrade fixture (make-upgrade-fixture.sh calls it; tests/upgrade-fixtures/README.md documents
// the result). `provider` summarises one provider's data folder (migrations, per-table row counts, file checksums);
// `fixture` writes the top-level manifest.json that names the build and carries the throwaway secrets a test needs.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, statSync, writeFileSync, existsSync } from 'node:fs';
import { join, relative } from 'node:path';
import { gunzipSync } from 'node:zlib';

const [command, ...rest] = process.argv.slice(2);
const o = {};
for (let i = 0; i < rest.length; i += 2) o[rest[i].replace(/^--/, '')] = rest[i + 1];

const sha = (buf) => createHash('sha256').update(buf).digest('hex');

function walk(dir, base = dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = join(dir, e.name);
    return e.isDirectory() ? walk(p, base) : [{ path: relative(base, p), size: statSync(p).size, sha256: sha(readFileSync(p)) }];
  });
}

if (command === 'provider') {
  const info = { provider: o.provider, files: walk(o.data).sort((a, b) => a.path.localeCompare(b.path)) };
  if (o.provider === 'sqlite') {
    const db = join(o.data, 'data.db');
    const q = (sql) => execFileSync('sqlite3', [db, sql], { encoding: 'utf8' }).trim();
    info.migrations = q('select MigrationId from __EFMigrationsHistory order by MigrationId').split('\n').filter(Boolean);
    info.rowCounts = {};
    for (const t of q("select name from sqlite_master where type='table' and name not like 'sqlite_%' order by name").split('\n').filter(Boolean)) {
      info.rowCounts[t] = Number(q(`select count(*) from "${t}"`));
    }
    info.integrityCheck = q('pragma integrity_check');
  } else {
    const dump = gunzipSync(readFileSync(join(o.pkg, 'dump.sql.gz'))).toString('utf8');
    info.migrations = [...dump.matchAll(/INSERT INTO public\."__EFMigrationsHistory" VALUES \('([^']+)'/g)].map((m) => m[1]).sort();
    info.rowCounts = {};
    for (const m of dump.matchAll(/^INSERT INTO public\.("?)([A-Za-z_]+)\1 /gm)) info.rowCounts[m[2]] = (info.rowCounts[m[2]] ?? 0) + 1;
    info.dumpSha256 = sha(readFileSync(join(o.pkg, 'dump.sql.gz')));
  }
  info.dataFolderSha256 = sha(readFileSync(join(o.pkg, 'data-folder.tar.gz')));
  writeFileSync(join(o.pkg, 'provider.json'), `${JSON.stringify(info, null, 2)}\n`);
} else if (command === 'fixture') {
  const providers = {};
  for (const name of ['sqlite', 'postgres']) {
    const p = join(o.dir, name, 'provider.json');
    if (existsSync(p)) providers[name] = JSON.parse(readFileSync(p, 'utf8'));
  }
  const safe = (cmd, args) => { try { return execFileSync(cmd, args, { encoding: 'utf8' }).trim(); } catch { return null; } };
  const version = safe('git', ['-C', o.source, 'show', `${o.commit}:Directory.Build.props`])?.match(/<Version>([^<]+)<\/Version>/)?.[1]
    ?? (existsSync(join(o.source, 'Directory.Build.props')) ? readFileSync(join(o.source, 'Directory.Build.props'), 'utf8').match(/<Version>([^<]+)<\/Version>/)?.[1] : null);
  const identity = safe('git', ['-C', o.source, 'show', `${o.commit}:src/Draughtsman.Server/KeyRing/DataProtectionIdentity.cs`]);
  const dataProtectionApplicationName = identity?.match(/ApplicationName\s*=\s*"([^"]+)"/)?.[1] ?? null;
  const manifest = {
    formatVersion: 1,
    label: o.label,
    note: 'Throwaway test data only. Every password, token and key in this fixture was generated for it and signs or protects nothing real.',
    createdAt: new Date().toISOString(),
    build: {
      commit: o.commit,
      subject: safe('git', ['-C', o.source, 'log', '-1', '--format=%s', o.commit]),
      committedAt: safe('git', ['-C', o.source, 'log', '-1', '--format=%cI', o.commit]),
      version,
      migrations: Object.values(providers)[0]?.migrations ?? [],
    },
    madeWith: { dotnet: safe('dotnet', ['--version']), node: process.version, os: `${process.platform} ${process.arch}` },
    // A build without a pinned Data Protection application name binds its payloads (the stored AI key, the SMTP password) to the
    // content root it ran under. A build that pins one (src/Draughtsman.Server/KeyRing/DataProtectionIdentity.cs) binds them to that
    // name instead, and records it here; the content root is then only where the program ran from.
    contentRoot: o['content-root'],
    ...(dataProtectionApplicationName ? { dataProtectionApplicationName } : {}),
    keyRing: o['key-ring'] === 'key'
      ? { protection: 'key', keyEnvironmentVariable: 'DRAUGHTSMAN_KEY_ENCRYPTION_KEY', keyBase64: o.kek, note: 'throwaway key-encryption key' }
      : { protection: 'none' },
    providers,
  };
  writeFileSync(join(o.dir, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
} else {
  console.error('usage: manifest.mjs provider|fixture ...');
  process.exit(2);
}
