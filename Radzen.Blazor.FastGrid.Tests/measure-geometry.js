// Reads the rendered geometry of every .pane on a page back out of Chromium and prints it as JSON.
//
// This exists because the header-row fault that started it all - a missing `th > div`, which the theme
// hangs its header padding off - leaves every class name correct and is invisible in a markup diff. It
// survived a person looking at a screenshot. Only measured box heights caught it.
//
//   node measure-geometry.js <path-to-page.html>

'use strict';

const path = require('path');
const fs = require('fs');
const { execSync } = require('child_process');

function loadPlaywright() {
    try {
        return require('playwright');
    } catch (local) {
        // Playwright is commonly installed globally on CI images rather than beside the test assembly.
        try {
            const globalRoot = execSync('npm root -g', { encoding: 'utf8' }).trim();
            return require(path.join(globalRoot, 'playwright'));
        } catch (global) {
            throw new Error(
                'Playwright could not be loaded, locally or from the global npm root. ' +
                'The geometry half of the grid parity check cannot run without it.\n' +
                `  local:  ${local.message}\n  global: ${global.message}`);
        }
    }
}

function chromiumPath() {
    // Preinstalled browsers, in the order they are worth trying. This script never downloads one: the
    // CI job installs Chromium as an explicit step, and a check that fetches a browser mid-run is a
    // check that can hang.
    const candidates = [
        process.env.PARITY_CHROMIUM,
        '/opt/pw-browsers/chromium',
    ].filter(Boolean);

    for (const candidate of candidates) {
        if (fs.existsSync(candidate)) {
            return candidate;
        }
    }

    // Fall back to whatever Playwright resolves for itself, so a normal dev machine still works.
    return undefined;
}

async function main() {
    const pagePath = process.argv[2];

    if (!pagePath) {
        throw new Error('usage: node measure-geometry.js <path-to-page.html>');
    }

    if (!fs.existsSync(pagePath)) {
        throw new Error(`page not found: ${pagePath}`);
    }

    const { chromium } = loadPlaywright();
    const executablePath = chromiumPath();

    let browser;

    try {
        browser = await chromium.launch(executablePath ? { executablePath } : {});
    } catch (error) {
        throw new Error(
            `Chromium failed to launch${executablePath ? ` from ${executablePath}` : ''}. ` +
            'Set PARITY_CHROMIUM to a Chromium binary if it lives elsewhere.\n' + error.message);
    }

    try {
        // Headless Chromium reports `prefers-reduced-motion: reduce` by default, which the auto-fit
        // transition honours - so without this the animation probe measures the guard rather than the
        // animation, and reads as a feature that never ran.
        const page = await browser.newPage({
            viewport: { width: 1100, height: 900 },
            reducedMotion: 'no-preference'
        });

        const failures = [];
        page.on('requestfailed', request => failures.push(`${request.url()} (${request.failure()?.errorText})`));

        // Whether the stylesheet arrived has to be observed on the wire: a file:// stylesheet counts as a
        // foreign origin, so reading sheet.cssRules back out of the document throws and would report zero
        // rules for a stylesheet that loaded perfectly well.
        const stylesheets = [];
        page.on('response', response => {
            if (response.request().resourceType() === 'stylesheet') {
                stylesheets.push({ url: response.url(), status: response.status() });
            }
        });

        await page.goto('file://' + path.resolve(pagePath), { waitUntil: 'load' });

        // Web fonts change text metrics, and text metrics change row heights. Wait for them rather than
        // racing them, so the numbers are stable run to run.
        await page.evaluate(() => document.fonts.ready.then(() => true));

        // The auto-fit pane is the one place this script runs the component's own code rather than only
        // looking at what that code produced. A fit is a measurement written in JavaScript, so the only
        // honest check of it is to run the shipped function against the real theme and read what the
        // columns became - and to read it off the page, never off the fit's own arithmetic.
        const autoFit = await page.evaluate(async () => {
            const pane = document.querySelector('.pane[data-autofit]');

            if (!pane || !window.__fastgrid) {
                return null;
            }

            const table = pane.querySelector('table');
            const round = value => Math.round(value * 100) / 100;

            // Header widths rather than body ones: under table-layout:fixed they are the column, and
            // the header row is the one row every pane has.
            const widths = () => [...table.querySelectorAll(':scope > thead > tr:first-child > th')]
                .map(th => round(th.getBoundingClientRect().width));

            // How many cells in each column are drawing an ellipsis - content wider than the box it
            // has. This is the state a fit exists to leave, and it is the browser's answer rather than
            // a restatement of what the fit computed.
            const truncated = () => {
                const counts = {};

                for (const row of table.querySelectorAll(':scope > tbody > tr.rz-data-row')) {
                    [...row.children].forEach((cell, index) => {
                        const span = cell.querySelector(':scope > .rz-cell-data');

                        if (span && span.scrollWidth > span.clientWidth + 1) {
                            counts[index] = (counts[index] || 0) + 1;
                        }
                    });
                }

                return counts;
            };

            const columns = [...table.querySelectorAll(':scope > colgroup > col')].length;
            const indices = [...Array(columns).keys()];

            // MaxWidth read back off the cell the server wrote it to, rather than restated here: a
            // check that tells the function what to clamp to has agreed with itself about the number.
            const bounds = indices.map(index => {
                const cell = table.querySelector(
                    `:scope > tbody > tr.rz-data-row > td:nth-child(${index + 1})`);

                return cell && cell.style.maxWidth ? cell.style.maxWidth : null;
            });

            const cols = table.querySelector(':scope > colgroup');
            const tableWidth = () => round(table.getBoundingClientRect().width);
            const before = { widths: widths(), truncated: truncated(), tableWidth: tableWidth() };

            // Standing in for the server, which passes the same things: no toggle column on this pane,
            // and the last column left bare so the browser hands it the remainder.
            // The pass itself, timed. This is the only channel that can answer what auto-fit costs:
            // gridbench is bUnit, so the reflow, the scrollWidth walk and the getComputedStyle calls
            // all read as zero there.
            const started = performance.now();

            const written = await window.__fastgrid.autoFit({
                table: table.id, indices, min: indices.map(() => null), max: bounds,
                toggleOffset: 0, bare: columns - 1, wait: false, animate: false,
                overflow: 'scroll', required: indices.map(() => false),
            });

            const elapsed = round(performance.now() - started);

            // Captured here, not at return time. Everything below deliberately disturbs the columns -
            // stacking the table, squeezing the pane, re-running the fit to watch it animate - and the
            // survey of what the real fit produced has to be the real fit's, not whatever the last
            // probe happened to leave behind.
            const after = { widths: widths(), truncated: truncated(), tableWidth: tableWidth() };

            // The Responsive guard, checked against the condition it actually tests rather than by
            // resizing the viewport - the theme's breakpoint is a media query, so a below-breakpoint
            // pane cannot sit on the same page as the others. `display: block` on the table is what
            // that media query applies, and it is what makes a colgroup width stop deciding anything.
            const widthsThen = [...cols.children].map(col => col.style.width);

            table.style.display = 'block';

            const declined = await window.__fastgrid.autoFit({
                table: table.id, indices, min: indices.map(() => null), max: bounds,
                toggleOffset: 0, bare: columns - 1, wait: false, animate: false,
                overflow: 'scroll', required: indices.map(() => false),
            });

            const widthsNow = [...cols.children].map(col => col.style.width);

            table.style.display = '';

            const stacked = {
                answered: declined,
                wroteNothing: widthsNow.every((w, i) => w === widthsThen[i])
            };

            // The animation, on the run that is supposed to have one. Sampled mid-flight rather than
            // by asking what CSS is declared: a transition that is declared and not running looks
            // identical to one that is, from the stylesheet.
            const at = () => [...table.querySelectorAll(':scope > thead > tr:first-child > th')]
                .map(cell => round(cell.getBoundingClientRect().width));

            // Counted rather than sampled mid-flight. Headless Chromium runs the animation clock free
            // of wall time - all four transitions here start and finish inside 90ms of a 200ms run -
            // so an intermediate width is not observable in this environment even though it is correct
            // in a real browser. What is observable, and is the whole contract, is whether a transition
            // ran at all and for which caller.
            const run = async (animate, overflow) => {
                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                const from = at();

                let started = 0;
                const count = () => { started++; };
                table.addEventListener('transitionstart', count, true);

                await window.__fastgrid.autoFit({
                    table: table.id, indices: indices, min: indices.map(() => null), max: bounds,
                    toggleOffset: 0, bare: columns - 1, wait: false, animate: animate,
                    overflow: overflow, required: indices.map(() => false),
                });

                await new Promise(resolve => setTimeout(resolve, 500));

                table.removeEventListener('transitionstart', count, true);

                return {
                    from,
                    settled: at(),
                    started,
                    stillAnimating: table.classList.contains('rz-fastgrid-animating')
                };
            };

            // Run under both overflow modes. Fitting to the container writes the columns from its own
            // arithmetic, and doing that before the transition was armed meant the final write had
            // nothing left to change - an asked-for fit on a fitted grid moved without animating.
            const animation = {
                asked: await run(true, 'scroll'),
                automatic: await run(false, 'scroll'),
                askedWhileFitting: await run(true, 'fit')
            };

            window.__fastgrid.releaseFit(table.id);
            table.style.minWidth = '';
            [...cols.children].forEach(col => { col.style.width = ''; });
            table.getBoundingClientRect();

            // A container too narrow for the fitted columns. The table is meant to overflow and the
            // wrapper to scroll - but a col with no width in an overflowed table gets nothing at all,
            // so the bare column would render zero pixels wide and its content would simply not be
            // there. Squeezed hard enough that no arrangement of these columns could fit.
            const squeezed = await (async () => {
                const restore = pane.style.width;

                [...cols.children].forEach(col => { col.style.width = ''; });
                pane.style.width = '150px';
                table.getBoundingClientRect();

                await window.__fastgrid.autoFit({
                    table: table.id, indices: indices, min: indices.map(() => null), max: bounds,
                    toggleOffset: 0, bare: columns - 1, wait: false, animate: false,
                    overflow: 'scroll', required: indices.map(() => false),
                });

                const widths = at();
                const scrolls = table.scrollWidth > pane.clientWidth;

                pane.style.width = restore;
                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                return { widths, bare: round(widths[columns - 1]), scrolls };
            })();

            // Fitting to the container instead of overflowing it. The first two columns are required
            // and must keep their measured width at every size; the rest give way down to their floor,
            // and below the point where every floor is met the grid scrolls instead.
            const fittedToContainer = await (async () => {
                const restore = pane.style.width;
                const floor = 50;
                const required = indices.map((_, k) => k < 2);

                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                await window.__fastgrid.autoFit({
                    table: table.id, indices: indices, min: indices.map(() => floor + 'px'), max: bounds,
                    toggleOffset: 0, bare: columns - 1, wait: false, animate: false,
                    overflow: 'fit', required: required,
                });

                // What the required columns settled on at full width is what they must keep.
                const wide = at();

                const steps = [];

                for (const width of [900, 700, 550, 460, 400, 320]) {
                    pane.style.width = width + 'px';
                    // One frame, because the observer answers on the next one rather than inline.
                    await new Promise(resolve => requestAnimationFrame(resolve));
                    await new Promise(resolve => requestAnimationFrame(resolve));

                    const widths = at();

                    steps.push({
                        pane: width,
                        widths,
                        floorTotal: round(parseFloat(table.style.minWidth) || 0),
                        requiredHeld: widths[0] === wide[0] && widths[1] === wide[1],
                        // A column whose content is narrower than the floor is not widened to reach
                        // it, so its effective floor is its own content width.
                        aboveFloor: widths.slice(2)
                            .every((w, i) => w >= Math.min(wide[i + 2], floor) - 1),
                        scrolls: table.scrollWidth > pane.clientWidth + 1,
                        total: round(widths.reduce((a, b) => a + b, 0))
                    });
                }

                window.__fastgrid.releaseFit(table.id);
                table.style.minWidth = '';
                pane.style.width = restore;
                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                return { wide, steps };
            })();

            // No MinWidth on anything, at two pressures. Easing off first: every heading should still
            // fit, because the soft floor holds there. Then past what the columns can give, where a
            // heading may be spent - but the values under it may not, and nothing may vanish.
            const defaultFloor = await (async () => {
                const restore = pane.style.width;

                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                await window.__fastgrid.autoFit({
                    table: table.id, indices: indices, min: indices.map(() => null), max: indices.map(() => null),
                    toggleOffset: 0, bare: columns - 1, wait: false, animate: false,
                    overflow: 'fit', required: indices.map(() => false),
                });

                // Asked as "is this ellipsised" rather than by re-deriving what it needs. Two traps in
                // one line: .rz-column-title is `flex: auto; width: 100%`, so it never overflows and
                // always reports the column's own width - the element that actually clips is
                // .rz-column-title-content, which carries the overflow and the ellipsis. Measured on
                // the wrong one it answers "nothing is clipped" at every width, including 38px.
                const clipped = (row, inner) => [...table.querySelectorAll(row)]
                    .map(cell => {
                        const el = cell.querySelector(inner);
                        return el ? el.scrollWidth > el.clientWidth + 1 : false;
                    });

                const squeeze = async width => {
                    pane.style.width = width + 'px';
                    await new Promise(resolve => requestAnimationFrame(resolve));
                    await new Promise(resolve => requestAnimationFrame(resolve));

                    const widths = at();

                    return {
                        pane: width,
                        widths,
                        total: round(widths.reduce((a, b) => a + b, 0)),
                        // What the table refuses to shrink below: the sum of every hard floor. A grid
                        // standing on it has nothing left to give that is not a value.
                        floorTotal: round(parseFloat(table.style.minWidth) || 0),
                        narrowest: round(Math.min(...widths)),
                        headings: clipped(':scope > thead > tr:first-child > th',
                            '.rz-column-title-content'),
                        values: clipped(':scope > tbody > tr.rz-data-row:first-child > td',
                            '.rz-cell-data')
                    };
                };

                const eased = await squeeze(700);
                const hard = await squeeze(120);

                window.__fastgrid.releaseFit(table.id);
                table.style.minWidth = '';
                pane.style.width = restore;
                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                return {
                    eased,
                    hard,
                    // Within a pixel per column of the floor total, which is what says the second
                    // round ran: stopping at the soft floors leaves the table wider than this.
                    restsOnItsFloor: Math.abs(hard.total - hard.floorTotal) <= hard.widths.length,
                    headingsHoldWhenEased: eased.headings.every(t => !t),
                    valuesHoldWhenHard: hard.values.every(t => !t),
                    narrowest: hard.narrowest
                };
            })();

            // A fitted grid, then one column fitted on its own - what a double-click on a resize
            // handle does. It cannot rebuild the distribution, but it must not take it down either.
            const singleColumnOnAFitGrid = await (async () => {
                const restore = pane.style.width;

                [...cols.children].forEach(col => { col.style.width = ''; });
                pane.style.width = '700px';
                table.getBoundingClientRect();

                await window.__fastgrid.autoFit({
                    table: table.id, indices: indices, min: indices.map(() => null), max: indices.map(() => null),
                    toggleOffset: 0, bare: columns - 1, wait: false, animate: false,
                    overflow: 'fit', required: indices.map(() => false),
                });

                await new Promise(resolve => requestAnimationFrame(resolve));

                const floorBefore = table.style.minWidth;

                await window.__fastgrid.autoFit({
                    table: table.id, indices: [1], min: [null], max: [null],
                    toggleOffset: 0, bare: -1, wait: false, animate: true,
                    overflow: 'keep', required: [false],
                });

                await new Promise(resolve => requestAnimationFrame(resolve));

                // The test of whether it is still watching: move the container and see if it answers.
                const wideAgain = at();
                pane.style.width = '520px';
                await new Promise(resolve => requestAnimationFrame(resolve));
                await new Promise(resolve => requestAnimationFrame(resolve));
                const narrowed = at();

                const answer = {
                    floorBefore,
                    floorAfter: table.style.minWidth,
                    stillFollowing: narrowed.some((w, i) => w !== wideAgain[i])
                };

                window.__fastgrid.releaseFit(table.id);
                table.style.minWidth = '';
                pane.style.width = restore;
                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                return answer;
            })();

            // A MinWidth written in something other than pixels. Under Scroll the browser is handed
            // `max(5rem, 123px)` and resolves it; fitting to a container is arithmetic and has to
            // arrive at the same number without parsing the string.
            const units = await (async () => {
                const restore = pane.style.width;

                // What 5rem is on this page, asked of the browser rather than assumed to be 80.
                const yardstick = document.createElement('div');
                yardstick.style.cssText = 'position:absolute;visibility:hidden;width:5rem;';
                pane.appendChild(yardstick);
                const rem5 = round(yardstick.getBoundingClientRect().width);
                pane.removeChild(yardstick);

                [...cols.children].forEach(col => { col.style.width = ''; });
                pane.style.width = '900px';
                table.getBoundingClientRect();

                await window.__fastgrid.autoFit({
                    table: table.id, indices: indices, min: indices.map(() => '5rem'), max: indices.map(() => null),
                    toggleOffset: 0, bare: columns - 1, wait: false, animate: false,
                    overflow: 'fit', required: indices.map(() => false),
                });

                pane.style.width = '150px';
                await new Promise(resolve => requestAnimationFrame(resolve));
                await new Promise(resolve => requestAnimationFrame(resolve));

                const widths = at();

                window.__fastgrid.releaseFit(table.id);
                table.style.minWidth = '';
                pane.style.width = restore;
                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                // And the same question for a percentage, which resolves against a containing block
                // rather than a font - a different way for a probe to be measured in the wrong place.
                [...cols.children].forEach(col => { col.style.width = ''; });
                pane.style.width = '900px';
                table.getBoundingClientRect();

                await window.__fastgrid.autoFit({
                    table: table.id, indices: indices, min: indices.map(() => '20%'), max: indices.map(() => null),
                    toggleOffset: 0, bare: columns - 1, wait: false, animate: false,
                    overflow: 'fit', required: indices.map(() => false),
                });

                pane.style.width = '150px';
                await new Promise(resolve => requestAnimationFrame(resolve));
                await new Promise(resolve => requestAnimationFrame(resolve));

                const percent = at();

                window.__fastgrid.releaseFit(table.id);
                table.style.minWidth = '';

                return {
                    rem5,
                    widths,
                    narrowest: round(Math.min(...widths)),
                    // Every column floored at 5rem rather than at what its values happened to need.
                    honoured: widths.every(w => w >= rem5 - 1),
                    percent,
                    // 20% of the 900px the fit was taken at. A percentage the probe could not resolve
                    // is dropped as unusable, and the columns fall back to their content instead.
                    percentHonoured: percent.every(w => w >= 900 * 0.2 - 2)
                };
            })();

            // A live fit, then the table stacked the way the Responsive breakpoint stacks it. The
            // observer must stop writing: the guard that refuses the first fit means nothing if the
            // one watching the container goes on answering after the colgroup has stopped deciding.
            const stackedWhileWatching = await (async () => {
                const restore = pane.style.width;

                [...cols.children].forEach(col => { col.style.width = ''; });
                pane.style.width = '900px';
                table.getBoundingClientRect();

                await window.__fastgrid.autoFit({
                    table: table.id, indices: indices, min: indices.map(() => null), max: indices.map(() => null),
                    toggleOffset: 0, bare: columns - 1, wait: false, animate: false,
                    overflow: 'fit', required: indices.map(() => false),
                });

                await new Promise(resolve => requestAnimationFrame(resolve));

                const before = [...cols.children].map(col => col.style.width);

                // What the media query does, and then a resize the observer would answer.
                table.style.display = 'block';
                pane.style.width = '400px';

                await new Promise(resolve => requestAnimationFrame(resolve));
                await new Promise(resolve => requestAnimationFrame(resolve));

                const after = [...cols.children].map(col => col.style.width);

                table.style.display = '';
                window.__fastgrid.releaseFit(table.id);
                table.style.minWidth = '';
                pane.style.width = restore;
                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                return { before, after, wroteNothing: after.every((w, i) => w === before[i]) };
            })();

            // Fitting to the container while one column is not being fitted at all - what a declared
            // Width or AutoFit="false" column is. Its width is space the others cannot have, so a fit
            // that ignores it sizes the rest to the whole container and the table overflows the one
            // mode whose entire purpose is not overflowing.
            const withReserved = await (async () => {
                const restore = pane.style.width;
                const kept = indices.slice(0, -1);

                [...cols.children].forEach(col => { col.style.width = ''; });
                // Narrow enough that the fitted columns must give something up, wide enough that they
                // can. With room to spare the bare column quietly absorbs the difference and a fit
                // that ignored the reserved column still looks correct; narrower than every floor put
                // together, both answers scroll and the difference is only how far. In between is the
                // one band where the right answer fits and the wrong one does not.
                pane.style.width = '700px';
                table.getBoundingClientRect();

                // The column left out keeps a width of its own, the way a declared one would.
                cols.children[columns - 1].style.width = '220px';
                table.getBoundingClientRect();

                await window.__fastgrid.autoFit({
                    table: table.id, indices: kept, min: kept.map(() => null),
                    max: kept.map(() => null), toggleOffset: 0, bare: kept[kept.length - 1],
                    wait: false, animate: false, overflow: 'fit',
                    required: kept.map(() => false),
                });

                await new Promise(resolve => requestAnimationFrame(resolve));

                const widths = at();
                const total = round(widths.reduce((a, b) => a + b, 0));
                const room = round(pane.clientWidth);

                const answer = {
                    widths,
                    total,
                    room,
                    reservedColumn: round(widths[columns - 1]),
                    fitsTheContainer: total <= room + 1,
                    floorTotal: round(parseFloat(table.style.minWidth) || 0)
                };

                window.__fastgrid.releaseFit(table.id);
                table.style.minWidth = '';
                pane.style.width = restore;
                [...cols.children].forEach(col => { col.style.width = ''; });
                table.getBoundingClientRect();

                return answer;
            })();

            return {
                animation,
                singleColumnOnAFitGrid,
                units,
                stackedWhileWatching,
                withReserved,
                defaultFloor,
                fittedToContainer,
                squeezed,
                stacked,
                before,
                after,
                written,
                elapsed,
                rowsMeasured: table.querySelectorAll(':scope > tbody > tr.rz-data-row').length,
                paneWidth: round(pane.getBoundingClientRect().width)
            };
        });

        const report = await page.evaluate(() => {
            const round = value => Math.round(value * 100) / 100;
            const height = element => (element ? round(element.getBoundingClientRect().height) : null);
            const width = element => (element ? round(element.getBoundingClientRect().width) : null);

            return {
                // Custom properties only the Radzen theme defines. If these resolve, the stylesheet did not
                // merely arrive - it is in effect on this document, which is the thing that matters.
                themeProbe: getComputedStyle(document.documentElement)
                    .getPropertyValue('--rz-grid-cell-padding').trim(),
                themeCellHeightProbe: getComputedStyle(document.documentElement)
                    .getPropertyValue('--rz-grid-cell-line-height').trim(),
                grids: [...document.querySelectorAll('.pane')].map(pane => ({
                    grid: pane.dataset.grid,
                    headerCell: height(pane.querySelector('thead th')),
                    bodyCell: height(pane.querySelector('tbody td')),
                    table: height(pane.querySelector('table')),
                    headerCellPadding: (() => {
                        const th = pane.querySelector('thead th');
                        return th ? getComputedStyle(th).padding : null;
                    })(),
                    rowCount: pane.querySelectorAll('tbody tr').length,

                    // Null on the panes with no row detail, which is what tells the tests which pane
                    // they are looking at without another dataset attribute to keep in step.
                    toggleCell: height(pane.querySelector('tbody td.rz-col-icon')),
                    dataRow: height(pane.querySelector('tbody tr')),

                    // Width as well as height for the toggle cell: an empty element with horizontal
                    // padding takes width without taking height, so a height-only check would call it
                    // inert and it would not be.
                    toggleCellWidth: width(pane.querySelector('tbody td.rz-col-icon')),

                    // The cell itself is inside a table-layout:fixed table, so its box says nothing
                    // about what it contains. The button's does: anything in the cell taking space
                    // moves it.
                    toggleButtonLeft: (() => {
                        const cell = pane.querySelector('tbody td.rz-col-icon');
                        const button = cell && cell.querySelector('button');
                        if (!button) { return null; }
                        return round(button.getBoundingClientRect().left - cell.getBoundingClientRect().left);
                    })(),
                    toggleButtonWidth: width(pane.querySelector('tbody td.rz-col-icon button')),

                    // Paint, not geometry. The theme nests its selected-row rule inside .rz-selectable,
                    // so a grid can put rz-state-highlight on exactly the right tr and still draw a row
                    // that looks like every other one. Reading the computed background of a selected
                    // cell and an unselected one is the only check that can tell those apart.
                    // A frozen column is only frozen if it survives a scroll. Scroll the pane's own
                    // container sideways and record how far the first cell moved against a cell that
                    // was never pinned: the frozen one should not have moved at all.
                    frozenHold: (() => {
                        const scroller = pane.querySelector('.rz-data-grid-data');
                        const first = pane.querySelector('tbody tr td');
                        const loose = pane.querySelector('tbody tr td:last-child');
                        if (!scroller || !first || !loose) { return null; }
                        const before = first.getBoundingClientRect().left;
                        const looseBefore = loose.getBoundingClientRect().left;
                        scroller.scrollLeft = 200;
                        const moved = round(first.getBoundingClientRect().left - before);
                        const looseMoved = round(loose.getBoundingClientRect().left - looseBefore);
                        scroller.scrollLeft = 0;
                        return { scrolled: 200, frozenMoved: moved, unfrozenMoved: looseMoved };
                    })(),

                    // Which element is actually on top where a frozen column overlaps a scrolled one.
                    // Position and inset can both be right while the column sliding underneath paints
                    // over the top: the theme makes every header cell sticky at the same z-index, so a
                    // frozen header ties with its neighbours and document order decides. Ask the
                    // document what it would hit at a point inside the frozen header.
                    frozenOverlap: (() => {
                        const scroller = pane.querySelector('.rz-data-grid-data');
                        const table = pane.querySelector('table');
                        if (!scroller || !table) { return null; }

                        // elementFromPoint works in viewport coordinates, and this pane sits well below
                        // the fold on a page of several grids - without this the hit test lands outside
                        // the window and reports nothing on top of anything.
                        pane.scrollIntoView({ block: 'start' });
                        scroller.scrollLeft = 200;

                        // Which columns are pinned is read from the title row, and then every row is
                        // asked what is on top at that column's x. Testing by position rather than by
                        // looking for the frozen class is the point: a row that never got the class at
                        // all - the filter row did not - has nothing to find, so a class-driven check
                        // skips it in silence and calls the grid clean.
                        const head = table.querySelector('thead tr');
                        if (!head) { return null; }

                        const pinned = [...head.children]
                            .map((cell, index) => ({ cell, index }))
                            .filter(c => c.cell.classList.contains('rz-frozen-cell'))
                            .map(c => ({ index: c.index, x: c.cell.getBoundingClientRect().left + 8 }));

                        const rows = [...table.querySelectorAll('tr')];
                        const covered = [];

                        // A row has to be visible in the window *and* inside the scroller before it can
                        // be asked what is on top of it: one clipped by the scroller's own overflow hit
                        // tests to whatever is painted there instead - the pager, the scrollbar - and
                        // would be reported as covered on a grid that is perfectly correct.
                        const clip = scroller.getBoundingClientRect();

                        for (const row of rows) {
                            const r = row.getBoundingClientRect();
                            if (r.height === 0 || r.bottom < 0 || r.top > window.innerHeight) { continue; }
                            if (r.top < clip.top || r.bottom > clip.bottom) { continue; }

                            for (const { index, x } of pinned) {
                                const hit = document.elementFromPoint(x, r.top + r.height / 2);
                                const cell = hit && hit.closest('th, td');

                                // Whatever is drawn at the pinned column's x has to be that column's own
                                // cell in this row. Anything else means something scrolled over it.
                                if (!cell || cell.parentNode !== row || cell.cellIndex !== index) {
                                    covered.push(row.parentNode.tagName.toLowerCase() + ' row ' +
                                        ([...row.parentNode.children].indexOf(row)) + ' col ' + index);
                                }
                            }
                        }

                        scroller.scrollLeft = 0;

                        return { rowsChecked: rows.length, pinnedColumns: pinned.length, covered };
                    })(),

                    // Every data cell's width, so a colgroup that is misaligned by one is visible. The
                    // toggle column is a cell with no col of its own, and without a col standing in for
                    // it each declared width lands on the column to its left.
                    dataCellWidths: [...(pane.querySelector('tbody tr')?.querySelectorAll('td') ?? [])]
                        .map(td => round(td.getBoundingClientRect().width)),

                    // The keyboard cursor, asked the way round that can fail. Not "does the focused
                    // cell carry rz-state-focused" - that is the check that passed while the filter
                    // row's pinning was missing, because a row that never got the class has nothing to
                    // find and is skipped in silence. Ask what is painted at the cursor instead, and
                    // whether it differs from the cells and rows around it.
                    focus: (() => {
                        const cell = pane.querySelector('tbody td.rz-state-focused');
                        if (!cell) { return null; }

                        const row = cell.parentNode;
                        const other = [...row.children].find(c => c !== cell);
                        // Two rows away, not one: striping is :nth-child, so adjacent rows differ
                        // whatever focus does and a comparison across them passes on its own. The same
                        // trap the selected-row probe two blocks down already carries.
                        const rows = [...pane.querySelectorAll('tbody tr')];
                        const at = rows.indexOf(row);
                        const elsewhere = rows[at + 2] || rows[at - 2];

                        const outline = e => getComputedStyle(e).outlineStyle + ' ' + getComputedStyle(e).outlineWidth;

                        // What the cell actually shows. A frozen cell keeps an opaque background of its
                        // own and carries the row's colour on the pseudo-element the theme uses for the
                        // seam, so reading the cell's own background there reports white on a row that
                        // is painted perfectly well.
                        const shown = td => {
                            const own = getComputedStyle(td).backgroundColor;
                            if (!td.classList.contains('rz-frozen-cell')) { return own; }
                            const seam = getComputedStyle(td, '::before').backgroundColor;
                            return seam && seam !== 'rgba(0, 0, 0, 0)' ? seam : own;
                        };

                        // A frozen cell paints its own background, so the one thing that can go wrong
                        // for a focused frozen cell is losing it and letting the column scrolling
                        // underneath show through. Ask the document what is on top - and bring the cell
                        // into the window first, because elementFromPoint works in viewport coordinates
                        // and a point below the fold answers null on a grid that is perfectly correct.
                        const scroller = pane.querySelector('.rz-data-grid-data');
                        let onTop = null;
                        let onTopWas = null;

                        if (scroller) {
                            cell.scrollIntoView({ block: 'center' });
                            scroller.scrollLeft = 200;

                            const box = cell.getBoundingClientRect();
                            const x = box.left + 8;
                            const y = box.top + box.height / 2;

                            // Null means the question could not be asked, which is not the same answer
                            // as "something is drawn over it" and must not be reported as one.
                            onTopWas = 'at ' + Math.round(x) + ',' + Math.round(y) +
                                ' in ' + window.innerWidth + 'x' + window.innerHeight;

                            if (x >= 0 && y >= 0 && x <= window.innerWidth && y <= window.innerHeight) {
                                const hit = document.elementFromPoint(x, y);
                                onTop = (hit && hit.closest('th, td')) === cell;
                                onTopWas = (hit
                                    ? hit.tagName.toLowerCase() + '.' + (hit.className || '(none)')
                                    : '(nothing)') + ' ' + onTopWas;
                            }

                            scroller.scrollLeft = 0;
                        }

                        // The cursor is an outline on the cell and a background on the row. Either
                        // alone shows a lit row with no cursor in it, or a cursor on an unlit row.
                        return {
                            outline: outline(cell),
                            otherOutline: other ? outline(other) : null,
                            background: shown(cell),

                            // The same column of an unfocused row, so the comparison is like for like -
                            // a frozen column and a scrolling one are painted differently whatever
                            // focus does.
                            otherRowBackground: elsewhere
                                ? shown(elsewhere.children[cell.cellIndex])
                                : null,
                            onTop,
                            onTopWas,
                        };
                    })(),

                    // Rows 1 and 3, not 1 and 2: striping is :nth-child, so adjacent rows differ
                    // whatever selection does and a comparison across them would pass on its own.
                    selectedRowBackground: (() => {
                        const row = pane.querySelector('tbody tr:nth-child(1)');
                        const td = row && row.classList.contains('rz-state-highlight')
                            ? row.querySelector('td') : null;
                        return td ? getComputedStyle(td).backgroundColor : null;
                    })(),
                    unselectedRowBackground: (() => {
                        const row = pane.querySelector('tbody tr:nth-child(3)');
                        const td = row && !row.classList.contains('rz-state-highlight')
                            ? row.querySelector('td') : null;
                        return td ? getComputedStyle(td).backgroundColor : null;
                    })(),
                })),
            };
        });

        if (failures.length > 0) {
            throw new Error('resources failed to load, so the page is not styled as intended:\n  ' +
                failures.join('\n  '));
        }

        report.stylesheets = stylesheets;
        report.autoFit = autoFit;

        process.stdout.write(JSON.stringify(report, null, 2) + '\n');
    } finally {
        await browser.close();
    }
}

main().catch(error => {
    process.stderr.write((error && error.stack ? error.stack : String(error)) + '\n');
    process.exit(1);
});
