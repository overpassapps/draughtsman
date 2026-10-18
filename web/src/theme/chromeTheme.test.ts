import { brandHeaderLogo, brandLogo, type ChromeThemeManifest } from './chromeTheme.ts';

function manifest(overrides: Partial<ChromeThemeManifest>): ChromeThemeManifest {
  return { name: 'T', productName: 'P', logo: '/api/theme/logo', logoLight: '/api/theme/logo-light', favicon: null, mode: 'dark', ...overrides };
}

describe('brandHeaderLogo', () => {
  it('uses the compact light-on-dark mark on a dark surface when the theme provides one', () => {
    const m = manifest({ mode: 'dark', logoCompact: '/api/theme/logo-compact', logoCompactLight: '/api/theme/logo-compact-light' });
    expect(brandHeaderLogo(m)).toBe('/api/theme/logo-compact-light');
  });

  it('uses the plain compact mark on a light surface', () => {
    const m = manifest({ mode: 'light', logoCompact: '/api/theme/logo-compact', logoCompactLight: '/api/theme/logo-compact-light' });
    expect(brandHeaderLogo(m)).toBe('/api/theme/logo-compact');
  });

  it('treats system as a dark surface, like brandLogo', () => {
    const m = manifest({ mode: 'system', logoCompact: '/api/theme/logo-compact', logoCompactLight: '/api/theme/logo-compact-light' });
    expect(brandHeaderLogo(m)).toBe('/api/theme/logo-compact-light');
  });

  it('uses the plain compact mark on a dark surface when the theme names no light companion', () => {
    expect(brandHeaderLogo(manifest({ mode: 'dark', logoCompact: '/api/theme/logo-compact' }))).toBe('/api/theme/logo-compact');
  });

  it('falls back to the normal logo when the theme provides no compact mark', () => {
    expect(brandHeaderLogo(manifest({ mode: 'dark' }))).toBe('/api/theme/logo-light');
    expect(brandHeaderLogo(manifest({ mode: 'light' }))).toBe('/api/theme/logo');
    expect(brandHeaderLogo(manifest({ mode: 'dark', logoCompact: null, logoCompactLight: null }))).toBe('/api/theme/logo-light');
  });

  it('returns null when there is no logo at all', () => {
    expect(brandHeaderLogo(manifest({ logo: null, logoLight: null }))).toBeNull();
  });

  it('leaves the full-page logo untouched by a compact mark', () => {
    const m = manifest({ mode: 'dark', logoCompact: '/api/theme/logo-compact', logoCompactLight: '/api/theme/logo-compact-light' });
    expect(brandLogo(m)).toBe('/api/theme/logo-light');
  });
});
