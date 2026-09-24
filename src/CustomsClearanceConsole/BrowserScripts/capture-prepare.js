// Expands nested scroll containers, grows same-origin iframes and hides the capture
// card before a full-page screenshot. Original inline styles, container scroll
// offsets, iframe heights and window scroll are stored so the capture path can
// always restore the page, including on failure.
(() => {
  if (window.__cccCaptureState) return 'already-prepared';
  const prev = [];
  const all = [...document.querySelectorAll('*')].reverse();
  for (const el of all) {
    if (el.id === 'ccc-widget-host' || (el.closest && el.closest('#ccc-widget-host'))) continue;
    const s = getComputedStyle(el);
    const scrollable = (s.overflowY === 'auto' || s.overflowY === 'scroll' || s.overflow === 'auto' || s.overflow === 'scroll');
    if (!scrollable || el.scrollHeight <= el.clientHeight + 20) continue;
    prev.push({
      el,
      height: el.style.getPropertyValue('height'), heightPriority: el.style.getPropertyPriority('height'),
      maxHeight: el.style.getPropertyValue('max-height'), maxHeightPriority: el.style.getPropertyPriority('max-height'),
      overflowY: el.style.getPropertyValue('overflow-y'), overflowYPriority: el.style.getPropertyPriority('overflow-y'),
      scrollTop: el.scrollTop, scrollLeft: el.scrollLeft
    });
    el.style.setProperty('height', el.scrollHeight + 'px', 'important');
    el.style.setProperty('max-height', 'none', 'important');
    el.style.setProperty('overflow-y', 'visible', 'important');
  }

  // Same-origin frames: let the frame element grow to its document height so the
  // expanded inner content is actually painted in the parent page screenshot.
  const frames = [];
  for (const frame of document.querySelectorAll('iframe')) {
    try {
      const inner = frame.contentDocument;
      if (!inner || !inner.documentElement) continue;
      const target = Math.max(inner.documentElement.scrollHeight, inner.body ? inner.body.scrollHeight : 0);
      if (target <= frame.clientHeight) continue;
      frames.push({ el: frame, height: frame.style.getPropertyValue('height'), heightPriority: frame.style.getPropertyPriority('height') });
      frame.style.setProperty('height', target + 'px', 'important');
    } catch (error) { /* cross-origin frames are not reachable and stay as rendered */ }
  }

  const widget = document.getElementById('ccc-widget-host');
  window.__cccCaptureState = {
    prev,
    frames,
    widget,
    widgetDisplay: widget ? widget.style.getPropertyValue('display') : '',
    widgetDisplayPriority: widget ? widget.style.getPropertyPriority('display') : '',
    widgetHiddenAtPrepare: !!widget,
    scrollX: window.scrollX,
    scrollY: window.scrollY
  };
  if (widget) widget.style.setProperty('display', 'none', 'important');
  window.scrollTo(0, 0);
  // Test-visible marker that the real prepare path ran, used by the controlled
  // result-change scenario to mutate the result mid-capture.
  window.__cccPrepareAt = Date.now();
  return 'prepared:' + prev.length + ':' + frames.length;
})()
