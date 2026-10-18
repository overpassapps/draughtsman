/**
 * Which modifier names to show (QA P6): `Cmd` and `Option` on a Mac, `Ctrl` and `Alt` elsewhere. The registry keeps one
 * spelling of every shortcut, `Cmd/Ctrl+Z`, because the keyboard accepts either modifier on every platform
 * (shortcuts.ts); only what is shown to the person changes, here, at the few places a shortcut is drawn.
 */

interface NavigatorLike {
  platform?: string;
  userAgent?: string;
  userAgentData?: { platform?: string };
}

/** Whether this is an Apple platform, from the client hints where the browser has them and `navigator.platform` otherwise. */
export function isApplePlatform(nav: NavigatorLike | undefined = typeof navigator === 'undefined' ? undefined : navigator): boolean {
  const platform = nav?.userAgentData?.platform ?? nav?.platform;
  return /mac|iphone|ipad|ipod/i.test(platform !== undefined && platform !== '' ? platform : (nav?.userAgent ?? ''));
}

/** A registry shortcut label as this platform names it: `Cmd/Ctrl+Alt+]` is `Cmd+Option+]` on a Mac and `Ctrl+Alt+]` elsewhere. */
export function platformShortcut(label: string, apple: boolean = isApplePlatform()): string {
  const modifier = apple ? label.replace(/Cmd\/Ctrl/g, 'Cmd').replace(/\bAlt\b/g, 'Option') : label.replace(/Cmd\/Ctrl/g, 'Ctrl');
  return modifier;
}

/** The name of the main modifier on this platform: `Cmd` or `Ctrl`. */
export function modifierName(apple: boolean = isApplePlatform()): string {
  return apple ? 'Cmd' : 'Ctrl';
}
