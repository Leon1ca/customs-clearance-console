// Restores every temporary change made by capture-prepare.js, including container
// scroll offsets and grown iframe heights, and records whether styles, scroll offsets
// and the window scroll really returned to their originals. Runs in a finally block
// with its own short timeout so a failed or rejected capture never leaves the page
// modified.
(() => {
  const state = window.__cccCaptureState;
  if (!state) return 'nothing-to-restore';
  const sameStyle = (el, property, value) => (el.style.getPropertyValue(property) || '') === (value || '');
  let stylesRestored = true;
  let scrollRestored = true;
  for (const item of state.prev || []) {
    const el = item.el;
    if (!el || !el.style) continue;
    if (item.height) el.style.setProperty('height', item.height, item.heightPriority || ''); else el.style.removeProperty('height');
    if (item.maxHeight) el.style.setProperty('max-height', item.maxHeight, item.maxHeightPriority || ''); else el.style.removeProperty('max-height');
    if (item.overflowY) el.style.setProperty('overflow-y', item.overflowY, item.overflowYPriority || ''); else el.style.removeProperty('overflow-y');
    if (!sameStyle(el, 'height', item.height) || !sameStyle(el, 'max-height', item.maxHeight) || !sameStyle(el, 'overflow-y', item.overflowY))
      stylesRestored = false;
    try {
      el.scrollTop = item.scrollTop || 0; el.scrollLeft = item.scrollLeft || 0;
      if (Math.abs((el.scrollTop || 0) - (item.scrollTop || 0)) > 1 || Math.abs((el.scrollLeft || 0) - (item.scrollLeft || 0)) > 1)
        scrollRestored = false;
    } catch (error) { scrollRestored = false; }
  }
  for (const item of state.frames || []) {
    const el = item.el;
    if (!el || !el.style) continue;
    if (item.height) el.style.setProperty('height', item.height, item.heightPriority || ''); else el.style.removeProperty('height');
    if (!sameStyle(el, 'height', item.height)) stylesRestored = false;
  }
  const widget = state.widget || document.getElementById('ccc-widget-host');
  if (widget && widget.style) {
    if (state.widgetDisplay) widget.style.setProperty('display', state.widgetDisplay, state.widgetDisplayPriority || '');
    else widget.style.removeProperty('display');
  }
  try { window.scrollTo(state.scrollX || 0, state.scrollY || 0); } catch (error) { /* ignore */ }
  const windowRestored = Math.abs(window.scrollX - (state.scrollX || 0)) <= 1 && Math.abs(window.scrollY - (state.scrollY || 0)) <= 1;
  window.__cccLastCapture = {
    restored: state.prev ? state.prev.length : 0,
    framesRestored: state.frames ? state.frames.length : 0,
    widgetWasHidden: !!state.widget,
    widgetHiddenAtPrepare: !!state.widgetHiddenAtPrepare,
    stylesRestored,
    scrollRestored: scrollRestored && windowRestored,
    widgetVisibleAfterRestore: !widget || widget.style.getPropertyValue('display') !== 'none'
  };
  delete window.__cccCaptureState;
  return 'restored:' + (state.prev ? state.prev.length : 0);
})()
