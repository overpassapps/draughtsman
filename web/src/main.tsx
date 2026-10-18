import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App.tsx';
import './fonts.css';
import './generated/tokens.css';
import './app.css';
import { applyChromeTheme } from './theme/chromeTheme.ts';

// The server's configured chrome theme overrides the build-time tokens above as soon as it arrives.
void applyChromeTheme();

const root = document.getElementById('root');
if (!root) {
  throw new Error('index.html has no #root element');
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
