// Strip mode for the comic reader: every page edge to edge at the window's height, scrolled continuously in
// reading direction, as in the desktop viewer. The browser scrolls natively (touch, scrollbar, touchpad); the
// mouse wheel and keys scroll with a short eased animation. The position is kept as a page and a share of
// it, so pages whose size arrives later never move the page being read. Pages load as they come near.

const PORTRAIT = 0.7;
const ANIMATION_MS = 120;
const WHEEL_NOTCH = 100;       // pixels of one wheel notch in most browsers
const WHEEL_SHARE = 0.25;      // share of the window one notch scrolls (as on the desktop)
const DRAG_MOVE = 10;

/**
 * Opens a strip in `stage`. `options`: `count`, `rtl`, `start` (first page shown), `src(index)` (page URL) and
 * `onMove({ current, atEnd })`, called after the position changed. Returns the strip's controls.
 */
export function createStrip(stage, options) {
  const { count, src, onMove } = options;
  let rtl = options.rtl;
  const lengths = new Array(count).fill(NaN);   // width / height of each page, NaN = not known yet
  let known = 0;
  let knownSum = 0;
  const offsets = new Float64Array(count + 1);   // prefix sums in units of the strip height
  let anchor = { index: Math.min(Math.max(0, options.start), Math.max(0, count - 1)), fraction: 0 };
  let animation = null;
  let relayoutQueued = false;
  let moveQueued = false;
  let drag = null;
  let destroyed = false;

  const scroller = document.createElement("div");
  scroller.className = "strip";
  const row = document.createElement("div");
  row.className = "strip-row";
  scroller.append(row);

  const images = [];
  for (let i = 0; i < count; i++) {
    const image = document.createElement("img");
    image.alt = "";
    image.draggable = false;
    image.decoding = "async";
    image.dataset.index = String(i);
    image.addEventListener("load", () => {
      if (image.naturalWidth > 0 && image.naturalHeight > 0) {
        setLength(i, image.naturalWidth / image.naturalHeight);
      }
    });
    images.push(image);
  }

  row.append(...images);
  stage.append(scroller);

  const loader = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (entry.isIntersecting) {
        const image = entry.target;
        if (!image.src) {
          image.src = src(Number(image.dataset.index));
        }

        loader.unobserve(image);
      }
    }
  }, { root: scroller, rootMargin: "0px 150% 0px 150%" });
  images.forEach((image) => loader.observe(image));

  const resize = new ResizeObserver(() => queueRelayout());
  resize.observe(scroller);

  scroller.addEventListener("scroll", onScroll, { passive: true });
  scroller.addEventListener("wheel", onWheel, { passive: false });
  scroller.addEventListener("pointerdown", onPointerDown);
  scroller.addEventListener("pointermove", onPointerMove);
  scroller.addEventListener("pointerup", endDrag);
  scroller.addEventListener("pointercancel", endDrag);
  scroller.addEventListener("touchstart", stopAnimation, { passive: true });

  setDirection(rtl);

  // ---------- Geometry ----------

  function placeholder() {
    return known > 0 ? knownSum / known : PORTRAIT;
  }

  function rebuild() {
    const fallback = placeholder();
    for (let i = 0; i < count; i++) {
      offsets[i + 1] = offsets[i] + (Number.isNaN(lengths[i]) ? fallback : lengths[i]);
    }
  }

  function height() {
    return scroller.clientHeight;
  }

  function maxReading() {
    return Math.max(0, offsets[count] * height() - scroller.clientWidth);
  }

  /** Distance scrolled from the reading start, in pixels. */
  function reading() {
    return rtl ? -scroller.scrollLeft : scroller.scrollLeft;
  }

  function setReading(px) {
    const value = Math.min(Math.max(0, px), maxReading());
    scroller.scrollLeft = rtl ? -value : value;
  }

  /** The page at a distance from the reading start (in strip-height units). */
  function indexAt(units) {
    let low = 0;
    let high = count - 1;
    while (low < high) {
      const mid = (low + high + 1) >> 1;
      if (offsets[mid] <= units) {
        low = mid;
      } else {
        high = mid - 1;
      }
    }

    return low;
  }

  function anchorAt(px) {
    const h = height();
    if (count === 0 || h <= 0) {
      return { index: 0, fraction: 0 };
    }

    const units = px / h;
    const index = indexAt(units);
    const length = offsets[index + 1] - offsets[index];
    return { index, fraction: length > 0 ? Math.min(Math.max(0, (units - offsets[index]) / length), 0.999999) : 0 };
  }

  function anchorPx(at) {
    const length = offsets[at.index + 1] - offsets[at.index];
    return (offsets[at.index] + at.fraction * length) * height();
  }

  // ---------- Layout ----------

  function setLength(index, length) {
    if (Number.isNaN(lengths[index])) {
      known++;
    } else {
      knownSum -= lengths[index];
    }

    lengths[index] = length;
    knownSum += length;
    queueRelayout();
  }

  function queueRelayout() {
    if (!relayoutQueued) {
      relayoutQueued = true;
      requestAnimationFrame(relayout);
    }
  }

  /** Sizes every page and puts the anchored page back where it was; a running animation moves along. */
  function relayout() {
    relayoutQueued = false;
    if (destroyed) {
      return;
    }

    const before = animation ? anchorPx(anchor) : 0;
    rebuild();
    const h = height();
    for (let i = 0; i < count; i++) {
      images[i].style.width = `${(offsets[i + 1] - offsets[i]) * h}px`;
    }

    const after = anchorPx(anchor);
    if (animation) {
      animation.from += after - before;
      animation.to += after - before;
    }

    setReading(after);
    notify();
  }

  // ---------- Scrolling ----------

  function onScroll() {
    anchor = anchorAt(reading());
    notify();
  }

  function notify() {
    if (moveQueued) {
      return;
    }

    moveQueued = true;
    requestAnimationFrame(() => {
      moveQueued = false;
      if (destroyed || count === 0) {
        return;
      }

      const px = reading();
      const h = height();
      const current = h > 0 ? indexAt((px + 0.1 * scroller.clientWidth) / h) : anchor.index;
      onMove({ current, atEnd: px >= maxReading() - 1 });
    });
  }

  /** Scrolls forward (positive) or backward by a share of the window. */
  function scrollBy(viewports) {
    const from = animation ? animation.to : reading();
    const to = Math.min(Math.max(0, from + viewports * scroller.clientWidth), maxReading());
    if (to === from) {
      return;
    }

    const start = performance.now();
    if (animation) {
      animation.from = reading();
      animation.to = to;
      animation.start = start;
      return;
    }

    animation = { from: reading(), to, start };
    requestAnimationFrame(step);
  }

  function step(now) {
    if (!animation || destroyed) {
      return;
    }

    const t = Math.min(1, (now - animation.start) / ANIMATION_MS);
    const eased = 1 - Math.pow(1 - t, 3);
    setReading(animation.from + (animation.to - animation.from) * eased);
    if (t >= 1) {
      animation = null;
    } else {
      requestAnimationFrame(step);
    }
  }

  function stopAnimation() {
    animation = null;
  }

  function onWheel(event) {
    if (event.ctrlKey) {
      return;
    }

    // A sideways touchpad swipe scrolls the strip natively.
    if (Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
      stopAnimation();
      return;
    }

    event.preventDefault();
    if (event.deltaY === 0) {
      return;
    }

    const notches = event.deltaMode === 1 ? event.deltaY / 3 : event.deltaMode === 2 ? event.deltaY : event.deltaY / WHEEL_NOTCH;
    scrollBy(notches * WHEEL_SHARE); // wheel down = forward
  }

  // Grab-scroll with the mouse; a press that does not move is a click for the reader.
  function onPointerDown(event) {
    if (event.pointerType !== "mouse" || event.button !== 0) {
      return;
    }

    stopAnimation();
    drag = { x: event.clientX, start: reading(), moved: false, id: event.pointerId };
  }

  function onPointerMove(event) {
    if (!drag || event.pointerId !== drag.id) {
      return;
    }

    const dx = event.clientX - drag.x;
    if (!drag.moved && Math.abs(dx) > DRAG_MOVE) {
      drag.moved = true;
      scroller.setPointerCapture(event.pointerId);
      scroller.classList.add("dragging");
    }

    if (drag.moved) {
      setReading(drag.start + (rtl ? dx : -dx));
    }
  }

  function endDrag() {
    drag = null;
    scroller.classList.remove("dragging");
  }

  // ---------- Controls ----------

  function goTo(page) {
    stopAnimation();
    anchor = { index: Math.min(Math.max(0, page), count - 1), fraction: 0 };
    setReading(anchorPx(anchor));
    notify();
  }

  function setDirection(value) {
    rtl = value;
    scroller.dir = rtl ? "rtl" : "ltr";
    stopAnimation();
    const keep = anchor;
    rebuild();
    const h = height();
    for (let i = 0; i < count; i++) {
      images[i].style.width = `${(offsets[i + 1] - offsets[i]) * h}px`;
    }

    anchor = keep;
    setReading(anchorPx(keep));
    notify();
  }

  return {
    scrollBy,
    goTo,
    home: () => goTo(0),
    end() {
      stopAnimation();
      setReading(maxReading());
    },
    setDirection,
    /** The first page at the reading start of the window. */
    get position() {
      return anchor.index;
    },
    destroy() {
      destroyed = true;
      animation = null;
      loader.disconnect();
      resize.disconnect();
      scroller.remove();
    },
  };
}
