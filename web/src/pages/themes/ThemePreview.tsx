/**
 * The two live previews on the Themes page: the app chrome and a sample diagram. Both are pure renders of a draft, so
 * they update on every keystroke and never touch the real shell. The chrome preview scopes the draft's custom
 * properties to one element and reuses the shell's own classes (`shell-bar`, buttons, links), so it shows what the
 * compiled stylesheet will do rather than a hand-made imitation; the diagram preview is `DiagramSvg` over the derived
 * diagram theme, on both the editor canvas and the export paper.
 */
import { useMemo } from 'react';
import { DiagramSvg } from '../../canvas/DiagramSvg.tsx';
import { SAMPLE_DIAGRAM, SAMPLE_DIAGRAM_SIZE } from '../../theme/sampleDiagram.ts';
import { applyDiagramOverride, chromeStyleVars, type OrgChrome, type OrgDiagram } from '../../theme/orgTheme.ts';

interface ChromePreviewProps {
  chrome: OrgChrome;
  set: 'dark' | 'light';
  productName: string;
  /** The brand logo for this set's surface, or null to show the product name alone. */
  logo: string | null;
}

export function ChromePreview({ chrome, set, productName, logo }: ChromePreviewProps) {
  const vars = useMemo(() => chromeStyleVars(chrome, set), [chrome, set]);
  return (
    <div className="theme-chrome-preview" style={vars} data-testid="chrome-preview" data-set={set} role="img" aria-label={`Preview of the app chrome, ${set} colours`}>
      <div className="shell-bar">
        <span className="shell-brand">
          {logo !== null && <img className="shell-brand-logo" src={logo} alt="" />}
          <span>{productName}</span>
        </span>
        <span className="shell-nav">
          <span>Documents</span>
          <span>Users</span>
          <span>Themes</span>
        </span>
        <span className="theme-preview-user">Sign out</span>
      </div>
      <div className="theme-chrome-preview-body">
        <p className="theme-preview-heading">Order service</p>
        <p>Body text reads on the surface. <span className="theme-preview-muted">Muted text carries the small print.</span></p>
        <p>
          <span className="theme-preview-link">An accent link</span> · <span className="theme-preview-danger">A danger message</span>
        </p>
        <div className="theme-preview-actions">
          <span className="theme-preview-primary">Save</span>
          <span className="theme-preview-secondary">Cancel</span>
          <span className="theme-preview-input">Search documents</span>
        </div>
        <p className="theme-preview-mono">n_api → n_db</p>
      </div>
    </div>
  );
}

interface DiagramPreviewProps {
  diagram: OrgDiagram;
  name: string;
  surface: 'canvas' | 'export';
  /** Keeps the marker and grid ids of two previews on one page from colliding. */
  idPrefix: string;
}

export function DiagramPreview({ diagram, name, surface, idPrefix }: DiagramPreviewProps) {
  const theme = useMemo(() => applyDiagramOverride(diagram, name), [diagram, name]);
  return (
    <div className="theme-diagram-preview" role="img" aria-label={`Preview of a sample diagram on the ${surface === 'canvas' ? 'editor canvas' : 'export paper'}`}>
      <DiagramSvg
        document={SAMPLE_DIAGRAM}
        theme={theme}
        viewport={{ x: 0, y: 0, zoom: 1 }}
        size={SAMPLE_DIAGRAM_SIZE}
        surface={surface}
        idPrefix={idPrefix}
      />
    </div>
  );
}
