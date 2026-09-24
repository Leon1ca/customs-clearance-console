// Expands nested scroll containers, grows same-origin iframes and hides the capture
// card before a full-page screenshot. The restore ledger is created BEFORE the first
// mutation so a later failure (or a lost command response) can still restore every
// element that was already touched. Original inline styles, priorities, container
// scroll offsets, iframe heights/max-heights and window scroll are stored.
(() => {
  if (window.__cccCaptureState) return 'already-prepared';
  const state = {
    prev: [],
    frames: [],
    widget: null,
    widgetDisplay: '',
    widgetDisplayPriority: '',
    widgetHiddenAtPrepare: false,
    scrollX: window.scrollX,
    scrollY: window.scrollY
  };
  // Publish the ledger first: every subsequent step can only add to it.
  window.__cccCaptureState = state;
  const remember = (el, properties) => {
    const entry = { el };
    for (const property of properties) {
      entry[property] = el.style.getPropertyValue(property);
      entry[property + 'Priority'] = el.style.getPropertyPriority(property);
    }
    return entry;
  };
  try {
    const all = [...document.querySelectorAll('*')].reverse();
    for (const el of all) {
      try {
        if (el.id === 'ccc-widget-host' || (el.closest && el.closest('#ccc-widget-host'))) continue;
        const s = getComputedStyle(el);
        const scrollable = (s.overflowY === 'auto' || s.overflowY === 'scroll' || s.overflow === 'auto' || s.overflow === 'scroll');
        if (!scrollable || el.scrollHeight <= el.clientHeight + 20) continue;
        state.prev.push(Object.assign(remember(el, ['height', 'max-height', 'overflow-y']), {
          scrollTop: el.scrollTop,
          scrollLeft: el.scrollLeft
        }));
        el.style.setProperty('height', el.scrollHeight + 'px', 'important');
        el.style.setProperty('max-height', 'none', 'important');
        el.style.setProperty('overflow-y', 'visible', 'important');
      } catch (error) { /* keep the ledger usable for the elements already recorded */ }
    }
  } catch (error) { /* fall through and still report what was prepared */ }

  // Same-origin frames: let the frame element grow to its document height so the
  // expanded inner content is actually painted in the parent page screenshot.
  try {
    for (const frame of document.querySelectorAll('iframe')) {
      try {
        const inner = frame.contentDocument;
        if (!inner || !inner.documentElement) continue;
        const target = Math.max(inner.documentElement.scrollHeight, inner.body ? inner.body.scrollHeight : 0);
        if (target <= frame.clientHeight) continue;
        state.frames.push(Object.assign(remember(frame, ['height', 'max-height']), { target: target }));
        frame.style.setProperty('height', target + 'px', 'important');
        frame.style.setProperty('max-height', 'none', 'important');
      } catch (error) { /* cross-origin frames are not reachable and stay as rendered */ }
    }
  } catch (error) { /* ignore */ }

  const widget = document.getElementById('ccc-widget-host');
  state.widget = widget;
  state.widgetDisplay = widget ? widget.style.getPropertyValue('display') : '';
  state.widgetDisplayPriority = widget ? widget.style.getPropertyPriority('display') : '';
  state.widgetHiddenAtPrepare = !!widget;
  if (widget) widget.style.setProperty('display', 'none', 'important');
  window.scrollTo(0, 0);
  // Test-visible marker that the real prepare path ran, used by the controlled
  // result-change scenario to mutate the result mid-capture.
  window.__cccPrepareAt = Date.now();
  return 'prepared:' + state.prev.length + ':' + state.frames.length;
})()
