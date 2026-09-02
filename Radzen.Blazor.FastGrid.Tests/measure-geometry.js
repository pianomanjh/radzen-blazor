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
        const page = await browser.newPage({ viewport: { width: 1100, height: 900 } });

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

        process.stdout.write(JSON.stringify(report, null, 2) + '\n');
    } finally {
        await browser.close();
    }
}

main().catch(error => {
    process.stderr.write((error && error.stack ? error.stack : String(error)) + '\n');
    process.exit(1);
});
