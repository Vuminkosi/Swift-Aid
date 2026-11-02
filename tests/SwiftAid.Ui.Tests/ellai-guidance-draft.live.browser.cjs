// Opt-in real Gateway/Muse test. Creates a synthetic guest conversation and a local-only draft.
if (process.env.SWIFTAID_RUN_LIVE_GUIDANCE !== '1') throw new Error('Set SWIFTAID_RUN_LIVE_GUIDANCE=1 to use the live provider.');
const assert = require('node:assert/strict');
require('node:fs').mkdirSync('.codex', {recursive:true});
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE);
(async () => {
 const browser=await chromium.launch({headless:true,executablePath:process.env.SWIFTAID_CHROMIUM_PATH});
 const page=await browser.newPage({viewport:{width:Number(process.env.SWIFTAID_VIEWPORT_WIDTH || 1280),height:900}}); let latest; const errors=[]; const plans=[]; const progress=[];
 page.on('response',async r=>{if(new URL(r.url()).pathname.endsWith('/guidance/steps/complete')){try{progress.push(await r.json());}catch{}}});
 page.on('pageerror',e=>errors.push(e.message));
 page.on('response',async r=>{if(new URL(r.url()).pathname.endsWith('/guidance/plan')){try{plans.push(await r.json());}catch{}}});
 page.on('response',async r=> { if(new URL(r.url()).pathname.endsWith('/responses')&&r.ok()) {try {latest=await r.json();}catch{}} });
 try {
 await page.goto(process.env.SWIFTAID_BASE_URL || 'http://localhost:8080');
 await page.getByRole('button',{name:'Continue with essential cookies',exact:true}).click();
 await page.locator('.ellai-launcher-menu__button').click();
 await page.getByRole('menuitem',{name:'Ask',exact:true}).click();
 await page.getByRole('textbox',{name:'Message Ellai'}).fill('Show me how to save a report draft. Guide me through the actual controls.');
 await page.getByRole('button',{name:'Send to Ellai',exact:true}).click();
 const deadline=Date.now()+120000;
 while(Date.now()<deadline) {
  const turn=latest?.conversation?.at(-1);
  if(turn && ['Completed','TerminalFailure','Cancelled'].includes(turn.outcome)) {
   if (turn.uiProposals?.length) {
    const tourDeadline=Date.now()+100000;
    while(Date.now()<tourDeadline && !plans.some(p=>!['Ready','NeedsNavigation','Generating'].includes(p.status)) && await page.getByRole('button',{name:'Pause guidance',exact:true}).count()===0) await page.waitForTimeout(500);
   }
   assert.equal(plans.at(-1)?.status, 'Ready');
   assert.equal(await page.locator('.bit-bonboarding-root').evaluate(el => getComputedStyle(el).position), 'fixed');
   const popup = page.locator('.bit-bonboarding-popover');
   await popup.waitFor({ state: 'visible' });
   let box = await popup.boundingBox();
   assert(box.width < 500 && box.x >= 0, 'Tour must render a bounded popup');
   await page.screenshot({path:'.codex/ellai-guidance-styled.png',fullPage:true});
   if(plans.at(-1).plan.steps[0].controlId === 'report.entry') await popup.getByRole('button', {name:'Next',exact:true}).click();
   await page.getByText('Human health', {exact:true}).click();
   await page.getByRole('button', {name:'Continue to details',exact:true}).click();
   await page.waitForTimeout(2000);
   await page.locator('[data-ellai-control="report.save"]').click();
   await page.waitForFunction(() => document.body.innerText.includes('report draft is saved on this device'), {timeout:15000});
   await page.waitForTimeout(1500);
   if(await popup.isVisible()) {
    const finish=popup.getByRole('button',{name:/Finish|Done|Next/});
    if(await finish.count()) await finish.first().click();
   }
   await page.waitForTimeout(1500);
   await page.screenshot({path:'.codex/ellai-guidance-draft-completed.png',fullPage:true});
   assert(progress.some(p=>p.status==='Completed' && p.completedStepIds?.length===plans.at(-1).plan.steps.length), 'Every step must have durable completion');
   assert.deepEqual(errors, []);
   console.log(JSON.stringify({draftSaved:await page.locator('body').innerText().then(t=>t.includes('report draft is saved on this device')),popupRemaining:await popup.count()}));
   await page.screenshot({path:'.codex/ellai-guidance-live-probe.png',fullPage:true});
   console.log('PASS: live Muse plan, styled popup, actual subject validation, local draft persistence and durable guidance completion.');
   return;
  }
  await page.waitForTimeout(250);
 }
 throw Error('No terminal outcome');
 } catch(error) { await page.screenshot({path:'.codex/ellai-guidance-interaction-failure.png',fullPage:true}); throw error; } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
