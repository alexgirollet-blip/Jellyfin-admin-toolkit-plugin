/*
 * Headless smoke test for the Admin Toolkit dashboard page with a mocked API.
 * The mocked responses (fixtures/api-sample.json) are produced by the real C# services in ApiSampleExport.cs.
 * Run: node tests/client/admin-smoke.js   (SCREENSHOTS=docs/screenshots to also save screenshots)
 */
'use strict';
const fs = require('fs');
const path = require('path');
let chromium;
try { ({ chromium } = require('playwright')); } catch (e) { ({ chromium } = require(require('child_process').execSync('npm root -g').toString().trim() + '/playwright')); }

const ORIGIN = 'http://jf.test';
const root = path.join(__dirname, '../..');
const pageHtml = fs.readFileSync(path.join(root, 'src/Jellyfin.Plugin.AdminToolkit/Web/adminToolkit.html'), 'utf8');
const fragment = pageHtml.slice(pageHtml.indexOf('<div id="AdminToolkitPage"'), pageHtml.lastIndexOf('</body>'));
const sample = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/api-sample.json'), 'utf8'));
const fixtures = path.join(root, 'tests/Jellyfin.Plugin.AdminToolkit.Tests/Fixtures');
const shots = process.env.SCREENSHOTS ? path.resolve(process.env.SCREENSHOTS) : null;

function harness(theme) {
    const bg = theme === 'dark' ? 'background:#101010;color:#eee' : 'background:#f2f2f2;color:#111';
    return `<!DOCTYPE html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head><body style="${bg};font-family:sans-serif;margin:0;padding:8px">
<script>
window.ApiClient = {
  accessToken: function(){ return 'admintok'; },
  serverId: function(){ return 'srv1'; },
  getUrl: function(p, params){ var q = params ? Object.keys(params).filter(function(k){return params[k]!==undefined&&params[k]!==null;}).map(function(k){return k+'='+encodeURIComponent(params[k]);}).join('&') : ''; return '${ORIGIN}/' + p.replace(/^\\//,'') + (q ? '?' + q : ''); }
};
</script>
${fragment}
<script>document.querySelector('#AdminToolkitPage').dispatchEvent(new Event('pageshow'));</script>
</body></html>`;
}

let failures = 0;
function check(cond, msg) { console.log((cond ? 'PASS ' : 'FAIL ') + msg); if (!cond) { failures++; } }

async function setup(browser, opts) {
    const context = await browser.newContext({ viewport: opts.viewport, colorScheme: opts.theme });
    const page = await context.newPage();
    const errors = [];
    const posted = {};
    page.on('pageerror', e => errors.push(String(e)));
    page.on('console', m => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) { errors.push(m.text()); } });
    const g = sample.duplicates.result.groups[0];
    await page.route(ORIGIN + '/**', async route => {
        const req = route.request();
        const url = new URL(req.url());
        const p = url.pathname.replace(/^\/AdminToolkit\/?/, '');
        const json = (b, status) => route.fulfill({ status: status || 200, contentType: 'application/json', body: JSON.stringify(b) });
        const fail = (status, code, message) => json({ error: code, message: message, endpoint: req.method() + ' ' + url.pathname, status: status }, status);
        if (url.pathname === '/admin') { return route.fulfill({ contentType: 'text/html; charset=utf-8', body: harness(opts.theme) }); }
        if (url.pathname.startsWith('/Items/')) { return route.fulfill({ contentType: 'image/jpeg', body: fs.readFileSync(path.join(fixtures, 'poster.jpg')) }); }
        if (req.headers()['authorization'] !== 'MediaBrowser Token="admintok"') { return fail(401, 'unauthorized', 'no token'); }
        if (req.method() === 'POST') { posted[p] = JSON.parse(req.postData() || 'null'); }
        switch (p) {
            case 'Status': return json(sample.status);
            case 'Duplicates': return json(sample.duplicates);
            case 'Duplicates/Plan': return json(sample.plan);
            case 'Duplicates/Execute': return json(Object.assign({}, sample.plan, { dryRun: true, steps: sample.plan.steps.map(s => Object.assign({}, s, { result: 'WouldDo' })) }));
            case 'Duplicates/Mark': return json({ keep: {}, marked: { [g.id]: [] } });
            case 'Libraries': return json(sample.libraries);
            case 'Health': return json(sample.health);
            case 'Storage': return json(sample.storage);
            case 'Storage/Drives': return fail(500, 'jellyfin_api_error', 'Jellyfin could not list the libraries: InvalidOperationException: database is locked');
            case 'Activity': return json(sample.activity);
            case 'Settings': return req.method() === 'GET' ? json(sample.settings) : json({ ok: true, settings: sample.settings.settings });
            case 'SelfCheck': return fail(500, 'jellyfin_version_mismatch', "Jellyfin could not list users: This Admin Toolkit build (made for Jellyfin 10.11.10) does not match the Jellyfin version running on this server: MissingMethodException: Method not found: 'IUserManager.get_Users()'.");
            case 'Scans': return json(sample.status.scans);
            case 'Images/Search': return json([sample.imageItem.item]);
            case 'Images/Analyze': return json(posted['Images/Analyze'].urls.map((u, i) => ({ url: u, ok: true, width: 800, height: 310, bytes: 40000, format: 'png', transparency: i === 0 ? 'Transparent' : 'Opaque', transparentRatio: i === 0 ? 0.61 : 0, aspectRatio: 2.58, warnings: i === 0 ? [] : ['This image has no transparent background; Logo images are drawn over artwork and normally need transparency.'] })));
            case 'Images/Proxy': return route.fulfill({ contentType: 'image/png', body: fs.readFileSync(path.join(fixtures, 'logo-transparent.png')) });
        }
        if (/^Duplicates\/Groups\/[^/]+\/Keep$/.test(p)) {
            const keepId = posted[p].keepItemId;
            const ng = JSON.parse(JSON.stringify(g));
            ng.keepId = keepId;
            ng.copies.forEach(c => { c.decision = c.item.id === keepId ? 'Keep' : 'Remove'; c.warnings = c.item.id === keepId ? ['You chose this copy instead of the recommendation (score 1 vs 2).'] : ['This is the copy Sonarr manages (/x).']; });
            return json(ng);
        }
        if (/^Images\/Item\/[^/]+$/.test(p)) { return json(sample.imageItem); }
        if (/^Images\/Item\/[^/]+\/Remote$/.test(p)) { return json([{ url: 'https://image.tmdb.org/t/p/original/logo1.png', thumbnailUrl: 'https://image.tmdb.org/t/p/w300/logo1.png', source: 'Jellyfin', provider: 'TheMovieDb', width: 800, height: 310, language: 'en' }, { url: 'https://assets.fanart.tv/logo2.png', source: 'Jellyfin', provider: 'Fanart', width: 800, height: 310, language: 'en' }]); }
        if (/^Images\/Item\/[^/]+\/Web$/.test(p)) { return json({ configured: false, help: 'No web image search provider is configured.', query: 'x', results: [] }); }
        return fail(404, 'not_found', 'unmocked ' + p);
    });
    await page.goto(ORIGIN + '/admin');
    return { page, errors, posted, context };
}

(async () => {
    const browser = await chromium.launch(process.env.PW_CHROMIUM ? { executablePath: process.env.PW_CHROMIUM } : {});
    // ---------- desktop, dark ----------
    let { page, errors, posted, context } = await setup(browser, { viewport: { width: 1366, height: 900 }, theme: 'dark' });
    await page.waitForSelector('.at-copy');
    check(await page.isVisible('text=Safe mode ON (view only)'), 'status bar shows safe mode ON');
    check(await page.isVisible('text=KEEP (recommended)'), 'duplicate group shows KEEP (recommended)');
    check(await page.isVisible('text=REMOVE (recommended)'), 'duplicate group shows REMOVE (recommended)');
    check(await page.locator('text=/path matches Sonarr\'s path/').count() > 0, 'reasons mention the Sonarr path');
    check(await page.locator('text=/has 24\\/24 episodes/').count() > 0, 'reasons show episode completeness');
    check(await page.isVisible('.at-badge:has-text("High confidence")'), 'confidence badge shown');
    if (shots) { fs.mkdirSync(shots, { recursive: true }); await page.screenshot({ path: path.join(shots, 'duplicates-dark.png'), fullPage: true }); }

    await page.click('text=Keep this copy instead');
    await page.waitForSelector('text=KEEP (your choice)');
    check(await page.isVisible('text=KEEP (your choice)'), 'one-click override swaps keep/remove');
    check(await page.locator('text=/This is the copy Sonarr manages/').count() > 0, 'override shows the managed-copy warning');

    await page.click('button:has-text("Remove...")');
    await page.waitForSelector('dialog[open]');
    check(await page.isChecked('dialog input[type=checkbox] >> nth=-1'), 'dry run ticked by default');
    await page.click('dialog >> text=Preview');
    await page.waitForSelector('dialog >> text=Exactly what will happen');
    check(await page.locator('dialog >> text=/Space freed/').count() === 1, 'preview shows space freed');
    check(posted['Duplicates/Plan'] && posted['Duplicates/Plan'].dryRun === true, 'plan request is a dry run');
    if (shots) { await page.screenshot({ path: path.join(shots, 'remove-preview-dark.png') }); }
    await page.click('dialog >> text=Run dry run');
    await page.waitForSelector('dialog >> text=Dry run: nothing was changed');
    check(posted['Duplicates/Execute'].token === sample.plan.token, 'execute sends the preview token');
    await page.click('dialog >> text=Close');

    // ---------- images ----------
    await page.click('[data-tab="images"]');
    await page.fill('[data-panel="images"] input[type=search] >> nth=0', 'Brooklyn');
    await page.click('[data-panel="images"] >> text=Search >> nth=0');
    await page.waitForSelector('.at-pick button');
    await page.click('.at-pick button');
    await page.waitForSelector('text=Find images');
    check(await page.locator('text=/3840x2160/').count() > 0, 'current images list dimensions');
    await page.selectOption('[data-panel="images"] .at-card:has-text("Find images") select', 'Logo');
    await page.click('[data-panel="images"] .at-card:has-text("Find images") >> button:has-text("Search")');
    await page.waitForSelector('.at-badge:has-text("Transparent")');
    check(await page.locator('.at-badge:has-text("Transparent")').count() === 1, 'one result is labelled Transparent');
    check(await page.locator('.at-badge:has-text("Opaque")').count() === 1, 'the other is labelled Opaque');
    check(await page.locator('text=/no transparent background/').count() > 0, 'opaque logo warning shown');
    check(await page.locator('text=No web image search provider is configured').count() > 0, 'missing web search provider explained');
    if (shots) { await page.screenshot({ path: path.join(shots, 'images-dark.png'), fullPage: true }); }
    await page.click('button:has-text("Use this") >> nth=0');
    await page.waitForSelector('dialog >> text=Before');
    check(await page.isDisabled('dialog >> button:has-text("Apply")'), 'apply disabled in safe mode');
    await page.click('dialog >> text=Close');

    // ---------- health ----------
    await page.click('[data-tab="health"]');
    await page.waitForSelector('text=Library folders');
    check(await page.locator('text=/Episode|episode/').count() > 0, 'health issues listed');
    if (shots) { await page.screenshot({ path: path.join(shots, 'health-dark.png'), fullPage: true }); }

    // ---------- storage: drives fail, sizes still load ----------
    await page.click('[data-tab="storage"]');
    await page.waitForSelector('text=Per library');
    const err = page.locator('[data-panel="storage"] .at-error').first();
    check(await err.isVisible(), 'failing Drives section shows an error box');
    const errText = await err.innerText();
    check(/database is locked/.test(errText), 'error box shows the real message');
    check(/HTTP 500/.test(errText) && /GET \/AdminToolkit\/Storage\/Drives/.test(errText), 'error box shows status and endpoint');
    check(await page.locator('[data-panel="storage"] button:has-text("Copy diagnostics")').count() > 0, 'copy diagnostics button present');
    check(await page.isVisible('text=/Not watched by anyone/'), 'sizes section loaded despite the drives failure');

    // ---------- activity ----------
    await page.click('[data-tab="activity"]');
    await page.waitForSelector('[data-panel="activity"] table');
    check(await page.locator('[data-panel="activity"] tr').count() > 1, 'activity log rows shown');

    // ---------- settings: self-check fails, form still works ----------
    await page.click('[data-tab="settings"]');
    await page.waitForSelector('text=Save settings');
    check(await page.locator('text=/IUserManager.get_Users/').count() > 0, 'self-check version mismatch is readable');
    const keyValue = await page.inputValue('[data-panel="settings"] input[type=password] >> nth=0');
    check(keyValue.startsWith('••••'), 'API key is masked in the form');
    if (shots) { await page.screenshot({ path: path.join(shots, 'settings-dark.png'), fullPage: true }); }
    await page.click('text=Save settings');
    await page.waitForSelector('text=Saved.');
    check(posted['Settings'] && posted['Settings'].sonarrApiKey === keyValue, 'saving sends the masked key back unchanged (server keeps the stored key)');
    check(errors.length === 0, 'no script errors on desktop' + (errors.length ? ': ' + errors.join(' | ') : ''));
    await context.close();

    // ---------- phone, light ----------
    ({ page, errors, context } = await setup(browser, { viewport: { width: 390, height: 844 }, theme: 'light' }));
    await page.waitForSelector('.at-copy');
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    check(overflow <= 1, 'no horizontal page scroll on a phone (overflow ' + overflow + 'px)');
    if (shots) { await page.screenshot({ path: path.join(shots, 'duplicates-phone-light.png'), fullPage: false }); }
    await page.click('[data-tab="settings"]');
    await page.waitForSelector('text=Save settings');
    const overflow2 = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    check(overflow2 <= 1, 'settings fit a phone width (overflow ' + overflow2 + 'px)');
    check(errors.length === 0, 'no script errors on phone' + (errors.length ? ': ' + errors.join(' | ') : ''));
    await context.close();

    // ---------- desktop, light ----------
    ({ page, errors, context } = await setup(browser, { viewport: { width: 1366, height: 900 }, theme: 'light' }));
    await page.waitForSelector('.at-copy');
    if (shots) { await page.screenshot({ path: path.join(shots, 'duplicates-light.png'), fullPage: false }); }
    await context.close();

    await browser.close();
    console.log(failures ? `\n${failures} check(s) failed` : '\nAll admin page checks passed');
    process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
