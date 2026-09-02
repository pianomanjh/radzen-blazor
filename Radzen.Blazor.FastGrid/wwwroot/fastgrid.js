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

  // The row index is written by the grid; the column index is the browser's own, so it costs no markup.
  // A click on padding between cells lands on the tr and resolves no cell, which is reported as -1
  // rather than dropped: a row click still has to happen.
  const locate = (event) => {
    const row = event.target.closest('tr[data-r]');

    if (!row || !tbody.contains(row)) {
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

    if (preventDefault) {
      event.preventDefault();
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
