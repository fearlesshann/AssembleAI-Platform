import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createLifecycleQueue } from '../../packages/app-sdk/src/lifecycle-queue.ts';

test('V19: forward navigation waits for a pending back-navigation unmount', async () => {
  const queue = createLifecycleQueue();
  const actions = [];
  let mounted = true;
  let releaseUnmount;
  const gate = new Promise((resolve) => { releaseUnmount = resolve; });
  const back = queue.run(async () => {
    actions.push('unmount-start');
    await gate;
    mounted = false;
    actions.push('unmount-end');
  });
  await Promise.resolve();
  const forward = queue.run(async () => {
    assert.equal(mounted, false, 'Do not remount while the old instance is still mounted');
    mounted = true;
    actions.push('mount');
  });
  await Promise.resolve();
  assert.deepEqual(actions, ['unmount-start']);
  releaseUnmount();
  await Promise.all([back, forward]);
  assert.equal(mounted, true);
  assert.deepEqual(actions, ['unmount-start', 'unmount-end', 'mount']);
});

test('V19: failed lifecycle work does not poison the next retry', async () => {
  const queue = createLifecycleQueue();
  let retried = false;
  await assert.rejects(queue.run(async () => { throw new Error('load failed'); }), /load failed/);
  await queue.run(async () => { retried = true; });
  assert.equal(retried, true);
});
