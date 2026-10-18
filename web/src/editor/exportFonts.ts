/**
 * Puts the bundled fonts inside an SVG that is about to be rasterised (PK-09). A browser draws an SVG loaded as an
 * `<img>` in isolation: the page's own @font-face files (fonts.css) are not reachable from it, only fonts installed on the
 * machine, so a PNG exported from a machine without Inter installed would still fall back to a system font and its
 * letters would not match the editor's. Embedding the same woff2 files as data URIs inside the SVG makes the PNG draw what
 * the editor showed. Only the faces the drawing uses are added: the latin file(s) of a family it names, the latin-ext
 * file only when its text needs it. An SVG exported for download is left as it is, a small file that names its font.
 * Any failure (a fetch that does not succeed) returns the SVG untouched: a PNG in the fallback font beats no PNG.
 */
import interLatin from '@fontsource-variable/inter/files/inter-latin-wght-normal.woff2?url';
import interLatinExt from '@fontsource-variable/inter/files/inter-latin-ext-wght-normal.woff2?url';
import monoLatin from '@fontsource-variable/jetbrains-mono/files/jetbrains-mono-latin-wght-normal.woff2?url';
import monoLatinExt from '@fontsource-variable/jetbrains-mono/files/jetbrains-mono-latin-ext-wght-normal.woff2?url';

interface Face {
  family: string;
  weight: string;
  url: string;
  /** The same ranges fonts.css gives the face, so the browser picks identically inside the SVG. */
  range: string;
}

const LATIN_RANGE = 'U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC,U+0304,U+0308,U+0329,U+2000-206F,U+20AC,U+2122,U+2191,U+2193,U+2212,U+2215,U+FEFF,U+FFFD';
const LATIN_EXT_RANGE = 'U+0100-02BA,U+02BD-02C5,U+02C7-02CC,U+02CE-02D7,U+02DD-02FF,U+0304,U+0308,U+0329,U+1D00-1DBF,U+1E00-1E9F,U+1EF2-1EFF,U+2020,U+20A0-20AB,U+20AD-20C0,U+2113,U+2C60-2C7F,U+A720-A7FF';

const INTER: readonly Face[] = [
  { family: 'Inter', weight: '100 900', url: interLatin, range: LATIN_RANGE },
  { family: 'Inter', weight: '100 900', url: interLatinExt, range: LATIN_EXT_RANGE },
];
const MONO: readonly Face[] = [
  { family: 'JetBrains Mono', weight: '100 800', url: monoLatin, range: LATIN_RANGE },
  { family: 'JetBrains Mono', weight: '100 800', url: monoLatinExt, range: LATIN_EXT_RANGE },
];

const NEEDS_LATIN_EXT = /[Ā-ʺʽ-˅ˇ-ˌˎ-˗˝-˿ᴀ-ᶿḀ-ẟỲ-ỿ†₠-₫₭-⃀ℓⱠ-Ɀ꜠-ꟿ]/;

/** The faces `svg` needs: a family only if it is named, its latin-ext file only if some text in the drawing is in that range. */
export function facesFor(svg: string): Face[] {
  const text = svg.replace(/<[^>]*>/g, '');
  const extra = NEEDS_LATIN_EXT.test(text);
  const pick = (faces: readonly Face[]): Face[] => faces.filter((_face, index) => index === 0 || extra);
  return [...(svg.includes('Inter') ? pick(INTER) : []), ...(svg.includes('JetBrains Mono') ? pick(MONO) : [])];
}

function toBase64(bytes: Uint8Array): string {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}

/** `svg` with an `@font-face` per needed face (data URI) in a leading `<style>`, or `svg` unchanged if there is nothing to add or a face cannot be read. */
export async function withEmbeddedFonts(svg: string, fetchImpl: typeof fetch = fetch): Promise<string> {
  const faces = facesFor(svg);
  if (faces.length === 0) {
    return svg;
  }
  try {
    const rules = await Promise.all(
      faces.map(async (face) => {
        const response = await fetchImpl(face.url);
        if (!response.ok) {
          throw new Error(`font ${face.url} answered ${String(response.status)}`);
        }
        const data = toBase64(new Uint8Array(await response.arrayBuffer()));
        return `@font-face{font-family:'${face.family}';font-style:normal;font-weight:${face.weight};src:url(data:font/woff2;base64,${data}) format('woff2');unicode-range:${face.range}}`;
      }),
    );
    const open = /<svg\b[^>]*>/.exec(svg);
    if (open === null) {
      return svg;
    }
    const at = open.index + open[0].length;
    return `${svg.slice(0, at)}<style>${rules.join('')}</style>${svg.slice(at)}`;
  } catch {
    return svg;
  }
}
