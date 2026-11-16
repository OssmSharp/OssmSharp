import React from 'react';
import {createRoot} from 'react-dom/client';
import {flushSync} from 'react-dom';
import {Button} from '@astryxdesign/core/Button';
import core from '@astryxdesign/core/astryx.css';
import theme from '@astryxdesign/theme-neutral/theme.css';

// Blazor owns the light DOM and event handlers; React owns only the shadow tree.
// The existing controller can synchronously set .disabled and aria-pressed.
const sheet = new CSSStyleSheet();
sheet.replaceSync(`${core}\n${theme}\n:host{display:inline-block;vertical-align:middle}button{min-height:44px;width:100%;font:600 14px/1.4 Geist,system-ui,sans-serif;border-radius:10px} :host([hidden]){display:none} :host(:focus-within){outline:3px solid var(--focus);outline-offset:3px;border-radius:10px} [data-astryx-theme]{--font-family-body:Geist,system-ui,sans-serif;--color-accent:light-dark(#006f70,#69ded5)} :host(.danger) button{background:light-dark(#b42336,#fda4af);color:light-dark(white,#350b16)} :host([aria-pressed=true]) button{background:var(--accent-fill);color:var(--accent-ink)} button:disabled{opacity:.5}`);
class OssmButton extends HTMLElement {
  static observedAttributes = ['disabled','aria-pressed','aria-label','title','class'];
  get disabled(){ return this.hasAttribute('disabled'); }
  set disabled(value){ this.toggleAttribute('disabled', Boolean(value)); }
  connectedCallback(){
    if (!this.shadowRoot) {
      const shadow = this.attachShadow({mode:'open'});
      shadow.adoptedStyleSheets = [sheet];
      this.mount = document.createElement('div');
      shadow.append(this.mount);
    }
    this.root = createRoot(this.mount);
    this.observer = new MutationObserver(()=>this.render());
    this.observer.observe(this, {childList:true, characterData:true, subtree:true});
    this.themeObserver = new MutationObserver(()=>this.render());
    this.themeObserver.observe(document.documentElement, {attributes:true, attributeFilter:['data-theme']});
    this.render();
  }
  disconnectedCallback(){this.observer?.disconnect();this.themeObserver?.disconnect();this.root?.unmount();this.root=null;}
  attributeChangedCallback(){this.render();}
  render(){
    if (!this.root) return;
    const label = this.getAttribute('aria-label') || this.textContent.trim();
    const variant = this.classList.contains('danger') ? 'destructive' : this.classList.contains('primary') || this.classList.contains('success') ? 'primary' : 'secondary';
    flushSync(()=>this.root.render(<div data-astryx-theme="neutral" data-theme={document.documentElement.dataset.theme || 'dark'} style={{colorScheme:document.documentElement.dataset.theme || 'dark'}}>
      <Button label={label} variant={variant} isDisabled={this.disabled} aria-pressed={this.getAttribute('aria-pressed') ?? undefined} title={this.title || undefined}><slot /></Button>
    </div>));
  }
}
customElements.define('ossm-button', OssmButton);
