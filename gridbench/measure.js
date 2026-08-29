const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium' });
  const page = await browser.newPage({ viewport: { width: 1100, height: 900 } });
  await page.goto('file://' + __dirname + '/compare.html');
  await page.waitForTimeout(300);
  const out = await page.evaluate(() => {
    const panes = [...document.querySelectorAll('.pane')];
    return panes.map(p => {
      const th = p.querySelector('thead th');
      const td = p.querySelector('tbody td');
      const table = p.querySelector('table');
      const r = e => e ? Math.round(e.getBoundingClientRect().height * 100) / 100 : null;
      return {
        name: p.querySelector('h2').textContent,
        headerCell: r(th), bodyCell: r(td), table: r(table),
        headerFont: th ? getComputedStyle(th.querySelector('.rz-column-title')||th).fontSize : null,
        thPadding: th ? getComputedStyle(th).padding : null,
      };
    });
  });
  console.table(out);
  await browser.close();
})();
