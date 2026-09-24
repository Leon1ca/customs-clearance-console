// Restores every temporary change made by capture-prepare.js, including container
// scroll offsets and grown iframe heights. Runs in a finally block with its own
// short timeout so a failed or rejected capture never leaves the page modified.
(() => {
  const state = window.__cccCaptureState;
  if (!state) return 'nothing-to-restore';
  for (const item of state.prev || []) {
    const el = item.el;
    if (!el || !el.style) continue;
    if (item.height) el.style.setProperty('height', item.height, item.heightPriority || ''); else el.style.removeProperty('height');
    if (item.maxHeight) el.style.setProperty('max-height', item.maxHeight, item.maxHeightPriority || ''); else el.style.removeProperty('max-height');
    if (item.overflowY) el.style.setProperty('overflow-y', item.overflowY, item.overflowYPriority || ''); else el.style.removeProperty('overflow-y');
    try { el.scrollTop = item.scrollTop || 0; el.scrollLeft = item.scrollLeft || 0; } catch (error) { /* ignore */ }
  }
  for (const item of state.frames || []) {
    const el = item.el;
    if (!el || !el.style) continue;
    if (item.height) el.style.setProperty('height', item.height, item.heightPriority || ''); else el.style.removeProperty('height');
  }
  const widget = state.widget || document.getElementById('ccc-widget-host');
  if (widget && widget.style) {
    if (state.widgetDisplay) widget.style.setProperty('display', state.widgetDisplay, state.widgetDisplayPriority || '');
    else widget.style.removeProperty('display');
  }
  try { window.scrollTo(state.scrollX || 0, state.scrollY || 0); } catch (error) { /* ignore */ }
  window.__cccLastCapture = {
    restored: state.prev ? state.prev.length : 0,
    framesRestored: state.frames ? state.frames.length : 0,
    widgetWasHidden: !!state.widget
  };
  delete window.__cccCaptureState;
  return 'restored:' + (state.prev ? state.prev.length : 0);
})()
