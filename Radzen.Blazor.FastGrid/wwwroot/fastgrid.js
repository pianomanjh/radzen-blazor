// One listener per grid body instead of one delegate per cell.
//
// A click handler bound in the render tree costs about 296 bytes per cell, which at a thousand rows
// and five columns is 1.4 MB of the grid's allocation. The browser already routes clicks to their
// ancestors, so the grid binds nothing and this resolves the row and column from the event itself.
//
// The grid falls back to per-cell handlers unless attach() returns true, so nothing here is required
// for the feature to work - a missing or failed script degrades to the binding it replaced.
export function attach(bodyId, dotNetRef, kinds) {
  const tbody = document.getElementById(bodyId);

  if (!tbody || !dotNetRef) {
    return false;
  }

  // Attaching twice is how the grid keeps the listener in step with what it is currently listening
  // for - a callback switched on after the first render changes which events matter.
  detach(bodyId);

  // The row index is written by the grid; the column index is the browser's own, so it costs no markup.
  // A click on padding between cells lands on the tr and resolves no cell, which is reported as -1
  // rather than dropped: a row click still has to happen.
  const locate = (event) => {
    const row = event.target.closest('tr[data-r]');

    // The row must be *this* tbody's own child, not merely inside it. A grid rendered into a row
    // detail template sits in this tbody too, and closest() finds its rows first - so a click on the
    // inner grid resolved to an outer row with the inner grid's index, raised the outer grid's
    // RowClick against an unrelated item, and matched the inner toggle well enough to collapse the
    // detail row the user was working in. The per-cell fallback never had this: its handler is on the
    // data tr and the detail row is a sibling, so nothing bubbles to it.
    if (!row || row.parentNode !== tbody) {
      return null;
    }

    const cell = event.target.closest('td');

    return {
      row: parseInt(row.dataset.r, 10),
      cell: cell && cell.parentNode === row ? cell.cellIndex : -1,
    };
  };

  const send = (kind, event, preventDefault) => {
    const at = locate(event);

    if (!at) {
      return;
    }

    // Only where a cell of this row resolved. Suppressing the browser menu over the padding between
    // cells - or over the row-detail toggle, which resolves to no column - would take the menu away
    // and raise nothing in its place, which is not what the per-cell binding this replaced did.
    if (preventDefault && at.cell >= 0) {
      event.preventDefault();
    }

    // The row-detail toggle carried @onclick:stopPropagation, so expanding a row never counted as
    // clicking it. It is its own kind here for the same reason: one click is one thing.
    //
    // Read from the markup rather than from a flag settled when the listener was attached: the
    // attribute is on the button only while the grid draws one, which is the same condition, and it
    // cannot go stale.
    if (kind === 'click' && event.target.closest('[data-toggle]')) {
      dotNetRef.invokeMethodAsync('RadzenFastGrid.OnDelegatedPointer', 'toggle', at.row, -1);

      return;
    }

    dotNetRef.invokeMethodAsync('RadzenFastGrid.OnDelegatedPointer', kind, at.row, at.cell);
  };

  const handlers = {};

  if (kinds.click) {
    handlers.click = (e) => send('click', e, false);
  }

  if (kinds.doubleClick) {
    handlers.dblclick = (e) => send('dblclick', e, false);
  }

  // The binding this replaces was @oncontextmenu:preventDefault, so the browser menu must still be
  // suppressed here or a grid with a context menu would show both.
  if (kinds.contextMenu) {
    handlers.contextmenu = (e) => send('contextmenu', e, true);
  }

  for (const [name, handler] of Object.entries(handlers)) {
    tbody.addEventListener(name, handler);
  }

  tbody._fastGridClicks = handlers;

  return true;
}

export function detach(bodyId) {
  const tbody = document.getElementById(bodyId);

  if (!tbody || !tbody._fastGridClicks) {
    return;
  }

  for (const [name, handler] of Object.entries(tbody._fastGridClicks)) {
    tbody.removeEventListener(name, handler);
  }

  tbody._fastGridClicks = null;
}

// ---- Keyboard navigation ----
//
// The effect layer, and nothing else. C# works out which cell is focused and calls in with it; this
// swaps a class, moves the id aria-activedescendant points at, and scrolls the cell into view. It
// decides nothing, so it cannot disagree with the component about where focus is - which is the fault
// RadzenDataGrid's focusTableRow has, where 156 lines of index arithmetic in JavaScript own state
// Blazor also writes.
//
// Two things here are measurements rather than decisions, and the distinction is the whole rule: the
// browser is asked how wide the pinned run is and how many rows fit in the viewport, because those are
// facts only it has. What to do with them is decided in C#.

// Attaches the guard that stops the browser scrolling for the keys the grid handles, and reports back
// what only the browser knows: the writing direction and the height of the viewport in rows.
export function attachNavigation(viewId, keys) {
  const view = document.getElementById(viewId);

  if (!view) {
    return null;
  }

  detachNavigation(viewId);

  // Both the browser and the grid scroll on an arrow key, so without this the container jitters: the
  // browser scrolls by a line, then focusCell scrolls the cell back. Space and PageDown scroll the
  // page itself, which is worse.
  //
  // Which keys to suppress is C#'s list rather than this file's. Tab is not on it, and must not be:
  // the grid is one tab stop and swallowing Tab would trap focus inside it.
  const guard = (event) => {
    if (keys.indexOf(event.key) >= 0) {
      event.preventDefault();
    }
  };

  view.addEventListener('keydown', guard);
  view._fastGridKeys = guard;

  return measure(view);
}

export function detachNavigation(viewId) {
  const view = document.getElementById(viewId);

  if (!view || !view._fastGridKeys) {
    return;
  }

  view.removeEventListener('keydown', view._fastGridKeys);
  view._fastGridKeys = null;
}

// Re-reads the direction and the viewport height. Called when focus enters rather than per keystroke,
// so a resized window is picked up without measuring on every arrow key.
export function measureNavigation(viewId) {
  const view = document.getElementById(viewId);

  return view ? measure(view) : null;
}

function measure(view) {
  const row = view.querySelector(':scope > table > tbody > tr');
  const height = row ? row.getBoundingClientRect().height : 0;

  return {
    rtl: getComputedStyle(view).direction === 'rtl',

    // What PageUp and PageDown move by. Zero means "not measurable yet" - an empty grid, or one that
    // has not been laid out - and C# falls back to its own step rather than treating it as no rows.
    rows: height > 0 ? Math.max(1, Math.floor(view.clientHeight / height)) : 0,
  };
}

// Moves the cursor to one cell. row is -1 for the header row; cell is the browser's own cell index,
// which is the index the click listener already reports and so counts the row-detail toggle.
//
// pinnedStart and pinnedEnd are how many cells of the row are frozen to each edge - C# knows which
// columns those are, and the browser measures how wide they came out.
//
// Returns whether the cell was found straight away. A row outside a virtualized window is not, and is
// waited for: see the scroll-and-settle below.
export function focusCell(viewId, row, cell, pinnedStart, pinnedEnd, itemSize) {
  const view = document.getElementById(viewId);

  if (!view) {
    return false;
  }

  clearFocus(view);

  // Supersedes any jump still waiting for its rows - the last call is the one that meant it.
  view._fastGridPending = null;

  const target = locate(view, row, cell);

  if (target) {
    paint(view, viewId, target, pinnedStart, pinnedEnd);

    return true;
  }

  if (row >= 0 && itemSize > 0) {
    // A virtualized row outside the rendered window is not there to focus. Scroll to where it will be
    // and wait for it: the window that arrives is rendered by Virtualize itself rather than by the
    // grid, so no OnAfterRenderAsync fires and there is no re-assert to catch it. Bounded, and
    // abandoned rather than retried forever, so a row that never arrives leaves no cursor rather than
    // a spinning one.
    view.scrollTop = row * itemSize;

    const token = {};

    view._fastGridPending = token;

    const settle = (frames) => {
      if (view._fastGridPending !== token) {
        return;
      }

      const arrived = locate(view, row, cell);

      if (arrived) {
        view._fastGridPending = null;
        paint(view, viewId, arrived, pinnedStart, pinnedEnd);
      } else if (frames > 0) {
        requestAnimationFrame(() => settle(frames - 1));
      } else {
        view._fastGridPending = null;
      }
    };

    requestAnimationFrame(() => settle(40));
  }

  return false;
}

function paint(view, viewId, target, pinnedStart, pinnedEnd) {
  // The row carries the background and the cell carries the outline, which is the pair of rules the
  // theme draws - one alone shows a row with no cursor in it, or a cursor on an unlit row.
  target.parentNode.classList.add('rz-state-focused');
  target.classList.add('rz-state-focused');

  // The id exists only while the cell is focused, so no cell carries one in the render tree. That is
  // what rules out roving tabindex too: either would be an attribute frame on every cell of the grid.
  target.id = viewId + '-focus';
  view.setAttribute('aria-activedescendant', target.id);
  view._fastGridFocus = target;

  bringIntoView(view, target, pinnedStart, pinnedEnd);
}

// Clears the cursor and the active descendant, leaving C#'s position alone so tabbing back in
// restores it. RadzenDataGrid never resets its equivalent, so its grid claims an active descendant
// while nothing in it is focused.
export function blurCell(viewId) {
  const view = document.getElementById(viewId);

  if (view) {
    clearFocus(view);
  }
}

function clearFocus(view) {
  const previous = view._fastGridFocus;

  if (previous) {
    previous.classList.remove('rz-state-focused');
    previous.removeAttribute('id');

    if (previous.parentNode) {
      previous.parentNode.classList.remove('rz-state-focused');
    }
  }

  view._fastGridFocus = null;
  view.removeAttribute('aria-activedescendant');
}

// Two ways to find a row, and C# decides which by what it emits rather than this file deciding by
// what it prefers. A grid whose rows carry data-r - delegated clicks, or a virtualized window whose
// index is a position in the whole data set - is addressed by it. Otherwise the rendered data rows are
// the model's rows in order, so the nth of them is the nth row, and no attribute is needed: an
// expanded row's detail is a sibling carrying rz-expanded-row-content, and Virtualize's spacer carries
// no class at all, so neither is counted. Not tbody.rows[i], which counts both.
function locate(view, row, cell) {
  const table = view.querySelector(':scope > table');

  if (!table) {
    return null;
  }

  if (row < 0) {
    const head = table.querySelector(':scope > thead > tr');

    return head ? head.children[cell] || null : null;
  }

  const body = table.querySelector(':scope > tbody');

  if (!body) {
    return null;
  }

  const addressed = body.querySelector(':scope > tr[data-r="' + row + '"]');

  // Counting is only right where nothing is addressed. On markup that carries the index, a miss means
  // the row is not rendered - a virtualized window scrolled past it - and counting there would answer
  // with a position in the window while the caller meant a position in the data.
  const tr = addressed
    || (body.querySelector(':scope > tr[data-r]')
      ? null
      : body.querySelectorAll(':scope > tr.rz-data-row')[row]);

  return tr ? tr.children[cell] || null : null;
}

function bringIntoView(view, target, pinnedStart, pinnedEnd) {
  const box = view.getBoundingClientRect();
  const cell = target.getBoundingClientRect();

  if (cell.top < box.top) {
    view.scrollTop -= box.top - cell.top;
  } else if (cell.bottom > box.bottom) {
    view.scrollTop += cell.bottom - box.bottom;
  }

  // A frozen column is pinned over the leading edge from the first paint, so a cell underneath one is
  // inside the container's rect and counts as visible to every built-in scroll helper while being
  // completely hidden. The pinned run's width is the inset the edge really sits at.
  const rtl = getComputedStyle(view).direction === 'rtl';
  const row = target.parentNode;
  const start = runWidth(row, pinnedStart, false);
  const end = runWidth(row, pinnedEnd, true);

  const left = box.left + (rtl ? end : start);
  const right = box.right - (rtl ? start : end);

  // scrollLeft is a physical offset in both directions - RTL only moves its range below zero - so one
  // signed delta is correct either way.
  if (cell.left < left) {
    view.scrollLeft -= left - cell.left;
  } else if (cell.right > right) {
    view.scrollLeft += cell.right - right;
  }
}

function runWidth(row, count, fromEnd) {
  let width = 0;

  for (let i = 0; i < count && i < row.children.length; i++) {
    const cell = row.children[fromEnd ? row.children.length - 1 - i : i];

    width += cell.getBoundingClientRect().width;
  }

  return width;
}

// Column auto-fit.
//
// The table is table-layout:fixed, so nothing here sizes itself to its content and there is no layout
// to read back: a width has to be worked out and written. This does both in the same pass, because the
// alternative is the server rendering every row again to deliver a handful of strings.
//
// Everything is batched. A read after a write costs a layout, so the measuring class goes on once,
// every measurement is taken, and only then does it come off and the widths go on.

const MEASURING = 'rz-fastgrid-measuring';
const ANIMATING = 'rz-fastgrid-animating';

// Long enough to read as movement, short enough that nobody waits for it. The curve is an ease-out:
// the columns leave quickly and settle into the new width rather than stopping dead on it.
const ANIMATION_MS = 200;

// The narrowest a column is ever left when nothing else says how narrow it may be. Wide enough to see
// and to grab a resize handle on, narrow enough that it is obviously not the whole column.
const VESTIGE = 5;

let measuringStyle;

function installMeasuringStyle() {
  if (measuringStyle) {
    return;
  }

  measuringStyle = document.createElement('style');

  // width:max-content is what makes either element measurable at all.
  //
  // .rz-cell-data is a block filling its column, so its scrollWidth is never less than the column it
  // sits in: a column wider than its content measures as itself, and a fit could only ever grow one.
  // .rz-column-title is worse - an inline-flex whose content child carries overflow:hidden, which
  // zeroes that child's automatic minimum size, so the line shrinks to whatever it has been given and
  // reports exactly that back.
  //
  // The title needs its flex growth turned off as well as its width set, and that is the half that is
  // easy to miss: it is `flex: auto` inside the header's flex line, and a flex item's used main size
  // comes from its flex properties rather than from `width`. Setting only the width leaves it filling
  // the line exactly as before, and measures every column at the width it already has - which is the
  // wrong answer that looks like a working one, because every column then fits to itself.
  //
  // !important on both, because these are the theme's own declarations being overridden rather than
  // an empty slot being filled, and a rule that loses is indistinguishable from one never written.
  measuringStyle.textContent =
    `.${MEASURING} .rz-cell-data{width:max-content !important;}`
    + `.${MEASURING} .rz-column-title{width:max-content !important;flex:0 0 max-content !important;}`
    // A col's width does transition, which is not obvious and was measured rather than assumed - and
    // the cells cannot: under table-layout:fixed the column decides, so a transition declared on a th
    // or a td animates nothing. Scoped under a class the fit adds for the length of the run, because a
    // permanent rule here would put a 200ms lag between a resize drag and the pointer.
    + `.${ANIMATING} col{transition:width ${ANIMATION_MS}ms cubic-bezier(0.22,0.61,0.36,1);}`
    + `@media (prefers-reduced-motion:reduce){.${ANIMATING} col{transition:none;}}`;

  document.head.appendChild(measuringStyle);
}

// Turns the transition on for one run and takes it off again. It has to come off: the class is on the
// table, so anything else that writes a column width - a resize drag, most of all - would inherit it.
function animateFor(table) {
  table.classList.add(ANIMATING);

  clearTimeout(table._fastGridAnimation);

  table._fastGridAnimation = setTimeout(() => {
    table.classList.remove(ANIMATING);
  }, ANIMATION_MS + 50);
}

// The cell's text span, found by walking siblings rather than by asking the selector engine. This
// runs once per rendered cell - five thousand times at a thousand rows - and it is the only part of
// the pass that scales with the grid, so what it costs each time is the whole cost of the feature.
function cellData(cell) {
  for (let child = cell.firstElementChild; child; child = child.nextElementSibling) {
    if (child.classList.contains('rz-cell-data')) {
      return child;
    }
  }

  return null;
}

// MinWidth and MaxWidth are free-form CSS - `50%`, `4rem`, `clamp(...)` - and fitting to a container is
// arithmetic, which needs numbers. Rather than parse them, which works for pixels and is quietly wrong
// for everything else, each one is given to the browser on a probe element and measured back.
//
// The probes go in the table's own wrapper so a percentage resolves against the width it was written
// against and a relative unit against the font it would inherit. All of them are written, then all of
// them are read: one layout for the set rather than one apiece. Once per fit, never on a resize.
function resolveLengths(table, values) {
  const resolved = new Array(values.length).fill(null);
  const host = table.parentElement;

  if (!host) {
    return resolved;
  }

  const probes = [];
  const holder = document.createElement('div');

  // Given the host's width explicitly rather than left to size itself. A percentage resolves against
  // its containing block, and an absolutely positioned box with no width of its own is shrink-to-fit -
  // a child asking for 20% of that gets 20% of nothing, measures zero, and is thrown away as a bound
  // the browser could not make sense of. `rem` was unaffected, which is how this survived a test.
  holder.style.cssText = 'position:absolute;visibility:hidden;height:0;overflow:hidden;width:'
    + host.clientWidth + 'px;';

  for (let i = 0; i < values.length; i++) {
    if (typeof values[i] !== 'string' || values[i].length === 0) {
      continue;
    }

    const probe = document.createElement('div');

    probe.style.width = values[i];
    holder.appendChild(probe);
    probes.push([i, probe]);
  }

  if (probes.length === 0) {
    return resolved;
  }

  host.appendChild(holder);

  try {
    for (const [i, probe] of probes) {
      const width = probe.getBoundingClientRect().width;

      // A length the browser could not make sense of measures as nothing, which is not a bound.
      if (width > 0) {
        resolved[i] = width;
      }
    }
  } finally {
    host.removeChild(holder);
  }

  return resolved;
}

function headCells(table) {
  const row = table.querySelector(':scope > thead > tr');

  return row ? row.children : [];
}

function dataRows(table) {
  const body = table.querySelector(':scope > tbody');

  return body ? body.querySelectorAll(':scope > tr.rz-data-row') : [];
}

// Horizontal padding and borders: the difference between what a cell's content needs and what its
// column has to be. Read per column rather than per grid, because a column's CssClass can change it.
function edges(element) {
  const style = getComputedStyle(element);

  return (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0)
    + (parseFloat(style.borderLeftWidth) || 0) + (parseFloat(style.borderRightWidth) || 0);
}

// clamp() rather than arithmetic. MinWidth and MaxWidth are authored CSS and may be in any unit, and
// the browser is the only thing that can compare a rem with a pixel - the same argument that has
// frozen insets summing their widths with calc() instead of parsing them.
function bound(px, min, max) {
  if (min && max) {
    return `clamp(${min},${px}px,${max})`;
  }

  return min ? `max(${min},${px}px)` : max ? `min(${px}px,${max})` : `${px}px`;
}

// The automatic fit is armed by a render and fired here, because Virtualize re-renders itself: its
// window arrives without a render of the grid, so the server cannot tell when there is anything to
// measure. Bounded, and it gives up into a header-only fit rather than never landing - an empty grid
// would otherwise re-arm on every render and wait again.
async function ready(tableId, wait) {
  // Bounded by the clock as well as by frames. requestAnimationFrame does not fire in a backgrounded
  // tab, so waiting on frames alone waits as long as the tab stays hidden - and the server has already
  // disarmed by then, so nothing would ever ask again.
  //
  // The clock has to be able to win the race rather than merely be consulted between frames: testing
  // the deadline at the top of the loop reads as a timeout and is not one, because the await above it
  // never returns for the tab that needs it.
  const deadline = performance.now() + 1000;

  for (;;) {
    const table = document.getElementById(tableId);

    if (!table || !wait || dataRows(table).length > 0) {
      return table;
    }

    const remaining = deadline - performance.now();

    if (remaining <= 0) {
      return table;
    }

    await new Promise(resolve => {
      const timer = setTimeout(resolve, remaining);

      requestAnimationFrame(() => {
        clearTimeout(timer);
        resolve();
      });
    });
  }
}

// Everything a fitted grid needs to redistribute itself when its container changes size, kept per
// table so a resize costs arithmetic rather than another measuring pass. Content widths do not change
// when the window does - only the room to put them in - so the expensive half is never repeated.
const fitted = new Map();

// Splits `available` across the columns, taking what is missing out of the ones that can spare it.
// A column gives up in proportion to how much it has above its floor, and one that reaches its floor
// stops giving and hands its share to the ones still above theirs - which is why this iterates rather
// than dividing once.
//
// Required columns are not a case here. They are given a floor equal to their content width, so they
// arrive with nothing above it and the same arithmetic leaves them alone. Saying it twice - a floor and
// a flag - is what let a mutation that deleted the flag pass every test.
function distribute(state, available) {
  const { content, soft, hard, out, last, cols, bare } = state;
  const n = content.length;

  let deficit = -available;

  for (let i = 0; i < n; i++) {
    out[i] = content[i];
    deficit += content[i];
  }

  // More room than the columns need. Handing every column its content width would leave the table
  // narrower than its container, and under table-layout:fixed the browser then shares the surplus
  // across every column in proportion - including the required ones, which is the one thing this
  // mode promises not to do. So the surplus goes to a single column with no width of its own, the
  // same way it does when the grid is not fitting at all.
  // With no bare column - every column frozen, so there is no trailing one to leave - the surplus has
  // to go somewhere all the same. Written widths that sum to less than the table make the browser share
  // the difference across every column in proportion, required ones included, which is the one thing
  // this mode promises not to do. So the last column that is not required absorbs it instead.
  if (deficit <= 0 && bare < 0) {
    let absorber = -1;

    for (let i = n - 1; i >= 0; i--) {
      if (!state.required[i]) {
        absorber = i;
        break;
      }
    }

    if (absorber >= 0) {
      out[absorber] -= deficit;
    }
  }

  if (deficit <= 0 && bare >= 0) {
    for (let i = 0; i < n; i++) {
      if (i === bare) {
        if (last[i] !== -1) {
          cols[i].style.width = '';
          last[i] = -1;
        }
      } else if (out[i] !== last[i]) {
        cols[i].style.width = out[i].toFixed(1) + 'px';
        last[i] = out[i];
      }
    }

    return;
  }

  // Two floors, taken in order. Everything comes off the soft floor first - the width at which a
  // column still shows its own heading. Only when every column is standing on that and the table
  // still does not fit does the second round start, which spends the difference between a heading
  // and the values under it.
  //
  // That difference is the whole answer to a column headed "Manufacturing Code" over six-character
  // codes: it is carrying width its values never needed, so it is the first thing worth spending -
  // but only once the columns with ordinary slack have already given theirs. A column whose heading
  // is about as wide as its content has almost nothing here and gives almost nothing.
  for (const floor of [soft, hard]) {
    for (let pass = 0; pass < 8 && deficit > 0.5; pass++) {
      let pool = 0;

      for (let i = 0; i < n; i++) {
        if (out[i] > floor[i]) {
          pool += out[i] - floor[i];
        }
      }

      if (pool <= 0) {
        break;
      }

      const wanted = Math.min(deficit, pool);
      let took = 0;

      for (let i = 0; i < n; i++) {
        if (out[i] <= floor[i]) {
          continue;
        }

        const give = Math.min(out[i] - floor[i], wanted * ((out[i] - floor[i]) / pool));

        out[i] -= give;
        took += give;
      }

      if (took <= 0) {
        break;
      }

      deficit -= took;
    }
  }

  // Written only where it changed. During a drag most columns move every frame, but the required ones
  // never do, and neither does anything already sitting on its floor.
  for (let i = 0; i < n; i++) {
    if (cols[i] && out[i] !== last[i]) {
      cols[i].style.width = out[i].toFixed(1) + 'px';
      last[i] = out[i];
    }
  }
}

// Follows the container. Throttled to one redistribution per frame: the arithmetic is free but each
// write forces the table to lay out again, and at a thousand rendered rows that is the whole cost.
function watch(table, state) {
  const wrapper = table.parentElement;

  if (!wrapper || typeof ResizeObserver === 'undefined') {
    return;
  }

  state.observer = new ResizeObserver(() => {
    if (state.queued) {
      return;
    }

    state.queued = requestAnimationFrame(() => {
      state.queued = 0;

      // The same question the fit asked before it ran, asked again because the answer changes:
      // a window narrowed past the Responsive breakpoint stacks the rows into cards, and a
      // colgroup width decides nothing there. Guarding only the first fit leaves the observer
      // writing widths into a table that is no longer one.
      if (getComputedStyle(table).display !== 'table') {
        return;
      }

      const available = wrapper.clientWidth - state.reserved;

      // The guard against feeding ourselves: writing column widths can change the table's width,
      // and a wrapper that sizes to its content would then report a new size and ask again. A
      // width we have already answered for is not a new question.
      if (available !== state.available && available > 0) {
        state.available = available;
        distribute(state, available);
      }
    });
  });

  state.observer.observe(wrapper);
}

// Drops the observer and the state, and nothing else. A fit about to build new state calls this: it is
// replacing what it watches with, not finishing with the table, so an animation already armed for the
// widths it is about to write has to survive.
function stopWatching(tableId) {
  const state = fitted.get(tableId);

  if (state) {
    if (state.queued) {
      cancelAnimationFrame(state.queued);
    }

    if (state.observer) {
      state.observer.disconnect();
    }

    fitted.delete(tableId);
  }
}

// Stops following a table's container and stops anything else this grid left running. Called when the
// grid goes away; a grid that is merely re-rendered keeps its observer, because the table element and
// the content widths are both still good.
//
// Cancelling the animation's timer is only half of it. That timer exists to take the class off, so
// cancelling it without doing that leaves the transition armed for good: a width written later for any
// other reason animates, and anything measuring the table straight after reads a value still in flight.
export function releaseFit(tableId) {
  stopWatching(tableId);

  const table = document.getElementById(tableId);

  if (table) {
    if (table._fastGridAnimation) {
      clearTimeout(table._fastGridAnimation);
      table._fastGridAnimation = 0;
    }

    table.classList.remove(ANIMATING);
  }
}


export async function autoFit(tableId, indices, minWidths, maxWidths, toggleOffset, bare, wait, animate,
  overflow, required) {
  const table = await ready(tableId, wait);

  if (!table) {
    return null;
  }

  const colgroup = table.querySelector(':scope > colgroup');

  if (!colgroup) {
    return null;
  }

  // Below the Responsive breakpoint the theme gives the table `table-layout: auto` and `display:
  // block`, hides the header row and stacks the body into cards - so a colgroup width is no longer
  // what decides a column, and there are no header cells to measure. Asked as "is this still a
  // table" rather than by checking a width against 768px: the breakpoint is the theme's number to
  // change, and any other reason the table stopped being one has the same consequence here.
  if (getComputedStyle(table).display !== 'table') {
    return null;
  }

  const headRow = table.querySelector(':scope > thead > tr');

  const rows = dataRows(table);
  const widths = [];

  // The bare column's own measurement, and the running total of everything being fitted - both only
  // needed for the no-slack case below.
  let bareWidth = null;
  let measured = 0;
  // The whole container. `state.available` is this minus what the columns nobody is fitting take, and
  // one word for the two was a way to reach for the wrong one.
  let container = 0;

  // What the columns this pass is not fitting are taking. A declared Width or AutoFit="false" column
  // is space the fitted ones cannot have, and fitting to a container means fitting to what is left of
  // it - not to the whole thing with those columns added on afterwards.
  let reserved = 0;
  const pixels = [];
  const headers = [];
  const bodies = [];

  installMeasuringStyle();
  table.classList.add(MEASURING);

  // Nothing to resolve on a grid that declares no bounds, which is most of them, so this costs nothing
  // there and one layout where it does.
  const minimums = resolveLengths(table, minWidths);
  const maximums = resolveLengths(table, maxWidths);

  try {
    for (let k = 0; k < indices.length; k++) {
      // The toggle column is a cell in every row and has a col of its own standing in for it, but no
      // column in the grid's own list - so every position the server names is that many further along.
      const at = toggleOffset + indices[k];

      let widest = 0;

      const th = headRow ? headRow.children[at] : null;
      const title = th ? th.querySelector('.rz-column-title') : null;

      if (title) {
        // The theme gives th padding:0 and hangs the header's padding off the div inside it, so a
        // header's chrome is that div's as well as the cell's. scrollWidth already covers the title's
        // own padding-inline, and the reserved sort glyph is a flex child of it.
        widest = title.scrollWidth + edges(th)
          + (title.parentElement && title.parentElement !== th ? edges(title.parentElement) : 0);
      }

      // Kept before the body can raise it. It is the width at which the column still says what it is,
      // and it is what a column falls back to when nobody has given it a MinWidth.
      const headerPx = widest;

      let bodyPx = 0;

      if (rows.length > 0) {
        const first = rows[0].children[at];
        let content = 0;

        for (let r = 0; r < rows.length; r++) {
          const cell = rows[r].children[at];
          const span = cell ? cellData(cell) : null;

          if (span && span.scrollWidth > content) {
            content = span.scrollWidth;
          }
        }

        const needed = content + (first ? edges(first) : 0);

        bodyPx = needed;

        if (needed > widest) {
          widest = needed;
        }
      }

      // scrollWidth rounds to an integer, and a fitted column one pixel short draws an ellipsis -
      // the single outcome that makes this look like it did not work. No over-fit beyond that: the
      // 3% RadzenSpreadsheet adds is for surviving a renderer other than the one that measured, and
      // here they are the same renderer.
      // Bounded here rather than only in the string handed to the browser. `measured` decides whether
      // there is slack left, and summing what a column would need if nothing capped it overstates a
      // MaxWidth-capped column by however much the cap removes - enough to conclude there is no slack
      // when there is. The fitting path had its own clamp and got this right; now there is one.
      const px = Math.max(
        minimums[k] === null ? 0 : minimums[k],
        Math.min(Math.ceil(widest) + 1, maximums[k] === null ? Infinity : maximums[k]));

      // The bare column takes whatever the fitted ones left, which under table-layout:fixed is what
      // a col with no width at all means. Its own measurement is kept rather than discarded: it is
      // what the column needs if it turns out there is nothing left to take.
      // Kept as a number as well as a string: fitting to the container is arithmetic, and it needs
      // the measurement rather than the expression the measurement turns into.
      pixels.push(px);
      headers.push(Math.ceil(headerPx));
      bodies.push(Math.ceil(bodyPx));

      if (indices[k] === bare) {
        bareWidth = bound(px, minWidths[k], maxWidths[k]);
        widths.push(null);
      } else {
        widths.push(bound(px, minWidths[k], maxWidths[k]));
      }

      measured += px;
    }
    // With the other reads, not after them. The measuring class changes what is inside a cell, never
    // the width of the cell itself - under table-layout:fixed that is the column's to decide - so a
    // header box measured here is the same box it will be afterwards. A column nobody is fitting keeps
    // whatever width it has, so its current one is also its final one.
    const beingFitted = new Set(indices);
    const cells = headCells(table);

    for (let i = 0; i < cells.length; i++) {
      if (!beingFitted.has(i - toggleOffset)) {
        reserved += cells[i].getBoundingClientRect().width;
      }
    }

    measured += reserved;

    container = table.parentElement ? table.parentElement.clientWidth : table.clientWidth;
  } finally {
    table.classList.remove(MEASURING);
  }

  // Bareness exists to absorb slack. When the fitted columns already fill the container there is none
  // to absorb, and a col with no width in a table that has overflowed its parent is given nothing at
  // all - the column renders zero pixels wide and its content is simply not there. The table is
  // supposed to overflow and the wrapper to scroll; the bare column disappearing is not part of that.
  //
  // Decided by arithmetic rather than by writing the widths and looking, because looking would cost a
  // second whole-table layout. Both figures it needs are read inside the measuring pass above, with
  // every other read - taking them from here instead put the pass over its own timing gate, which is
  // what that gate is for.
  if (bare >= 0 && bareWidth !== null && measured >= container) {
    widths[indices.indexOf(bare)] = bareWidth;
  }

  if (animate) {
    // A column with no width of its own computes to `auto`, and auto does not interpolate - so a first
    // fit would jump while every later one glides. Pinning each such column to the width it already
    // has gives the transition a start value to leave from. The flush is what makes it real: without
    // it the browser only ever sees the final value and compares that against auto.
    //
    // Cheap precisely because the pin writes what is already there - the layout it forces has nothing
    // to move. Measured at 2.5ms over a thousand rendered rows.
    const pinned = [];

    for (let k = 0; k < indices.length; k++) {
      const col = colgroup.children[toggleOffset + indices[k]];

      // Never the bare column: it is supposed to have no width, and it follows the others anyway -
      // being the remainder, the browser recomputes it from them on every frame of the transition.
      if (col && !col.style.width && indices[k] !== bare) {
        pinned.push([col, headRow ? headRow.children[toggleOffset + indices[k]] : null]);
      }
    }

    for (const [col, cell] of pinned) {
      col.style.width = (cell ? cell.getBoundingClientRect().width : 0) + 'px';
    }

    animateFor(table);

    void table.offsetWidth;
  }

  // `overflow` says both what the grid is doing and what this call may do about it: 'fit' rebuilds the
  // distribution, 'keep' is a Fit grid whose call is not a whole-grid one - a single column, which
  // cannot be redistributed against - and 'scroll' is a grid that is not fitting at all. Only the last
  // takes the fit down, which a bare boolean could not express: a double-click on a fitted grid sent
  // the same false as leaving the mode, and tore down the container fit it was supposed to leave alone.
  if (overflow === 'fit') {
    // Keeping the table inside its container instead of letting it overflow. Nothing is left bare -
    // bareness hands leftover space to one column, and here the leftover is being shared out on
    // purpose - so every column is given a width and the arithmetic decides what it is.
    const n = indices.length;
    const state = {
      cols: new Array(n),
      content: new Float64Array(n),
      required: new Array(n),
      soft: new Float64Array(n),
      hard: new Float64Array(n),
      // Which column absorbs the surplus while there is one. Index into the fitted set, not the grid.
      bare: bare >= 0 ? indices.indexOf(bare) : -1,
      out: new Float64Array(n),
      // Deliberately NaN so the first pass writes every column: 0 would look like a width already set.
      last: new Float64Array(n).fill(NaN),
      available: 0,
      reserved: 0,
      observer: null,
      queued: 0
    };

    let floors = 0;
    let needed = 0;

    for (let k = 0; k < n; k++) {
      state.cols[k] = colgroup.children[toggleOffset + indices[k]];
      state.required[k] = !!(required && required[k]);

      // Already bounded when it was measured, in `clamp()`'s order - the minimum last, so it wins over
      // a maximum beneath it as CSS has min-width beat max-width. Getting that order wrong leaves a
      // floor above the width it is a floor for, the table's min-width then overstates what the columns
      // can sum to, and the browser scales them back up to reach it: columns promised they would not
      // move, move.
      state.content[k] = pixels[k];
      // Where required-ness lives: both floors at the content width, so the distribution has nothing
      // to take in either round. There is no second test for it anywhere.
      if (state.required[k]) {
        state.soft[k] = state.content[k];
        state.hard[k] = state.content[k];
      } else {
        // The floor an author gave, or what the values themselves need. This is as narrow as the
        // column ever goes, and below it the grid scrolls instead.
        state.hard[k] = minimums[k] === null
          ? Math.min(state.content[k], Math.max(VESTIGE, bodies[k] || 0))
          : minimums[k];

        // And above it, the width that still shows the heading - never below the hard floor, since a
        // heading is worth less than the values it labels.
        state.soft[k] = Math.max(
          state.hard[k],
          Math.min(state.content[k], Math.max(VESTIGE, headers[k] || 0)));
      }

      floors += state.hard[k];
      needed += state.content[k];
    }

    // Below this there is no arrangement that honours every floor, so the table stops shrinking and
    // the grid scrolls - the same answer Scroll gives, arrived at only once nothing else is left. The
    // columns this pass did not fit still occupy their width, so they are part of the floor too.
    state.reserved = reserved;
    table.style.minWidth = Math.ceil(floors + reserved) + 'px';

    stopWatching(tableId);
    fitted.set(tableId, state);

    const room = (table.parentElement ? table.parentElement.clientWidth : needed + reserved) - reserved;

    state.available = room;
    distribute(state, room);
    watch(table, state);

    // What the columns actually became, so the grid's own model agrees with the page it is looking at.
    // A null where the surplus is being absorbed, which is a width the model already knows how to hold.
    for (let k = 0; k < n; k++) {
      widths[k] = state.last[k] === -1 ? null : state.out[k].toFixed(1) + 'px';
    }
  } else if (overflow !== 'keep') {
    // Leaving Fit is as much a change as entering it: the floor and the observer both have to go, or a
    // grid switched back to Scroll would keep following its container and refuse to shrink.
    if (fitted.has(tableId)) {
      table.style.minWidth = '';
      releaseFit(tableId);
    }
  }

  for (let k = 0; k < indices.length; k++) {
    const col = colgroup.children[toggleOffset + indices[k]];

    if (col) {
      col.style.width = widths[k] === null ? '' : widths[k];
    }
  }

  // The strings that were written, not the numbers behind them: the server stores these and re-emits
  // them on its next render, and anything it derives differently is a width that drifts from the page.
  return widths;
}
