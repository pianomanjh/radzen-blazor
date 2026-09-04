// Drives the published, trimmed app in a real browser and asserts the grid still works.
//
// Publishing without a trim warning says the linker had no objection. It does not say the application
// runs: a trimmed member is only missing once something reaches for it, which is at run time. So this
// loads the app, sorts by a string column and a numeric one, filters, and checks the answers - the
// three things the column-composed path exists to do.
//
// Playwright is resolved from the global npm root when there is no node_modules beside this file,
// matching how the styling parity check finds it.
const path = require('path');

function playwright() {
    try {
        return require('playwright');
    } catch {
        const root = require('child_process').execSync('npm root -g').toString().trim();

        return require(path.join(root, 'playwright'));
    }
}

const url = process.argv[2] || 'http://localhost:8477/';
const failures = [];

function expect(what, actual, expected) {
    const a = JSON.stringify(actual);
    const e = JSON.stringify(expected);

    if (a !== e) {
        failures.push(`${what}\n    expected ${e}\n    actual   ${a}`);
    }
}

(async () => {
    const browser = await playwright().chromium.launch();
    const page = await browser.newPage();
    const errors = [];

    page.on('pageerror', e => errors.push(`pageerror: ${e.message}`));
    page.on('console', m => {
        // A missing favicon is not a trimming failure.
        if (m.type() === 'error' && !m.text().includes('favicon')) {
            errors.push(`console: ${m.text()}`);
        }
    });

    await page.goto(url, { waitUntil: 'networkidle', timeout: 120000 });
    await page.waitForSelector('table.rz-grid-table tbody tr', { timeout: 120000 });

    const names = () => page.$$eval('tbody tr td:first-child', tds => tds.map(t => t.textContent.trim()));

    expect('the grid renders its rows', await names(),
        ['Alice', 'Bob', 'Charlie', 'Diana', 'Eve']);

    // The lookup columns, which is what a trimmed member would be missing from: their cells resolve an
    // id the row carries against names the grid holds once, and a trimmer that took either apart would
    // leave them blank rather than throw.
    const column = n => page.$$eval(`tbody tr td:nth-child(${n})`, tds => tds.map(t => t.textContent.trim()));

    expect('a lookup cell shows the name its id stands for', await column(7),
        ['Ops', 'Design', 'Ops', 'Design', 'Ops']);

    expect('a lookup collection cell lists them', await column(8),
        ['Red, Green', 'Blue', '', 'Green, Blue', 'Red']);

    // Twice: the sample is already ascending by name, so only the descending click proves a sort ran.
    await page.click('thead th:first-child');
    await page.waitForTimeout(300);
    await page.click('thead th:first-child');
    await page.waitForTimeout(300);

    expect('sorting a string column descending', await names(),
        ['Eve', 'Diana', 'Charlie', 'Bob', 'Alice']);

    // A numeric column too, so the comparison is not all strings.
    await page.click('thead th:nth-child(2)');
    await page.waitForTimeout(300);

    expect('sorting an int column ascending', await names(),
        ['Charlie', 'Eve', 'Alice', 'Diana', 'Bob']);

    // Contains, case-sensitive by default - which is why Alice, with no lowercase a, is not a match.
    // Scoped to the column now that more than one box is on the page.
    const box = n => `thead th:nth-child(${n}) input.rz-textbox`;

    await page.fill(box(1), 'a');
    await page.dispatchEvent(box(1), 'change');
    await page.waitForTimeout(500);

    expect('filtering by a string', await names(), ['Charlie', 'Diana']);

    await page.fill(box(1), '');
    await page.dispatchEvent(box(1), 'change');
    await page.waitForTimeout(500);

    // A lookup column matches what is typed against the names and filters by the ids they carry, which
    // is the path that would have needed a member reached by name if it had been built the other way.
    await page.fill(box(7), 'Ops');
    await page.dispatchEvent(box(7), 'change');
    await page.waitForTimeout(500);

    // Still in the ascending-by-Value order the sort above left, which is the point of asserting the
    // order rather than the set: the filter narrows and does not re-order.
    expect('filtering a lookup column by a name', await names(), ['Charlie', 'Eve', 'Alice']);

    await browser.close();

    for (const error of errors) {
        failures.push(`the page reported an error: ${error}`);
    }

    if (failures.length > 0) {
        console.error(`The trimmed app did not behave:\n\n  ${failures.join('\n  ')}\n`);
        process.exit(1);
    }

    console.log('The trimmed app renders, resolves its lookups, sorts by string and by number, and filters.');
})().catch(e => {
    console.error(`Could not drive the trimmed app: ${e.message}`);
    process.exit(1);
});
