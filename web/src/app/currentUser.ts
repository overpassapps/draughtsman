/**
 * The signed-in user, for the few places deep in the tree that only need their role (an admin-only line in the AI
 * panel). AppShell provides it; it is undefined outside the shell (a test, a preview), where nothing admin-only shows.
 */
import { createContext, useContext } from 'react';
import type { AuthUser } from '../model/authClient.ts';

export const CurrentUserContext = createContext<AuthUser | undefined>(undefined);

export function useCurrentUser(): AuthUser | undefined {
  return useContext(CurrentUserContext);
}
