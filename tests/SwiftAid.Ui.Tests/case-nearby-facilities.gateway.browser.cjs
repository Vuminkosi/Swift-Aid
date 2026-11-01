const assert=require('node:assert/strict');
const {chromium}=require('playwright');
(async()=>{
 const b=await chromium.connectOverCDP('http://127.0.0.1:19226');
 const p=b.contexts().flatMap(c=>c.pages()).find(p=>p.url().startsWith('http://localhost:8080/'));
 p.setDefaultTimeout(45000);
 const errors=[];p.on('pageerror',e=>errors.push(e.message));
 await p.setViewportSize({width:1440,height:1000});
 await p.goto('http://localhost:8080/my-cases');
 await p.locator('a[href^="/case/"]').first().waitFor();
 const hrefs=await p.locator('a[href^="/case/"]').evaluateAll(a=>[...new Set(a.map(x=>x.getAttribute('href')))].slice(0,20));
 let target, result;
 for(const href of hrefs){
  const id=href.split('/')[2].split('?')[0];
  const response=await p.evaluate(async id=>{const r=await fetch('/api/v1/cases/'+id+'/report-details');const d=r.ok?await r.json():null;return {status:r.status,cache:r.headers.get('cache-control'),nearby:d?.nearbyFacilities,privateCoordinates:d?(/"latitude"|"longitude"/i.test(JSON.stringify(d))):false}},id);
  assert.equal(response.status,200);assert.equal(response.privateCoordinates,false);assert.match(response.cache,/no-store/);
  if(response.nearby?.clinic && response.nearby?.hospital){target=href;result=response;break;}
 }
 const hasCoordinates=Boolean(target);
 if(process.env.SWIFTAID_EXPECT_NEARBY==='1') assert.ok(hasCoordinates,'Expected a live report-coordinate or locality-centre result.');
 target ??= hrefs[0];
 assert.ok(target,'An authorized test case is required.');
 await p.goto('http://localhost:8080'+target);
 const section=p.getByRole('region',{name:'Nearby medical facilities'});
 await section.waitFor();
 const composer=p.getByRole('button',{name:'Close Ellai composer',exact:true});
 if(await composer.isVisible()) await composer.click();
 await p.waitForTimeout(1500);
 await section.waitFor();
 if(hasCoordinates){
 assert.match(await section.innerText(),/km away/);
 assert.match(await section.innerText(),/road distances/);
 if(result.nearby.localityCentre) assert.ok((await section.innerText()).includes('mapped centre of '+result.nearby.localityCentre));
 for(const type of ['clinic','hospital']){
  const facility=result.nearby[type];const link=section.getByRole('link',{name:facility.name+' ↗',exact:true});
  assert.equal(await link.getAttribute('href'),facility.mapUrl);
  assert.match(facility.mapUrl,/^https:\/\/www.openstreetmap.org\/(node|way|relation)\/\d+$/);
  assert.ok(facility.distanceKilometres>=0);
 }
 }else{assert.match(await section.innerText(),/Distances unavailable/);}
 await section.scrollIntoViewIfNeeded();await p.screenshot({path:'artifacts/live-integration/case-nearby-facilities.png'});
 await p.setViewportSize({width:390,height:844});await p.waitForTimeout(500);
 const nav=p.getByRole('button',{name:'Close navigation',exact:true});if(await nav.isVisible())await nav.click();await p.waitForTimeout(500);
 await section.scrollIntoViewIfNeeded();
 assert.ok(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
 await p.screenshot({path:'artifacts/live-integration/case-nearby-facilities-mobile.png'});
 assert.deepEqual(errors,[]);
 console.log(JSON.stringify({authorizedReport:'PASS',noPrivateCoordinates:'PASS',clinicAndHospital:hasCoordinates?'PASS':'NOT VERIFIED: existing cases lack coordinates',facilityLinks:hasCoordinates?'PASS':'NOT VERIFIED',missingCoordinates:hasCoordinates?'NOT APPLICABLE':'PASS',responsive:'PASS',errors}));
 await p.setViewportSize({width:1440,height:1000});process.exit(0);
})().catch(e=>{console.error(e.stack);process.exit(1)});
