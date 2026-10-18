/** The chrome theme's product name and logos for a page that renders its own brand mark (the login, first-run and shell bars). */
import { useEffect, useState } from 'react';
import { BRANDING_CHANGED_EVENT, fetchChromeThemeManifest, type ChromeThemeManifest } from './chromeTheme.ts';

export function useChromeBrand(): ChromeThemeManifest | undefined {
  const [manifest, setManifest] = useState<ChromeThemeManifest>();

  useEffect(() => {
    let cancelled = false;
    const load = (): void => {
      void fetchChromeThemeManifest().then((result) => {
        if (!cancelled) {
          setManifest(result);
        }
      });
    };
    load();
    // The branding admin page fires this after a save, so the shell's brand updates without a reload.
    window.addEventListener(BRANDING_CHANGED_EVENT, load);
    return () => {
      cancelled = true;
      window.removeEventListener(BRANDING_CHANGED_EVENT, load);
    };
  }, []);

  return manifest;
}
