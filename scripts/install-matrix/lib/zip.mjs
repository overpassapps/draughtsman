// The entry names of a zip, read from its central directory with no dependency (the support bundle is checked without unzip, which the
// runtime image does not have). Not a general reader: names only, no decompression, no ZIP64 beyond the sentinel check. Pure; test/zip.test.mjs.

const EOCD = 0x06054b50;
const CENTRAL = 0x02014b50;

/** @param {Buffer} buf @returns {string[]} the names of every entry, in directory order. Throws on something that is not a zip. */
export function zipEntryNames(buf) {
  let end = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 22 - 65535); i--) {
    if (buf.readUInt32LE(i) === EOCD) { end = i; break; }
  }
  if (end < 0) throw new Error('not a zip file: no end-of-central-directory record');
  const count = buf.readUInt16LE(end + 10);
  let offset = buf.readUInt32LE(end + 16);
  if (count === 0xffff || offset === 0xffffffff) throw new Error('ZIP64 archives are not supported by this reader');
  const names = [];
  for (let n = 0; n < count; n++) {
    if (offset + 46 > buf.length || buf.readUInt32LE(offset) !== CENTRAL) throw new Error('corrupt zip central directory');
    const nameLength = buf.readUInt16LE(offset + 28);
    const extraLength = buf.readUInt16LE(offset + 30);
    const commentLength = buf.readUInt16LE(offset + 32);
    names.push(buf.toString('utf8', offset + 46, offset + 46 + nameLength));
    offset += 46 + nameLength + extraLength + commentLength;
  }
  return names;
}
