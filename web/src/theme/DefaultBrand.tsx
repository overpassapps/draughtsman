/**
 * The Draughtsman mark and lockup: the product's own identity, and only ever the DEFAULT. An organisation sets its
 * own product name and logo on the Branding page (docs/api.md "## Branding"), and the moment it has set either, its
 * brand is all the shell and the sign-in pages draw: this mark never replaces it and never sits beside it, so
 * `usesDefaultBrand` (defaultBrand.ts) is the one gate every caller asks. The About page is the one place the lockup is always
 * shown, as the product's identity, in a block of its own apart from the organisation's.
 *
 * The files are static assets (web/public/brand, copied from the chosen logo and never redrawn), loaded as plain
 * images from this origin. Each has a navy-on-light and a white-and-red-on-dark version, picked from the chrome
 * theme's mode: `dark` and `light` name one, `system` lets the browser's prefers-color-scheme choose, exactly as the
 * theme's own colour tokens do (ChromeThemeCss), so the mark can never disagree with the surface it sits on.
 */
import type { ChromeThemeManifest } from './chromeTheme.ts';

interface Pair {
  light: string;
  dark: string;
}

const MARK: Pair = { light: '/brand/mark.svg', dark: '/brand/mark-on-dark.svg' };
const LOCKUP: Pair = { light: '/brand/lockup-light.svg', dark: '/brand/lockup-dark.svg' };

interface ImageProps {
  mode: ChromeThemeManifest['mode'];
  className: string;
  alt: string;
}

function ThemedImage({ pair, mode, className, alt }: ImageProps & { pair: Pair }) {
  if (mode === 'light') {
    return <img className={className} src={pair.light} alt={alt} />;
  }
  if (mode === 'dark') {
    return <img className={className} src={pair.dark} alt={alt} />;
  }
  // `system`: the chrome tokens are the dark set, switched to the light set by this same media query.
  return (
    <picture>
      <source media="(prefers-color-scheme: light)" srcSet={pair.light} />
      <img className={className} src={pair.dark} alt={alt} />
    </picture>
  );
}

/**
 * The compass mark. Decorative (`alt=""`) wherever the product name is drawn beside it, as it is in the header and on
 * the sign-in cards, so a screen reader says the name once.
 */
export function DefaultBrandMark({ mode, className, alt = '' }: { mode: ChromeThemeManifest['mode']; className: string; alt?: string }) {
  return <ThemedImage pair={MARK} mode={mode} className={className} alt={alt} />;
}

/** The lockup: the mark, "Draughtsman" and the plain-text line "by Overpass". Its text is in the picture, so it carries the alt text. */
export function DefaultBrandLockup({ mode, className }: { mode: ChromeThemeManifest['mode']; className: string }) {
  return <ThemedImage pair={LOCKUP} mode={mode} className={className} alt="Draughtsman by Overpass" />;
}
