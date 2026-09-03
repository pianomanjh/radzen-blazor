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

/// Attaches the guard that stops the browser scrolling for the keys the grid handles, and reports back
/// what only the browser knows: the writing direction and the height of the viewport in rows.
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

/// Re-reads the direction and the viewport height. Called when focus enters rather than per keystroke,
/// so a resized window is picked up without measuring on every arrow key.
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

/// Moves the cursor to one cell. row is -1 for the header row; cell is the browser's own cell index,
/// which is the index the click listener already reports and so counts the row-detail toggle.
///
/// pinnedStart and pinnedEnd are how many cells of the row are frozen to each edge - C# knows which
/// columns those are, and the browser measures how wide they came out.
///
/// Returns whether the cell was found straight away. A row outside a virtualized window is not, and is
/// waited for: see the scroll-and-settle below.
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

/// Clears the cursor and the active descendant, leaving C#'s position alone so tabbing back in
/// restores it. RadzenDataGrid never resets its equivalent, so its grid claims an active descendant
/// while nothing in it is focused.
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
