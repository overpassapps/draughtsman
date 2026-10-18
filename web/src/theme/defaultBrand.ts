/** When the Draughtsman mark may stand in for an organisation's brand; the components that draw it are in DefaultBrand.tsx. */
import { brandLogo, type ChromeThemeManifest } from './chromeTheme.ts';

/** The product name the shipped theme carries; any other name is an organisation's own. */
export const DEFAULT_PRODUCT_NAME = 'Draughtsman';

/**
 * True when the organisation has set no logo and no product name of its own, so the Draughtsman mark may stand in.
 * False until the theme manifest has loaded (or when it could not be read), so a signed-in organisation never sees
 * the product's mark flash up before its own logo arrives.
 */
export function usesDefaultBrand(brand: ChromeThemeManifest | undefined): boolean {
  return brand !== undefined && brandLogo(brand) === null && brand.productName === DEFAULT_PRODUCT_NAME;
}
