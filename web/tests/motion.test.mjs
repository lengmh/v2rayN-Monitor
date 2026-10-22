import { test } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { createUiMotion } from '../src/lib/motion.ts';
function setup(t, reduced = false) {
  const dom = new JSDOM('<button id="toggle">More</button><div id="target"><button id="old">Old</button></div>', { pretendToBeVisual: true });
  const w = dom.window, calls = [], listeners = [];
  w.matchMedia = () => ({ matches: reduced, addEventListener: (_, fn) => listeners.push(fn) });
  w.HTMLElement.prototype.getClientRects = function () { return [this.getBoundingClientRect()]; };
  w.HTMLElement.prototype.getBoundingClientRect = function () { return { left: 0, top: Number(this.dataset.top || 0), width: 300, height: Number(this.dataset.height || 100), bottom: 100, right: 300 }; };
  w.HTMLElement.prototype.animate = function (frames, options) {
    let resolve, reject;
    const animation = { target: this, frames, options, cancelled: false,
      finished: new Promise((yes, no) => { resolve = yes; reject = no; }),
      finish: () => resolve(), cancel: () => { animation.cancelled = true; reject(new Error('cancelled')); } };
    calls.push(animation); return animation;
  };
  const previous = Object.fromEntries(['window','document','HTMLElement','getComputedStyle'].map(k => [k, globalThis[k]]));
  Object.assign(globalThis, { window: w, document: w.document, HTMLElement: w.HTMLElement, getComputedStyle: w.getComputedStyle.bind(w) });
  const motion = createUiMotion();
  t.after(() => { motion.cancel(); dom.window.close(); Object.assign(globalThis, previous); });
  return { w, calls, motion, target: w.document.querySelector('#target'), control: w.document.querySelector('#toggle'), listeners };
}
const settle = () => new Promise(resolve => setImmediate(resolve));
test('crossfade retains inert old content while new content is current, then removes snapshot', async t => {
  const { w, calls, motion, target } = setup(t);
  motion.crossfade(target, () => { target.textContent = 'New'; });
  const ghost = w.document.querySelector('.motion-snapshot');
  assert.equal(ghost.textContent, 'Old'); assert.equal(target.textContent, 'New');
  assert.equal(ghost.inert, true); assert.equal(ghost.getAttribute('aria-hidden'), 'true');
  assert.equal(ghost.querySelector('[id]'), null); assert.equal(ghost.id, '');
  assert.equal(calls.length, 2); assert.equal(calls[0].frames[0].opacity, 1); assert.equal(calls[1].frames[0].opacity, 0);
  assert.equal(calls[1].options.duration, 220);
  calls.forEach(a => a.finish()); await settle();
  assert.equal(w.document.querySelector('.motion-snapshot'), null);
});
test('rapid replacement and polling cancellation never leave a stale snapshot or final mutation', async t => {
  const { w, calls, motion, target } = setup(t);
  motion.crossfade(target, () => { target.textContent = 'B'; });
  motion.crossfade(target, () => { target.textContent = 'C'; });
  assert.equal(w.document.querySelectorAll('.motion-snapshot').length, 1);
  assert.equal(w.document.querySelector('.motion-snapshot').textContent, 'B');
  assert.ok(calls.slice(0,2).every(a => a.cancelled));
  motion.cancel(); target.textContent = 'Collector refresh'; await settle();
  assert.equal(w.document.querySelectorAll('.motion-snapshot').length, 0);
  assert.equal(target.textContent, 'Collector refresh');
  assert.equal(calls.length, 4);
});
test('FLIP follows stable source keys and moves both rows of each pair together', async t => {
  const { calls, motion, target } = setup(t);
  const rows = positions => { target.replaceChildren(...positions.map(([key, top]) => { const r = document.createElement('div'); r.dataset.motionKey = key; r.dataset.top = top; return r; })); };
  rows([['A:0',0],['A:1',20],['B:0',40],['B:1',60]]);
  motion.reorder(target, () => rows([['B:0',0],['B:1',20],['A:0',40],['A:1',60]]));
  assert.deepEqual(calls.map(a=>a.frames[0].transform), ['translateY(40px)','translateY(40px)','translateY(-40px)','translateY(-40px)']);
  assert.deepEqual([...target.children].map(r=>r.dataset.motionKey),['B:0','B:1','A:0','A:1']);
  calls.forEach(a=>a.finish()); await settle(); assert.ok(calls.every(a=>a.cancelled));
});
test('disclosure can reverse midway, preserves latest state, and returns focus before hiding', async t => {
  const { w, calls, motion, target, control } = setup(t);
  target.dataset.height = '0'; motion.disclose(target, control, true);
  target.dataset.height = '45'; w.document.querySelector('#old').focus();
  motion.disclose(target, control, false);
  assert.equal(w.document.activeElement, control); assert.equal(target.inert, true);
  assert.equal(calls[1].frames[0].height, '45px'); assert.equal(control.getAttribute('aria-expanded'), 'false');
  motion.disclose(target, control, true);
  calls.at(-1).finish(); await settle();
  assert.equal(target.classList.contains('expanded'), true); assert.equal(target.inert, false);
  assert.equal(control.getAttribute('aria-expanded'), 'true');
});
test('reduced motion commits all states without snapshots or animation', t => {
  const { w, calls, motion, target, control } = setup(t, true);
  motion.crossfade(target,()=>{ target.textContent='Now'; });
  motion.reorder(target,()=>{ target.textContent='Sorted'; });
  motion.disclose(target,control,true);
  assert.equal(target.textContent,'Sorted'); assert.equal(target.classList.contains('expanded'),true);
  assert.equal(calls.length,0); assert.equal(w.document.querySelector('.motion-snapshot'),null);
});

test('outgoing snapshot preserves scrolled content and comparison style context', t => {
  const { w, calls, motion, target } = setup(t);
  const wrapper = w.document.createElement('div'); wrapper.className='comparison-tables';
  target.replaceWith(wrapper); wrapper.append(target);
  target.scrollTop=36; target.firstElementChild.scrollLeft=24;
  motion.crossfade(target,()=>{target.textContent='Replacement';});
  const old=w.document.querySelector('.motion-snapshot');
  assert.equal(old.classList.contains('motion-comparison-context'),true);
  assert.equal(old.scrollTop,36); assert.equal(old.firstElementChild.scrollLeft,24);
  old.firstElementChild.dispatchEvent(new w.Event('scroll'));
  assert.ok(calls.every(a=>!a.cancelled));
  target.dispatchEvent(new w.Event('scroll'));
  assert.equal(w.document.querySelector('.motion-snapshot'),null);
});
