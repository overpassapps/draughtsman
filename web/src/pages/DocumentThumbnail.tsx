/**
 * A document's thumbnail on its home-screen card (design/atlas/document-list.md): the server's SVG shown through <img src>, never
 * inlined, so script in it stays inert. The box is reserved at the image's own size before it arrives (width and height attributes
 * plus the CSS aspect ratio), so nothing moves when it loads; a neutral skeleton fills it meanwhile, and the browser fetches the
 * image only as its card nears the screen (loading="lazy"). If the image cannot be had (the request failed, was rate limited, or the
 * file is not an image) the card shows the type's glyph instead, so a card is never a broken-image icon. The image's alt text names
 * the document; the glyph is decoration.
 */

import { useEffect, useRef, useState, type ReactNode } from 'react';
import { THUMBNAIL_HEIGHT, THUMBNAIL_WIDTH, thumbnailUrl } from '../model/thumbnailUrl.ts';

type LoadState = 'loading' | 'loaded' | 'failed';

interface DocumentThumbnailProps {
  id: string;
  title: string;
  /** The document's last-modified time: part of the address, so an edit shows the new picture. */
  updatedAt: string;
  /** What to show if the picture cannot be had. */
  fallback: ReactNode;
}

export function DocumentThumbnail({ id, title, updatedAt, fallback }: DocumentThumbnailProps) {
  const src = thumbnailUrl(id, updatedAt);
  const [loaded, setLoaded] = useState<{ src: string; state: LoadState }>({ src, state: 'loading' });
  const image = useRef<HTMLImageElement>(null);
  // A new address (the document was edited) starts over; the state of an older one never applies to it.
  const state: LoadState = loaded.src === src ? loaded.state : 'loading';

  // An image the browser already holds can be complete before React attaches its load listener, so look once it is mounted.
  useEffect(() => {
    const element = image.current;
    if (element?.complete === true && element.naturalWidth > 0) {
      setLoaded({ src, state: 'loaded' });
    }
  }, [src]);

  return (
    <div className="home-thumb" data-state={state}>
      {state !== 'failed' && (
        <img
          ref={image}
          className="home-thumb-image"
          src={src}
          alt={`Preview of ${title}`}
          width={THUMBNAIL_WIDTH}
          height={THUMBNAIL_HEIGHT}
          loading="lazy"
          decoding="async"
          onLoad={() => { setLoaded({ src, state: 'loaded' }); }}
          onError={() => { setLoaded({ src, state: 'failed' }); }}
        />
      )}
      {state === 'loading' && <span className="home-thumb-skeleton" aria-hidden="true" />}
      {state === 'failed' && <span className="home-thumb-fallback" aria-hidden="true">{fallback}</span>}
    </div>
  );
}
