import {test,expect} from '@playwright/test';
import {readFile} from 'node:fs/promises';
const bundle = await readFile(new URL('../../src/OssmSharp.Web.Common/wwwroot/astryx/elements.js',import.meta.url),'utf8');
test('Astryx boundary preserves native clicks, disabled state, focus, and selections', async ({page})=>{
 await page.setContent('<html data-theme="dark"><body><ossm-button class="primary" data-pattern="0" aria-pressed="false">Simple stroke</ossm-button></body></html>');
 await page.addScriptTag({content:bundle,type:'module'});
 const button=page.getByRole('button',{name:'Simple stroke'});
 await expect(button).toBeVisible();
 await page.evaluate(()=>{window.clicks=0;document.querySelector('ossm-button').addEventListener('click',()=>window.clicks++);});
 await button.focus();await page.keyboard.press('Enter');
 expect(await page.evaluate(()=>window.clicks)).toBe(1);
 await page.evaluate(()=>{const el=document.querySelector('ossm-button');el.disabled=true;el.setAttribute('aria-pressed','true');});
 await expect(button).toBeDisabled();await expect(button).toHaveAttribute('aria-pressed','true');
 await page.evaluate(()=>{document.querySelector('ossm-button').disabled=false;});
 await button.focus();
 await page.evaluate(()=>document.documentElement.dataset.theme='light');
 await expect(button).toBeFocused();
 await page.evaluate(()=>document.querySelector('ossm-button').textContent='Deeper');
 await expect(page.getByRole('button',{name:'Deeper'})).toBeVisible();
 await page.evaluate(()=>{const el=document.querySelector('ossm-button');el.remove();document.body.append(el);});
 await expect(page.getByRole('button',{name:'Deeper'})).toBeEnabled();
});
test('guest controls fit a narrow viewport', async ({page})=>{
 await page.setViewportSize({width:390,height:844});
 await page.setContent('<html data-theme="light"><body><div style="display:flex;gap:10px;width:350px"><ossm-button class="success">Send</ossm-button><ossm-button class="danger">Stop</ossm-button></div></body></html>');
 await page.addScriptTag({content:bundle,type:'module'});
 await expect(page.getByRole('button',{name:'Stop'})).toBeVisible();
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});
