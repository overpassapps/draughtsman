/** The admin pages, in the order the Admin menu (and the compact Menu) lists them. API tokens is not here: every signed-in person has it. */
export const ADMIN_LINKS: readonly { href: string; label: string }[] = [
  { href: '/admin/users', label: 'Users' },
  { href: '/admin/branding', label: 'Branding' },
  { href: '/admin/themes', label: 'Themes' },
  { href: '/admin/ai', label: 'AI settings' },
  { href: '/admin/email', label: 'Email' },
  { href: '/admin/security', label: 'Security' },
  { href: '/admin/status', label: 'Status' },
  { href: '/admin/connection', label: 'Connection check' },
];
