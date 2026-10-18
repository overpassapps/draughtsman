import assert from 'node:assert/strict';
import { test } from 'node:test';
import { zipEntryNames } from '../lib/zip.mjs';

// A minimal valid zip with two stored (uncompressed) entries, built by hand so the test needs no tool.
function makeZip(entries) {
  const locals = []; const centrals = []; let offset = 0;
  for (const { name, data } of entries) {
    const nameBuf = Buffer.from(name); const dataBuf = Buffer.from(data);
    const local = Buffer.alloc(30); local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt32LE(dataBuf.length, 18); local.writeUInt32LE(dataBuf.length, 22); local.writeUInt16LE(nameBuf.length, 26);
    const central = Buffer.alloc(46); central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt32LE(dataBuf.length, 20); central.writeUInt32LE(dataBuf.length, 24); central.writeUInt16LE(nameBuf.length, 28); central.writeUInt32LE(offset, 42);
    locals.push(local, nameBuf, dataBuf); centrals.push(central, nameBuf); offset += 30 + nameBuf.length + dataBuf.length;
  }
  const dir = Buffer.concat(centrals);
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(entries.length, 8); end.writeUInt16LE(entries.length, 10); end.writeUInt32LE(dir.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, dir, end]);
}

test('reads the entry names from the central directory', () => {
  const zip = makeZip([{ name: 'README.txt', data: 'hi' }, { name: 'doctor.txt', data: 'ok' }, { name: 'data-folder/listing.txt', data: 'x' }]);
  assert.deepEqual(zipEntryNames(zip), ['README.txt', 'doctor.txt', 'data-folder/listing.txt']);
});

test('refuses something that is not a zip', () => {
  assert.throws(() => zipEntryNames(Buffer.from('this is not a zip file at all, just text')), /not a zip/);
});

test('an empty zip has no entries', () => {
  assert.deepEqual(zipEntryNames(makeZip([])), []);
});
