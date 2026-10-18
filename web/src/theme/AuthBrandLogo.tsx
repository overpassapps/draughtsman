/**
 * The logo at the top of the sign-in, first-run and password-reset cards: the organisation's own when it has set one,
 * else the Draughtsman mark when it has set nothing at all (defaultBrand.ts), else nothing. Decorative either way, because
 * the product name is drawn beside it.
 */
import { brandLogo, type ChromeThemeManifest } from './chromeTheme.ts';
import { DefaultBrandMark } from './DefaultBrand.tsx';
import { usesDefaultBrand } from './defaultBrand.ts';

export function AuthBrandLogo({ brand }: { brand: ChromeThemeManifest | undefined }) {
  if (brand === undefined) {
    return null;
  }
  if (usesDefaultBrand(brand)) {
    return <DefaultBrandMark mode={brand.mode} className="auth-logo" />;
  }
  const logo = brandLogo(brand);
  return logo === null ? null : <img className="auth-logo" src={logo} alt="" />;
}
